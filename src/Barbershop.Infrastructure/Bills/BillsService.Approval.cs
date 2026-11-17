using Barbershop.Application.Bills;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Application.Notifications;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Bills;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Loyalty;
using Barbershop.Domain.Memberships;
using Barbershop.Domain.Products;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Authorization;
using Barbershop.Infrastructure.Products;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Bills;

/// <summary>
/// ADR-0020 stage 3: the approval of a whole bill, with all its postings in one transaction. Locks follow one global
/// order (bill → customer profiles → memberships → redemptions → products), compatible with ADR-0014 and ADR-0015; a
/// retried approval (same key) returns the stored one, and a second approver gets a 409.
/// </summary>
internal sealed partial class BillsService
{
    internal const string SelfApprovalForbiddenMessage =
        "No puedes aprobar una cuenta en la que tú eres el cliente o el profesional de alguna línea. Pide a otra persona que la revise.";

    private const string AlreadyApprovedMessage = "Esta cuenta ya fue aprobada por otra persona. Recarga la bandeja.";

    public async Task<BillApprovalPreviewView> PreviewAsync(Guid currentUserId, Guid billId, CancellationToken cancellationToken = default)
    {
        var actor = await LoadActorAsync(currentUserId, cancellationToken);
        if (!actor.Has(PermissionCodes.BillsApprove))
        {
            throw new UnauthorizedAccessException("No tienes permiso para aprobar cuentas.");
        }

        var bill = await LoadVisibleAsync(actor, billId, tracked: false, cancellationToken);
        var lines = bill.Lines.OrderBy(line => line.Position).ToList();
        var shares = AllocateDiscounts(bill);
        var loyaltyEnabled = await _wallet.IsProgramEnabledAsync(cancellationToken);
        var points = await EligiblePointsAsync(bill, loyaltyEnabled, cancellationToken);
        var staffIds = lines.Where(line => line.StaffProfileId is not null).Select(line => line.StaffProfileId!.Value).Distinct().ToArray();
        var staff = await _dbContext.StaffProfiles.AsNoTracking()
            .Where(profile => staffIds.Contains(profile.Id))
            .ToDictionaryAsync(profile => profile.Id, profile => new { profile.DisplayName, profile.IsActive }, cancellationToken);
        var rateIds = lines.Where(line => line.ServiceId is not null).Select(line => line.ServiceId!.Value).Distinct().ToArray();
        var ratePrices = await _dbContext.Services.AsNoTracking()
            .Where(rate => rateIds.Contains(rate.Id))
            .ToDictionaryAsync(rate => rate.Id, rate => rate.BasePrice, cancellationToken);
        var productIds = lines.Where(line => line.ProductId is not null).Select(line => line.ProductId!.Value).Distinct().ToArray();
        var productPrices = await _dbContext.Products.AsNoTracking()
            .Where(product => productIds.Contains(product.Id))
            .ToDictionaryAsync(product => product.Id, product => product.Price, cancellationToken);

        var previews = lines.Select(line =>
        {
            var share = shares.GetValueOrDefault(line.Id);
            var net = line.IsChargeable ? share.Net : line.Type == BillLineType.Tip && line.TipPaymentMode == TipPaymentMode.WithBill ? line.UnitPrice : 0;
            var professional = ProfessionalShare(line);
            int? catalogNow = line.Type switch
            {
                BillLineType.Service when line.ServiceId is { } rateId => ratePrices.GetValueOrDefault(rateId),
                BillLineType.Product => productPrices.GetValueOrDefault(line.ProductId!.Value),
                _ => null
            };
            return new BillApprovalLinePreview(
                line.Id,
                line.Type.ToString(),
                line.NameSnapshot,
                line.StaffProfileId is { } staffId && staff.TryGetValue(staffId, out var profile) ? profile.DisplayName : null,
                line.StaffProfileId is not { } activeId || !staff.TryGetValue(activeId, out var active) || active.IsActive,
                line.GrossAmount,
                share.Discount,
                net,
                professional,
                line.IsChargeable || line.Type == BillLineType.Tip ? net - professional : 0,
                points.GetValueOrDefault(line.Id),
                catalogNow,
                catalogNow is { } price && line.IsChargeable && price != line.UnitPrice,
                EffectOf(line));
        }).ToArray();

        var view = await BuildViewAsync(bill, actor, cancellationToken);
        var warnings = view.Warnings.ToList();
        if (previews.Any(line => line.PriceChanged))
        {
            warnings.Add("El precio de catálogo cambió desde que se agregó alguna línea. Se aprueba lo que se cobró.");
        }

        var needsDeliver = lines.Any(line => line.Type == BillLineType.Redemption);
        if (needsDeliver && !actor.Has(PermissionCodes.LoyaltyRedemptionsDeliver))
        {
            warnings.Add("Esta cuenta trae canjes de puntos: para aprobarla necesitas también el permiso de entregar canjes.");
        }

        return new BillApprovalPreviewView(
            bill.Id,
            bill.Version,
            previews,
            await IsBeneficiaryAsync(currentUserId, bill, cancellationToken),
            needsDeliver,
            loyaltyEnabled,
            warnings);
    }

