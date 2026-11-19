using System.Globalization;
using System.Text.Json;
using Barbershop.Application.Bills;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Common.Paging;
using Barbershop.Application.Notifications;
using Barbershop.Application.ServiceRecords;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Bills;
using Barbershop.Domain.Common;
using Barbershop.Domain.Loyalty;
using Barbershop.Domain.Memberships;
using Barbershop.Domain.ServiceRecords;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Appointments;
using Barbershop.Infrastructure.Loyalty;
using Barbershop.Infrastructure.Memberships;
using Barbershop.Infrastructure.Notifications;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Barbershop.Infrastructure.Bills;

/// <summary>
/// ADR-0020 stages 2 and 3: bills. Every change runs in a transaction that locks the bill row (<c>FOR UPDATE</c>) and
/// compares the version the client saw (<c>xmin</c>), so two people editing the same bill never overwrite each other.
/// Permissions are read from the database on every request (a revoked permission stops working at once): Admin, or the
/// <c>bills.*</c> permission of each action. Every change is audited in <see cref="BillEvent"/>.
/// </summary>
internal sealed partial class BillsService : IBillsService, IBillApprovalService, IPaymentMethodsService
{
    private const int MaxCustomerOptions = 10;

    /// <summary>SONDA BUG-157: a name is searched from 3 letters; a phone only by its full number (7 digits or more).</summary>
    internal const int MinCustomerSearchLength = 3;
    internal const int MinCustomerPhoneDigits = 7;
    internal const string OnlyTodayMessage =
        "La cuenta se abre el día de la atención, con la fecha de hoy. Lo que no se registró ese día ya no se registra.";
    internal const string PastAppointmentMessage =
        "Esta cita fue de un día anterior. Su cuenta se abría ese día; lo que no se registró ese día ya no se registra.";
    internal const string DateIsFixedMessage = "La fecha de una cuenta abierta no se cambia: es la del día de la atención.";

    internal const string StaleMessage = "La cuenta cambió mientras la editabas. Recarga e intenta de nuevo.";
    internal const string MissingVersionMessage = "Falta la versión de la cuenta. Recarga e intenta de nuevo.";
    internal const string NotFoundMessage = "La cuenta no existe o no tienes acceso a ella.";
    internal const string WalkInLimitMessage =
        "Una cuenta de cliente ocasional no puede usar membresías, canjes ni puntos. Elige un perfil de cliente.";
    internal const string AssignOtherStaffMessage =
        "Solo puedes cargar servicios y propinas a tu nombre. Para cargarlos a otro profesional necesitas el permiso «Cargar servicios y propinas a nombre de otro profesional».";
    internal const string DiscountPermissionMessage = "Solo administración o quien tenga el permiso de descuentos puede dar descuentos o cortesías.";
    internal const string DiscountTooLargeMessage = "El descuento no puede ser mayor que lo que descuenta.";
    internal const string CancelledAppointmentMessage =
        "La cita de esta cuenta se canceló o el cliente no asistió. Anula la cuenta o desvincúlala de la cita si el servicio sí se prestó.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("es-CO");

    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _timeProvider;
    private readonly MembershipLedgerService _ledger;
    private readonly LoyaltyWalletService _wallet;
    private readonly LoyaltyRedemptionService _redemptions;
    private readonly CustomerNotificationOutbox _notifications;

    public BillsService(AppDbContext dbContext, MembershipLedgerService ledger, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _ledger = ledger;
        _timeProvider = timeProvider;
        _wallet = new LoyaltyWalletService(dbContext, timeProvider);
        _redemptions = new LoyaltyRedemptionService(dbContext, timeProvider);
        _notifications = new CustomerNotificationOutbox(dbContext, timeProvider);
    }

    // ── Reading ─────────────────────────────────────────────────────────────────────────────────

    public async Task<BillOptionsView> GetOptionsAsync(Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var actor = await LoadActorAsync(currentUserId, cancellationToken);
        var rates = await _dbContext.Services.AsNoTracking()
            .Where(rate => !rate.IsDeleted && rate.IsActive)
            .Select(rate => new { rate.Id, rate.Name, rate.BasePrice, rate.LoyaltyPoints, rate.GeneralServiceId })
            .ToListAsync(cancellationToken);
        var generals = await (
                from general in _dbContext.GeneralServices.AsNoTracking()
                join category in _dbContext.ServiceCategories.AsNoTracking() on general.CategoryId equals category.Id
                where general.IsActive && !general.IsDeleted && category.IsActive && !category.IsDeleted
                orderby category.SortOrder, category.Name, general.SortOrder, general.Name
                select new { general.Id, general.Name, CategoryName = category.Name })
            .ToListAsync(cancellationToken);
        var services = generals
            .Select(general => new BillGeneralServiceOption(
                general.Id,
                general.Name,
                general.CategoryName,
                rates.Where(rate => rate.GeneralServiceId == general.Id)
                    .OrderBy(rate => rate.Name, StringComparer.CurrentCulture)
                    .Select(rate => new BillRateOption(rate.Id, rate.Name, rate.BasePrice, rate.LoyaltyPoints, true))
                    .ToArray()))
            .ToArray();

        var products = await _dbContext.Products.AsNoTracking()
            .Where(product => product.IsActive && !product.IsDeleted)
            .OrderBy(product => product.SortOrder).ThenBy(product => product.Name)
            .Select(product => new { product.Id, product.Name, product.Price })
            .ToListAsync(cancellationToken);
        var available = await AvailableStockAsync(products.Select(product => product.Id).ToArray(), excludeBillId: null, cancellationToken);

        var staff = await _dbContext.StaffProfiles.AsNoTracking()
            .OrderBy(profile => profile.DisplayName)
            .Select(profile => new BillStaffOption(profile.Id, profile.DisplayName, profile.IsActive))
            .ToListAsync(cancellationToken);
        var methods = await _dbContext.PaymentMethods.AsNoTracking()
            .Where(method => method.IsActive)
            .OrderBy(method => method.SortOrder).ThenBy(method => method.Name)
            .Select(method => new BillPaymentMethodOption(method.Id, method.Name))
            .ToListAsync(cancellationToken);

        return new BillOptionsView(
            services,
            products.Select(product => new BillProductOption(product.Id, product.Name, product.Price, available.GetValueOrDefault(product.Id))).ToArray(),
            staff,
            methods,
            actor.StaffProfileId,
            actor.Has(PermissionCodes.BillsManage),
            actor.Has(PermissionCodes.BillsDiscount),
            actor.Has(PermissionCodes.BillsCharge),
            actor.Has(PermissionCodes.BillsClose),
            actor.Has(PermissionCodes.BillsApprove),
            actor.Has(PermissionCodes.BillsVoid),
            actor.Has(PermissionCodes.BillsAssignOtherStaff));
    }

    public async Task<PagedResult<BillSummaryView>> ListAsync(Guid currentUserId, BillListQuery query, CancellationToken cancellationToken = default)
    {
        var actor = await LoadActorAsync(currentUserId, cancellationToken);
        var page = PageRequest.Create(query.Page, query.PageSize);
        var status = ParseStatus(query.Status);
        var range = HistoryRange.ResolveOptional(query.From, query.To);

        var bills = VisibleBills(actor);
        if (status is { } wanted)
        {
            bills = bills.Where(bill => bill.Status == wanted);
        }

        if (range is not null)
        {
            bills = bills.Where(bill => bill.ServiceDate >= range.From && bill.ServiceDate <= range.To);
        }

        if (query.StaffProfileId is { } staffId)
        {
            bills = bills.Where(bill => bill.StaffProfileId == staffId || bill.Lines.Any(line => line.StaffProfileId == staffId));
        }

        if (query.CustomerId is { } customerId)
        {
            var family = await ProfileFamilyAsync(_dbContext, customerId, cancellationToken);
            bills = bills.Where(bill => bill.CustomerId != null && family.Contains(bill.CustomerId.Value));
        }

        if (query.PaymentMethodId is { } paymentMethodId)
        {
            bills = bills.Where(bill => bill.PaymentMethodId == paymentMethodId);
        }

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var term = query.Q.Trim();
            var number = ParseNumber(term);
            var lowered = term.ToLowerInvariant();
            bills = bills.Where(bill => (number != null && bill.Number == number) || bill.CustomerNameSnapshot.ToLower().Contains(lowered));
        }

