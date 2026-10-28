using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Domain.Memberships;
using Barbershop.Domain.Services;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Barbershop.Infrastructure.Memberships;

internal sealed class MembershipPlanManagementService : IAdminMembershipPlansService
{
    private const int MaxServicesPerPlan = 50;
    private const int MaxGiftRulesPerPlan = 20;

    private const string DuplicateNameMessage = "Ya existe un tipo de membresía con ese nombre.";
    private const string ConcurrencyConflictMessage = "El tipo de membresía fue modificado por otra operación. Recarga e intenta de nuevo.";

    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public MembershipPlanManagementService(AppDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<MembershipPlanView>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var plans = await _dbContext.MembershipPlans
            .Include(plan => plan.Services)
            .Include(plan => plan.GiftRules)
            .Where(plan => !plan.IsDeleted)
            .OrderBy(plan => plan.Name)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        var services = await LoadServicesAsync(plans.SelectMany(ReferencedServiceIds), cancellationToken);
        return plans.Select(plan => Map(plan, services)).ToArray();
    }

    public async Task<MembershipPlanView> GetByIdAsync(Guid planId, CancellationToken cancellationToken = default)
    {
        var plan = await LoadAsync(planId, cancellationToken);
        return await MapAsync(plan, cancellationToken);
    }

    public async Task<MembershipPlanView> CreateAsync(MembershipPlanUpsertRequest request, CancellationToken cancellationToken = default)
    {
        var valid = await ValidateAsync(request, existingPlan: null, cancellationToken);
        await EnsureNameIsUniqueAsync(request.Name, excludeId: null, cancellationToken);

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var plan = new MembershipPlan(
            request.Name, request.Description, request.Price, valid.Mode, valid.PoolQuantity,
            request.DefaultDurationDays, request.Terms, request.SpecificConditions, utcNow);

        foreach (var service in valid.Services)
        {
            plan.Services.Add(new MembershipPlanIncludedService(plan.Id, service.ServiceId, service.Quantity));
        }

        for (var position = 0; position < valid.GiftRules.Count; position++)
        {
            var rule = valid.GiftRules[position];
            plan.GiftRules.Add(new MembershipPlanGiftRule(
                plan.Id, rule.Kind, rule.ServiceId, rule.Description, rule.Quantity, rule.Condition, position, utcNow));
        }

        _dbContext.MembershipPlans.Add(plan);
        await SaveChangesAsync(cancellationToken);
        return await MapAsync(plan, cancellationToken);
    }

    public async Task<MembershipPlanView> UpdateAsync(Guid planId, MembershipPlanUpsertRequest request, CancellationToken cancellationToken = default)
    {
        var plan = await LoadAsync(planId, cancellationToken);
        var valid = await ValidateAsync(request, plan, cancellationToken);
        await EnsureNameIsUniqueAsync(request.Name, planId, cancellationToken);

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        plan.Update(
            request.Name, request.Description, request.Price, valid.Mode, valid.PoolQuantity,
            request.DefaultDurationDays, request.Terms, request.SpecificConditions, utcNow);

        SyncServices(plan, valid.Services);
        SyncGiftRules(plan, valid.GiftRules, utcNow);

        await SaveChangesAsync(cancellationToken);
        return await MapAsync(plan, cancellationToken);
    }

    public async Task<MembershipPlanView> UpdateStatusAsync(Guid planId, MembershipPlanStatusUpdateRequest request, CancellationToken cancellationToken = default)
    {
        var plan = await LoadAsync(planId, cancellationToken);
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        if (request.IsActive)
        {
            plan.Activate(utcNow);
        }
        else
        {
            plan.Deactivate(utcNow);
        }

        await SaveChangesAsync(cancellationToken);
        return await MapAsync(plan, cancellationToken);
    }

    public async Task SoftDeleteAsync(Guid planId, CancellationToken cancellationToken = default)
    {
        var plan = await LoadAsync(planId, cancellationToken);
        plan.MarkDeleted(_timeProvider.GetUtcNow().UtcDateTime);
        await SaveChangesAsync(cancellationToken);
    }

    /// <summary>Keeps rows for services that stay (updating their quantity), drops removed ones and adds new ones.</summary>
    private static void SyncServices(MembershipPlan plan, IReadOnlyList<ValidService> desired)
    {
        var desiredById = desired.ToDictionary(service => service.ServiceId);

        foreach (var current in plan.Services.ToList())
        {
            if (desiredById.TryGetValue(current.ServiceId, out var wanted))
            {
                current.SetQuantity(wanted.Quantity);
            }
            else
            {
                plan.Services.Remove(current);
            }
        }

        var currentIds = plan.Services.Select(service => service.ServiceId).ToHashSet();
        foreach (var service in desired.Where(service => !currentIds.Contains(service.ServiceId)))
        {
            plan.Services.Add(new MembershipPlanIncludedService(plan.Id, service.ServiceId, service.Quantity));
        }
    }

