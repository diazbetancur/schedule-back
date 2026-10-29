using System.Net.Mail;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Customers.Admin;
using Barbershop.Domain.Customers;
using Barbershop.Domain.Memberships;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Barbershop.Infrastructure.Customers;

internal sealed class CustomerManagementService : IAdminCustomersService
{
    private const int SearchLimit = 200;
    private const int CandidateLimit = 20;
    private const int MinPhoneDigits = 7;
    private const int PhoneKeyLength = 10;
    private const int ReasonMinLength = 3;
    private const int ReasonMaxLength = 500;

    private const string ConcurrencyConflictMessage = "El cliente fue modificado por otra operación. Recarga e intenta de nuevo.";
    private const string AccountAlreadyLinkedMessage = "Esta cuenta ya está vinculada a otro cliente.";

    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public CustomerManagementService(AppDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<CustomerListItem>> SearchAsync(string? query, CancellationToken cancellationToken = default)
    {
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

        var results = await customers
            .OrderBy(customer => customer.FullName)
            .Take(SearchLimit)
            .ToListAsync(cancellationToken);

        var users = await LoadUsersAsync(results.SelectMany(LinkUserIds), cancellationToken);
        return results.Select(customer => MapListItem(customer, users)).ToArray();
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
        var user = await LoadLinkableUserAsync(request.UserId, cancellationToken);

        // Name and email come from the account as they are today; the phone can be typed when the account has none.
        var phone = string.IsNullOrWhiteSpace(request.Phone) ? user.PhoneNumber : request.Phone;
        ValidateDetails(user.FullName, phone, user.Email);

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var customer = new CustomerProfile(user.FullName, phone!, user.Email, currentUserId, utcNow);
        customer.LinkAccount(user.Id, currentUserId, utcNow);

        _dbContext.Customers.Add(customer);
        _dbContext.CustomerEvents.Add(new CustomerEvent(customer.Id, CustomerEventType.Created, currentUserId, utcNow));
        _dbContext.CustomerEvents.Add(new CustomerEvent(customer.Id, CustomerEventType.AccountLinked, currentUserId, utcNow, userId: user.Id));

        // A single SaveChanges is one database transaction: the profile never exists without its link.
        await SaveChangesAsync(cancellationToken);

        return await MapDetailAsync(customer, cancellationToken);
    }

    public async Task<CustomerDetailView> LinkAccountAsync(
        Guid currentUserId,
        Guid customerId,
        CustomerLinkAccountRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureIdentityVerified(request.IdentityVerified);

        var customer = await LoadLiveAsync(customerId, cancellationToken);
        if (customer.UserId is not null)
        {
            throw new ConflictException("El cliente ya tiene una cuenta vinculada. Desvincúlala antes de vincular otra.");
        }

        var user = await LoadLinkableUserAsync(request.UserId, cancellationToken);

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        customer.LinkAccount(user.Id, currentUserId, utcNow);
        _dbContext.CustomerEvents.Add(new CustomerEvent(customer.Id, CustomerEventType.AccountLinked, currentUserId, utcNow, userId: user.Id));
        await SaveChangesAsync(cancellationToken);

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
        var target = await LoadAsync(targetCustomerId, cancellationToken);
        var source = await LoadAsync(request.SourceCustomerId, cancellationToken);

        var preview = await BuildMergePreviewAsync(target, source, cancellationToken);
        if (!preview.CanMerge)
        {
            throw new ConflictException(preview.BlockingReasons[0]);
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        // Two saves inside one transaction: the source must leave the "live linked profiles" unique
        // index before the target takes over its account, and PostgreSQL checks that index per statement.
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

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

            var sourceMemberships = await _dbContext.CustomerMemberships.CountAsync(membership => membership.CustomerId == source.Id, cancellationToken);
            if (sourceMemberships > 0)
            {
                warnings.Add($"{sourceMemberships} membresía(s) del cliente origen pasarán al cliente destino, con sus saldos, obsequios e historial.");
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

        return new CustomerMergePreview(
            MapListItem(target, users),
            sourceItem,
            blockingReasons.Count == 0,
            blockingReasons,
            warnings,
            transfersAccount ? sourceItem.LinkedAccount : null);
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

    /// <summary>An account can back a profile only if it is active, has the Customer role and no live profile uses it.</summary>
    private async Task<User> LoadLinkableUserAsync(Guid userId, CancellationToken cancellationToken)
    {
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

    private static void ValidateDetails(string? fullName, string? phone, string? email)
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