        // The approval tray (closed) reads oldest first, like the service records queue; any other list newest first.
        var ordered = status == BillStatus.Closed
            ? bills.OrderBy(bill => bill.ClosedAt).ThenBy(bill => bill.Id)
            : bills.OrderByDescending(bill => bill.OpenedAt).ThenBy(bill => bill.Id);
        return await ordered.Include(bill => bill.Lines).AsSplitQuery()
            .ToPagedResultAsync(page, (rows, ct) => MapSummariesAsync(rows, ct), cancellationToken);
    }

    public async Task<BillPendingView> GetPendingAsync(Guid currentUserId, BillPendingQuery? query = null, CancellationToken cancellationToken = default)
    {
        var actor = await LoadActorAsync(currentUserId, cancellationToken);
        // SONDA BUG-181: «page» was ignored and every open bill came at once (≈170 KB with 370 bills).
        var page = PageRequest.Create(query?.Page, query?.PageSize);
        var openQuery = VisibleBills(actor).Where(bill => bill.Status == BillStatus.Open);
        var openTotal = await openQuery.CountAsync(cancellationToken);
        var lastPage = Math.Max(1, (openTotal + page.PageSize - 1) / page.PageSize);
        var pageNumber = Math.Min(page.Page, lastPage);
        var open = await openQuery
            .OrderBy(bill => bill.OpenedAt).ThenBy(bill => bill.Id)
            .Skip((pageNumber - 1) * page.PageSize).Take(page.PageSize)
            .Include(bill => bill.Lines).AsSplitQuery()
            .ToListAsync(cancellationToken);
        var appointments = await PendingAppointmentsQuery(actor)
            .OrderBy(appointment => appointment.StartsAt)
            .Select(appointment => new { appointment.Id, appointment.StartsAt, appointment.CustomerName, appointment.GeneralServiceNameSnapshot, appointment.StaffProfileId })
            .ToListAsync(cancellationToken);
        var staffIds = appointments.Select(appointment => appointment.StaffProfileId).Distinct().ToArray();
        var staffNames = await _dbContext.StaffProfiles.AsNoTracking()
            .Where(staff => staffIds.Contains(staff.Id))
            .ToDictionaryAsync(staff => staff.Id, staff => staff.DisplayName, cancellationToken);

        var summaries = await MapSummariesAsync(open, cancellationToken);
        return new BillPendingView(
            summaries,
            appointments.Select(appointment => new BillPendingAppointmentView(
                appointment.Id, appointment.StartsAt, appointment.CustomerName, appointment.GeneralServiceNameSnapshot,
                appointment.StaffProfileId, staffNames.GetValueOrDefault(appointment.StaffProfileId, string.Empty))).ToArray(),
            openTotal + appointments.Count,
            pageNumber,
            page.PageSize,
            openTotal);
    }

    public async Task<BillCountView> GetPendingCountAsync(Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var actor = await LoadActorAsync(currentUserId, cancellationToken);
        var open = await VisibleBills(actor).CountAsync(bill => bill.Status == BillStatus.Open, cancellationToken);
        var appointments = await PendingAppointmentsQuery(actor).CountAsync(cancellationToken);
        return new BillCountView(open + appointments);
    }

    public async Task<BillView> GetAsync(Guid currentUserId, Guid billId, CancellationToken cancellationToken = default)
    {
        var actor = await LoadActorAsync(currentUserId, cancellationToken);
        var bill = await LoadVisibleAsync(actor, billId, tracked: false, cancellationToken);
        return await BuildViewAsync(bill, actor, cancellationToken);
    }

    public async Task<IReadOnlyList<BillEventView>> GetEventsAsync(Guid currentUserId, Guid billId, CancellationToken cancellationToken = default)
    {
        var actor = await LoadActorAsync(currentUserId, cancellationToken);
        await LoadVisibleAsync(actor, billId, tracked: false, cancellationToken);
        var events = await _dbContext.BillEvents.AsNoTracking()
            .Where(billEvent => billEvent.BillId == billId)
            .OrderBy(billEvent => billEvent.CreatedAt).ThenBy(billEvent => billEvent.Id)
            .ToListAsync(cancellationToken);
        var userIds = events.Select(billEvent => billEvent.UserId).Distinct().ToArray();
        var names = await _dbContext.Users.AsNoTracking()
            .Where(user => userIds.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, user => user.FullName, cancellationToken);
        return events.Select(billEvent => new BillEventView(
                billEvent.Id, billEvent.Type.ToString(), billEvent.StatusAtEvent.ToString(), names.GetValueOrDefault(billEvent.UserId),
                billEvent.CreatedAt, billEvent.Reason, billEvent.BeforeJson, billEvent.AfterJson))
            .ToArray();
    }

    internal const string CustomerSearchPermissionMessage = "Solo quien abre cuentas puede buscar clientes.";

    public async Task<IReadOnlyList<BillCustomerOption>> SearchCustomersAsync(
        Guid currentUserId, string? query, CancellationToken cancellationToken = default)
    {
        // ADR-0021 L15 (SONDA BUG-157): the search serves to open a bill, so only the Admin and bills.open use it, read
        // from the database on every request like the other bill permissions.
        var actor = await LoadActorAsync(currentUserId, cancellationToken);
        if (!actor.Has(PermissionCodes.BillsOpen))
        {
            throw new UnauthorizedAccessException(CustomerSearchPermissionMessage);
        }

        // SONDA BUG-157, same minimization as ADR-0009: a name by its letters, an email or a phone only when written in full
        // (no fragments such as "@gmail.com" that would walk the base), few results and only hints of the contact data.
        var customers = _dbContext.Customers.AsNoTracking().Where(customer => customer.MergedIntoCustomerId == null);
        var term = query?.Trim() ?? string.Empty;
        if (term.Length < MinCustomerSearchLength)
        {
            return [];
        }

        if (term.Contains('@'))
        {
            var at = term.IndexOf('@');
            var domain = term[(at + 1)..];
            if (at == 0 || !domain.Contains('.') || domain.StartsWith('.') || domain.EndsWith('.') || term.Count(c => c == '@') != 1)
            {
                return [];
            }

            var email = term.ToUpperInvariant(); // the stored NormalizedEmail: trimmed and upper case
            customers = customers.Where(customer => customer.NormalizedEmail == email);
        }
        else if (term.All(c => char.IsAsciiDigit(c) || c is ' ' or '+' or '-' or '(' or ')'))
        {
            var digits = new string(term.Where(char.IsAsciiDigit).ToArray());
            if (digits.Length < MinCustomerPhoneDigits)
            {
                return [];
            }

            var withCountry = "57" + digits;
            var withoutCountry = digits.Length == 12 && digits.StartsWith("57", StringComparison.Ordinal) ? digits[2..] : digits;
            customers = customers.Where(customer =>
                customer.NormalizedPhone == digits || customer.NormalizedPhone == withCountry || customer.NormalizedPhone == withoutCountry);
        }
        else
        {
            var name = term.ToLowerInvariant();
            customers = customers.Where(customer => customer.FullName.ToLower().Contains(name));
        }

        return (await customers
                .OrderBy(customer => customer.FullName)
                .Take(MaxCustomerOptions)
                .Select(customer => new { customer.Id, customer.FullName, customer.Phone, customer.Email })
                .ToListAsync(cancellationToken))
            .Select(customer => new BillCustomerOption(
                customer.Id,
                customer.FullName,
                ServiceRecordCustomerPrivacy.PhoneHint(customer.Phone),
                ServiceRecordCustomerPrivacy.EmailHint(customer.Email)))
            .ToArray();
    }

    public async Task<IReadOnlyList<BillMembershipOption>> GetMembershipOptionsAsync(
        Guid currentUserId, Guid billId, CancellationToken cancellationToken = default)
    {
        var actor = await LoadActorAsync(currentUserId, cancellationToken);
        var bill = await LoadVisibleAsync(actor, billId, tracked: false, cancellationToken);
        if (bill.CustomerId is not { } customerId)
        {
            return [];
        }

        var live = await _wallet.ResolveLiveCustomerIdAsync(customerId, cancellationToken);
        var memberships = await _dbContext.CustomerMemberships.AsNoTracking()
            .Include(membership => membership.Allowances).ThenInclude(allowance => allowance.Services)
            .AsSplitQuery()
            .Where(membership => membership.CustomerId == live && membership.Status == MembershipStatus.Active
                && membership.StartsOn <= bill.ServiceDate && membership.EndsOn >= bill.ServiceDate)
            .OrderBy(membership => membership.EndsOn)
            .ToListAsync(cancellationToken);
        var rateGenerals = await RateGeneralServicesAsync(
            memberships.SelectMany(membership => membership.Allowances.SelectMany(allowance => allowance.Services.Select(service => service.ServiceId))),
            cancellationToken);

        var options = new List<BillMembershipOption>();
        foreach (var membership in memberships)
        {
            var available = await _ledger.LoadAvailableAsync(membership, cancellationToken);
            var services = membership.Allowances
                .SelectMany(allowance => allowance.Services.Select(service => new { service.ServiceId, service.ServiceNameSnapshot, Available = available[allowance.Id] }))
                .GroupBy(service => service.ServiceId)
                .Select(group => new BillMembershipServiceOption(
                    group.Key,
                    group.First().ServiceNameSnapshot,
                    rateGenerals.GetValueOrDefault(group.Key),
                    group.Sum(service => service.Available) - PlannedConsumptions(bill, membership.Id, group.Key)))
                .Where(service => service.Available > 0 && service.GeneralServiceId != Guid.Empty)
                .ToArray();
            if (services.Length > 0)
            {
                options.Add(new BillMembershipOption(membership.Id, membership.PlanName, membership.EndsOn, services));
            }
        }

        return options;
    }

    public async Task<IReadOnlyList<BillRedemptionOption>> GetRedemptionOptionsAsync(
        Guid currentUserId, Guid billId, CancellationToken cancellationToken = default)
    {
        var actor = await LoadActorAsync(currentUserId, cancellationToken);
        var bill = await LoadVisibleAsync(actor, billId, tracked: false, cancellationToken);
        if (bill.CustomerId is not { } customerId)
        {
            return [];
        }

        var live = await _wallet.ResolveLiveCustomerIdAsync(customerId, cancellationToken);
        var taken = await TakenRedemptionIdsAsync(cancellationToken);
        return (await _dbContext.LoyaltyRedemptions.AsNoTracking()
                .Where(redemption => redemption.CustomerId == live && redemption.Status == LoyaltyRedemptionStatus.Approved)
                .OrderBy(redemption => redemption.DeliverBy)
                .ToListAsync(cancellationToken))
            .Where(redemption => !taken.Contains(redemption.Id))
            .Select(redemption => new BillRedemptionOption(
                redemption.Id, redemption.RewardNameSnapshot, redemption.RewardKindSnapshot.ToString(), redemption.ServiceIdSnapshot,
                redemption.ServiceNameSnapshot, redemption.PointsCost, redemption.DeliverBy))
            .ToArray();
    }

    // ── Opening ─────────────────────────────────────────────────────────────────────────────────

    public async Task<BillView> OpenAsync(Guid currentUserId, BillOpenRequest request, CancellationToken cancellationToken = default)
    {
        if (request.IdempotencyKey == Guid.Empty)
        {
            throw Invalid("idempotencyKey", "Falta la clave de la operación; recarga la página e intenta de nuevo.");
        }

        var actor = await LoadActorAsync(currentUserId, cancellationToken);
        if (!actor.Has(PermissionCodes.BillsOpen) && !actor.Has(PermissionCodes.BillsManage))
        {
            throw new UnauthorizedAccessException("No tienes permiso para abrir cuentas.");
        }

        if (await ReplayOpenAsync(actor, request, cancellationToken) is { } replay)
        {
            return replay;
        }

        Guid billId;
        try
        {
            await using var transaction = await BeginTransactionAsync(cancellationToken);
            var bill = request.AppointmentId is { } appointmentId
                ? await OpenFromAppointmentAsync(actor, appointmentId, request, cancellationToken)
                : await OpenWithoutAppointmentAsync(actor, request, cancellationToken);
            _dbContext.Bills.Add(bill);
            _dbContext.BillEvents.Add(new BillEvent(
                bill.Id, BillEventType.Opened, BillStatus.Open, currentUserId, Now(), afterJson: HeaderJson(bill)));
            await _dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            billId = bill.Id;
        }
        catch (Exception) when (ResetTracker())
        {
            throw;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // A concurrent retry with the same key won, or the appointment got its bill meanwhile.
            _dbContext.ChangeTracker.Clear();
            if (await ReplayOpenAsync(actor, request, cancellationToken) is { } stored)
            {
                return stored;
            }

            throw new ConflictException("Esta cita ya tiene una cuenta. Ábrela desde «Cuentas».");
        }

        return await GetAsync(currentUserId, billId, cancellationToken);
    }

    /// <summary>
    /// SONDA BUG-127: a retry with the same key must carry the same opening data. Every field the request gives is
    /// compared with the header the bill was opened with (its «Opened» event), not with later edits.
    /// </summary>
    private async Task<bool> SameOpeningAsync(Guid billId, BillOpenRequest request, CancellationToken cancellationToken)
    {
        var opened = await _dbContext.BillEvents.AsNoTracking()
            .Where(billEvent => billEvent.BillId == billId && billEvent.Type == BillEventType.Opened)
            .OrderBy(billEvent => billEvent.CreatedAt)
            .Select(billEvent => billEvent.AfterJson)
            .FirstOrDefaultAsync(cancellationToken);
        if (opened is null)
        {
            return true;
        }

        using var header = JsonDocument.Parse(opened);
        var root = header.RootElement;
        string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        return (request.CustomerId is not { } customerId || string.Equals(Text("customerId"), customerId.ToString(), StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(request.CustomerName)
                || string.Equals(Text("customerName")?.Trim(), request.CustomerName.Trim(), StringComparison.OrdinalIgnoreCase))
            && (request.ServiceDate is not { } serviceDate || Text("serviceDate") == serviceDate.ToString("yyyy-MM-dd"))
            && (request.StaffProfileId is not { } staffId || string.Equals(Text("staffProfileId"), staffId.ToString(), StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(request.Notes) || Text("notes")?.Trim() == request.Notes.Trim());
    }

    private async Task<BillView?> ReplayOpenAsync(Actor actor, BillOpenRequest request, CancellationToken cancellationToken)
    {
        var previous = await _dbContext.Bills.AsNoTracking()
            .SingleOrDefaultAsync(bill => bill.IdempotencyKey == request.IdempotencyKey, cancellationToken);
        if (previous is null)
        {
            return null;
        }

        if (previous.OpenedByUserId != actor.UserId || previous.AppointmentId != request.AppointmentId
            || !await SameOpeningAsync(previous.Id, request, cancellationToken))
        {
            throw new ConflictException("Esta cuenta ya se abrió con otros datos. Recarga la página para abrir una nueva.");
        }

        return await GetAsync(actor.UserId, previous.Id, CancellationToken.None);
    }

    private async Task<Bill> OpenFromAppointmentAsync(Actor actor, Guid appointmentId, BillOpenRequest request, CancellationToken cancellationToken)
    {
        // Serializes the bills of the same appointment, so the checks below hold (the unique index backs them).
        await LockAsync($"SELECT 1 FROM public.appointments WHERE \"Id\" = {appointmentId} FOR UPDATE", cancellationToken);
        var appointment = await _dbContext.Appointments.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == appointmentId, cancellationToken);
        if (appointment is null || (!actor.Has(PermissionCodes.BillsManage) && appointment.StaffProfileId != actor.StaffProfileId))
        {
            throw new NotFoundException("La cita no existe o no es tuya.");
        }

        if (request.CustomerId is not null || !string.IsNullOrWhiteSpace(request.CustomerName) || request.ServiceDate is not null)
        {
            throw Invalid("appointmentId", "Con una cita, el cliente y la fecha son los de la cita.");
        }

        if (appointment.Status is AppointmentStatus.Cancelled or AppointmentStatus.NoShow)
        {
            throw new ConflictException("La cita está cancelada o el cliente no asistió; no se puede abrir su cuenta.");
        }

        if (BogotaDate(appointment.StartsAt) > Today())
        {
            throw new ConflictException("La cita es de un día futuro. Abre su cuenta el día de la cita.");
        }

        // ADR-0021 L8: only on the day of the appointment, never after.
        if (BogotaDate(appointment.StartsAt) < Today())
        {
            throw new ConflictException(PastAppointmentMessage);
        }

        await EnsureAppointmentWithoutBillAsync(_dbContext, appointment.Id, cancellationToken);
        return await NewAppointmentBillAsync(_dbContext, appointment, actor.UserId, request.IdempotencyKey, request.Notes, Now(), cancellationToken);
    }

    private async Task<Bill> OpenWithoutAppointmentAsync(Actor actor, BillOpenRequest request, CancellationToken cancellationToken)
    {
        var (customerId, customerName) = await ResolveCustomerAsync(request.CustomerId, request.CustomerName, cancellationToken);
        // ADR-0021 L8: without an appointment the bill is of today; no past (or future) date.
        var serviceDate = request.ServiceDate ?? Today();
        if (serviceDate != Today())
        {
            throw Invalid("serviceDate", OnlyTodayMessage);
        }

        Guid? staffProfileId;
        if (request.StaffProfileId is { } requestedStaff && requestedStaff != actor.StaffProfileId)
        {
            if (!actor.Has(PermissionCodes.BillsManage))
            {
                throw new UnauthorizedAccessException("Solo puedes abrir cuentas a tu nombre.");
            }

            if (!await _dbContext.StaffProfiles.AnyAsync(staff => staff.Id == requestedStaff && staff.IsActive, cancellationToken))
            {
                throw Invalid("staffProfileId", "El profesional no existe o está inactivo.");
            }

            staffProfileId = requestedStaff;
        }
        else
        {
            staffProfileId = actor.StaffProfileId;
            if (staffProfileId is null && !actor.Has(PermissionCodes.BillsManage))
            {
                // A deactivated professional can neither open nor close (ADR-0020, like the service records).
                throw new UnauthorizedAccessException("Tu usuario no tiene un perfil de profesional activo; no puedes abrir cuentas.");
            }
        }

        if (request.Notes is { Length: > Bill.MaxNotesLength })
        {
            throw Invalid("notes", $"Las notas deben tener {Bill.MaxNotesLength} caracteres o menos.");
        }

        return new Bill(
            request.IdempotencyKey, null, customerId, customerName, staffProfileId, serviceDate, request.Notes?.Trim(), actor.UserId, Now());
    }

    /// <summary>The appointment has no bill that is not voided, and (older ones) no service record registered.</summary>
    internal static async Task EnsureAppointmentWithoutBillAsync(AppDbContext dbContext, Guid appointmentId, CancellationToken cancellationToken)
    {
        var existing = await dbContext.Bills.AsNoTracking()
            .Where(bill => bill.AppointmentId == appointmentId && bill.Status != BillStatus.Voided)
            .Select(bill => new { bill.Series, bill.Number })
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            throw new ConflictException(existing.Number is { } number
                ? $"Esta cita ya tiene la cuenta {Bill.FormatNumber(existing.Series!, number)}."
                : "Esta cita ya tiene una cuenta abierta. Ábrela desde «Cuentas».");
        }

        if (await dbContext.ServiceRecords.AnyAsync(
                record => record.AppointmentId == appointmentId && record.Status != ServiceRecordStatus.Rejected, cancellationToken))
        {
            throw new ConflictException(
                "Esta cita ya tiene servicios realizados registrados; se revisan en la bandeja de servicios realizados.");
        }
    }

    /// <summary>
    /// The bill of an appointment: its customer (the live profile linked to the account that booked, or a walk-in with the
    /// appointment's name), its professional and date, and a service line of its general service without a rate yet.
    /// </summary>
    internal static async Task<Bill> NewAppointmentBillAsync(
        AppDbContext dbContext, Appointment appointment, Guid userId, Guid idempotencyKey, string? notes, DateTime utcNow,
        CancellationToken cancellationToken)
    {
        Guid? customerId = null;
        var customerName = appointment.CustomerName;
        if (appointment.CustomerUserId is { } customerUserId)
        {
            var profile = await dbContext.Customers.AsNoTracking()
                .Where(customer => customer.UserId == customerUserId && customer.MergedIntoCustomerId == null)
                .Select(customer => new { customer.Id, customer.FullName })
                .FirstOrDefaultAsync(cancellationToken);
            if (profile is not null)
            {
                (customerId, customerName) = (profile.Id, profile.FullName);
            }
        }

        if (customerName.Trim().Length < Bill.MinCustomerNameLength)
        {
            customerName = "Cliente ocasional";
        }

        var bill = new Bill(
            idempotencyKey, appointment.Id, customerId, customerName.Trim(), appointment.StaffProfileId, BogotaDate(appointment.StartsAt),
            notes?.Trim(), userId, utcNow);
        if (appointment.GeneralServiceId is { } generalServiceId)
        {
            bill.AddLine(
                BillLine.Service(appointment.StaffProfileId, generalServiceId, appointment.GeneralServiceNameSnapshot ?? "Servicio", null, 0, null, false, null, utcNow),
                utcNow);
            bill.Recalculate();
        }

        return bill;
    }

    /// <summary>
    /// BUG-092: completing an appointment opens its bill, unless it already has one (not voided) or older service records.
    /// Added to the caller's unit of work; the caller saves it with the appointment.
    /// </summary>
    internal static async Task<Bill?> OpenForCompletedAppointmentAsync(
        AppDbContext dbContext, Appointment appointment, Guid userId, DateTime utcNow, CancellationToken cancellationToken)
    {
        var hasBill = await dbContext.Bills.AnyAsync(
            bill => bill.AppointmentId == appointment.Id && bill.Status != BillStatus.Voided, cancellationToken);
        var hasRecords = await dbContext.ServiceRecords.AnyAsync(
            record => record.AppointmentId == appointment.Id && record.Status != ServiceRecordStatus.Rejected, cancellationToken);
        // ADR-0021 L8: an appointment of a past day can still be completed (ADR-0018 D2), but its bill is not opened.
        var today = DateOnly.FromDateTime(BogotaClock.ToLocal(utcNow));
        if (hasBill || hasRecords || DateOnly.FromDateTime(BogotaClock.ToLocal(appointment.StartsAt)) != today)
        {
            return null;
        }

        var bill = await NewAppointmentBillAsync(dbContext, appointment, userId, Guid.NewGuid(), null, utcNow, cancellationToken);
        dbContext.Bills.Add(bill);
        dbContext.BillEvents.Add(new BillEvent(
            bill.Id, BillEventType.Opened, BillStatus.Open, userId, utcNow, "Se abrió al completar la cita.", afterJson: HeaderJson(bill)));
        return bill;
    }

    // ── Editing ─────────────────────────────────────────────────────────────────────────────────

    public Task<BillView> UpdateHeaderAsync(Guid currentUserId, Guid billId, BillHeaderRequest request, CancellationToken cancellationToken = default)
        => MutateAsync(currentUserId, billId, request.Version, async (actor, bill) =>
        {
            if (bill.Status != BillStatus.Open)
            {
                throw new ConflictException("Los datos del cliente y la fecha solo cambian mientras la cuenta está abierta.");
            }

            if (request.Notes is { Length: > Bill.MaxNotesLength })
            {
                throw Invalid("notes", $"Las notas deben tener {Bill.MaxNotesLength} caracteres o menos.");
            }

            var before = HeaderJson(bill);
            var (customerId, customerName) = request.CustomerId is null && string.IsNullOrWhiteSpace(request.CustomerName)
                ? (bill.CustomerId, bill.CustomerNameSnapshot)
                : await ResolveCustomerAsync(request.CustomerId, request.CustomerName, cancellationToken);
            // ADR-0021 L8: the date of an open bill is the day of the service and is not changed (bills opened before the
            // rule keep theirs too).
            var serviceDate = request.ServiceDate ?? bill.ServiceDate;
            if (serviceDate != bill.ServiceDate)
            {
                throw Invalid("serviceDate", bill.AppointmentId is not null ? "Con una cita, la fecha es la de la cita." : DateIsFixedMessage);
            }
            if (customerId != bill.CustomerId
                && bill.Lines.Any(line => line.Type is BillLineType.MembershipConsumption or BillLineType.Redemption))
            {
                throw new ConflictException("Quita primero las líneas de membresía y de canjes: pertenecen al cliente actual.");
            }

            bill.ChangeHeader(customerId, customerName, serviceDate, request.Notes?.Trim(), Now());
            return new BillChange(BillEventType.HeaderChanged, null, before, HeaderJson(bill));
        }, cancellationToken);

    public Task<BillView> AddLineAsync(Guid currentUserId, Guid billId, BillLineRequest request, CancellationToken cancellationToken = default)
        => MutateAsync(currentUserId, billId, request.Version, async (actor, bill) =>
        {
            var type = ParseLineType(request.Type);
            var line = await BuildLineAsync(actor, bill, type, request, cancellationToken);
            if (bill.Lines.Count >= Bill.MaxLines)
            {
                throw new ConflictException($"Una cuenta tiene como máximo {Bill.MaxLines} líneas.");
            }

            bill.AddLine(line, Now());
            return new BillChange(BillEventType.LineAdded, null, null, LineJson(line));
        }, cancellationToken);

    public Task<BillView> UpdateLineAsync(
        Guid currentUserId, Guid billId, Guid lineId, BillLineRequest request, CancellationToken cancellationToken = default)
        => MutateAsync(currentUserId, billId, request.Version, async (actor, bill) =>
        {
            var line = bill.Lines.SingleOrDefault(candidate => candidate.Id == lineId)
                ?? throw new NotFoundException("La línea no existe en esta cuenta.");
            if (request.Type is { } requestedType && ParseLineType(requestedType) != line.Type)
            {
                throw Invalid("type", "El tipo de una línea no cambia. Quítala y agrega otra.");
            }

            var before = LineJson(line);
            await ApplyLineChangesAsync(actor, bill, line, request, cancellationToken);
            return new BillChange(BillEventType.LineChanged, null, before, LineJson(line));
        }, cancellationToken);

    public Task<BillView> RemoveLineAsync(Guid currentUserId, Guid billId, Guid lineId, uint? version, CancellationToken cancellationToken = default)
        => MutateAsync(currentUserId, billId, version, async (actor, bill) =>
        {
            var line = bill.Lines.SingleOrDefault(candidate => candidate.Id == lineId)
                ?? throw new NotFoundException("La línea no existe en esta cuenta.");
            if (bill.Lines.Any(other => other.AppliesToLineId == line.Id))
            {
                throw new ConflictException("Quita primero el descuento de esta línea.");
            }

            var before = LineJson(line);
            // SONDA BUG-177 (ADR-0021 L9): in a bill being corrected the line has the (reversed) postings of the previous
            // approval, which point to it; deleting it broke that reference (a 409 «Otra persona cambió…»). It is kept,
            // marked as removed, and stops counting: the new approval posts only the lines still in the bill.
            var hasPostings = await _dbContext.BillPostings.AnyAsync(posting => posting.BillLineId == line.Id, cancellationToken)
                || await _dbContext.IncomeEntries.AnyAsync(entry => entry.BillLineId == line.Id, cancellationToken)
                || await _dbContext.LoyaltyRedemptions.AnyAsync(redemption => redemption.BillLineId == line.Id, cancellationToken);
            bill.RemoveLine(line, Now(), keepRecord: hasPostings);
            if (!hasPostings)
            {
                _dbContext.BillLines.Remove(line);
            }

            return new BillChange(BillEventType.LineRemoved, null, before, null);
        }, cancellationToken);

    public Task<BillView> DetachAppointmentAsync(Guid currentUserId, Guid billId, BillReasonRequest request, CancellationToken cancellationToken = default)
    {
        var reason = ValidateReason(request.Reason);
        return MutateAsync(currentUserId, billId, request.Version, async (actor, bill) =>
        {
            if (bill.Status != BillStatus.Open || bill.AppointmentId is not { } appointmentId)
            {
                throw new ConflictException("Solo se desvincula de su cita una cuenta abierta que tiene cita.");
            }

            var status = await _dbContext.Appointments.AsNoTracking()
                .Where(appointment => appointment.Id == appointmentId)
                .Select(appointment => appointment.Status)
                .SingleAsync(cancellationToken);
            if (status is not (AppointmentStatus.Cancelled or AppointmentStatus.NoShow))
            {
                throw new ConflictException("Solo se desvincula una cuenta cuya cita se canceló o en la que el cliente no asistió.");
            }

            var before = HeaderJson(bill);
            bill.DetachAppointment(Now());
            return new BillChange(BillEventType.AppointmentDetached, reason, before, HeaderJson(bill));
        }, cancellationToken);
    }

    // ── Closing, voiding, payment method ────────────────────────────────────────────────────────

    public async Task<BillView> CloseAsync(Guid currentUserId, Guid billId, BillCloseRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Version is null)
        {
            throw Invalid("version", MissingVersionMessage);
        }

        var actor = await LoadActorAsync(currentUserId, cancellationToken);
        try
        {
            await using var transaction = await BeginTransactionAsync(cancellationToken);
            var bill = await LoadLockedAsync(actor, billId, cancellationToken);
            if (bill.Status != BillStatus.Open)
            {
                // A retried close returns the stored one; anything else is a conflict.
                if (bill.Status == BillStatus.Closed && bill.ClosedByUserId == currentUserId)
                {
                    return await GetAsync(currentUserId, billId, cancellationToken);
                }

                throw new ConflictException("La cuenta ya no está abierta. Recarga la página.");
            }

            EnsureVersion(bill, request.Version.Value);
            if (!actor.CanClose(bill))
            {
                throw new UnauthorizedAccessException("No tienes permiso para cerrar esta cuenta.");
            }

            await ValidateBeforeCloseAsync(bill, cancellationToken);
            bill.Recalculate();
            Guid? methodId = null;
            string? methodName = null;
            if (bill.Total > 0)
            {
                if (!actor.Has(PermissionCodes.BillsCharge))
                {
                    throw new UnauthorizedAccessException("Para cerrar una cuenta con cobro necesitas el permiso de registrar cobros.");
                }

                var method = request.PaymentMethodId is { } wanted
                    ? await _dbContext.PaymentMethods.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == wanted && candidate.IsActive, cancellationToken)
                    : null;
                if (method is null)
                {
                    throw Invalid("paymentMethodId", "Elige el método de pago.");
                }

                (methodId, methodName) = (method.Id, method.Name);
            }

            // Q15: closing the bill of a pending or confirmed appointment completes it, with the rule of ADR-0018 D2.
            if (bill.AppointmentId is { } appointmentId)
            {
                await CompleteAppointmentAsync(appointmentId, cancellationToken);
            }

            var utcNow = Now();
            await LockAsync($"SELECT 1 FROM public.bill_number_series WHERE \"Prefix\" = {BillNumberSeries.DefaultPrefix} FOR UPDATE", cancellationToken);
            var series = await _dbContext.BillNumberSeries.SingleAsync(candidate => candidate.Prefix == BillNumberSeries.DefaultPrefix, cancellationToken);
            var number = series.Take(utcNow);
            bill.Close(series.Prefix, number, methodId, methodName, currentUserId, utcNow);
            _dbContext.BillEvents.Add(new BillEvent(
                bill.Id, BillEventType.Closed, BillStatus.Open, currentUserId, utcNow,
                afterJson: Json(new { number = bill.DisplayNumber, bill.Total, paymentMethod = methodName })));

            if (bill.CustomerId is { } customerId)
            {
                // P8 / Q19: the receipt reaches the account linked to the live profile, in the optional category «Compras».
                var live = await _wallet.ResolveLiveCustomerIdAsync(customerId, cancellationToken);
                await _notifications.QueueForCustomerAsync(live, UserNotificationTypes.BillClosed, ClosedMessage(bill), cancellationToken);
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
            throw new ConflictException(StaleMessage);
        }

        return await GetAsync(currentUserId, billId, cancellationToken);
    }

    public async Task<BillView> VoidAsync(Guid currentUserId, Guid billId, BillReasonRequest request, CancellationToken cancellationToken = default)
    {
        var reason = ValidateReason(request.Reason);
        if (request.Version is null)
        {
            throw Invalid("version", MissingVersionMessage);
        }

        var actor = await LoadActorAsync(currentUserId, cancellationToken);
        if (!actor.Has(PermissionCodes.BillsVoid))
        {
            throw new UnauthorizedAccessException("No tienes permiso para anular cuentas.");
        }

        try
        {
            await using var transaction = await BeginTransactionAsync(cancellationToken);
            var bill = await LoadLockedAsync(actor, billId, cancellationToken);
            if (bill.Status == BillStatus.Voided)
            {
                if (bill.VoidReason == reason)
                {
                    return await GetAsync(currentUserId, billId, cancellationToken);
                }

                throw new ConflictException("La cuenta ya está anulada.");
            }

            if (bill.Status == BillStatus.Approved)
            {
                throw new ConflictException("La cuenta está aprobada y tiene asientos: no se puede anular.");
            }

            EnsureVersion(bill, request.Version.Value);
            var previousStatus = bill.Status;
            bill.Void(reason, currentUserId, Now());
            _dbContext.BillEvents.Add(new BillEvent(bill.Id, BillEventType.Voided, previousStatus, currentUserId, Now(), reason));
            if (bill.Number is not null && bill.CustomerId is { } customerId)
            {
                // ADR-0020 §7: the customer received this receipt; they are told it was voided.
                var live = await _wallet.ResolveLiveCustomerIdAsync(customerId, cancellationToken);
                await _notifications.QueueForCustomerAsync(live, UserNotificationTypes.BillVoided, VoidedMessage(bill), cancellationToken);
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
            throw new ConflictException(StaleMessage);
        }

        return await GetAsync(currentUserId, billId, cancellationToken);
    }

    public Task<BillView> ChangePaymentMethodAsync(
        Guid currentUserId, Guid billId, BillPaymentMethodRequest request, CancellationToken cancellationToken = default)
    {
        // ADR-0021 L11: the correction is audited with its reason.
        var reason = ValidateReason(request.Reason);
        return MutateAsync(currentUserId, billId, request.Version, async (actor, bill) =>
        {
            if (bill.Status is not (BillStatus.Closed or BillStatus.Unapproved))
            {
                throw new ConflictException("El método de pago se cambia en una cuenta cerrada que espera aprobación.");
            }

            if (bill.Total == 0)
            {
                throw new ConflictException("Esta cuenta no tiene nada que cobrar; no lleva método de pago.");
            }

            var method = request.PaymentMethodId is { } wanted
                ? await _dbContext.PaymentMethods.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == wanted && candidate.IsActive, cancellationToken)
                : null;
            if (method is null)
            {
                throw Invalid("paymentMethodId", "Elige el método de pago.");
            }

            var before = Json(new { paymentMethod = bill.PaymentMethodNameSnapshot, paymentMethodId = bill.PaymentMethodId });
            bill.CorrectPaymentMethod(method.Id, method.Name, Now());
            return new BillChange(
                BillEventType.PaymentMethodChanged, reason, before,
                Json(new { paymentMethod = method.Name, paymentMethodId = method.Id, chargedTotal = bill.ChargedTotal }));
        }, cancellationToken);
    }

    // ── Payment methods (Q1) ────────────────────────────────────────────────────────────────────

    async Task<IReadOnlyList<PaymentMethodView>> IPaymentMethodsService.ListAsync(CancellationToken cancellationToken)
        => await _dbContext.PaymentMethods.AsNoTracking()
            .OrderBy(method => method.SortOrder).ThenBy(method => method.Name)
            .Select(method => new PaymentMethodView(method.Id, method.Name, method.SortOrder, method.IsActive))
            .ToListAsync(cancellationToken);

    public async Task<PaymentMethodView> CreateAsync(PaymentMethodUpsertRequest request, CancellationToken cancellationToken = default)
    {
        var name = ValidatePaymentMethodName(request.Name);
        await EnsurePaymentMethodNameFreeAsync(name, null, cancellationToken);
        var sortOrder = request.SortOrder ?? ((await _dbContext.PaymentMethods.MaxAsync(method => (int?)method.SortOrder, cancellationToken) ?? 0) + 10);
        var method = new PaymentMethod(name, Math.Clamp(sortOrder, 0, 9_999), Now());
        if (request.IsActive == false)
        {
            method.Update(name, method.SortOrder, false, Now());
        }

        _dbContext.PaymentMethods.Add(method);
        await SavePaymentMethodAsync(cancellationToken);
        return new PaymentMethodView(method.Id, method.Name, method.SortOrder, method.IsActive);
    }

    public async Task<PaymentMethodView> UpdateAsync(Guid paymentMethodId, PaymentMethodUpsertRequest request, CancellationToken cancellationToken = default)
    {
        var method = await _dbContext.PaymentMethods.SingleOrDefaultAsync(candidate => candidate.Id == paymentMethodId, cancellationToken)
            ?? throw new NotFoundException("El método de pago no existe.");
        var name = ValidatePaymentMethodName(request.Name);
        await EnsurePaymentMethodNameFreeAsync(name, paymentMethodId, cancellationToken);
        method.Update(name, Math.Clamp(request.SortOrder ?? method.SortOrder, 0, 9_999), request.IsActive ?? method.IsActive, Now());
        await SavePaymentMethodAsync(cancellationToken);
        return new PaymentMethodView(method.Id, method.Name, method.SortOrder, method.IsActive);
    }

    private static string ValidatePaymentMethodName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        return trimmed.Length is < PaymentMethod.MinNameLength or > PaymentMethod.MaxNameLength
            ? throw Invalid("name", $"El nombre debe tener entre {PaymentMethod.MinNameLength} y {PaymentMethod.MaxNameLength} caracteres.")
            : trimmed;
    }

    private async Task EnsurePaymentMethodNameFreeAsync(string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        var lowered = name.ToLowerInvariant();
        if (await _dbContext.PaymentMethods.AnyAsync(method => method.Id != exceptId && method.Name.ToLower() == lowered, cancellationToken))
        {
            throw new ConflictException("Ya existe un método de pago con ese nombre.");
        }
    }

    private async Task SavePaymentMethodAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            throw new ConflictException("Ya existe un método de pago con ese nombre.");
        }
    }

    // ── Lines ───────────────────────────────────────────────────────────────────────────────────

    private async Task<BillLine> BuildLineAsync(Actor actor, Bill bill, BillLineType type, BillLineRequest request, CancellationToken cancellationToken)
    {
        var utcNow = Now();
        switch (type)
        {
            case BillLineType.Service:
            {
                EnsureCanAssignStaff(actor, request.StaffProfileId);
                var staffId = await RequireStaffAsync(request.StaffProfileId, cancellationToken);
                var general = await RequireGeneralServiceAsync(request.GeneralServiceId, cancellationToken);
                if (request.IsCourtesy)
                {
                    EnsureCanDiscount(actor);
                }

                EnsureSingleServiceUnit(request);
                var line = BillLine.Service(staffId, general.Id, general.Name, null, 0, null, request.IsCourtesy, CourtesyReason(request), utcNow);
                if (request.ServiceId is { } rateId)
                {
                    var rate = await RequireRateAsync(rateId, general.Id, allowDeleted: false, cancellationToken);
                    EnsureCatalogPrice(request, rate.BasePrice);
                    line.SetRate(general.Id, rate.Id, $"{general.Name} · {rate.Name}", rate.BasePrice, rate.BusinessPercentage, utcNow);
                }
                else
                {
                    EnsureCatalogPrice(request, 0);
                }

                return line;
            }

            case BillLineType.Product:
            {
                var product = await _dbContext.Products.AsNoTracking()
                    .SingleOrDefaultAsync(candidate => candidate.Id == request.ProductId && !candidate.IsDeleted && candidate.IsActive, cancellationToken)
                    ?? throw Invalid("productId", "El producto no existe o está inactivo.");
                var quantity = ValidateQuantity(request.Quantity);
                EnsureCatalogPrice(request, product.Price);
                EnsureLineAmountFits(quantity, product.Price);
                Guid? staffId = request.StaffProfileId is null ? null : await RequireStaffAsync(request.StaffProfileId, cancellationToken);
                return BillLine.Product(product.Id, product.Name, quantity, product.Price, staffId, utcNow);
            }

            case BillLineType.MembershipConsumption:
            {
                var customerId = bill.CustomerId ?? throw new ConflictException(WalkInLimitMessage);
                EnsureCanAssignStaff(actor, request.StaffProfileId);
                var staffId = await RequireStaffAsync(request.StaffProfileId, cancellationToken);
                var membershipId = request.CustomerMembershipId ?? throw Invalid("customerMembershipId", "Elige la membresía de la que se descuenta el servicio.");
                var rateId = request.ServiceId ?? throw Invalid("serviceId", "Elige el servicio de la membresía.");
                // SONDA BUG-145: each consumption line is one unit; a quantity was accepted and silently counted as 1.
                if (request.Quantity is { } consumedQuantity && consumedQuantity != 1)
                {
                    throw Invalid("quantity", "Cada línea de consumo de membresía descuenta una unidad. Para descontar dos, agrega dos líneas.");
                }

                // ADR-0021 L10: locked after the bill (the order of the approval), so an annulment at the same moment either
                // sees this line or makes this one see the membership annulled.
                await LockAsync($"SELECT 1 FROM public.customer_memberships WHERE \"Id\" = {membershipId} FOR UPDATE", cancellationToken);
                await EnsureMembershipBalanceAsync(bill, customerId, membershipId, rateId, extra: 1, cancellationToken);
                var rate = await _dbContext.Services.AsNoTracking().SingleAsync(candidate => candidate.Id == rateId, cancellationToken);
                var generalName = await _dbContext.GeneralServices.AsNoTracking()
                    .Where(general => general.Id == rate.GeneralServiceId).Select(general => general.Name).SingleAsync(cancellationToken);
                return BillLine.MembershipConsumption(
                    staffId, rate.GeneralServiceId, rate.Id, $"{generalName} · {rate.Name}", rate.BusinessPercentage, membershipId, utcNow);
            }

            case BillLineType.Redemption:
            {
                var customerId = bill.CustomerId ?? throw new ConflictException(WalkInLimitMessage);
                var redemptionId = request.LoyaltyRedemptionId ?? throw Invalid("loyaltyRedemptionId", "Elige el canje aprobado que se entrega.");
                // SONDA BUG-129: the same redemption added to two bills at once. Locking the redemption (after the bill, the
                // order every bill operation follows) makes the second one wait and then see the first: 409, never 200/200.
                await LockAsync($"SELECT 1 FROM public.loyalty_redemptions WHERE \"Id\" = {redemptionId} FOR UPDATE", cancellationToken);
                var live = await _wallet.ResolveLiveCustomerIdAsync(customerId, cancellationToken);
                var redemption = await _dbContext.LoyaltyRedemptions.AsNoTracking()
                    .SingleOrDefaultAsync(candidate => candidate.Id == redemptionId && candidate.CustomerId == live, cancellationToken)
                    ?? throw Invalid("loyaltyRedemptionId", "El canje no existe o no es de este cliente.");
                if (redemption.Status != LoyaltyRedemptionStatus.Approved)
                {
                    throw new ConflictException("Solo se agrega un canje aprobado que aún no se ha entregado.");
                }

                if (bill.Lines.Any(line => line.LoyaltyRedemptionId == redemptionId) || (await TakenRedemptionIdsAsync(cancellationToken)).Contains(redemptionId))
                {
                    throw new ConflictException("Este canje ya está en otra cuenta.");
                }

                if (redemption.RewardKindSnapshot != LoyaltyRewardKind.Service)
                {
                    return BillLine.Redemption(redemption.Id, redemption.RewardNameSnapshot, null, null, null, 0, null, utcNow);
                }

                EnsureCanAssignStaff(actor, request.StaffProfileId);
                var staffId = await RequireStaffAsync(request.StaffProfileId, cancellationToken);
                var rate = await _dbContext.Services.AsNoTracking().SingleAsync(candidate => candidate.Id == redemption.ServiceIdSnapshot, cancellationToken);
                return BillLine.Redemption(
                    redemption.Id, $"Canje: {redemption.RewardNameSnapshot}", staffId, rate.GeneralServiceId, rate.Id, rate.BasePrice,
                    rate.BusinessPercentage, utcNow);
            }

            case BillLineType.Discount:
            {
                EnsureCanDiscount(actor);
                var amount = ValidateAmount(request.Amount);
                var reason = ValidateReason(request.Reason);
                if (request.AppliesToLineId is { } targetId
                    && !bill.Lines.Any(line => line.Id == targetId && line.IsChargeable))
                {
                    throw Invalid("appliesToLineId", "El descuento solo se aplica a un servicio cobrado o a un producto de esta cuenta.");
                }

                if (request.AppliesToLineId is { } discountedId)
                {
                    await EnsureStillSoldAsync(bill.Lines.Single(line => line.Id == discountedId), "appliesToLineId", cancellationToken);
                }

                return BillLine.Discount(amount, reason, request.AppliesToLineId, utcNow);
            }

            case BillLineType.Tip:
            {
                EnsureCanAssignStaff(actor, request.StaffProfileId);
                var staffId = await RequireStaffAsync(request.StaffProfileId, cancellationToken);
                var amount = ValidateAmount(request.Amount);
                var mode = ParseTipMode(request.TipPaymentMode);
                return BillLine.Tip(staffId, amount, mode, utcNow);
            }

            default:
                throw Invalid("type", "Tipo de línea no válido.");
        }
    }

    private async Task ApplyLineChangesAsync(Actor actor, Bill bill, BillLine line, BillLineRequest request, CancellationToken cancellationToken)
    {
        var utcNow = Now();
        if (request.StaffProfileId is { } staffId && staffId != line.StaffProfileId)
        {
            // ADR-0021 L1: only a change of professional is checked, so lines already on another professional's name
            // (open bills from before the rule) keep working: their rate, courtesy or amount can still be edited.
            if (line.Type != BillLineType.Product)
            {
                EnsureCanAssignStaff(actor, staffId);
            }

            line.SetStaff(await RequireStaffAsync(staffId, cancellationToken), utcNow);
        }

        switch (line.Type)
        {
            case BillLineType.Service:
            {
                EnsureSingleServiceUnit(request);
                var generalId = request.GeneralServiceId ?? line.GeneralServiceId!.Value;
                if (request.ServiceId is { } rateId && (rateId != line.ServiceId || generalId != line.GeneralServiceId))
                {
                    var general = await RequireGeneralServiceAsync(generalId, cancellationToken);
                    var rate = await RequireRateAsync(rateId, general.Id, allowDeleted: false, cancellationToken);
                    EnsureCatalogPrice(request, rate.BasePrice);
                    line.SetRate(general.Id, rate.Id, $"{general.Name} · {rate.Name}", rate.BasePrice, rate.BusinessPercentage, utcNow);
                }
                else if (request.GeneralServiceId is { } newGeneral && newGeneral != line.GeneralServiceId)
                {
                    throw Invalid("serviceId", "Elige la tarifa del nuevo servicio.");
                }
                else
                {
                    EnsureCatalogPrice(request, line.UnitPrice);
                }

                if (request.IsCourtesy != line.IsCourtesy || (request.IsCourtesy && CourtesyReason(request) != line.Reason))
                {
                    // SONDA BUG-180 (ADR-0021 L17): a rate that was deleted or deactivated only allows another rate, another
                    // professional or removing the line, as when adding one.
                    await EnsureStillSoldAsync(line, "isCourtesy", cancellationToken);
                    if (request.IsCourtesy)
                    {
                        EnsureCanDiscount(actor);
                        if (bill.Lines.Any(other => other.AppliesToLineId == line.Id))
                        {
                            throw new ConflictException("Quita primero el descuento de esta línea: una cortesía no lleva descuento.");
                        }
                    }

                    line.SetCourtesy(request.IsCourtesy, CourtesyReason(request), utcNow);
                }

                break;
            }

            case BillLineType.Product:
                EnsureCatalogPrice(request, line.UnitPrice);
                if (request.Quantity is { } quantity && quantity != line.Quantity)
                {
                    // SONDA BUG-156: more units of a product that was deleted or deactivated are refused, as when adding a
                    // line; fewer units (a correction downwards) are still allowed.
                    if (quantity > line.Quantity)
                    {
                        await EnsureStillSoldAsync(line, "quantity", cancellationToken);
                    }

                    var validated = ValidateQuantity(quantity);
                    EnsureLineAmountFits(validated, line.UnitPrice);
                    line.SetQuantity(validated, utcNow);
                }

                if (request.StaffProfileId is null && line.StaffProfileId is not null && request.Type is not null)
                {
                    line.SetStaff(null, utcNow);
                }

                break;

            case BillLineType.Discount:
                EnsureCanDiscount(actor);
                if (line.AppliesToLineId is { } discountedId && bill.Lines.SingleOrDefault(target => target.Id == discountedId) is { } target)
                {
                    await EnsureStillSoldAsync(target, "amount", cancellationToken);
                }

                line.SetAmount(ValidateAmount(request.Amount ?? line.UnitPrice), ValidateReason(request.Reason ?? line.Reason), null, utcNow);
                break;

            case BillLineType.Tip:
                line.SetAmount(
                    ValidateAmount(request.Amount ?? line.UnitPrice), null,
                    request.TipPaymentMode is null ? line.TipPaymentMode : ParseTipMode(request.TipPaymentMode), utcNow);
                break;

            case BillLineType.MembershipConsumption:
            case BillLineType.Redemption:
                // Only the professional changes; another membership or redemption is another line.
                break;
        }
    }

    private static string? CourtesyReason(BillLineRequest request) => request.IsCourtesy ? ValidateReason(request.Reason) : null;

    internal const string ProductGoneMessage = "El producto ya no existe o está inactivo: solo puedes bajar su cantidad o quitar la línea.";
    internal const string RateGoneMessage =
        "La tarifa de esta línea ya no está vigente: no admite descuentos ni cortesías. Elige otra tarifa o quita la línea.";
    internal const string CatalogPriceMessage = "El precio sale del catálogo y no se cambia en la cuenta.";
    internal const string ServiceUnitMessage = "Cada línea de servicio es de una unidad. Para cobrar dos, agrega dos líneas.";

    /// <summary>
    /// SONDA BUG-156 / BUG-180 (ADR-0021 L17): a line of a product or a rate that was deleted or deactivated only allows
    /// what lowers the charge (fewer units), another professional, another rate or removing it; nothing that charges it
    /// again (more units, a discount, a courtesy), as when adding a line.
    /// </summary>
    private async Task EnsureStillSoldAsync(BillLine line, string field, CancellationToken cancellationToken)
    {
        if (line.Type == BillLineType.Product
            && !await _dbContext.Products.AnyAsync(product => product.Id == line.ProductId && !product.IsDeleted && product.IsActive, cancellationToken))
        {
            throw Invalid(field, ProductGoneMessage);
        }

        if (line.Type == BillLineType.Service && line.ServiceId is { } rateId
            && !await _dbContext.Services.AnyAsync(rate => rate.Id == rateId && !rate.IsDeleted && rate.IsActive, cancellationToken))
        {
            throw Invalid(field, RateGoneMessage);
        }
    }

    /// <summary>SONDA BUG-180: the price is the catalog's; a request with another one is refused instead of ignored.</summary>
    private static void EnsureCatalogPrice(BillLineRequest request, int catalogPrice)
    {
        if (request.UnitPrice is { } price && price != catalogPrice)
        {
            throw Invalid("unitPrice", CatalogPriceMessage);
        }
    }

    private static void EnsureSingleServiceUnit(BillLineRequest request)
    {
        if (request.Quantity is { } quantity && quantity != 1)
        {
            throw Invalid("quantity", ServiceUnitMessage);
        }
    }

    /// <summary>SONDA BUG-182: one line of 1.000 × 2.500.000 overflowed (500) before the totals were checked.</summary>
    private static void EnsureLineAmountFits(int quantity, int unitPrice)
    {
        if (!BillLine.AmountFits(quantity, unitPrice))
        {
            throw Invalid("quantity", TotalTooLargeMessage);
        }
    }

    /// <summary>
    /// ADR-0021 L1: services, membership consumptions, service redemptions and tips go on the actor's own name unless
    /// they hold bills.assign-other-staff (Admin by default). Products are not checked: their professional is only
    /// informative (Q11). The line an appointment bill opens with is the appointment's professional and is not checked.
    /// </summary>
    private static void EnsureCanAssignStaff(Actor actor, Guid? staffProfileId)
    {
        if (staffProfileId is { } staffId && staffId != actor.StaffProfileId && !actor.Has(PermissionCodes.BillsAssignOtherStaff))
        {
            throw new UnauthorizedAccessException(AssignOtherStaffMessage);
        }
    }

    private static void EnsureCanDiscount(Actor actor)
    {
        if (!actor.Has(PermissionCodes.BillsDiscount))
        {
            throw new UnauthorizedAccessException(DiscountPermissionMessage);
        }
    }

    internal const string TotalTooLargeMessage =
        "La cuenta superaría el máximo técnico de $ 2.147.483.647. Revisa las cantidades o divide el cobro en otra cuenta.";

    /// <summary>
    /// SONDA BUG-131: the amounts of a bill are 32-bit (like every money column). They are added up in 64 bits first, so
    /// a bill that would pass the technical maximum is a clear 422 instead of an overflow (500).
    /// </summary>
    private static void EnsureTotalsFit(Bill bill)
    {
        long subtotal = 0, tips = 0, directTips = 0;
        foreach (var line in bill.Lines)
        {
            var gross = (long)line.Quantity * line.UnitPrice;
            if (line.IsChargeable)
            {
                subtotal += gross;
            }
            else if (line.Type == BillLineType.Tip)
            {
                if (line.TipPaymentMode == TipPaymentMode.Direct) directTips += line.UnitPrice; else tips += line.UnitPrice;
            }

            if (gross > int.MaxValue)
            {
                throw Invalid("quantity", TotalTooLargeMessage);
            }
        }

        if (subtotal > int.MaxValue || subtotal + tips > int.MaxValue || directTips > int.MaxValue)
        {
            throw Invalid("amount", TotalTooLargeMessage);
        }
    }

    /// <summary>Q2: a discount never exceeds what it discounts: its line, or (on the whole bill) what is left to charge.</summary>
    private static void ValidateDiscounts(Bill bill)
    {
        var discounts = bill.Lines.Where(line => line.Type == BillLineType.Discount).ToList();
        foreach (var discount in discounts.Where(discount => discount.AppliesToLineId is not null))
        {
            var target = bill.Lines.SingleOrDefault(line => line.Id == discount.AppliesToLineId && line.IsChargeable)
                ?? throw new ConflictException("El descuento apunta a una línea que ya no se cobra. Quita primero el descuento.");
            var onTarget = discounts.Where(other => other.AppliesToLineId == target.Id).Sum(other => (long)other.UnitPrice);
            if (onTarget > target.GrossAmount)
            {
                throw new ConflictException(DiscountTooLargeMessage);
            }
        }

        var subtotal = bill.Lines.Where(line => line.IsChargeable).Sum(line => (long)line.GrossAmount);
        if (discounts.Sum(discount => (long)discount.UnitPrice) > subtotal)
        {
            throw new ConflictException(DiscountTooLargeMessage);
        }
    }

    private async Task ValidateBeforeCloseAsync(Bill bill, CancellationToken cancellationToken)
    {
        if (!bill.Lines.Any(line => line.Type is BillLineType.Service or BillLineType.Product or BillLineType.MembershipConsumption or BillLineType.Redemption))
        {
            throw new ConflictException("Agrega al menos un servicio o un producto antes de cerrar la cuenta.");
        }

        if (bill.Lines.FirstOrDefault(line => line.Type == BillLineType.Service && line.ServiceId is null) is { } withoutRate)
        {
            throw new ConflictException($"Elige la tarifa de «{withoutRate.NameSnapshot}» antes de cerrar la cuenta.");
        }

        ValidateDiscounts(bill);

        // Revalidated now: the membership must still cover what the bill consumes (nothing charged that cannot be used).
        foreach (var group in bill.Lines.Where(line => line.Type == BillLineType.MembershipConsumption)
                     .GroupBy(line => (line.CustomerMembershipId!.Value, line.ServiceId!.Value)))
        {
            await EnsureMembershipBalanceAsync(bill, bill.CustomerId!.Value, group.Key.Item1, group.Key.Item2, extra: 0, cancellationToken);
        }

        var redemptionIds = bill.Lines.Where(line => line.LoyaltyRedemptionId is not null).Select(line => line.LoyaltyRedemptionId!.Value).ToArray();
        if (redemptionIds.Length > 0)
        {
            var live = await _wallet.ResolveLiveCustomerIdAsync(bill.CustomerId!.Value, cancellationToken);
            var valid = await _dbContext.LoyaltyRedemptions.AsNoTracking()
                .CountAsync(redemption => redemptionIds.Contains(redemption.Id) && redemption.CustomerId == live
                    && redemption.Status == LoyaltyRedemptionStatus.Approved, cancellationToken);
            if (valid != redemptionIds.Length)
            {
                throw new ConflictException("Un canje de la cuenta ya no está aprobado (venció, se canceló o se entregó). Quita esa línea.");
            }
        }
    }

    private async Task CompleteAppointmentAsync(Guid appointmentId, CancellationToken cancellationToken)
    {
        await LockAsync($"SELECT 1 FROM public.appointments WHERE \"Id\" = {appointmentId} FOR UPDATE", cancellationToken);
        var appointment = await _dbContext.Appointments.SingleAsync(candidate => candidate.Id == appointmentId, cancellationToken);
        switch (appointment.Status)
        {
            case AppointmentStatus.Cancelled or AppointmentStatus.NoShow:
                throw new ConflictException(CancelledAppointmentMessage);
            case AppointmentStatus.Completed:
                return;
        }

        if (BogotaDate(appointment.StartsAt) > Today())
        {
            throw new ConflictException(AppointmentManagementService.CompleteFutureAppointmentMessage);
        }

        appointment.UpdateStatus(AppointmentStatus.Completed, Now());
    }

    /// <summary>
    /// The membership is the bill customer's (live profile), active, valid on the service date and still has the rate
    /// for every consumption this bill plans (plus <paramref name="extra"/> for a line being added).
    /// </summary>
    private async Task EnsureMembershipBalanceAsync(
        Bill bill, Guid customerId, Guid membershipId, Guid rateId, int extra, CancellationToken cancellationToken)
    {
        var live = await _wallet.ResolveLiveCustomerIdAsync(customerId, cancellationToken);
        var membership = await _dbContext.CustomerMemberships.AsNoTracking()
            .Include(candidate => candidate.Allowances).ThenInclude(allowance => allowance.Services)
            .AsSplitQuery()
            .SingleOrDefaultAsync(candidate => candidate.Id == membershipId, cancellationToken);
        if (membership is null || membership.CustomerId != live)
        {
            throw Invalid("customerMembershipId", "La membresía no existe o no es de este cliente.");
        }

        if (membership.Status != MembershipStatus.Active)
        {
            throw new ConflictException($"La membresía «{membership.PlanName}» no está activa.");
        }

        if (bill.ServiceDate < membership.StartsOn || bill.ServiceDate > membership.EndsOn)
        {
            throw new ConflictException($"La membresía «{membership.PlanName}» no está vigente en la fecha de la cuenta.");
        }

        var allowances = membership.Allowances.Where(allowance => allowance.Services.Any(service => service.ServiceId == rateId)).ToList();
        if (allowances.Count == 0)
        {
            throw Invalid("serviceId", "El servicio no está incluido en esta membresía.");
        }

        var available = await _ledger.LoadAvailableAsync(membership, cancellationToken);
        var left = allowances.Sum(allowance => available[allowance.Id]);
        if (PlannedConsumptions(bill, membershipId, rateId) + extra > left)
        {
            throw new ConflictException($"La membresía «{membership.PlanName}» no tiene saldo para ese servicio: quedan {left}.");
        }
    }

    private static int PlannedConsumptions(Bill bill, Guid membershipId, Guid rateId)
        => bill.Status is BillStatus.Open or BillStatus.Closed or BillStatus.Unapproved
            ? bill.Lines.Count(line => line.Type == BillLineType.MembershipConsumption && line.CustomerMembershipId == membershipId && line.ServiceId == rateId)
            : 0;

    /// <summary>Redemptions already on a line of a bill that is not voided (one redemption, one bill).</summary>
    private async Task<HashSet<Guid>> TakenRedemptionIdsAsync(CancellationToken cancellationToken)
        => (await (
                from line in _dbContext.BillLines.AsNoTracking()
                join bill in _dbContext.Bills.AsNoTracking() on line.BillId equals bill.Id
                where line.LoyaltyRedemptionId != null && bill.Status != BillStatus.Voided
                select line.LoyaltyRedemptionId!.Value)
            .ToListAsync(cancellationToken)).ToHashSet();

    // ── Shared pipeline ─────────────────────────────────────────────────────────────────────────

    private sealed record BillChange(BillEventType Type, string? Reason, string? Before, string? After);

    /// <summary>
    /// Locks the bill, checks the version and the permission to edit it in its current status, applies the change,
    /// recomputes and validates the money, audits it and saves, all in one transaction.
    /// </summary>
    private async Task<BillView> MutateAsync(
        Guid currentUserId, Guid billId, uint? version, Func<Actor, Bill, Task<BillChange>> apply, CancellationToken cancellationToken)
    {
        if (version is null)
        {
            throw Invalid("version", MissingVersionMessage);
        }

        var actor = await LoadActorAsync(currentUserId, cancellationToken);
        try
        {
            await using var transaction = await BeginTransactionAsync(cancellationToken);
            var bill = await LoadLockedAsync(actor, billId, cancellationToken);
            EnsureEditableBy(actor, bill);
            EnsureVersion(bill, version.Value);

            var statusBefore = bill.Status;
            var hadMethod = bill.PaymentMethodId is not null;
            var change = await apply(actor, bill);
            EnsureTotalsFit(bill);
            bill.Recalculate();
            ValidateDiscounts(bill);
            if (statusBefore is BillStatus.Closed or BillStatus.Unapproved)
            {
                // A closed bill keeps its single payment method (P6): it cannot start charging without one.
                if (bill.Total > 0 && !hadMethod)
                {
                    throw new ConflictException("Esta cuenta se cerró sin cobro. Para cobrar algo, anúlala y abre otra.");
                }

                if (bill.Total == 0 && hadMethod)
                {
                    bill.ChangePaymentMethod(null, null, Now());
                }
            }

            bill.Touch(Now());
            _dbContext.BillEvents.Add(new BillEvent(bill.Id, change.Type, statusBefore, currentUserId, Now(), change.Reason, change.Before, change.After));
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

        return await GetAsync(currentUserId, billId, cancellationToken);
    }

    private static void EnsureEditableBy(Actor actor, Bill bill)
    {
        switch (bill.Status)
        {
            case BillStatus.Approved:
                throw new ConflictException("La cuenta está aprobada y ya no se puede cambiar.");
            case BillStatus.Voided:
                throw new ConflictException("La cuenta está anulada y ya no se puede cambiar.");
            case BillStatus.Closed or BillStatus.Unapproved when !actor.Has(PermissionCodes.BillsApprove):
                // Q17: the professional does not correct a closed bill; the approver does (audited) or voids it.
                throw new UnauthorizedAccessException("Una cuenta cerrada solo la corrige quien aprueba cuentas, o se anula.");
            case BillStatus.Open when !actor.CanEdit(bill):
                throw new UnauthorizedAccessException("No puedes editar esta cuenta.");
        }
    }

    private static void EnsureVersion(Bill bill, uint version)
    {
        if (bill.Version != version)
        {
            throw new ConflictException(StaleMessage);
        }
    }

    private async Task<Bill> LoadLockedAsync(Actor actor, Guid billId, CancellationToken cancellationToken)
    {
        await LockAsync($"SELECT 1 FROM public.bills WHERE \"Id\" = {billId} FOR UPDATE", cancellationToken);
        return await LoadVisibleAsync(actor, billId, tracked: true, cancellationToken);
    }

    private async Task<Bill> LoadVisibleAsync(Actor actor, Guid billId, bool tracked, CancellationToken cancellationToken)
    {
        var query = _dbContext.Bills.Include(bill => bill.Lines).AsSplitQuery();
        var bill = await (tracked ? query : query.AsNoTracking()).SingleOrDefaultAsync(candidate => candidate.Id == billId, cancellationToken);
        return bill is not null && actor.Sees(bill) ? bill : throw new NotFoundException(NotFoundMessage);
    }

    private IQueryable<Bill> VisibleBills(Actor actor)
    {
        var bills = _dbContext.Bills.AsNoTracking();
        if (actor.SeesAll)
        {
            return bills;
        }

        var userId = actor.UserId;
        var staffId = actor.StaffProfileId;
        return bills.Where(bill => bill.OpenedByUserId == userId || (staffId != null && bill.StaffProfileId == staffId));
    }

    /// <summary>
    /// BUG-092: completed appointments with no bill (not voided) and nothing registered. ADR-0021 L8: only today's, the
    /// only ones whose bill can still be opened.
    /// </summary>
    private IQueryable<Appointment> PendingAppointmentsQuery(Actor actor)
    {
        var earliest = BogotaClock.ToUtc(Today(), TimeOnly.MinValue);
        var latest = BogotaClock.ToUtc(Today().AddDays(1), TimeOnly.MinValue);
        var appointments = _dbContext.Appointments.AsNoTracking()
            .Where(appointment => appointment.Status == AppointmentStatus.Completed && appointment.StartsAt >= earliest && appointment.StartsAt < latest
                && !_dbContext.Bills.Any(bill => bill.AppointmentId == appointment.Id && bill.Status != BillStatus.Voided)
                && !_dbContext.ServiceRecords.Any(record => record.AppointmentId == appointment.Id && record.Status != ServiceRecordStatus.Rejected));
        if (actor.Has(PermissionCodes.BillsManage))
        {
            return appointments;
        }

        var staffId = actor.StaffProfileId;
        return staffId is null ? appointments.Where(_ => false) : appointments.Where(appointment => appointment.StaffProfileId == staffId);
    }

    // ── Actor ───────────────────────────────────────────────────────────────────────────────────

    private sealed record Actor(Guid UserId, bool IsAdmin, IReadOnlySet<string> Permissions, Guid? StaffProfileId)
    {
        public bool Has(string code) => IsAdmin || Permissions.Contains(code);

        public bool SeesAll => Has(PermissionCodes.BillsManage) || Has(PermissionCodes.BillsApprove) || Has(PermissionCodes.BillsUnapprove)
            || Has(PermissionCodes.BillsVoid);

        public bool Owns(Bill bill) => bill.OpenedByUserId == UserId || (StaffProfileId is { } staffId && bill.StaffProfileId == staffId);

        public bool Sees(Bill bill) => SeesAll || Owns(bill);

        public bool CanEdit(Bill bill) => bill.Status switch
        {
            BillStatus.Open => Has(PermissionCodes.BillsManage) || (Has(PermissionCodes.BillsOpen) && Owns(bill)),
            BillStatus.Closed or BillStatus.Unapproved => Has(PermissionCodes.BillsApprove),
            _ => false
        };

        public bool CanClose(Bill bill)
            => bill.Status == BillStatus.Open && Has(PermissionCodes.BillsClose) && (Owns(bill) || Has(PermissionCodes.BillsManage));
    }

    private async Task<Actor> LoadActorAsync(Guid userId, CancellationToken cancellationToken)
    {
        var normalizedAdmin = RoleNames.Admin.ToUpperInvariant();
        var user = await _dbContext.Users.AsNoTracking()
            .Where(candidate => candidate.Id == userId && candidate.IsActive)
            .Select(candidate => new
            {
                IsAdmin = candidate.UserRoles.Any(userRole => userRole.Role.NormalizedName == normalizedAdmin),
                Codes = candidate.UserRoles.SelectMany(userRole => userRole.Role.RolePermissions.Select(rolePermission => rolePermission.Permission.Code)).ToList(),
            })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new UnauthorizedAccessException("Tu usuario no está activo.");
        var staffId = await _dbContext.StaffProfiles.AsNoTracking()
            .Where(staff => staff.UserId == userId && staff.IsActive)
            .Select(staff => (Guid?)staff.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return new Actor(userId, user.IsAdmin, user.Codes.Where(PermissionCodes.IsInCatalog).ToHashSet(StringComparer.Ordinal), staffId);
    }

    // ── Views ───────────────────────────────────────────────────────────────────────────────────

    private async Task<BillView> BuildViewAsync(Bill bill, Actor actor, CancellationToken cancellationToken)
    {
        var lines = bill.Lines.OrderBy(line => line.Position).ToList();
        var staffIds = lines.Where(line => line.StaffProfileId is not null).Select(line => line.StaffProfileId!.Value)
            .Append(bill.StaffProfileId ?? Guid.Empty).Distinct().ToArray();
        var staff = await _dbContext.StaffProfiles.AsNoTracking()
            .Where(profile => staffIds.Contains(profile.Id))
            .ToDictionaryAsync(profile => profile.Id, profile => new { profile.DisplayName, profile.IsActive }, cancellationToken);
        var userIds = new[] { bill.OpenedByUserId, bill.ClosedByUserId, bill.ApprovedByUserId, bill.VoidedByUserId }
            .OfType<Guid>().Distinct().ToArray();
        var users = await _dbContext.Users.AsNoTracking()
            .Where(user => userIds.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, user => user.FullName, cancellationToken);
        var appointment = bill.AppointmentId is { } appointmentId
            ? await _dbContext.Appointments.AsNoTracking()
                .Where(candidate => candidate.Id == appointmentId)
                .Select(candidate => new { candidate.StartsAt, candidate.Status })
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        var membershipIds = lines.Where(line => line.CustomerMembershipId is not null).Select(line => line.CustomerMembershipId!.Value).Distinct().ToArray();
        var membershipNames = await _dbContext.CustomerMemberships.AsNoTracking()
            .Where(membership => membershipIds.Contains(membership.Id))
            .ToDictionaryAsync(membership => membership.Id, membership => membership.PlanName, cancellationToken);
        var rateIds = lines.Where(line => line.ServiceId is not null).Select(line => line.ServiceId!.Value).Distinct().ToArray();
        var ratePrices = await _dbContext.Services.AsNoTracking()
            .Where(rate => rateIds.Contains(rate.Id))
            .ToDictionaryAsync(rate => rate.Id, rate => rate.BasePrice, cancellationToken);
        var productIds = lines.Where(line => line.ProductId is not null).Select(line => line.ProductId!.Value).Distinct().ToArray();
        var productPrices = await _dbContext.Products.AsNoTracking()
            .Where(product => productIds.Contains(product.Id))
            .ToDictionaryAsync(product => product.Id, product => product.Price, cancellationToken);
        var pendingStock = bill.Status is BillStatus.Open or BillStatus.Closed or BillStatus.Unapproved;
        var available = pendingStock && productIds.Length > 0
            ? await AvailableStockAsync(productIds, bill.Id, cancellationToken)
            : new Dictionary<Guid, int?>();

        var warnings = new List<string>();
        if (appointment is { Status: AppointmentStatus.Cancelled or AppointmentStatus.NoShow } && bill.Status is BillStatus.Open or BillStatus.Closed)
        {
            warnings.Add(bill.Status == BillStatus.Open
                ? "La cita de esta cuenta se canceló o el cliente no asistió. No se puede cerrar mientras siga ligada a ella."
                : "La cita de esta cuenta se canceló después de cerrarla. Revisa antes de aprobar.");
        }

        if (bill.IsOverdueOn(Now()))
        {
            warnings.Add($"Esta cuenta lleva abierta más de {Bill.OverdueAfterDays} días.");
        }

        if (bill.CustomerId is { } customerId && pendingStock
            && await _dbContext.Customers.AsNoTracking().AnyAsync(customer => customer.Id == customerId && customer.MergedIntoCustomerId != null, cancellationToken))
        {
            warnings.Add("El cliente se fusionó con otro perfil; al aprobar se usa el perfil que se conserva.");
        }

        foreach (var line in lines.Where(line => line.Type == BillLineType.Service && line.ServiceId is null))
        {
            warnings.Add($"Falta elegir la tarifa de «{line.NameSnapshot}».");
        }

        if (pendingStock)
        {
            foreach (var product in lines.Where(line => line.Type == BillLineType.Product).GroupBy(line => line.ProductId!.Value))
            {
                var wanted = product.Sum(line => line.Quantity);
                if (available.GetValueOrDefault(product.Key) is var left && (left ?? 0) < wanted)
                {
                    warnings.Add($"Stock insuficiente de «{product.First().NameSnapshot}»: quedan {left ?? 0} disponibles. "
                        + "Puedes cerrar igual; la existencia quedará en negativo.");
                }
            }

            foreach (var inactive in lines.Where(line => line.StaffProfileId is { } id && staff.TryGetValue(id, out var profile) && !profile.IsActive)
                         .Select(line => staff[line.StaffProfileId!.Value].DisplayName).Distinct())
            {
                warnings.Add($"«{inactive}» está inactivo.");
            }
        }

        var lineViews = lines.Select(line => new BillLineView(
                line.Id,
                line.Position,
                line.Type.ToString(),
                line.StaffProfileId,
                line.StaffProfileId is { } staffId && staff.TryGetValue(staffId, out var profile) ? profile.DisplayName : null,
                line.StaffProfileId is not { } activeId || !staff.TryGetValue(activeId, out var activeProfile) || activeProfile.IsActive,
                line.GeneralServiceId,
                line.ServiceId,
                line.NameSnapshot,
                line.Quantity,
                line.UnitPrice,
                line.Amount,
                line.BusinessPercentageSnapshot,
                line.IsCourtesy,
                line.Reason,
                line.ProductId,
                line.CustomerMembershipId,
                line.CustomerMembershipId is { } membershipId ? membershipNames.GetValueOrDefault(membershipId) : null,
                line.LoyaltyRedemptionId,
                line.AppliesToLineId,
                line.TipPaymentMode?.ToString(),
                line.Type switch
                {
                    BillLineType.Service when line.ServiceId is { } rateId => ratePrices.GetValueOrDefault(rateId),
                    BillLineType.Product => productPrices.GetValueOrDefault(line.ProductId!.Value),
                    _ => null
                },
                line.Type == BillLineType.Product && pendingStock ? available.GetValueOrDefault(line.ProductId!.Value) ?? 0 : null))
            .ToArray();

        var appointmentClosed = appointment is { Status: AppointmentStatus.Cancelled or AppointmentStatus.NoShow };
        var actions = new BillActionsView(
            actor.CanEdit(bill),
            actor.Has(PermissionCodes.BillsDiscount) && actor.CanEdit(bill),
            actor.CanClose(bill),
            actor.Has(PermissionCodes.BillsCharge),
            actor.Has(PermissionCodes.BillsVoid) && bill.Status is BillStatus.Open or BillStatus.Closed or BillStatus.Unapproved,
            actor.Has(PermissionCodes.BillsApprove) && bill.Status is BillStatus.Closed or BillStatus.Unapproved,
            bill.Status == BillStatus.Open && appointmentClosed && actor.CanEdit(bill),
            actor.Has(PermissionCodes.BillsApprove) && bill.Status is BillStatus.Closed or BillStatus.Unapproved && bill.Total > 0,
            actor.Has(PermissionCodes.BillsUnapprove) && bill.Status == BillStatus.Approved);

        var approvals = await _dbContext.BillApprovals.AsNoTracking()
            .Where(approval => approval.BillId == bill.Id)
            .Select(approval => new { approval.Sequence, approval.UnapprovedAt, approval.UnapprovedByUserId, approval.UnapprovalReason })
            .ToListAsync(cancellationToken);
        var lastUnapproval = approvals.Where(approval => approval.UnapprovedAt != null).OrderByDescending(approval => approval.Sequence).FirstOrDefault();
        var unapprovedByName = lastUnapproval?.UnapprovedByUserId is { } unapprovedBy
            ? await _dbContext.Users.AsNoTracking().Where(user => user.Id == unapprovedBy).Select(user => user.FullName).SingleOrDefaultAsync(cancellationToken)
            : null;

        return new BillView(
            bill.Id,
            bill.DisplayNumber,
            bill.Status.ToString(),
            bill.AppointmentId,
            appointment?.StartsAt,
            appointment?.Status.ToString(),
            bill.CustomerId,
            bill.CustomerNameSnapshot,
            bill.IsWalkIn,
            bill.StaffProfileId,
            bill.StaffProfileId is { } ownerId && staff.TryGetValue(ownerId, out var owner) ? owner.DisplayName : null,
            bill.ServiceDate,
            bill.Notes,
            bill.OpenedAt,
            users.GetValueOrDefault(bill.OpenedByUserId),
            bill.ClosedAt,
            bill.ClosedByUserId is { } closedBy ? users.GetValueOrDefault(closedBy) : null,
            bill.PaymentMethodId,
            bill.PaymentMethodNameSnapshot,
            bill.Subtotal,
            bill.DiscountTotal,
            bill.TipTotal,
            bill.DirectTipTotal,
            bill.Total,
            bill.ApprovedAt,
            bill.ApprovedByUserId is { } approvedBy ? users.GetValueOrDefault(approvedBy) : null,
            bill.IsSelfApproved,
            bill.VoidedAt,
            bill.VoidedByUserId is { } voidedBy ? users.GetValueOrDefault(voidedBy) : null,
            bill.VoidReason,
            bill.IsOverdueOn(Now()),
            bill.Version,
            lineViews,
            warnings,
            actions,
            approvals.Count,
            lastUnapproval?.UnapprovedAt,
            unapprovedByName,
            lastUnapproval?.UnapprovalReason);
    }

    private async Task<IReadOnlyList<BillSummaryView>> MapSummariesAsync(IReadOnlyList<Bill> bills, CancellationToken cancellationToken)
    {
        var staffIds = bills.Where(bill => bill.StaffProfileId is not null).Select(bill => bill.StaffProfileId!.Value).Distinct().ToArray();
        var staff = await _dbContext.StaffProfiles.AsNoTracking()
            .Where(profile => staffIds.Contains(profile.Id))
            .ToDictionaryAsync(profile => profile.Id, profile => new { profile.DisplayName, profile.IsActive }, cancellationToken);
        var utcNow = Now();
        return bills.Select(bill => new BillSummaryView(
                bill.Id,
                bill.DisplayNumber,
                bill.Status.ToString(),
                bill.AppointmentId,
                bill.CustomerNameSnapshot,
                bill.IsWalkIn,
                bill.StaffProfileId,
                bill.StaffProfileId is { } staffId && staff.TryGetValue(staffId, out var profile) ? profile.DisplayName : null,
                bill.StaffProfileId is not { } activeId || !staff.TryGetValue(activeId, out var activeProfile) || activeProfile.IsActive,
                bill.ServiceDate,
                bill.Total,
                bill.Lines.Count,
                bill.PaymentMethodNameSnapshot,
                bill.OpenedAt,
                bill.ClosedAt,
                bill.IsOverdueOn(utcNow),
                bill.IsSelfApproved,
                bill.Lines.Any(line => line.Type == BillLineType.Redemption)))
            .ToArray();
    }

    /// <summary>
    /// ADR-0020 §6: units on hand minus the units of closed bills not approved yet (they left the shop but are not
    /// discounted until the approval). Null for a product that never had a stock row (no stock, no cost: 0 on hand).
    /// </summary>
    internal async Task<Dictionary<Guid, int?>> AvailableStockAsync(Guid[] productIds, Guid? excludeBillId, CancellationToken cancellationToken)
    {
        var onHand = await _dbContext.ProductStocks.AsNoTracking()
            .Where(stock => productIds.Contains(stock.ProductId))
            .ToDictionaryAsync(stock => stock.ProductId, stock => stock.OnHand, cancellationToken);
        var committed = await CommittedStockQuery(_dbContext, excludeBillId)
            .Where(row => productIds.Contains(row.ProductId))
            .ToDictionaryAsync(row => row.ProductId, row => row.Quantity, cancellationToken);
        return productIds.ToDictionary(id => id, id => (int?)(onHand.GetValueOrDefault(id) - committed.GetValueOrDefault(id)));
    }

    internal sealed class CommittedRow
    {
        public Guid ProductId { get; init; }
        public int Quantity { get; init; }
    }

    internal static IQueryable<CommittedRow> CommittedStockQuery(AppDbContext dbContext, Guid? excludeBillId = null)
        => from line in dbContext.BillLines.AsNoTracking()
           join bill in dbContext.Bills.AsNoTracking() on line.BillId equals bill.Id
           where line.ProductId != null && (bill.Status == BillStatus.Closed || bill.Status == BillStatus.Unapproved)
               && bill.Id != excludeBillId
           group line by line.ProductId!.Value into product
           select new CommittedRow { ProductId = product.Key, Quantity = product.Sum(line => line.Quantity) };

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private async Task<(Guid? CustomerId, string CustomerName)> ResolveCustomerAsync(
        Guid? customerId, string? customerName, CancellationToken cancellationToken)
    {
        if (customerId is { } id)
        {
            var profile = await _dbContext.Customers.AsNoTracking()
                .Where(customer => customer.Id == id)
                .Select(customer => new { customer.Id, customer.FullName, customer.MergedIntoCustomerId })
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw Invalid("customerId", "El cliente no existe.");
            return profile.MergedIntoCustomerId is not null
                ? throw Invalid("customerId", "El cliente fue fusionado con otro perfil. Búscalo de nuevo.")
                : (profile.Id, profile.FullName);
        }

        var name = customerName?.Trim() ?? string.Empty;
        return name.Length is < Bill.MinCustomerNameLength or > Bill.MaxCustomerNameLength
            ? throw Invalid("customerName", "Elige el cliente o escribe el nombre del cliente ocasional (entre 2 y 120 caracteres).")
            : (null, name);
    }

    private async Task<Guid> RequireStaffAsync(Guid? staffProfileId, CancellationToken cancellationToken)
    {
        if (staffProfileId is not { } staffId)
        {
            throw Invalid("staffProfileId", "Elige el profesional.");
        }

        return await _dbContext.StaffProfiles.AnyAsync(staff => staff.Id == staffId && staff.IsActive, cancellationToken)
            ? staffId
            : throw Invalid("staffProfileId", "El profesional no existe o está inactivo.");
    }

    private async Task<Barbershop.Domain.Services.GeneralService> RequireGeneralServiceAsync(Guid? generalServiceId, CancellationToken cancellationToken)
        => await _dbContext.GeneralServices.AsNoTracking()
            .SingleOrDefaultAsync(general => general.Id == generalServiceId && !general.IsDeleted, cancellationToken)
           ?? throw Invalid("generalServiceId", "El servicio no existe en el catálogo.");

    private async Task<Barbershop.Domain.Services.Service> RequireRateAsync(Guid rateId, Guid generalServiceId, bool allowDeleted, CancellationToken cancellationToken)
    {
        if (!allowDeleted && _dbContext.Database.IsNpgsql())
        {
            // ADR-0021 L14: shared after the bill, so deleting the rate at the same moment waits for this line.
            await _dbContext.ShareRateAsync(rateId, cancellationToken);
        }

        var rate = await _dbContext.Services.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == rateId, cancellationToken);
        if (rate is null || (!allowDeleted && (rate.IsDeleted || !rate.IsActive)))
        {
            throw Invalid("serviceId", "La tarifa no existe o no está vigente.");
        }

        return rate.GeneralServiceId == generalServiceId
            ? rate
            : throw Invalid("serviceId", "La tarifa elegida no pertenece al servicio elegido.");
    }

    private async Task<Dictionary<Guid, Guid>> RateGeneralServicesAsync(IEnumerable<Guid> rateIds, CancellationToken cancellationToken)
    {
        var ids = rateIds.Distinct().ToArray();
        return await _dbContext.Services.AsNoTracking()
            .Where(rate => ids.Contains(rate.Id))
            .ToDictionaryAsync(rate => rate.Id, rate => rate.GeneralServiceId, cancellationToken);
    }

    private static int ValidateQuantity(int? quantity)
        => quantity is >= 1 and <= BillLine.MaxQuantity
            ? quantity.Value
            : throw Invalid("quantity", $"La cantidad debe estar entre 1 y {BillLine.MaxQuantity}.");

    private static int ValidateAmount(int? amount)
        => amount is >= 1 and <= BillLine.MaxAmount
            ? amount.Value
            : throw Invalid("amount", "El valor debe ser mayor que cero y como máximo $ 100.000.000.");

    private static string ValidateReason(string? reason)
    {
        var trimmed = reason?.Trim() ?? string.Empty;
        return trimmed.Length is < Bill.MinReasonLength or > Bill.MaxReasonLength
            ? throw Invalid("reason", $"El motivo debe tener entre {Bill.MinReasonLength} y {Bill.MaxReasonLength} caracteres.")
            : trimmed;
    }

    private static BillLineType ParseLineType(string? type)
        => ParseName<BillLineType>(type) ?? throw Invalid("type",
            "El tipo de línea debe ser Service, Product, MembershipConsumption, Redemption, Discount o Tip.");

    private static TipPaymentMode ParseTipMode(string? mode)
        => ParseName<TipPaymentMode>(mode) ?? throw Invalid("tipPaymentMode",
            "Indica si la propina se cobra con la cuenta (WithBill) o se pagó directo al profesional (Direct).");

    private static BillStatus? ParseStatus(string? status)
        => string.IsNullOrWhiteSpace(status)
            ? null
            : ParseName<BillStatus>(status) ?? throw Invalid("status", "El estado debe ser Open, Closed, Approved, Unapproved o Voided.");

    private static int? ParseNumber(string term)
    {
        var digits = term.StartsWith(BillNumberSeries.DefaultPrefix + "-", StringComparison.OrdinalIgnoreCase) ? term[2..] : term;
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;
    }

    private static TEnum? ParseName<TEnum>(string? value)
        where TEnum : struct, Enum
    {
        var name = Enum.GetNames<TEnum>().FirstOrDefault(candidate => string.Equals(candidate, value?.Trim(), StringComparison.OrdinalIgnoreCase));
        return name is null ? null : Enum.Parse<TEnum>(name);
    }

    private static ValidationProblemException Invalid(string field, string message)
        => new(new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });

    internal static string HeaderJson(Bill bill)
        => Json(new { bill.CustomerId, customerName = bill.CustomerNameSnapshot, bill.ServiceDate, bill.AppointmentId, bill.StaffProfileId, bill.Notes });

    private static string? LineJson(BillLine? line)
        => line is null
            ? null
            : Json(new
            {
                line.Id,
                type = line.Type.ToString(),
                name = line.NameSnapshot,
                line.StaffProfileId,
                line.ServiceId,
                line.ProductId,
                line.Quantity,
                line.UnitPrice,
                line.Amount,
                line.IsCourtesy,
                line.Reason,
                line.CustomerMembershipId,
                line.LoyaltyRedemptionId,
                line.AppliesToLineId,
                tipPaymentMode = line.TipPaymentMode?.ToString(),
            });

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonOptions);

    private static PushNotificationMessage ClosedMessage(Bill bill)
    {
        var total = $"$ {bill.Total.ToString("N0", DisplayCulture)}";
        return new PushNotificationMessage(
            $"Tu recibo de caja {bill.DisplayNumber}",
            bill.Total > 0
                ? $"Registramos tu cuenta {bill.DisplayNumber} por {total}, pagada con {bill.PaymentMethodNameSnapshot}. Gracias por tu visita."
                : $"Registramos tu cuenta {bill.DisplayNumber}. Gracias por tu visita.",
            $"/customer/bills/{bill.Id}",
            $"bill-{bill.Id:N}",
            IsTimeSensitive: false);
    }

    private static PushNotificationMessage CorrectedMessage(Bill bill)
        => new(
            $"Corregimos tu recibo de caja {bill.DisplayNumber}",
            bill.Total > 0
                ? $"Tu cuenta {bill.DisplayNumber} quedó corregida por $ {bill.Total.ToString("N0", DisplayCulture)}."
                : $"Tu cuenta {bill.DisplayNumber} quedó corregida.",
            $"/customer/bills/{bill.Id}",
            $"bill-{bill.Id:N}",
            IsTimeSensitive: false);

    private static PushNotificationMessage VoidedMessage(Bill bill)
        => new(
            $"Anulamos tu recibo de caja {bill.DisplayNumber}",
            $"La cuenta {bill.DisplayNumber} quedó anulada. Si tienes dudas, pregúntanos en la barbería.",
            $"/customer/bills/{bill.Id}",
            $"bill-{bill.Id:N}",
            IsTimeSensitive: false);

    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginTransactionAsync(CancellationToken cancellationToken)
        => _dbContext.Database.IsRelational() ? await _dbContext.Database.BeginTransactionAsync(cancellationToken) : null;

    private static async Task CommitAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
    }

    /// <summary>PostgreSQL row lock held until the transaction ends; other providers (unit tests) skip it.</summary>
    private async Task LockAsync(FormattableString lockQuery, CancellationToken cancellationToken)
    {
        if (_dbContext.Database.IsNpgsql())
        {
            await _dbContext.Database.ExecuteSqlInterpolatedAsync(lockQuery, cancellationToken);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception)
        => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>
    /// Used as an exception filter (always false, so it never handles): a failed operation leaves nothing half applied
    /// in the unit of work, so the next operation of the same scope starts from what is stored.
    /// </summary>
    private bool ResetTracker()
    {
        _dbContext.ChangeTracker.Clear();
        return false;
    }

    private DateTime Now() => _timeProvider.GetUtcNow().UtcDateTime;

    private DateOnly Today() => BogotaDate(Now());

    private static DateOnly BogotaDate(DateTime utc) => DateOnly.FromDateTime(BogotaClock.ToLocal(utc));
}