    /// <summary>
    /// Rules with an id are edited in place, rules without one are added, and live rules left out are
    /// soft-deleted (never removed) so gifts already granted from them keep a valid origin.
    /// </summary>
    private static void SyncGiftRules(MembershipPlan plan, IReadOnlyList<ValidGiftRule> desired, DateTime utcNow)
    {
        var keptIds = desired.Where(rule => rule.Id is not null).Select(rule => rule.Id!.Value).ToHashSet();
        var liveRules = plan.GiftRules.Where(rule => !rule.IsDeleted).ToDictionary(rule => rule.Id);

        foreach (var rule in liveRules.Values.Where(rule => !keptIds.Contains(rule.Id)))
        {
            rule.MarkDeleted(utcNow);
        }

        for (var position = 0; position < desired.Count; position++)
        {
            var rule = desired[position];
            if (rule.Id is { } ruleId)
            {
                liveRules[ruleId].Update(rule.Kind, rule.ServiceId, rule.Description, rule.Quantity, rule.Condition, position, utcNow);
            }
            else
            {
                plan.GiftRules.Add(new MembershipPlanGiftRule(
                    plan.Id, rule.Kind, rule.ServiceId, rule.Description, rule.Quantity, rule.Condition, position, utcNow));
            }
        }
    }

    private async Task<ValidPlan> ValidateAsync(
        MembershipPlanUpsertRequest request,
        MembershipPlan? existingPlan,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length is < 2 or > 120)
        {
            errors["name"] = ["El nombre debe tener entre 2 y 120 caracteres."];
        }

        if (request.Description is { Length: > 1000 })
        {
            errors["description"] = ["La descripción debe tener 1000 caracteres o menos."];
        }

        if (request.Price < 0)
        {
            errors["price"] = ["El precio debe ser cero o mayor."];
        }

        if (request.DefaultDurationDays is < 1 or > MembershipPlan.MaxDurationDays)
        {
            errors["defaultDurationDays"] = [$"La duración debe estar entre 1 y {MembershipPlan.MaxDurationDays} días."];
        }

        if (string.IsNullOrWhiteSpace(request.Terms) || request.Terms.Trim().Length > 4000)
        {
            errors["terms"] = ["Los términos y condiciones son obligatorios (máximo 4000 caracteres)."];
        }

        if (request.SpecificConditions is { Length: > 2000 })
        {
            errors["specificConditions"] = ["Las condiciones particulares deben tener 2000 caracteres o menos."];
        }

        var mode = ParseName<MembershipPlanMode>(request.Mode);
        if (mode is null)
        {
            errors["mode"] = ["Elige una modalidad válida: bolsa común o cantidades por servicio."];
        }

        int? poolQuantity = null;
        if (mode == MembershipPlanMode.Pool)
        {
            if (request.PoolQuantity is not { } pool || pool is < 1 or > MembershipPlan.MaxQuantity)
            {
                errors["poolQuantity"] = [$"En modalidad bolsa común indica la cantidad total de servicios (entre 1 y {MembershipPlan.MaxQuantity})."];
            }
            else
            {
                poolQuantity = pool;
            }
        }
        else if (mode == MembershipPlanMode.PerService && request.PoolQuantity is not null)
        {
            errors["poolQuantity"] = ["En modalidad por servicio la cantidad se define en cada servicio, no como total."];
        }

