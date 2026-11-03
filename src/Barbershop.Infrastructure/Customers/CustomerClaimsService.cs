using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Customers.Admin;
using Barbershop.Application.Customers.Claims;
using Barbershop.Application.ServiceRecords;
using Barbershop.Domain.Customers;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Barbershop.Infrastructure.Customers;

/// <summary>
/// How a registered account gets its customer profile: a new one of its own, or its historical one through a claim
/// approved by someone with customers.claims.approve. Nothing is ever linked automatically by matching data, and the
/// customer never sees a profile that is not yet theirs. Approval links with the existing account-link rules
/// (<see cref="CustomerManagementService.LinkAccountAsync"/>) in the same transaction that decides the claim.
/// </summary>
internal sealed class CustomerClaimsService : IOwnCustomerProfileService, IAdminCustomerClaimsService
{
    private const int MaxListed = 200;
    private const int MaxCandidates = 20;
    private const int MinPhoneDigits = 7;

    private const string AlreadyDecidedMessage = "La solicitud ya fue revisada por otra persona. Recarga la bandeja.";
    private const string StaleMessage = "La solicitud cambió mientras la revisabas. Recarga e intenta de nuevo.";
    private const string PendingClaimMessage = "Ya tienes una solicitud pendiente. Espera la respuesta de la barbería.";

    private readonly AppDbContext _dbContext;
    private readonly CustomerManagementService _customers;
    private readonly TimeProvider _timeProvider;

    public CustomerClaimsService(AppDbContext dbContext, CustomerManagementService customers, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _customers = customers;
        _timeProvider = timeProvider;
    }

    // ── Customer ───────────────────────────────────────────────────────────────────────────────

    public async Task<OwnCustomerStatusView> GetStatusAsync(Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var user = await _dbContext.Users.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == currentUserId, cancellationToken)
            ?? throw new NotFoundException("La cuenta no existe.");
        var profile = await _customers.FindLiveLinkedAsync(currentUserId, cancellationToken);
        var claims = await _dbContext.CustomerClaims.AsNoTracking()
            .Where(claim => claim.UserId == currentUserId)
            .OrderByDescending(claim => claim.SubmittedAt)
            .ToListAsync(cancellationToken);

