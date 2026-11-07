using System.Net.Mail;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Common.Paging;
using Barbershop.Application.Customers.Admin;
using Barbershop.Domain.Customers;
using Barbershop.Domain.Memberships;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Loyalty;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Barbershop.Infrastructure.Customers;

internal sealed class CustomerManagementService : IAdminCustomersService
{
    private const int CandidateLimit = 20;
    private const int MinPhoneDigits = 7;
    private const int PhoneKeyLength = 10;
    private const int ReasonMinLength = 3;
    private const int ReasonMaxLength = 500;

    private const string ConcurrencyConflictMessage = "El cliente fue modificado por otra operación. Recarga e intenta de nuevo.";
    private const string AccountAlreadyLinkedMessage = "Esta cuenta ya está vinculada a otro cliente.";
    private const string AccountHasPendingClaimMessage =
        "La cuenta tiene una solicitud de vinculación pendiente. Resuélvela en la bandeja de reclamaciones.";
    internal const string OwnProfilePendingClaimMessage =
        "Tienes una solicitud para vincular tu perfil histórico pendiente; espera su respuesta.";

    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _timeProvider;
    private readonly LoyaltyWalletService _wallet;

    public CustomerManagementService(AppDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
        _wallet = new LoyaltyWalletService(dbContext, timeProvider);
    }

    public async Task<PagedResult<CustomerListItem>> SearchAsync(
        string? query, int? page = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var pageRequest = PageRequest.Create(page, pageSize);
        var customers = _dbContext.Customers.Where(customer => customer.MergedIntoCustomerId == null);

        var term = query?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            var upper = term.ToUpperInvariant();
            var digits = CustomerProfile.NormalizePhone(term);
            customers = customers.Where(customer =>
                customer.FullName.ToUpper().Contains(upper)
                || customer.NormalizedEmail.Contains(upper)
                || (digits.Length >= 3 && customer.NormalizedPhone.Contains(digits)));
        }