        var services = await ValidateServicesAsync(request.Services ?? [], mode, existingPlan, errors, cancellationToken);
        var giftRules = ValidateGiftRules(request.GiftRules ?? [], services, existingPlan, errors);

        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }

        return new ValidPlan(mode!.Value, poolQuantity, services, giftRules);
    }

    private async Task<IReadOnlyList<ValidService>> ValidateServicesAsync(
        IReadOnlyList<MembershipPlanServiceRequest> requested,
        MembershipPlanMode? mode,
        MembershipPlan? existingPlan,
        Dictionary<string, string[]> errors,
        CancellationToken cancellationToken)
    {
        if (requested.Count == 0)
        {
            errors["services"] = ["Incluye al menos un servicio."];
            return [];
        }

        if (requested.Count > MaxServicesPerPlan)
        {
            errors["services"] = [$"Un tipo de membresía admite como máximo {MaxServicesPerPlan} servicios."];
            return [];
        }

        if (requested.Select(service => service.ServiceId).Distinct().Count() != requested.Count)
        {
            errors["services"] = ["No repitas un servicio dentro del mismo tipo de membresía."];
        }

        var ids = requested.Select(service => service.ServiceId).Distinct().ToArray();
        var catalog = await _dbContext.Services
            .Where(service => ids.Contains(service.Id))
            .ToDictionaryAsync(service => service.Id, cancellationToken);
        var alreadyIncluded = existingPlan?.Services.Select(service => service.ServiceId).ToHashSet() ?? [];

        var valid = new List<ValidService>(requested.Count);
        for (var index = 0; index < requested.Count; index++)
        {
            var item = requested[index];
            var key = $"services[{index}]";

            if (!catalog.TryGetValue(item.ServiceId, out var service) || service.IsDeleted)
            {
                errors[$"{key}.serviceId"] = ["El servicio no existe en el catálogo."];
                continue;
            }

            // A service deactivated after it was added can stay, but new additions must be active.
            if (!service.IsActive && !alreadyIncluded.Contains(service.Id))
            {
                errors[$"{key}.serviceId"] = [$"El servicio \"{service.Name}\" está inactivo."];
                continue;
            }

            if (mode == MembershipPlanMode.Pool && item.Quantity is not null)
            {
                errors[$"{key}.quantity"] = ["En modalidad bolsa común los servicios no llevan cantidad propia."];
                continue;
            }

            if (mode == MembershipPlanMode.PerService && item.Quantity is not (>= 1 and <= MembershipPlan.MaxQuantity))
            {
                errors[$"{key}.quantity"] = [$"Indica una cantidad entre 1 y {MembershipPlan.MaxQuantity} para \"{service.Name}\"."];
                continue;
            }

            valid.Add(new ValidService(service.Id, item.Quantity));
        }

        return valid;
    }

    private static IReadOnlyList<ValidGiftRule> ValidateGiftRules(
        IReadOnlyList<MembershipPlanGiftRuleRequest> requested,
        IReadOnlyList<ValidService> planServices,
        MembershipPlan? existingPlan,
        Dictionary<string, string[]> errors)
    {
        if (requested.Count > MaxGiftRulesPerPlan)
        {
            errors["giftRules"] = [$"Un tipo de membresía admite como máximo {MaxGiftRulesPerPlan} obsequios."];
            return [];
        }

        var includedServiceIds = planServices.Select(service => service.ServiceId).ToHashSet();
        var liveRuleIds = existingPlan?.GiftRules.Where(rule => !rule.IsDeleted).Select(rule => rule.Id).ToHashSet() ?? [];
        var seenRuleIds = new HashSet<Guid>();
        var valid = new List<ValidGiftRule>(requested.Count);

        for (var index = 0; index < requested.Count; index++)
        {
            var item = requested[index];
            var key = $"giftRules[{index}]";
            var before = errors.Count;

            if (item.Id is { } ruleId && (!liveRuleIds.Contains(ruleId) || !seenRuleIds.Add(ruleId)))
            {
                errors[$"{key}.id"] = ["El obsequio no pertenece a este tipo de membresía."];
            }

            var kind = ParseName<MembershipGiftKind>(item.Kind);
            if (kind is null)
            {
                errors[$"{key}.kind"] = ["Elige si el obsequio es un servicio o un producto físico."];
            }

            var condition = ParseName<MembershipGiftCondition>(item.Condition);
            if (condition is null)
            {
                errors[$"{key}.condition"] = ["Elige cuándo se entrega: en cada compra o solo en la primera compra de este tipo."];
            }

            if (item.Quantity is < 1 or > MembershipPlan.MaxQuantity)
            {
                errors[$"{key}.quantity"] = [$"La cantidad del obsequio debe estar entre 1 y {MembershipPlan.MaxQuantity}."];
            }

            if (kind == MembershipGiftKind.Service)
            {
                if (item.ServiceId is not { } serviceId || !includedServiceIds.Contains(serviceId))
                {
                    errors[$"{key}.serviceId"] = ["Solo se pueden obsequiar servicios incluidos en este tipo de membresía."];
                }

                if (!string.IsNullOrWhiteSpace(item.Description))
                {
                    errors[$"{key}.description"] = ["Un obsequio de servicio no lleva descripción."];
                }
            }
            else if (kind == MembershipGiftKind.Physical)
            {
                if (item.ServiceId is not null)
                {
                    errors[$"{key}.serviceId"] = ["Un obsequio físico no se asocia a un servicio."];
                }

                if (string.IsNullOrWhiteSpace(item.Description) || item.Description.Trim().Length is < 2 or > 200)
                {
                    errors[$"{key}.description"] = ["Describe el obsequio físico (entre 2 y 200 caracteres)."];
                }
            }

            if (errors.Count == before)
            {
                valid.Add(new ValidGiftRule(
                    item.Id,
                    kind!.Value,
                    kind == MembershipGiftKind.Service ? item.ServiceId : null,
                    kind == MembershipGiftKind.Physical ? item.Description!.Trim() : null,
                    item.Quantity,
                    condition!.Value));
            }
        }

        return valid;
    }

    /// <summary>Accepts only the enum's names (any casing), never numeric strings.</summary>
    private static TEnum? ParseName<TEnum>(string? value)
        where TEnum : struct, Enum
    {
        var name = Enum.GetNames<TEnum>().FirstOrDefault(candidate =>
            string.Equals(candidate, value?.Trim(), StringComparison.OrdinalIgnoreCase));
        return name is null ? null : Enum.Parse<TEnum>(name);
    }

    private async Task EnsureNameIsUniqueAsync(string name, Guid? excludeId, CancellationToken cancellationToken)
    {
        var normalized = name.Trim().ToUpperInvariant();
        var duplicateExists = await _dbContext.MembershipPlans.AnyAsync(
            plan => !plan.IsDeleted && plan.Id != excludeId && plan.Name.ToUpper() == normalized,
            cancellationToken);

        if (duplicateExists)
        {
            throw new ConflictException(DuplicateNameMessage);
        }
    }

    private async Task<MembershipPlan> LoadAsync(Guid planId, CancellationToken cancellationToken)
    {
        return await _dbContext.MembershipPlans
            .Include(plan => plan.Services)
            .Include(plan => plan.GiftRules)
            .AsSplitQuery()
            .SingleOrDefaultAsync(plan => plan.Id == planId && !plan.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("El tipo de membresía no existe.");
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
            // The only unique index besides primary keys is the live plan name.
            throw new ConflictException(DuplicateNameMessage);
        }
    }

    private async Task<MembershipPlanView> MapAsync(MembershipPlan plan, CancellationToken cancellationToken)
    {
        var services = await LoadServicesAsync(ReferencedServiceIds(plan), cancellationToken);
        return Map(plan, services);
    }

    /// <summary>Loads referenced services even if later deleted from the catalog, so their names still show.</summary>
    private async Task<Dictionary<Guid, Service>> LoadServicesAsync(IEnumerable<Guid> serviceIds, CancellationToken cancellationToken)
    {
        var ids = serviceIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        return await _dbContext.Services
            .Where(service => ids.Contains(service.Id))
            .ToDictionaryAsync(service => service.Id, cancellationToken);
    }

    private static IEnumerable<Guid> ReferencedServiceIds(MembershipPlan plan)
        => plan.Services.Select(service => service.ServiceId)
            .Concat(plan.GiftRules.Where(rule => rule.ServiceId is not null).Select(rule => rule.ServiceId!.Value));

    private static MembershipPlanView Map(MembershipPlan plan, IReadOnlyDictionary<Guid, Service> services)
    {
        var includedServices = plan.Services
            .Select(included =>
            {
                services.TryGetValue(included.ServiceId, out var service);
                return new MembershipPlanServiceView(
                    included.ServiceId,
                    service?.Name ?? string.Empty,
                    service?.BasePrice ?? 0,
                    service is { IsActive: true, IsDeleted: false },
                    included.Quantity);
            })
            .OrderBy(view => view.ServiceName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var giftRules = plan.GiftRules
            .Where(rule => !rule.IsDeleted)
            .OrderBy(rule => rule.Position)
            .Select(rule => new MembershipPlanGiftRuleView(
                rule.Id,
                rule.Kind.ToString(),
                rule.ServiceId,
                rule.ServiceId is { } serviceId && services.TryGetValue(serviceId, out var giftService) ? giftService.Name : null,
                rule.Description,
                rule.Quantity,
                rule.Condition.ToString()))
            .ToArray();

        return new MembershipPlanView(
            plan.Id,
            plan.Name,
            plan.Description,
            plan.Price,
            plan.Mode.ToString(),
            plan.PoolQuantity,
            plan.DefaultDurationDays,
            plan.Terms,
            plan.SpecificConditions,
            plan.IsActive,
            includedServices,
            giftRules,
            plan.CreatedAt,
            plan.UpdatedAt);
    }

    private sealed record ValidService(Guid ServiceId, int? Quantity);

    private sealed record ValidGiftRule(
        Guid? Id,
        MembershipGiftKind Kind,
        Guid? ServiceId,
        string? Description,
        int Quantity,
        MembershipGiftCondition Condition);

    private sealed record ValidPlan(
        MembershipPlanMode Mode,
        int? PoolQuantity,
        IReadOnlyList<ValidService> Services,
        IReadOnlyList<ValidGiftRule> GiftRules);
}