    public async Task<BillView> ApproveAsync(Guid currentUserId, Guid billId, BillApproveRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Version is null)
        {
            throw Invalid("version", MissingVersionMessage);
        }

        if (request.IdempotencyKey == Guid.Empty)
        {
            throw Invalid("idempotencyKey", "Falta la clave de la operación; recarga la página e intenta de nuevo.");
        }

        if (request.Notes is { Length: > Bill.MaxNotesLength })
        {
            throw Invalid("notes", $"Las notas deben tener {Bill.MaxNotesLength} caracteres o menos.");
        }

        var actor = await LoadActorAsync(currentUserId, cancellationToken);
        if (!actor.Has(PermissionCodes.BillsApprove))
        {
            throw new UnauthorizedAccessException("No tienes permiso para aprobar cuentas.");
        }

        if (await ReplayApprovalAsync(currentUserId, billId, request.IdempotencyKey, cancellationToken) is { } replay)
        {
            return replay;
        }

        try
        {
            await using var transaction = await BeginTransactionAsync(cancellationToken);
            var bill = await LoadLockedAsync(actor, billId, cancellationToken);
            switch (bill.Status)
            {
                case BillStatus.Approved:
                    throw new ConflictException(AlreadyApprovedMessage);
                case BillStatus.Open:
                    throw new ConflictException("La cuenta todavía está abierta: debe cerrarse antes de aprobarla.");
                case BillStatus.Voided:
                    throw new ConflictException("La cuenta está anulada.");
            }

            EnsureVersion(bill, request.Version.Value);
            var lines = bill.Lines.OrderBy(line => line.Position).ToList();
            if (lines.Any(line => line.Type == BillLineType.Redemption) && !actor.Has(PermissionCodes.LoyaltyRedemptionsDeliver))
            {
                // C8: a redemption needs both permissions, as R13 required for the service records.
                throw new UnauthorizedAccessException(
                    "Esta cuenta trae canjes de puntos: para aprobarla necesitas también el permiso de entregar canjes.");
            }

            if (lines.FirstOrDefault(line => line.Type == BillLineType.Service && line.ServiceId is null) is { } withoutRate)
            {
                throw new ConflictException($"Elige la tarifa de «{withoutRate.NameSnapshot}» antes de aprobar.");
            }

            ValidateDiscounts(bill);

            // P4 / ADR-0018 D1: nobody approves a bill where they are the customer or a professional of a line; an Admin
            // may, and the bill and its incomes say so.
            var selfApproved = await SelfApprovalGuard.ResolveAsync(
                _dbContext, currentUserId, await IsBeneficiaryAsync(currentUserId, bill, cancellationToken), SelfApprovalForbiddenMessage,
                cancellationToken);

            var utcNow = Now();
            Guid? live = bill.CustomerId is { } customerId ? await _wallet.LockLiveCustomerAsync(customerId, cancellationToken) : null;
            await LockInOrderAsync("customer_memberships", lines.Select(line => line.CustomerMembershipId), cancellationToken);
            await LockInOrderAsync("loyalty_redemptions", lines.Select(line => line.LoyaltyRedemptionId), cancellationToken);
            foreach (var productId in lines.Where(line => line.ProductId is not null).Select(line => line.ProductId!.Value).Distinct().Order())
            {
                await _dbContext.LockProductAsync(productId, cancellationToken);
            }

            var sequence = await _dbContext.BillApprovals.CountAsync(approval => approval.BillId == bill.Id, cancellationToken) + 1;
            // ADR-0020 (decided 2026-11-15): a new approval after an unapproval posts its money and stock on the day of
            // the new approval, so the period of the service never changes; the counter-entries are on the day of the
            // unapproval. Membership consumptions keep the service date (their validity is checked against it).
            var postingDate = sequence > 1 ? Today() : bill.ServiceDate;
            var approvalId = Guid.NewGuid();
            var shares = AllocateDiscounts(bill);
            var loyaltyEnabled = await _wallet.IsProgramEnabledAsync(cancellationToken);
            var points = live is null ? new Dictionary<Guid, int>() : await EligiblePointsAsync(bill, loyaltyEnabled, cancellationToken);
            var noPoints = (request.NoPointsLineIds ?? []).ToHashSet();
            var postings = new List<BillPosting>();
            var snapshot = new List<object>();
            var stocks = new Dictionary<Guid, ProductStock>();

            foreach (var line in lines)
            {
                var share = shares.GetValueOrDefault(line.Id);
                int? credited = null;
                switch (line.Type)
                {
                    case BillLineType.Service when !line.IsCourtesy:
                    {
                        var income = IncomeEntry.CreateBillService(
                            line.ServiceId!.Value, line.NameSnapshot, line.UnitPrice, line.BusinessPercentageSnapshot!.Value,
                            line.StaffProfileId!.Value, share.Net, line.GrossAmount, postingDate, bill.Id, line.Id, currentUserId, utcNow);
                        _dbContext.IncomeEntries.Add(income);
                        postings.Add(new BillPosting(Guid.NewGuid(), approvalId, line.Id, BillPostingEffect.Income, income.Id, utcNow));
                        if (live is { } customer && points.TryGetValue(line.Id, out var linePoints) && !noPoints.Contains(line.Id))
                        {
                            // The posting id is the source of the lot: a new approval (stage 4) earns again, a retry never does.
                            var postingId = Guid.NewGuid();
                            var lot = await _wallet.AccrueInCurrentTransactionAsync(
                                customer, LoyaltySourceType.BillLine, postingId, linePoints, currentUserId, utcNow, cancellationToken);
                            if (lot is not null)
                            {
                                postings.Add(new BillPosting(postingId, approvalId, line.Id, BillPostingEffect.LoyaltyAccrual, lot.Id, utcNow));
                                credited = linePoints;
                            }
                        }

                        break;
                    }

                    case BillLineType.Product:
                    {
                        var stock = await LoadStockAsync(line.ProductId!.Value, stocks, utcNow, cancellationToken);
                        var costBefore = stock.AverageUnitCost;
                        var movementPostingId = Guid.NewGuid();
                        var movement = stock.Sell(line.Quantity, postingDate, movementPostingId, currentUserId, utcNow);
                        _dbContext.InventoryMovements.Add(movement);
                        var income = IncomeEntry.CreateProductSale(
                            line.ProductId!.Value, line.NameSnapshot, line.Quantity, line.UnitPrice, costBefore, share.Net, postingDate,
                            bill.Id, line.Id, currentUserId, utcNow);
                        _dbContext.IncomeEntries.Add(income);
                        postings.Add(new BillPosting(Guid.NewGuid(), approvalId, line.Id, BillPostingEffect.Income, income.Id, utcNow));
                        postings.Add(new BillPosting(movementPostingId, approvalId, line.Id, BillPostingEffect.InventorySale, movement.Id, utcNow));
                        break;
                    }

                    case BillLineType.MembershipConsumption:
                    {
                        var postingId = Guid.NewGuid();
                        MembershipConsumption consumption;
                        try
                        {
                            consumption = await _ledger.RegisterInCurrentTransactionAsync(
                                currentUserId, line.CustomerMembershipId!.Value,
                                new MembershipConsumptionRequest(line.ServiceId!.Value, 1, bill.ServiceDate, line.StaffProfileId, postingId),
                                cancellationToken, expectedCustomerId: live);
                        }
                        catch (ValidationProblemException exception)
                        {
                            var detail = exception.Errors.Values.SelectMany(messages => messages).FirstOrDefault() ?? "sin saldo";
                            throw new ConflictException($"No se puede descontar «{line.NameSnapshot}» de la membresía: {detail} Edita la línea antes de aprobar.");
                        }

                        postings.Add(new BillPosting(postingId, approvalId, line.Id, BillPostingEffect.MembershipConsumption, consumption.Id, utcNow));
                        break;
                    }

                    case BillLineType.Redemption:
                    {
                        var redemption = await _redemptions.DeliverForBillInCurrentTransactionAsync(
                            line.LoyaltyRedemptionId!.Value, live ?? throw new ConflictException(WalkInLimitMessage), line.ServiceId, line.Id,
                            currentUserId, utcNow, cancellationToken);
                        postings.Add(new BillPosting(Guid.NewGuid(), approvalId, line.Id, BillPostingEffect.RedemptionDelivery, redemption.Id, utcNow));
                        break;
                    }

                    case BillLineType.Tip when line.TipPaymentMode == TipPaymentMode.WithBill:
                    {
                        var income = IncomeEntry.CreateTip(line.StaffProfileId!.Value, line.UnitPrice, postingDate, bill.Id, line.Id, currentUserId, utcNow);
                        _dbContext.IncomeEntries.Add(income);
                        postings.Add(new BillPosting(Guid.NewGuid(), approvalId, line.Id, BillPostingEffect.Income, income.Id, utcNow));
                        break;
                    }
                }

                snapshot.Add(new
                {
                    lineId = line.Id,
                    type = line.Type.ToString(),
                    name = line.NameSnapshot,
                    line.StaffProfileId,
                    line.ServiceId,
                    line.ProductId,
                    line.Quantity,
                    line.UnitPrice,
                    line.BusinessPercentageSnapshot,
                    line.IsCourtesy,
                    gross = line.GrossAmount,
                    discountShare = share.Discount,
                    net = share.Net,
                    tipPaymentMode = line.TipPaymentMode?.ToString(),
                    points = credited,
                });
            }

            var approval = new BillApproval(
                bill.Id, sequence, request.IdempotencyKey, currentUserId, utcNow, selfApproved, live, Json(snapshot), request.Notes?.Trim(),
                approvalId);
            _dbContext.BillApprovals.Add(approval);
            _dbContext.BillPostings.AddRange(postings);
            var statusBefore = bill.Status;
            bill.Approve(approvalId, currentUserId, utcNow, selfApproved);
            _dbContext.BillEvents.Add(new BillEvent(
                bill.Id, BillEventType.Approved, statusBefore, currentUserId, utcNow, request.Notes?.Trim(),
                afterJson: Json(new { sequence, selfApproved, postings = postings.Count })));
            if (sequence > 1 && live is { } corrected)
            {
                // ADR-0020 §7: the customer saw the receipt; a corrected approval tells them.
                await _notifications.QueueForCustomerAsync(corrected, UserNotificationTypes.BillCorrected, CorrectedMessage(bill), cancellationToken);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }
        catch (Exception) when (ResetTracker())
        {
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(AlreadyApprovedMessage);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            _dbContext.ChangeTracker.Clear();
            if (await ReplayApprovalAsync(currentUserId, billId, request.IdempotencyKey, cancellationToken) is { } stored)
            {
                return stored;
            }

            throw new ConflictException(AlreadyApprovedMessage);
        }

        return await GetAsync(currentUserId, billId, cancellationToken);
    }