        return await customers
            .AsNoTracking()
            .OrderBy(customer => customer.FullName)
            .ThenBy(customer => customer.Id)
            .ToPagedResultAsync(
                pageRequest,
                async (results, ct) =>
                {
                    var users = await LoadUsersAsync(results.SelectMany(LinkUserIds), ct);
                    return (IReadOnlyList<CustomerListItem>)results.Select(customer => MapListItem(customer, users)).ToArray();
                },
                cancellationToken);
    }

    public async Task<CustomerDetailView> GetByIdAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        var customer = await LoadAsync(customerId, cancellationToken);
        return await MapDetailAsync(customer, cancellationToken);
    }

    public async Task<IReadOnlyList<CustomerDuplicateCandidate>> FindDuplicatesAsync(
        string? email,
        string? phone,
        Guid? excludeCustomerId,
        Guid? excludeUserId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = string.IsNullOrWhiteSpace(email) ? null : CustomerProfile.NormalizeEmail(email);
        var phoneDigits = string.IsNullOrWhiteSpace(phone) ? string.Empty : CustomerProfile.NormalizePhone(phone);
        var hasPhone = phoneDigits.Length >= MinPhoneDigits;

        if (normalizedEmail is null && !hasPhone)
        {
            return [];
        }

        // Compare the local number only, so "+57 300 123 4567" and "300 123 4567" are the same phone.
        var phoneKey = phoneDigits.Length > PhoneKeyLength ? phoneDigits[^PhoneKeyLength..] : phoneDigits;
        var candidates = new List<CustomerDuplicateCandidate>();

        var customers = await _dbContext.Customers
            .Where(customer => customer.MergedIntoCustomerId == null
                && customer.Id != excludeCustomerId
                && ((normalizedEmail != null && customer.NormalizedEmail == normalizedEmail)
                    || (hasPhone && customer.NormalizedPhone.EndsWith(phoneKey))))
            .OrderBy(customer => customer.FullName)
            .ToListAsync(cancellationToken);

        foreach (var customer in customers)
        {
            candidates.Add(new CustomerDuplicateCandidate(
                "customer",
                customer.Id,
                customer.FullName,
                customer.Email,
                customer.Phone,
                MatchedOn(customer.NormalizedEmail == normalizedEmail, hasPhone && customer.NormalizedPhone.EndsWith(phoneKey)),
                customer.UserId is not null));
        }

        // The account a profile is being created from, or the one linked to the profile being edited,
        // is not a duplicate of it.
        Guid? excludedUserId = excludeUserId ?? (excludeCustomerId is null
            ? null
            : await _dbContext.Customers
                .Where(customer => customer.Id == excludeCustomerId)
                .Select(customer => customer.UserId)
                .SingleOrDefaultAsync(cancellationToken));

        // Users store the phone as typed (with spaces or dashes); narrow by the last four digits in SQL,
        // which are rarely split by a separator, then compare the normalized numbers in memory.
        var phoneLastFour = hasPhone ? phoneDigits[^4..] : null;
        var users = await _dbContext.Users
            .Where(user => user.IsActive
                && user.Id != excludedUserId
                && ((normalizedEmail != null && user.NormalizedEmail == normalizedEmail)
                    || (phoneLastFour != null && user.PhoneNumber != null && user.PhoneNumber.Contains(phoneLastFour))))
            .OrderBy(user => user.FullName)
            .ToListAsync(cancellationToken);

        var matchedUserIds = users.Select(user => user.Id).ToArray();
        var linkedUserIds = matchedUserIds.Length == 0
            ? []
            : (await _dbContext.Customers
                .Where(customer => customer.UserId != null
                    && customer.MergedIntoCustomerId == null
                    && matchedUserIds.Contains(customer.UserId.Value))
                .Select(customer => customer.UserId!.Value)
                .ToListAsync(cancellationToken)).ToHashSet();

        foreach (var user in users)
        {
            var emailMatches = user.NormalizedEmail == normalizedEmail;
            var phoneMatches = hasPhone && user.PhoneNumber is not null && CustomerProfile.NormalizePhone(user.PhoneNumber).EndsWith(phoneKey, StringComparison.Ordinal);
            if (!emailMatches && !phoneMatches)
            {
                continue;
            }

            candidates.Add(new CustomerDuplicateCandidate(
                "account",
                user.Id,
                user.FullName,
                user.Email,
                user.PhoneNumber,
                MatchedOn(emailMatches, phoneMatches),
                linkedUserIds.Contains(user.Id)));
        }

        return candidates;
    }

    public async Task<CustomerDetailView> CreateAsync(Guid currentUserId, CustomerCreateRequest request, CancellationToken cancellationToken = default)
    {
        ValidateDetails(request.FullName, request.Phone, request.Email);

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var customer = new CustomerProfile(request.FullName, request.Phone, request.Email, currentUserId, utcNow);

        _dbContext.Customers.Add(customer);
        _dbContext.CustomerEvents.Add(new CustomerEvent(customer.Id, CustomerEventType.Created, currentUserId, utcNow));
        await SaveChangesAsync(cancellationToken);

        return await MapDetailAsync(customer, cancellationToken);
    }

    public async Task<CustomerDetailView> UpdateAsync(Guid customerId, CustomerUpdateRequest request, CancellationToken cancellationToken = default)
    {
        ValidateDetails(request.FullName, request.Phone, request.Email);
        var customer = await LoadLiveAsync(customerId, cancellationToken);

        customer.UpdateDetails(request.FullName, request.Phone, request.Email, _timeProvider.GetUtcNow().UtcDateTime);
        await SaveChangesAsync(cancellationToken);

        return await MapDetailAsync(customer, cancellationToken);
    }

    public async Task<IReadOnlyList<CustomerAccountCandidate>> GetAccountCandidatesAsync(string? query, CancellationToken cancellationToken = default)
    {
        var customerRole = RoleNames.Customer.ToUpperInvariant();
        var linkedUserIds = _dbContext.Customers
            .Where(customer => customer.UserId != null && customer.MergedIntoCustomerId == null)
            .Select(customer => customer.UserId!.Value);

        var users = _dbContext.Users.Where(user =>
            user.IsActive
            && user.UserRoles.Any(assignment => assignment.Role.NormalizedName == customerRole)
            && !linkedUserIds.Contains(user.Id));

        var term = query?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            var upper = term.ToUpperInvariant();
            users = users.Where(user =>
                user.FullName.ToUpper().Contains(upper)
                || user.NormalizedEmail.Contains(upper)
                || (user.PhoneNumber != null && user.PhoneNumber.Contains(term)));
        }

        return await users
            .OrderBy(user => user.FullName)
            .Take(CandidateLimit)
            .Select(user => new CustomerAccountCandidate(user.Id, user.FullName, user.Email, user.PhoneNumber, user.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<CustomerDetailView> CreateFromAccountAsync(
        Guid currentUserId,
        CustomerCreateFromAccountRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureIdentityVerified(request.IdentityVerified);
        var customer = await InAccountTransactionAsync(async () =>
        {
            var user = await LoadLinkableUserAsync(request.UserId, cancellationToken);

            // Name and email come from the account as they are today; the phone can be typed when the account has none.
            var phone = string.IsNullOrWhiteSpace(request.Phone) ? user.PhoneNumber : request.Phone;
            ValidateDetails(user.FullName, phone, user.Email);

            var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
            var created = new CustomerProfile(user.FullName, phone!, user.Email, currentUserId, utcNow);
            created.LinkAccount(user.Id, currentUserId, utcNow);

            _dbContext.Customers.Add(created);
            _dbContext.CustomerEvents.Add(new CustomerEvent(created.Id, CustomerEventType.Created, currentUserId, utcNow));
            _dbContext.CustomerEvents.Add(new CustomerEvent(created.Id, CustomerEventType.AccountLinked, currentUserId, utcNow, userId: user.Id));

            // The profile never exists without its link.
            await SaveChangesAsync(cancellationToken);
            return created;
        }, cancellationToken);

        return await MapDetailAsync(customer, cancellationToken);
    }

    /// <summary>
    /// "I am a new customer": a profile linked to the caller's own account, created by the caller. Same validations and
    /// account rules as <see cref="CreateFromAccountAsync"/>, but the identity is the signed-in account itself, so no
    /// in-person check is needed. Returns the existing profile when the account already has one (retries, double
    /// clicks), and refuses while the account has a pending claim. Runs under the account lock, so a concurrent claim
    /// or link of the same account is decided before or after it, never in between.
    /// </summary>
    internal async Task<CustomerProfile> CreateOwnLinkedAsync(
        Guid userId, string fullName, string phone, string email, CancellationToken cancellationToken)
    {
        if (await FindLiveLinkedAsync(userId, cancellationToken) is { } existing)
        {
            return existing;
        }

        ValidateDetails(fullName, phone, email);
        try
        {
            return await InAccountTransactionAsync(async () =>
            {
                await LockAccountAsync(userId, cancellationToken);
                if (await FindLiveLinkedAsync(userId, cancellationToken) is { } concurrent)
                {
                    // A concurrent retry linked a profile while this one waited for the lock: return it.
                    return concurrent;
                }

                // A pending claim means "I already have a profile": creating another one would make a duplicate.
                if (await HasPendingClaimAsync(userId, null, cancellationToken))
                {
                    throw new ConflictException(OwnProfilePendingClaimMessage);
                }

                var user = await LoadLinkableUserAsync(userId, cancellationToken);
                var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
                var customer = new CustomerProfile(fullName, phone, email, user.Id, utcNow);
                customer.LinkAccount(user.Id, user.Id, utcNow);
                _dbContext.Customers.Add(customer);
                _dbContext.CustomerEvents.Add(new CustomerEvent(customer.Id, CustomerEventType.Created, user.Id, utcNow));
                _dbContext.CustomerEvents.Add(new CustomerEvent(customer.Id, CustomerEventType.AccountLinked, user.Id, utcNow, userId: user.Id));
                await _dbContext.SaveChangesAsync(cancellationToken);
                return customer;
            }, cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A path that does not take the account lock (a merge moving the account) linked it first: return that one.
            _dbContext.ChangeTracker.Clear();
            return await FindLiveLinkedAsync(userId, cancellationToken) ?? throw new ConflictException(AccountAlreadyLinkedMessage);
        }
    }

    /// <summary>
    /// The per-account lock (the account's row, FOR UPDATE) taken by everything that gives an account a live profile
    /// or a pending claim: own profile, claim submission, admin create/link and claim approval. Held until the
    /// surrounding transaction ends; a no-op outside PostgreSQL.
    /// </summary>
    internal async Task LockAccountAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (_dbContext.Database.IsNpgsql())
        {
            await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM public.users WHERE \"Id\" = {userId} FOR UPDATE", cancellationToken);
        }
    }

    internal Task<bool> HasPendingClaimAsync(Guid userId, Guid? exceptClaimId, CancellationToken cancellationToken)
        => _dbContext.CustomerClaims.AnyAsync(
            claim => claim.UserId == userId && claim.Status == CustomerClaimStatus.Pending && claim.Id != exceptClaimId,
            cancellationToken);

    /// <summary>Runs <paramref name="work"/> in a transaction, or in the caller's when one is already open.</summary>
    private async Task<T> InAccountTransactionAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        if (_dbContext.Database.CurrentTransaction is not null)
        {
            return await work();
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var result = await work();
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    internal Task<CustomerProfile?> FindLiveLinkedAsync(Guid userId, CancellationToken cancellationToken)
        => _dbContext.Customers.AsNoTracking()
            .SingleOrDefaultAsync(customer => customer.UserId == userId && customer.MergedIntoCustomerId == null, cancellationToken);

    public Task<CustomerDetailView> LinkAccountAsync(
        Guid currentUserId,
        Guid customerId,
        CustomerLinkAccountRequest request,
        CancellationToken cancellationToken = default)
        => LinkAccountAsync(currentUserId, customerId, request, approvingClaimId: null, cancellationToken);

    /// <summary>
    /// Links the account; <paramref name="approvingClaimId"/> is the pending claim being approved, the only one the
    /// account may have while it is linked.
    /// </summary>
    internal async Task<CustomerDetailView> LinkAccountAsync(
        Guid currentUserId,
        Guid customerId,
        CustomerLinkAccountRequest request,
        Guid? approvingClaimId,
        CancellationToken cancellationToken)
    {
        EnsureIdentityVerified(request.IdentityVerified);

        var customer = await InAccountTransactionAsync(async () =>
        {
            var linked = await LoadLiveAsync(customerId, cancellationToken);
            if (linked.UserId is not null)
            {
                throw new ConflictException("El cliente ya tiene una cuenta vinculada. Desvincúlala antes de vincular otra.");
            }

            var user = await LoadLinkableUserAsync(request.UserId, cancellationToken, approvingClaimId);

            var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
            linked.LinkAccount(user.Id, currentUserId, utcNow);
            _dbContext.CustomerEvents.Add(new CustomerEvent(linked.Id, CustomerEventType.AccountLinked, currentUserId, utcNow, userId: user.Id));
            await SaveChangesAsync(cancellationToken);
            return linked;
        }, cancellationToken);

        return await MapDetailAsync(customer, cancellationToken);
    }

    public async Task<CustomerDetailView> UnlinkAccountAsync(
        Guid currentUserId,
        Guid customerId,
        CustomerUnlinkAccountRequest request,
        CancellationToken cancellationToken = default)
    {
        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is < ReasonMinLength or > ReasonMaxLength)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["reason"] = [$"El motivo debe tener entre {ReasonMinLength} y {ReasonMaxLength} caracteres."]
            });
        }

        var customer = await LoadLiveAsync(customerId, cancellationToken);
        if (customer.UserId is null)
        {
            throw new ConflictException("El cliente no tiene una cuenta vinculada.");
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var unlinkedUserId = customer.UnlinkAccount(utcNow);
        _dbContext.CustomerEvents.Add(new CustomerEvent(
            customer.Id, CustomerEventType.AccountUnlinked, currentUserId, utcNow, userId: unlinkedUserId, reason: reason));
        await SaveChangesAsync(cancellationToken);

        return await MapDetailAsync(customer, cancellationToken);
    }

    public async Task<CustomerMergePreview> GetMergePreviewAsync(
        Guid targetCustomerId,
        Guid sourceCustomerId,
        CancellationToken cancellationToken = default)
    {
        var target = await LoadAsync(targetCustomerId, cancellationToken);
        var source = await LoadAsync(sourceCustomerId, cancellationToken);
        return await BuildMergePreviewAsync(target, source, cancellationToken);
    }

    public async Task<CustomerDetailView> MergeAsync(
        Guid currentUserId,
        Guid targetCustomerId,
        CustomerMergeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.PreviewToken))
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["previewToken"] = ["Revisa la vista previa de la fusión antes de confirmarla."]
            });
        }

        // Lock order: customer profiles (ascending id) → the source's memberships, as linking and annulling do. The
        // preview is rebuilt under those locks: if anything it showed changed, the merge is refused (ADR-0013 D7).
        // Two saves inside one transaction: the source must leave the "live linked profiles" unique
        // index before the target takes over its account, and PostgreSQL checks that index per statement.
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await _wallet.LockCustomersAsync([targetCustomerId, request.SourceCustomerId], cancellationToken);
        if (_dbContext.Database.IsNpgsql())
        {
            await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM public.customer_memberships WHERE \"CustomerId\" = {request.SourceCustomerId} ORDER BY \"Id\" FOR UPDATE",
                cancellationToken);
        }

        var target = await LoadAsync(targetCustomerId, cancellationToken);
        var source = await LoadAsync(request.SourceCustomerId, cancellationToken);

        var preview = await BuildMergePreviewAsync(target, source, cancellationToken);
        if (!preview.CanMerge)
        {
            throw new ConflictException(preview.BlockingReasons[0]);
        }

        if (!string.Equals(preview.PreviewToken, request.PreviewToken, StringComparison.Ordinal))
        {
            throw new ConflictException(
                "Los clientes cambiaron desde la vista previa (puntos, deuda, lotes, membresías o cuentas). Genera una vista previa nueva antes de fusionar.");
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        Guid? transferredUserId = source.UserId is not null && target.UserId is null
            ? source.UnlinkAccount(utcNow)
            : null;

        source.MarkMergedInto(target.Id, currentUserId, utcNow);
        _dbContext.CustomerEvents.Add(new CustomerEvent(
            source.Id, CustomerEventType.MergedInto, currentUserId, utcNow, userId: transferredUserId, relatedCustomerId: target.Id));
        await SaveChangesAsync(cancellationToken);

        if (transferredUserId is { } userId)
        {
            target.LinkAccount(userId, currentUserId, utcNow);
        }
        else
        {
            target.Touch(utcNow);
        }

        // Every membership follows the surviving profile; OriginalCustomerId keeps who bought it.
        var memberships = await _dbContext.CustomerMemberships
            .Where(membership => membership.CustomerId == source.Id)
            .ToListAsync(cancellationToken);
        foreach (var membership in memberships)
        {
            membership.MoveToCustomer(target.Id, utcNow);
            _dbContext.MembershipEvents.Add(new MembershipEvent(
                membership.Id, MembershipEventType.MovedByMerge, currentUserId, utcNow, relatedCustomerId: source.Id));
        }

        _dbContext.CustomerEvents.Add(new CustomerEvent(
            target.Id, CustomerEventType.MergedFrom, currentUserId, utcNow, userId: transferredUserId, relatedCustomerId: source.Id));

        // Usable points and outstanding debt follow too, with new movements; expired points stay behind.
        await _wallet.TransferForMergeInCurrentTransactionAsync(source.Id, target.Id, currentUserId, utcNow, cancellationToken);
        await SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return await MapDetailAsync(target, cancellationToken);
    }

    private async Task<CustomerMergePreview> BuildMergePreviewAsync(CustomerProfile target, CustomerProfile source, CancellationToken cancellationToken)
    {
        var blockingReasons = new List<string>();
        var warnings = new List<string>();

        if (target.Id == source.Id)
        {
            blockingReasons.Add("No puedes fusionar un cliente consigo mismo.");
        }

        if (target.IsMerged)
        {
            blockingReasons.Add("El cliente destino ya fue fusionado en otro perfil.");
        }

        if (source.IsMerged)
        {
            blockingReasons.Add("El cliente origen ya fue fusionado en otro perfil.");
        }

        if (target.UserId is not null && source.UserId is not null && target.UserId != source.UserId)
        {
            blockingReasons.Add("Ambos clientes tienen cuentas vinculadas distintas. Desvincula una antes de fusionar.");
        }

        var transfersAccount = source.UserId is not null && target.UserId is null;
        if (transfersAccount)
        {
            warnings.Add("La cuenta vinculada al cliente origen pasará al cliente destino.");
        }

        if (target.Id != source.Id)
        {
            warnings.Add("Se conservan el nombre, teléfono y correo del cliente destino. El cliente origen quedará marcado como fusionado y no se elimina.");

            var sourceMembershipCount = await _dbContext.CustomerMemberships.CountAsync(membership => membership.CustomerId == source.Id, cancellationToken);
            if (sourceMembershipCount > 0)
            {
                warnings.Add($"{sourceMembershipCount} membresía(s) del cliente origen pasarán al cliente destino, con sus saldos, obsequios e historial.");
            }

            // Both profiles may already have received a first-purchase gift for the same plan; both are kept.
            var duplicatedFirstPurchasePlans = await (
                    from gift in _dbContext.MembershipGifts
                    join membership in _dbContext.CustomerMemberships on gift.CustomerMembershipId equals membership.Id
                    where gift.Condition == MembershipGiftCondition.FirstPurchaseOfPlan
                        && (membership.CustomerId == source.Id || membership.CustomerId == target.Id)
                    group membership by new { membership.MembershipPlanId, membership.PlanName } into byPlan
                    where byPlan.Select(membership => membership.CustomerId).Distinct().Count() > 1
                    select byPlan.Key.PlanName)
                .ToListAsync(cancellationToken);
            foreach (var planName in duplicatedFirstPurchasePlans)
            {
                warnings.Add($"Ambos perfiles recibieron el obsequio de primera compra de \"{planName}\"; se conservan los dos.");
            }
        }

        var users = await LoadUsersAsync(LinkUserIds(target).Concat(LinkUserIds(source)), cancellationToken);
        var sourceItem = MapListItem(source, users);

        // Points (ADR-0013 D7). A pending redemption (requested or approved) in either profile blocks the merge (D5).
        var (loyalty, walletFingerprint) = await _wallet.BuildMergePreviewAsync(source.Id, target.Id, cancellationToken);
        var (sourcePending, targetPending, redemptionsFingerprint) = await new LoyaltyRedemptionService(_dbContext, _timeProvider)
            .PendingForMergeAsync(source.Id, target.Id, cancellationToken);
        if (sourcePending > 0)
        {
            blockingReasons.Add(
                $"El cliente origen tiene {sourcePending} canje(s) de puntos pendiente(s) de aprobación o de entrega. Resuélvelos antes de fusionar.");
        }

        if (targetPending > 0)
        {
            blockingReasons.Add(
                $"El cliente destino tiene {targetPending} canje(s) de puntos pendiente(s) de aprobación o de entrega. Resuélvelos antes de fusionar.");
        }
        if (loyalty.SourceExpiredPointsNotMoved > 0)
        {
            warnings.Add($"{loyalty.SourceExpiredPointsNotMoved} punto(s) vencido(s) del cliente origen no se traspasan.");
        }

        // Everything the admin saw: both profiles (versions, links, merge state), the source's memberships and points.
        var sourceMemberships = await _dbContext.CustomerMemberships.AsNoTracking()
            .Where(membership => membership.CustomerId == source.Id)
            .OrderBy(membership => membership.Id)
            .Select(membership => membership.Id)
            .ToListAsync(cancellationToken);
        var fingerprint = string.Join(
            '|',
            $"T{target.Id}:{target.Version}:{target.UserId}:{target.MergedIntoCustomerId}",
            $"S{source.Id}:{source.Version}:{source.UserId}:{source.MergedIntoCustomerId}",
            $"M{string.Join(',', sourceMemberships)}",
            walletFingerprint,
            redemptionsFingerprint);

        return new CustomerMergePreview(
            MapListItem(target, users),
            sourceItem,
            blockingReasons.Count == 0,
            blockingReasons,
            warnings,
            transfersAccount ? sourceItem.LinkedAccount : null,
            loyalty,
            LoyaltyWalletService.HashToken(fingerprint));
    }

    private static void EnsureIdentityVerified(bool identityVerified)
    {
        if (!identityVerified)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["identityVerified"] = ["Debes confirmar que verificaste la identidad del cliente en persona."]
            });
        }
    }

    /// <summary>
    /// An account can back a profile only if it is active, has the Customer role, no live profile uses it and no
    /// claim of it waits for review (other than the one being approved). Takes the account lock first, so the answer
    /// holds until the caller's transaction ends.
    /// </summary>
    private async Task<User> LoadLinkableUserAsync(Guid userId, CancellationToken cancellationToken, Guid? approvingClaimId = null)
    {
        await LockAccountAsync(userId, cancellationToken);
        var user = await _dbContext.Users
            .Include(candidate => candidate.UserRoles)
            .ThenInclude(assignment => assignment.Role)
            .SingleOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken)
            ?? throw new NotFoundException("La cuenta no existe.");

        if (!user.IsActive)
        {
            throw new ConflictException("La cuenta está inactiva.");
        }

        if (user.UserRoles.All(assignment => assignment.Role.NormalizedName != RoleNames.Customer.ToUpperInvariant()))
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["userId"] = ["Solo se pueden vincular cuentas con rol de cliente."]
            });
        }

        var alreadyLinked = await _dbContext.Customers.AnyAsync(
            candidate => candidate.UserId == user.Id && candidate.MergedIntoCustomerId == null,
            cancellationToken);
        if (alreadyLinked)
        {
            throw new ConflictException(AccountAlreadyLinkedMessage);
        }

        if (await HasPendingClaimAsync(user.Id, approvingClaimId, cancellationToken))
        {
            throw new ConflictException(AccountHasPendingClaimMessage);
        }

        return user;
    }

    private async Task<CustomerProfile> LoadAsync(Guid customerId, CancellationToken cancellationToken)
    {
        return await _dbContext.Customers.SingleOrDefaultAsync(customer => customer.Id == customerId, cancellationToken)
            ?? throw new NotFoundException("El cliente no existe.");
    }

    private async Task<CustomerProfile> LoadLiveAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var customer = await LoadAsync(customerId, cancellationToken);
        if (customer.IsMerged)
        {
            throw new ConflictException("El cliente fue fusionado en otro perfil y ya no se puede modificar.");
        }

        return customer;
    }

    private async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(ConcurrencyConflictMessage);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConflictException(AccountAlreadyLinkedMessage);
        }
    }

    private async Task<CustomerDetailView> MapDetailAsync(CustomerProfile customer, CancellationToken cancellationToken)
    {
        // Newest first. Events written by one operation share a timestamp (e.g. "created" and "account
        // linked" when creating from an account); they are tie-broken by lifecycle step, which the enum
        // values follow. Sorted in memory because the type is stored as text.
        var events = (await _dbContext.CustomerEvents
                .Where(customerEvent => customerEvent.CustomerId == customer.Id)
                .ToListAsync(cancellationToken))
            .OrderByDescending(customerEvent => customerEvent.PerformedAt)
            .ThenByDescending(customerEvent => customerEvent.Type)
            .ThenByDescending(customerEvent => customerEvent.Id)
            .ToList();

        var userIds = LinkUserIds(customer)
            .Concat(events.Select(customerEvent => customerEvent.PerformedByUserId))
            .Concat(events.Where(customerEvent => customerEvent.UserId is not null).Select(customerEvent => customerEvent.UserId!.Value));
        var users = await LoadUsersAsync(userIds, cancellationToken);

        var relatedIds = events
            .Where(customerEvent => customerEvent.RelatedCustomerId is not null)
            .Select(customerEvent => customerEvent.RelatedCustomerId!.Value)
            .Append(customer.MergedIntoCustomerId ?? Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        var relatedNames = relatedIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await _dbContext.Customers
                .Where(related => relatedIds.Contains(related.Id))
                .ToDictionaryAsync(related => related.Id, related => related.FullName, cancellationToken);

        var eventViews = events.Select(customerEvent => new CustomerEventView(
            customerEvent.Id,
            customerEvent.Type.ToString(),
            customerEvent.PerformedAt,
            users.TryGetValue(customerEvent.PerformedByUserId, out var performer) ? performer.FullName : string.Empty,
            customerEvent.UserId,
            customerEvent.UserId is { } eventUserId && users.TryGetValue(eventUserId, out var eventUser) ? eventUser.Email : null,
            customerEvent.RelatedCustomerId,
            customerEvent.RelatedCustomerId is { } relatedId && relatedNames.TryGetValue(relatedId, out var relatedName) ? relatedName : null,
            customerEvent.Reason)).ToArray();

        return new CustomerDetailView(
            customer.Id,
            customer.FullName,
            customer.Phone,
            customer.Email,
            MapLinkedAccount(customer, users),
            customer.MergedIntoCustomerId,
            customer.MergedIntoCustomerId is { } mergedIntoId && relatedNames.TryGetValue(mergedIntoId, out var mergedIntoName) ? mergedIntoName : null,
            customer.MergedAt,
            customer.CreatedAt,
            customer.UpdatedAt,
            eventViews);
    }

    private async Task<Dictionary<Guid, User>> LoadUsersAsync(IEnumerable<Guid> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        return await _dbContext.Users
            .Where(user => ids.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, cancellationToken);
    }

    private static IEnumerable<Guid> LinkUserIds(CustomerProfile customer)
    {
        if (customer.UserId is { } userId)
        {
            yield return userId;
        }

        if (customer.LinkedByUserId is { } linkedById)
        {
            yield return linkedById;
        }
    }

    private static CustomerListItem MapListItem(CustomerProfile customer, IReadOnlyDictionary<Guid, User> users)
        => new(customer.Id, customer.FullName, customer.Phone, customer.Email, MapLinkedAccount(customer, users), customer.CreatedAt);

    private static CustomerLinkedAccountView? MapLinkedAccount(CustomerProfile customer, IReadOnlyDictionary<Guid, User> users)
    {
        if (customer.UserId is not { } userId || !users.TryGetValue(userId, out var user))
        {
            return null;
        }

        var linkedByName = customer.LinkedByUserId is { } linkedById && users.TryGetValue(linkedById, out var linkedBy)
            ? linkedBy.FullName
            : null;

        return new CustomerLinkedAccountView(user.Id, user.FullName, user.Email, user.PhoneNumber, customer.LinkedAt ?? customer.CreatedAt, linkedByName);
    }

    private static IReadOnlyList<string> MatchedOn(bool email, bool phone)
    {
        var matches = new List<string>(2);
        if (email)
        {
            matches.Add("email");
        }

        if (phone)
        {
            matches.Add("phone");
        }

        return matches;
    }

    /// <summary>Same contact rules everywhere a profile's data is entered (also claims).</summary>
    internal static void ValidateDetails(string? fullName, string? phone, string? email)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(fullName) || fullName.Trim().Length is < 2 or > 120)
        {
            errors["fullName"] = ["El nombre debe tener entre 2 y 120 caracteres."];
        }

        if (string.IsNullOrWhiteSpace(phone) || phone.Trim().Length > 40 || CustomerProfile.NormalizePhone(phone).Length < MinPhoneDigits)
        {
            errors["phone"] = [$"El teléfono es obligatorio y debe tener al menos {MinPhoneDigits} dígitos."];
        }

        if (string.IsNullOrWhiteSpace(email) || email.Trim().Length > 256 || !MailAddress.TryCreate(email.Trim(), out _))
        {
            errors["email"] = ["Ingresa un correo electrónico válido."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }
    }
}