        var views = claims.Select(MapOwn).ToArray();
        return new OwnCustomerStatusView(
            profile is null ? null : MapOwnProfile(profile),
            views.FirstOrDefault(claim => claim.Status == nameof(CustomerClaimStatus.Pending)),
            views,
            user.FullName,
            user.Email,
            user.PhoneNumber);
    }

    public async Task<OwnCustomerProfileView> CreateOwnProfileAsync(
        Guid currentUserId, OwnCustomerProfileCreateRequest request, CancellationToken cancellationToken = default)
    {
        // Refuses while a claim is pending, under the same account lock as SubmitClaimAsync.
        var profile = await _customers.CreateOwnLinkedAsync(
            currentUserId, request.FullName?.Trim() ?? string.Empty, request.Phone?.Trim() ?? string.Empty,
            request.Email?.Trim() ?? string.Empty, cancellationToken);
        return MapOwnProfile(profile);
    }

    public async Task<OwnCustomerClaimView> SubmitClaimAsync(
        Guid currentUserId, CustomerClaimCreateRequest request, CancellationToken cancellationToken = default)
    {
        CustomerManagementService.ValidateDetails(request.FullName, request.Phone, request.Email);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.Notes is { Length: > CustomerClaim.MaxNotesLength })
        {
            errors["notes"] = [$"Las notas deben tener {CustomerClaim.MaxNotesLength} caracteres o menos."];
        }

        if (request.IdempotencyKey == Guid.Empty)
        {
            errors["idempotencyKey"] = ["Falta la clave de la operación; recarga la página e intenta de nuevo."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }

        // A retried request (same key) returns the claim already stored.
        if (await FindByKeyAsync(request.IdempotencyKey, cancellationToken) is { } previous)
        {
            return previous.UserId == currentUserId
                ? MapOwn(previous)
                : throw new ConflictException("Esta solicitud ya se registró. Recarga la página.");
        }

        CustomerClaim claim;
        try
        {
            // The account lock serializes this with own-profile creation and admin links of the same account: the
            // checks below hold until the claim is stored.
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            await _customers.LockAccountAsync(currentUserId, cancellationToken);
            if (await FindByKeyAsync(request.IdempotencyKey, cancellationToken) is { } concurrent)
            {
                // The same key stored while this request waited for the lock.
                return concurrent.UserId == currentUserId
                    ? MapOwn(concurrent)
                    : throw new ConflictException("Esta solicitud ya se registró. Recarga la página.");
            }

            await EnsureCanClaimAsync(currentUserId, cancellationToken);

            claim = new CustomerClaim(
                currentUserId, request.IdempotencyKey, request.FullName.Trim(), request.Phone.Trim(), request.Email.Trim(),
                string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(), _timeProvider.GetUtcNow().UtcDateTime);
            _dbContext.CustomerClaims.Add(claim);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Backstop of the unique indexes (the transaction is already rolled back here): the same key stored by
            // the same user is returned; anything else is another pending claim.
            _dbContext.ChangeTracker.Clear();
            if (await FindByKeyAsync(request.IdempotencyKey, cancellationToken) is { } stored && stored.UserId == currentUserId)
            {
                return MapOwn(stored);
            }

            throw new ConflictException(PendingClaimMessage);
        }

        return MapOwn(claim);
    }

    /// <summary>Same account rules as linking: active, Customer role, no live profile yet, no other claim waiting.</summary>
    private async Task EnsureCanClaimAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await _dbContext.Users.AsNoTracking()
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
            throw new UnauthorizedAccessException("Solo las cuentas de cliente pueden reclamar un perfil.");
        }

        if (await _customers.FindLiveLinkedAsync(userId, cancellationToken) is not null)
        {
            throw new ConflictException("Tu cuenta ya tiene un perfil de cliente.");
        }

        if (await _customers.HasPendingClaimAsync(userId, null, cancellationToken))
        {
            throw new ConflictException(PendingClaimMessage);
        }
    }

    private Task<CustomerClaim?> FindByKeyAsync(Guid key, CancellationToken cancellationToken)
        => _dbContext.CustomerClaims.AsNoTracking().SingleOrDefaultAsync(claim => claim.IdempotencyKey == key, cancellationToken);

    // ── Administrator ──────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<CustomerClaimView>> ListAsync(string? status, CancellationToken cancellationToken = default)
    {
        var statusFilter = status is null ? CustomerClaimStatus.Pending : ParseStatus(status);
        var query = _dbContext.CustomerClaims.AsNoTracking().Where(claim => claim.Status == statusFilter);
        query = statusFilter == CustomerClaimStatus.Pending
            ? query.OrderBy(claim => claim.SubmittedAt)
            : query.OrderByDescending(claim => claim.DecidedAt);
        return await MapAsync(await query.Take(MaxListed).ToListAsync(cancellationToken), cancellationToken);
    }

    public async Task<CustomerClaimView> GetByIdAsync(Guid claimId, CancellationToken cancellationToken = default)
    {
        var claim = await _dbContext.CustomerClaims.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == claimId, cancellationToken)
            ?? throw new NotFoundException("La solicitud no existe.");
        return (await MapAsync([claim], cancellationToken))[0];
    }

    public async Task<IReadOnlyList<CustomerClaimCandidate>> GetCandidatesAsync(
        Guid claimId, string? query, CancellationToken cancellationToken = default)
    {
        var claim = await _dbContext.CustomerClaims.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == claimId, cancellationToken)
            ?? throw new NotFoundException("La solicitud no existe.");

        var live = _dbContext.Customers.AsNoTracking().Where(customer => customer.MergedIntoCustomerId == null);
        var term = query?.Trim();
        IQueryable<CustomerProfile> matches;
        if (!string.IsNullOrEmpty(term))
        {
            var upper = term.ToUpperInvariant();
            var digits = CustomerProfile.NormalizePhone(term);
            matches = live.Where(customer =>
                customer.FullName.ToUpper().Contains(upper)
                || customer.NormalizedEmail.Contains(upper)
                || (digits.Length >= 4 && customer.NormalizedPhone.Contains(digits)));
        }
        else
        {
            // Suggestions from the data the customer sent; the reviewer still chooses and verifies in person.
            var phoneKey = PhoneKey(claim.NormalizedPhone);
            matches = live.Where(customer =>
                customer.NormalizedEmail == claim.NormalizedEmail
                || (phoneKey.Length >= MinPhoneDigits && customer.NormalizedPhone.EndsWith(phoneKey)));
        }

        var found = await matches.OrderBy(customer => customer.FullName).Take(MaxCandidates).ToListAsync(cancellationToken);
        var ids = found.Select(customer => customer.Id).ToArray();
        var membershipCounts = await _dbContext.CustomerMemberships.AsNoTracking()
            .Where(membership => ids.Contains(membership.CustomerId))
            .GroupBy(membership => membership.CustomerId)
            .Select(group => new { CustomerId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.CustomerId, item => item.Count, cancellationToken);
        var claimPhoneKey = PhoneKey(claim.NormalizedPhone);

        return found
            .Select(customer =>
            {
                var matchedOn = new List<string>();
                if (customer.NormalizedEmail == claim.NormalizedEmail)
                {
                    matchedOn.Add("email");
                }

                if (claimPhoneKey.Length >= MinPhoneDigits && customer.NormalizedPhone.EndsWith(claimPhoneKey, StringComparison.Ordinal))
                {
                    matchedOn.Add("phone");
                }

                return new CustomerClaimCandidate(
                    customer.Id,
                    customer.FullName,
                    ServiceRecordCustomerPrivacy.PhoneHint(customer.Phone),
                    ServiceRecordCustomerPrivacy.EmailHint(customer.Email),
                    customer.CreatedAt,
                    membershipCounts.GetValueOrDefault(customer.Id),
                    customer.UserId is not null,
                    matchedOn);
            })
            .ToArray();
    }

    public async Task<CustomerClaimView> ApproveAsync(
        Guid currentUserId, Guid claimId, CustomerClaimApproveRequest request, CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.Version is null)
        {
            errors["version"] = ["Falta la versión de la solicitud. Recarga la bandeja e intenta de nuevo."];
        }

        if (request.CustomerId == Guid.Empty)
        {
            errors["customerId"] = ["Elige el perfil del cliente."];
        }

        if (request.Notes is { Length: > CustomerClaim.MaxReasonLength })
        {
            errors["notes"] = [$"Las notas deben tener {CustomerClaim.MaxReasonLength} caracteres o menos."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }

        try
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            var claim = await LoadLockedAsync(claimId, cancellationToken);
            if (claim.Status != CustomerClaimStatus.Pending)
            {
                // A retried approval of the same profile returns the stored decision.
                if (claim.Status == CustomerClaimStatus.Approved && claim.ApprovedCustomerId == request.CustomerId)
                {
                    return await GetByIdAsync(claim.Id, cancellationToken);
                }

                throw new ConflictException(AlreadyDecidedMessage);
            }

            if (claim.Version != request.Version!.Value)
            {
                throw new ConflictException(StaleMessage);
            }

            // The existing link rules: identity verified in person, profile live and without an account, account active
            // with the Customer role and no other live profile, under the account lock. The profile's version stops a
            // concurrent link of it.
            await _customers.LinkAccountAsync(
                currentUserId, request.CustomerId, new CustomerLinkAccountRequest(claim.UserId, request.IdentityVerified),
                approvingClaimId: claim.Id, cancellationToken);

            claim.Approve(request.CustomerId, currentUserId, request.Notes?.Trim(), _timeProvider.GetUtcNow().UtcDateTime);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(AlreadyDecidedMessage);
        }

        return await GetByIdAsync(claimId, cancellationToken);
    }

    public async Task<CustomerClaimView> RejectAsync(
        Guid currentUserId, Guid claimId, CustomerClaimRejectRequest request, CancellationToken cancellationToken = default)
    {
        var reason = request.Reason?.Trim() ?? string.Empty;
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (reason.Length is < CustomerClaim.MinReasonLength or > CustomerClaim.MaxReasonLength)
        {
            errors["reason"] = [$"El motivo debe tener entre {CustomerClaim.MinReasonLength} y {CustomerClaim.MaxReasonLength} caracteres."];
        }

        if (request.Version is null)
        {
            errors["version"] = ["Falta la versión de la solicitud. Recarga la bandeja e intenta de nuevo."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }

        try
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            var claim = await LoadLockedAsync(claimId, cancellationToken);
            if (claim.Status != CustomerClaimStatus.Pending)
            {
                if (claim.Status == CustomerClaimStatus.Rejected && claim.DecisionNotes == reason)
                {
                    return await GetByIdAsync(claim.Id, cancellationToken);
                }

                throw new ConflictException(AlreadyDecidedMessage);
            }

            if (claim.Version != request.Version!.Value)
            {
                throw new ConflictException(StaleMessage);
            }

            claim.Reject(reason, currentUserId, _timeProvider.GetUtcNow().UtcDateTime);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(AlreadyDecidedMessage);
        }

        return await GetByIdAsync(claimId, cancellationToken);
    }

    private async Task<CustomerClaim> LoadLockedAsync(Guid claimId, CancellationToken cancellationToken)
    {
        if (_dbContext.Database.IsNpgsql())
        {
            await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM public.customer_claims WHERE \"Id\" = {claimId} FOR UPDATE", cancellationToken);
        }

        return await _dbContext.CustomerClaims.SingleOrDefaultAsync(claim => claim.Id == claimId, cancellationToken)
            ?? throw new NotFoundException("La solicitud no existe.");
    }

    private static CustomerClaimStatus ParseStatus(string status)
        => Enum.GetNames<CustomerClaimStatus>().FirstOrDefault(name => string.Equals(name, status.Trim(), StringComparison.OrdinalIgnoreCase)) is { } name
            ? Enum.Parse<CustomerClaimStatus>(name)
            : throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["status"] = ["El estado debe ser Pending, Approved o Rejected."]
            });

    /// <summary>The local number (last 10 digits), so "+57 300 123 4567" and "300 123 4567" compare equal.</summary>
    private static string PhoneKey(string normalizedPhone)
        => normalizedPhone.Length > 10 ? normalizedPhone[^10..] : normalizedPhone;

    // ── Mapping ────────────────────────────────────────────────────────────────────────────────

    private static OwnCustomerProfileView MapOwnProfile(CustomerProfile profile)
        => new(profile.Id, profile.FullName, profile.Phone, profile.Email, profile.LinkedAt ?? profile.CreatedAt);

    private static OwnCustomerClaimView MapOwn(CustomerClaim claim)
        => new(
            claim.Id,
            claim.Status.ToString(),
            claim.FullName,
            claim.Phone,
            claim.Email,
            claim.Notes,
            claim.SubmittedAt,
            claim.DecidedAt,
            claim.Status == CustomerClaimStatus.Rejected ? claim.DecisionNotes : null);

    private async Task<IReadOnlyList<CustomerClaimView>> MapAsync(IReadOnlyList<CustomerClaim> claims, CancellationToken cancellationToken)
    {
        if (claims.Count == 0)
        {
            return [];
        }

        var userIds = claims.Select(claim => claim.UserId)
            .Concat(claims.Where(claim => claim.DecidedByUserId is not null).Select(claim => claim.DecidedByUserId!.Value))
            .Distinct().ToArray();
        var users = await _dbContext.Users.AsNoTracking()
            .Where(user => userIds.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, cancellationToken);
        var customerIds = claims.Where(claim => claim.ApprovedCustomerId is not null).Select(claim => claim.ApprovedCustomerId!.Value).ToArray();
        var customerNames = await _dbContext.Customers.AsNoTracking()
            .Where(customer => customerIds.Contains(customer.Id))
            .ToDictionaryAsync(customer => customer.Id, customer => customer.FullName, cancellationToken);

        return claims.Select(claim =>
        {
            users.TryGetValue(claim.UserId, out var account);
            return new CustomerClaimView(
                claim.Id,
                claim.Status.ToString(),
                claim.UserId,
                account?.FullName ?? string.Empty,
                account?.Email ?? string.Empty,
                account?.IsActive ?? false,
                claim.FullName,
                claim.Phone,
                claim.Email,
                claim.Notes,
                claim.SubmittedAt,
                claim.ApprovedCustomerId,
                claim.ApprovedCustomerId is { } customerId ? customerNames.GetValueOrDefault(customerId) : null,
                claim.DecidedAt,
                claim.DecidedByUserId is { } decidedBy && users.TryGetValue(decidedBy, out var reviewer) ? reviewer.FullName : null,
                claim.DecisionNotes,
                claim.Version);
        }).ToArray();
    }
}