    internal const string SelfUnapprovalForbiddenMessage =
        "No puedes desaprobar una cuenta en la que tú eres el cliente o el profesional de alguna línea. Pide a otra persona que la revise.";

    /// <summary>
    /// ADR-0020 stage 4 (Q8, Q9, Q20): unapproves the approved bill. In one transaction, with the same lock order as the
    /// approval and the version the client saw, every posting of the current approval is reversed: incomes get their
    /// counter-entry dated today, sold units come back at their cost, membership consumptions are reversed in the ledger,
    /// earned points are withdrawn (what was spent becomes a debt) and delivered redemptions go back to Approved with their
    /// points reserved. A retry with the same key returns the stored result; anything else on a bill not approved is a 409.
    /// </summary>
    public async Task<BillView> UnapproveAsync(Guid currentUserId, Guid billId, BillUnapproveRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Version is null)
        {
            throw Invalid("version", MissingVersionMessage);
        }

        if (request.IdempotencyKey == Guid.Empty)
        {
            throw Invalid("idempotencyKey", "Falta la clave de la operación; recarga la página e intenta de nuevo.");
        }

        var reason = ValidateReason(request.Reason);
        var actor = await LoadActorAsync(currentUserId, cancellationToken);
        if (!actor.Has(PermissionCodes.BillsUnapprove))
        {
            throw new UnauthorizedAccessException("No tienes permiso para desaprobar cuentas.");
        }

        if (await ReplayUnapprovalAsync(currentUserId, billId, request.IdempotencyKey, cancellationToken) is { } replay)
        {
            return replay;
        }

        try
        {
            await using var transaction = await BeginTransactionAsync(cancellationToken);
            var bill = await LoadLockedAsync(actor, billId, cancellationToken);
            if (bill.Status != BillStatus.Approved)
            {
                throw new ConflictException(bill.Status == BillStatus.Unapproved
                    ? "Esta cuenta ya fue desaprobada por otra persona. Recarga la página."
                    : "Solo se desaprueba una cuenta aprobada.");
            }

            EnsureVersion(bill, request.Version.Value);
            // ADR-0018 D1, applied to the unapproval too (decided 2026-11-15): an Admin may, and it stays marked.
            var selfUnapproved = await SelfApprovalGuard.ResolveAsync(
                _dbContext, currentUserId, await IsBeneficiaryAsync(currentUserId, bill, cancellationToken), SelfUnapprovalForbiddenMessage,
                cancellationToken);

            var approval = await _dbContext.BillApprovals.SingleAsync(candidate => candidate.Id == bill.CurrentApprovalId, cancellationToken);
            var postings = await _dbContext.BillPostings
                .Where(posting => posting.ApprovalId == approval.Id && posting.ReversedAt == null)
                .OrderBy(posting => posting.CreatedAt).ThenBy(posting => posting.Id)
                .ToListAsync(cancellationToken);

            // Same global order as the approval: bill → customer profiles → memberships → redemptions → products.
            Guid? live = approval.CustomerId is { } customerId ? await _wallet.LockLiveCustomerAsync(customerId, cancellationToken) : null;
            var consumptionIds = postings.Where(posting => posting.MembershipConsumptionId is not null).Select(posting => posting.MembershipConsumptionId!.Value).ToArray();
            var membershipIds = await _dbContext.MembershipConsumptions.AsNoTracking()
                .Where(consumption => consumptionIds.Contains(consumption.Id))
                .Select(consumption => (Guid?)consumption.CustomerMembershipId)
                .ToListAsync(cancellationToken);
            await LockInOrderAsync("customer_memberships", membershipIds, cancellationToken);
            await LockInOrderAsync("loyalty_redemptions", postings.Select(posting => posting.LoyaltyRedemptionId), cancellationToken);
            var movementIds = postings.Where(posting => posting.InventoryMovementId is not null).Select(posting => posting.InventoryMovementId!.Value).ToArray();
            var sales = await _dbContext.InventoryMovements.AsNoTracking()
                .Where(movement => movementIds.Contains(movement.Id))
                .ToDictionaryAsync(movement => movement.Id, cancellationToken);
            foreach (var productId in sales.Values.Select(movement => movement.ProductId).Distinct().Order())
            {
                await _dbContext.LockProductAsync(productId, cancellationToken);
            }

            var utcNow = Now();
            var today = Today();
            var number = bill.DisplayNumber!;
            var stocks = new Dictionary<Guid, ProductStock>();
            foreach (var posting in postings)
            {
                switch (posting.Effect)
                {
                    case BillPostingEffect.Income:
                    {
                        var original = await _dbContext.IncomeEntries.AsNoTracking().SingleAsync(entry => entry.Id == posting.IncomeEntryId, cancellationToken);
                        var counter = IncomeEntry.CreateReversal(original, today, currentUserId, utcNow);
                        _dbContext.IncomeEntries.Add(counter);
                        posting.MarkReversed(counter.Id, utcNow);
                        break;
                    }

                    case BillPostingEffect.InventorySale:
                    {
                        var sale = sales[posting.InventoryMovementId!.Value];
                        var stock = await LoadStockAsync(sale.ProductId, stocks, utcNow, cancellationToken);
                        var returned = stock.ReturnSale(sale.Quantity, sale.UnitCost, today, Guid.NewGuid(), currentUserId, utcNow);
                        _dbContext.InventoryMovements.Add(returned);
                        posting.MarkReversed(returned.Id, utcNow);
                        break;
                    }

                    case BillPostingEffect.MembershipConsumption:
                    {
                        var reversal = await _ledger.ReverseInCurrentTransactionAsync(
                            currentUserId, posting.MembershipConsumptionId!.Value, $"Cuenta {number} desaprobada: {reason}", cancellationToken);
                        posting.MarkReversed(reversal.Id, utcNow);
                        break;
                    }

                    case BillPostingEffect.LoyaltyAccrual:
                    {
                        var (_, debt) = await _wallet.WithdrawForBillUnapprovalInCurrentTransactionAsync(
                            posting.Id, live ?? throw new ConflictException(WalkInLimitMessage), number, currentUserId, utcNow, cancellationToken);
                        posting.MarkReversed(debt?.Id ?? posting.LoyaltyLotId, utcNow);
                        break;
                    }

                    case BillPostingEffect.RedemptionDelivery:
                    {
                        var redemption = await _redemptions.ReopenForBillUnapprovalInCurrentTransactionAsync(
                            posting.LoyaltyRedemptionId!.Value, live ?? throw new ConflictException(WalkInLimitMessage), posting.Id, currentUserId,
                            utcNow, cancellationToken);
                        posting.MarkReversed(redemption.Id, utcNow);
                        break;
                    }
                }
            }

            approval.Unapprove(request.IdempotencyKey, reason, currentUserId, utcNow, selfUnapproved);
            bill.Unapprove(utcNow);
            _dbContext.BillEvents.Add(new BillEvent(
                bill.Id, BillEventType.Unapproved, BillStatus.Approved, currentUserId, utcNow, reason,
                afterJson: Json(new { approval.Sequence, reversed = postings.Count, selfUnapproved })));
            await _dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }
        catch (Exception) when (ResetTracker())
        {
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(StaleMessage);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            _dbContext.ChangeTracker.Clear();
            if (await ReplayUnapprovalAsync(currentUserId, billId, request.IdempotencyKey, cancellationToken) is { } stored)
            {
                return stored;
            }

            throw new ConflictException("Esta cuenta ya fue desaprobada por otra persona. Recarga la página.");
        }

        return await GetAsync(currentUserId, billId, cancellationToken);
    }

    private async Task<BillView?> ReplayUnapprovalAsync(Guid currentUserId, Guid billId, Guid key, CancellationToken cancellationToken)
    {
        var previous = await _dbContext.BillApprovals.AsNoTracking().SingleOrDefaultAsync(approval => approval.UnapprovalKey == key, cancellationToken);
        if (previous is null)
        {
            return null;
        }

        return previous.BillId == billId && previous.UnapprovedByUserId == currentUserId
            ? await GetAsync(currentUserId, billId, cancellationToken)
            : throw new ConflictException("Esta desaprobación ya se usó para otra cuenta. Recarga la página.");
    }

    private async Task<BillView?> ReplayApprovalAsync(Guid currentUserId, Guid billId, Guid key, CancellationToken cancellationToken)
    {
        var previous = await _dbContext.BillApprovals.AsNoTracking().SingleOrDefaultAsync(approval => approval.IdempotencyKey == key, cancellationToken);
        if (previous is null)
        {
            return null;
        }

        return previous.BillId == billId && previous.ApprovedByUserId == currentUserId
            ? await GetAsync(currentUserId, billId, cancellationToken)
            : throw new ConflictException("Esta aprobación ya se usó para otra cuenta. Recarga la bandeja.");
    }

    /// <summary>The approver is the bill's customer (profile linked to their account, live or merged) or a professional of it.</summary>
    private async Task<bool> IsBeneficiaryAsync(Guid currentUserId, Bill bill, CancellationToken cancellationToken)
    {
        if (bill.CustomerId is { } customerId)
        {
            var live = await _wallet.ResolveLiveCustomerIdAsync(customerId, cancellationToken);
            if (await _dbContext.Customers.AsNoTracking()
                    .AnyAsync(customer => (customer.Id == customerId || customer.Id == live) && customer.UserId == currentUserId, cancellationToken))
            {
                return true;
            }
        }

        var staffIds = bill.Lines.Where(line => line.StaffProfileId is not null).Select(line => line.StaffProfileId!.Value)
            .Append(bill.StaffProfileId ?? Guid.Empty).Distinct().ToArray();
        return await _dbContext.StaffProfiles.AsNoTracking().AnyAsync(staff => staffIds.Contains(staff.Id) && staff.UserId == currentUserId, cancellationToken);
    }

    /// <summary>
    /// Points of each paid service line (ADR-0009 rule, read at approval): an identified customer, the rate's points in
    /// the catalog now, and the program enabled. Courtesies, consumptions, redemptions, products and walk-ins earn none.
    /// </summary>
    private async Task<Dictionary<Guid, int>> EligiblePointsAsync(Bill bill, bool loyaltyEnabled, CancellationToken cancellationToken)
    {
        if (!loyaltyEnabled || bill.CustomerId is null)
        {
            return [];
        }

        var serviceLines = bill.Lines.Where(line => line.Type == BillLineType.Service && !line.IsCourtesy && line.ServiceId is not null).ToList();
        var rateIds = serviceLines.Select(line => line.ServiceId!.Value).Distinct().ToArray();
        var pointsByRate = await _dbContext.Services.AsNoTracking()
            .Where(rate => rateIds.Contains(rate.Id) && rate.LoyaltyPoints > 0)
            .ToDictionaryAsync(rate => rate.Id, rate => rate.LoyaltyPoints, cancellationToken);
        return serviceLines.Where(line => pointsByRate.ContainsKey(line.ServiceId!.Value))
            .ToDictionary(line => line.Id, line => pointsByRate[line.ServiceId!.Value]);
    }

    internal readonly record struct DiscountShare(int Discount, int Net);

    /// <summary>
    /// ADR-0020 §1: a line discount applies to its line; a discount on the whole bill is spread over the chargeable lines
    /// in proportion to what is left of each, rounded to whole pesos by largest remainder (ties by position), so every
    /// income gets its net and the nets add up to what was charged.
    /// </summary>
    internal static Dictionary<Guid, DiscountShare> AllocateDiscounts(Bill bill)
    {
        var chargeable = bill.Lines.Where(line => line.IsChargeable).OrderBy(line => line.Position).ToList();
        var discounts = bill.Lines.Where(line => line.Type == BillLineType.Discount).ToList();
        var afterLine = chargeable.ToDictionary(
            line => line.Id,
            line => (long)line.GrossAmount - discounts.Where(discount => discount.AppliesToLineId == line.Id).Sum(discount => (long)discount.UnitPrice));
        var lineDiscount = chargeable.ToDictionary(line => line.Id, line => line.GrossAmount - afterLine[line.Id]);
        var total = discounts.Where(discount => discount.AppliesToLineId is null).Sum(discount => (long)discount.UnitPrice);
        var basis = afterLine.Values.Sum();
        var shares = chargeable.ToDictionary(line => line.Id, _ => 0L);
        if (total > 0 && basis > 0)
        {
            var remainders = new List<(Guid Id, long Remainder, int Position)>();
            foreach (var line in chargeable)
            {
                var exact = total * afterLine[line.Id];
                shares[line.Id] = exact / basis;
                remainders.Add((line.Id, exact % basis, line.Position));
            }

            var left = total - shares.Values.Sum();
            foreach (var (id, _, _) in remainders.OrderByDescending(item => item.Remainder).ThenBy(item => item.Position).Take((int)left))
            {
                shares[id] += 1;
            }
        }

        return chargeable.ToDictionary(
            line => line.Id,
            line => new DiscountShare((int)(lineDiscount[line.Id] + shares[line.Id]), (int)(afterLine[line.Id] - shares[line.Id])));
    }

    /// <summary>P7: the commission of a service uses its full price; products give none (Q11); a tip is all the professional's.</summary>
    private static int ProfessionalShare(BillLine line) => line.Type switch
    {
        BillLineType.Service when !line.IsCourtesy && line.BusinessPercentageSnapshot is { } percentage
            => line.GrossAmount - IncomeEntry.BusinessShare(line.GrossAmount, percentage),
        BillLineType.Tip when line.TipPaymentMode == TipPaymentMode.WithBill => line.UnitPrice,
        _ => 0
    };

    private static string EffectOf(BillLine line) => line.Type switch
    {
        BillLineType.Service when line.IsCourtesy => "Courtesy",
        BillLineType.Service => "ServiceIncome",
        BillLineType.Product => "ProductSale",
        BillLineType.MembershipConsumption => "MembershipConsumption",
        BillLineType.Redemption => "RedemptionDelivery",
        BillLineType.Tip when line.TipPaymentMode == TipPaymentMode.WithBill => "TipIncome",
        BillLineType.Tip => "DirectTip",
        _ => "Discount"
    };

    private async Task<ProductStock> LoadStockAsync(Guid productId, Dictionary<Guid, ProductStock> loaded, DateTime utcNow, CancellationToken cancellationToken)
    {
        if (loaded.TryGetValue(productId, out var known))
        {
            return known;
        }

        var stock = await _dbContext.ProductStocks.SingleOrDefaultAsync(candidate => candidate.ProductId == productId, cancellationToken);
        if (stock is null)
        {
            // Lazy row (ADR-0020 stage 1): a product never received has 0 units and no cost.
            stock = new ProductStock(productId, utcNow);
            _dbContext.ProductStocks.Add(stock);
        }

        loaded[productId] = stock;
        return stock;
    }

    private async Task LockInOrderAsync(string table, IEnumerable<Guid?> ids, CancellationToken cancellationToken)
    {
        if (!_dbContext.Database.IsNpgsql())
        {
            return;
        }

        foreach (var id in ids.OfType<Guid>().Distinct().Order())
        {
            // The table name is a constant of this class, never user input.
#pragma warning disable EF1002
            await _dbContext.Database.ExecuteSqlRawAsync($"SELECT 1 FROM public.{table} WHERE \"Id\" = {{0}} FOR UPDATE", [id], cancellationToken);
#pragma warning restore EF1002
        }
    }
}
