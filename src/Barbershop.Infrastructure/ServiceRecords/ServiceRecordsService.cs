using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Common.Paging;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Application.ServiceRecords;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Common;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Loyalty;
using Barbershop.Domain.Memberships;
using Barbershop.Domain.ServiceRecords;
using Barbershop.Domain.Staff;
using Barbershop.Infrastructure.Loyalty;
using Barbershop.Infrastructure.Memberships;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Barbershop.Infrastructure.ServiceRecords;

/// <summary>
/// Services performed: professionals register them (pending), administrators confirm or reject them. Confirming a paid
/// service creates exactly one service income; confirming a membership consumption goes through the membership ledger;
/// a courtesy creates neither. The confirmation and what it creates are saved in one transaction, with the record
/// locked, so retries and concurrent reviews never duplicate money or consumptions.
/// </summary>
internal sealed class ServiceRecordsService : IStaffServiceRecordsService, IAdminServiceRecordsService
{
    private const int MaxCustomerOptions = 20;

    /// <summary>How far back the professional's own appointments are offered for registration.</summary>
    private const int AppointmentWindowDays = 30;

    private const string AlreadyReviewedMessage = "Este servicio ya fue revisado por otra persona. Recarga la bandeja.";
    private const string StaleMessage = "El registro cambió mientras lo revisabas. Recarga e intenta de nuevo.";

    private readonly AppDbContext _dbContext;
    private readonly MembershipLedgerService _ledger;
    private readonly TimeProvider _timeProvider;
    private readonly LoyaltyWalletService _wallet;
    private readonly LoyaltyRedemptionService _redemptions;

    public ServiceRecordsService(AppDbContext dbContext, MembershipLedgerService ledger, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _ledger = ledger;
        _timeProvider = timeProvider;
        _wallet = new LoyaltyWalletService(dbContext, timeProvider);
        _redemptions = new LoyaltyRedemptionService(dbContext, timeProvider);
    }

    // ── Professional ────────────────────────────────────────────────────────────────────────────

    /// <summary>The active general services (ADR-0016 F4): the professional registers what they did, not a rate.</summary>
    public async Task<IReadOnlyList<ServiceRecordCatalogItem>> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        var services = await (
                from service in _dbContext.GeneralServices.AsNoTracking()
                join category in _dbContext.ServiceCategories.AsNoTracking() on service.CategoryId equals category.Id
                where service.IsActive && !service.IsDeleted && category.IsActive && !category.IsDeleted
                select new { service.Id, service.Name, service.SortOrder, CategoryId = category.Id, CategoryName = category.Name, CategorySortOrder = category.SortOrder })
            .ToListAsync(cancellationToken);
        return services
            .OrderBy(service => service.CategorySortOrder).ThenBy(service => service.CategoryName)
            .ThenBy(service => service.SortOrder).ThenBy(service => service.Name)
            .Select(service => new ServiceRecordCatalogItem(service.Id, service.Name, service.CategoryId, service.CategoryName))
            .ToArray();
    }

    public async Task<ServiceRecordEligibilityView> GetEligibilityAsync(Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var staff = await _dbContext.StaffProfiles.AsNoTracking()
            .SingleOrDefaultAsync(profile => profile.UserId == currentUserId && profile.IsActive, cancellationToken);
        return staff is null
            ? new ServiceRecordEligibilityView(false, null, null)
            : new ServiceRecordEligibilityView(true, staff.Id, staff.DisplayName);
    }

    public async Task<IReadOnlyList<ServiceRecordAppointmentOption>> GetAppointmentsAsync(
        Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var staff = await LoadOwnStaffProfileAsync(currentUserId, cancellationToken);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var earliest = BogotaClock.ToUtc(Today().AddDays(-AppointmentWindowDays), TimeOnly.MinValue);

        // Completed appointments included; their status is never changed here.
        var appointments = await _dbContext.Appointments.AsNoTracking()
            .Where(appointment => appointment.StaffProfileId == staff.Id
                && appointment.StartsAt <= now
                && appointment.StartsAt >= earliest
                && appointment.Status != AppointmentStatus.Cancelled
                && appointment.Status != AppointmentStatus.NoShow)
            .OrderByDescending(appointment => appointment.StartsAt)
            .ToListAsync(cancellationToken);

        var ids = appointments.Select(appointment => (Guid?)appointment.Id).ToArray();
        var registered = await RegisteredGeneralServicesQuery()
            .Where(item => ids.Contains(item.AppointmentId) && item.Status != ServiceRecordStatus.Rejected)
            .Select(item => new { AppointmentId = item.AppointmentId!.Value, item.GeneralServiceId })
            .ToListAsync(cancellationToken);

        return appointments
            .Select(appointment => new ServiceRecordAppointmentOption(
                appointment.Id,
                appointment.StartsAt,
                appointment.CustomerName,
                appointment.Status.ToString(),
                registered.Where(item => item.AppointmentId == appointment.Id && item.GeneralServiceId != null)
                    .Select(item => item.GeneralServiceId!.Value).Distinct().ToArray(),
                appointment.GeneralServiceId,
                appointment.GeneralServiceNameSnapshot))
            .ToArray();
    }

    public async Task<PagedResult<ServiceRecordView>> GetMineAsync(
        Guid currentUserId, PageQuery? query = null, CancellationToken cancellationToken = default)
    {
        var page = (query ?? new PageQuery()).ToRequest();
        var staff = await LoadOwnStaffProfileAsync(currentUserId, cancellationToken);
        // Pending first (oldest first, like the review queue), then the reviewed ones newest first; a total order.
        return await _dbContext.ServiceRecords.AsNoTracking()
            .Where(record => record.StaffProfileId == staff.Id)
            .OrderBy(record => record.Status == ServiceRecordStatus.Pending ? 0 : 1)
            .ThenBy(record => record.Status == ServiceRecordStatus.Pending ? record.RegisteredAt : DateTime.MinValue)
            .ThenByDescending(record => record.RegisteredAt)
            .ThenBy(record => record.Position)
            .ThenBy(record => record.Id)
            .ToPagedResultAsync(page, MapAsync, cancellationToken);
    }

    public async Task<IReadOnlyList<ServiceRecordView>> RegisterAsync(
        Guid currentUserId, ServiceRecordRegisterRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRegisterShape(request);
        var staff = await LoadOwnStaffProfileAsync(currentUserId, cancellationToken);

        // A retried request (same key) returns what was already registered instead of registering again.
        if (await FindRegistrationAsync(request.IdempotencyKey, cancellationToken) is { Count: > 0 } previous)
        {
            return await ReplayRegistrationAsync(previous, currentUserId, request, cancellationToken);
        }

        var serviceIds = request.GeneralServiceIds!;
        try
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            Appointment? appointment = null;
            if (request.AppointmentId is { } appointmentId)
            {
                // Serializes registrations of the same appointment, so the duplicate check below holds.
                await LockAsync($"SELECT 1 FROM public.appointments WHERE \"Id\" = {appointmentId} FOR UPDATE", cancellationToken);
                appointment = await _dbContext.Appointments.AsNoTracking()
                    .SingleOrDefaultAsync(candidate => candidate.Id == appointmentId && candidate.StaffProfileId == staff.Id, cancellationToken)
                    ?? throw new NotFoundException("La cita no existe o no es tuya.");
            }

            var (performedOn, customerId, customerName) = await ResolveContextAsync(request, appointment, cancellationToken);
            var services = await _dbContext.GeneralServices.AsNoTracking()
                .Where(service => serviceIds.Contains(service.Id) && service.IsActive && !service.IsDeleted)
                .ToDictionaryAsync(service => service.Id, cancellationToken);

            var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
            for (var index = 0; index < serviceIds.Count; index++)
            {
                if (!services.ContainsKey(serviceIds[index]))
                {
                    errors[$"generalServiceIds[{index}]"] = ["El servicio no existe en el catálogo o está inactivo."];
                }
            }

            if (errors.Count > 0)
            {
                throw new ValidationProblemException(errors);
            }

            if (appointment is not null)
            {
                // Older records of the appointment registered a rate: their general service is the rate's.
                var nullableIds = serviceIds.Select(id => (Guid?)id).ToArray();
                var alreadyRegistered = await RegisteredGeneralServicesQuery()
                    .Where(item => item.AppointmentId == appointment.Id
                        && item.Status != ServiceRecordStatus.Rejected
                        && nullableIds.Contains(item.GeneralServiceId))
                    .Select(item => item.ServiceNameSnapshot)
                    .FirstOrDefaultAsync(cancellationToken);
                if (alreadyRegistered is not null)
                {
                    throw new ConflictException($"El servicio \"{alreadyRegistered}\" ya está registrado en esta cita.");
                }
            }

            var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
            var records = serviceIds
                .Select((serviceId, position) => new ServiceRecord(
                    request.IdempotencyKey, position, appointment?.Id, staff.Id, null, services[serviceId].Name,
                    performedOn, customerId, customerName, request.Notes?.Trim(), currentUserId, utcNow, serviceId))
                .ToList();
            _dbContext.ServiceRecords.AddRange(records);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await MapAsync(records, cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent retry with the same key won; return what it stored.
            _dbContext.ChangeTracker.Clear();
            var stored = await FindRegistrationAsync(request.IdempotencyKey, cancellationToken);
            if (stored.Count == 0)
            {
                throw new ConflictException("El registro no se pudo guardar por una operación simultánea. Intenta de nuevo.");
            }

            return await ReplayRegistrationAsync(stored, currentUserId, request, cancellationToken);
        }
    }

    private async Task<(DateOnly PerformedOn, Guid? CustomerId, string? CustomerName)> ResolveContextAsync(
        ServiceRecordRegisterRequest request, Appointment? appointment, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var today = Today();

        if (appointment is null)
        {
            if (request.PerformedOn is not { } date)
            {
                throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["performedOn"] = ["Indica la fecha en que hiciste el servicio."]
                });
            }

            if (date > today)
            {
                errors["performedOn"] = ["La fecha del servicio no puede ser futura."];
            }

            if (errors.Count > 0)
            {
                throw new ValidationProblemException(errors);
            }

            var typedName = string.IsNullOrWhiteSpace(request.CustomerName) ? null : request.CustomerName.Trim();
            return (date, null, typedName);
        }

        // Completing or not the appointment is not touched here, and is not a payment confirmation either.
        if (appointment.Status is AppointmentStatus.Cancelled or AppointmentStatus.NoShow)
        {
            throw new ConflictException("La cita está cancelada o el cliente no asistió; no se pueden registrar servicios en ella.");
        }

        var appointmentDate = DateOnly.FromDateTime(BogotaClock.ToLocal(appointment.StartsAt));
        if (request.PerformedOn is { } requested && requested != appointmentDate)
        {
            errors["performedOn"] = ["Con una cita, la fecha del servicio es la de la cita."];
        }

        if (!string.IsNullOrWhiteSpace(request.CustomerName))
        {
            errors["customerName"] = ["Con una cita, el cliente es el de la cita."];
        }

        if (appointmentDate > today)
        {
            errors["appointmentId"] = ["La cita todavía no ha ocurrido."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }

        Guid? customerId = null;
        if (appointment.CustomerUserId is { } customerUserId)
        {
            customerId = await _dbContext.Customers.AsNoTracking()
                .Where(customer => customer.UserId == customerUserId && customer.MergedIntoCustomerId == null)
                .Select(customer => (Guid?)customer.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return (appointmentDate, customerId, appointment.CustomerName);
    }

    private async Task<IReadOnlyList<ServiceRecordView>> ReplayRegistrationAsync(
        IReadOnlyList<ServiceRecord> stored, Guid currentUserId, ServiceRecordRegisterRequest request, CancellationToken cancellationToken)
    {
        var sameRequest = stored.All(record => record.RegisteredByUserId == currentUserId && record.AppointmentId == request.AppointmentId)
            && stored.OrderBy(record => record.Position).Select(record => record.GeneralServiceId).SequenceEqual(
                request.GeneralServiceIds!.Select(id => (Guid?)id));
        if (!sameRequest)
        {
            throw new ConflictException("Este registro ya se guardó con otros datos. Recarga la página para registrar uno nuevo.");
        }

        return await MapAsync(stored, cancellationToken);
    }

    private async Task<List<ServiceRecord>> FindRegistrationAsync(Guid registrationKey, CancellationToken cancellationToken)
        => await _dbContext.ServiceRecords.AsNoTracking()
            .Where(record => record.RegistrationKey == registrationKey)
            .OrderBy(record => record.Position)
            .ToListAsync(cancellationToken);

    private async Task<StaffProfile> LoadOwnStaffProfileAsync(Guid currentUserId, CancellationToken cancellationToken)
    {
        // The permission alone is not enough: only an active professional registers, and always as themselves.
        return await _dbContext.StaffProfiles.AsNoTracking()
                .SingleOrDefaultAsync(staff => staff.UserId == currentUserId && staff.IsActive, cancellationToken)
            ?? throw new UnauthorizedAccessException("Tu usuario no tiene un perfil de profesional activo; no puedes registrar servicios.");
    }

    private static void ValidateRegisterShape(ServiceRecordRegisterRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var serviceIds = request.GeneralServiceIds ?? [];

        if (serviceIds.Count == 0)
        {
            errors["generalServiceIds"] = ["Elige al menos un servicio."];
        }
        else if (serviceIds.Count > ServiceRecord.MaxServicesPerRegistration)
        {
            errors["generalServiceIds"] = [$"Registra como máximo {ServiceRecord.MaxServicesPerRegistration} servicios a la vez."];
        }
        else if (serviceIds.Distinct().Count() != serviceIds.Count || serviceIds.Contains(Guid.Empty))
        {
            errors["generalServiceIds"] = ["No repitas un servicio en el mismo registro."];
        }

        if (request.CustomerName is { } name && !string.IsNullOrWhiteSpace(name) && name.Trim().Length is < 2 or > 120)
        {
            errors["customerName"] = ["El nombre del cliente debe tener entre 2 y 120 caracteres."];
        }

        if (request.Notes is { Length: > ServiceRecord.MaxNotesLength })
        {
            errors["notes"] = [$"Las notas deben tener {ServiceRecord.MaxNotesLength} caracteres o menos."];
        }

        if (request.IdempotencyKey == Guid.Empty)
        {
            errors["idempotencyKey"] = ["Falta la clave de la operación; recarga la página e intenta de nuevo."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }
    }

    // ── Administrator ───────────────────────────────────────────────────────────────────────────

    public async Task<PagedResult<ServiceRecordView>> ListAsync(ServiceRecordListQuery listQuery, CancellationToken cancellationToken = default)
    {
        var page = PageRequest.Create(listQuery.Page, listQuery.PageSize);
        var (status, staffProfileId) = (listQuery.Status, listQuery.StaffProfileId);
        var statusFilter = status is null ? ServiceRecordStatus.Pending : ParseName<ServiceRecordStatus>(status)
            ?? throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["status"] = ["El estado debe ser Pending, Approved o Rejected."]
            });

        // Pending is the review queue: no default date, so an old pending record is never hidden. Approved and
        // rejected are history: the last 30 days by default and at most 30 days per query.
        var range = statusFilter == ServiceRecordStatus.Pending
            ? HistoryRange.ResolveOptional(listQuery.From, listQuery.To)
            : HistoryRange.Resolve(listQuery.From, listQuery.To, Today());
        var (from, to) = (range?.From, range?.To);

        var query = _dbContext.ServiceRecords.AsNoTracking().Where(record => record.Status == statusFilter);
        if (from is { } fromDate)
        {
            query = query.Where(record => (record.ApprovedPerformedOn ?? record.PerformedOn) >= fromDate);
        }

        if (to is { } toDate)
        {
            query = query.Where(record => (record.ApprovedPerformedOn ?? record.PerformedOn) <= toDate);
        }

        if (staffProfileId is { } staffId)
        {
            query = query.Where(record => (record.ApprovedStaffProfileId ?? record.StaffProfileId) == staffId);
        }

        if (listQuery.GeneralServiceId is { } generalServiceId)
        {
            query = query.Where(record => (record.ApprovedGeneralServiceId ?? record.GeneralServiceId) == generalServiceId);
        }

        query = statusFilter == ServiceRecordStatus.Pending
            ? query.OrderBy(record => record.RegisteredAt).ThenBy(record => record.Position).ThenBy(record => record.Id)
            : query.OrderByDescending(record => record.ApprovedAt ?? record.RejectedAt).ThenBy(record => record.Position).ThenBy(record => record.Id);

        return await query.ToPagedResultAsync(page, MapAsync, cancellationToken);
    }

    public async Task<ServiceRecordView> GetByIdAsync(Guid serviceRecordId, CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.ServiceRecords.AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.Id == serviceRecordId, cancellationToken)
            ?? throw new NotFoundException("El servicio realizado no existe.");
        return (await MapAsync([record], cancellationToken))[0];
    }

    public async Task<ServiceRecordOptionsView> GetOptionsAsync(CancellationToken cancellationToken = default)
    {
        // Rates labelled "Servicio general · Tarifa"; the review picks the general service first, then one of its rates.
        var services = await (
                from rate in _dbContext.Services.AsNoTracking()
                join general in _dbContext.GeneralServices.AsNoTracking() on rate.GeneralServiceId equals general.Id
                where !rate.IsDeleted
                orderby general.Name, rate.Name
                select new ServiceRecordServiceOption(
                    rate.Id, rate.Name, rate.BasePrice, rate.LoyaltyPoints, rate.IsActive, general.Id, general.Name))
            .ToListAsync(cancellationToken);
        var generalServices = await (
                from general in _dbContext.GeneralServices.AsNoTracking()
                join category in _dbContext.ServiceCategories.AsNoTracking() on general.CategoryId equals category.Id
                where !general.IsDeleted
                orderby category.SortOrder, category.Name, general.SortOrder, general.Name
                select new ServiceRecordGeneralServiceOption(general.Id, general.Name, category.Name, general.IsActive))
            .ToListAsync(cancellationToken);
        var staff = await _dbContext.StaffProfiles.AsNoTracking()
            .OrderBy(profile => profile.DisplayName)
            .Select(profile => new ServiceRecordStaffOption(profile.Id, profile.DisplayName, profile.IsActive))
            .ToListAsync(cancellationToken);
        return new ServiceRecordOptionsView(services, staff, await IsLoyaltyEnabledAsync(cancellationToken), generalServices);
    }

    public async Task<ServiceRecordRelatedView> GetRelatedAsync(Guid serviceRecordId, CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.ServiceRecords.AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.Id == serviceRecordId, cancellationToken)
            ?? throw new NotFoundException("El servicio realizado no existe.");

        var staffId = record.ApprovedStaffProfileId ?? record.StaffProfileId;
        var date = record.ApprovedPerformedOn ?? record.PerformedOn;
        var customerId = record.ApprovedCustomerId ?? record.CustomerId;

        // The same service: the rate confirmed or registered (older records) or, for a pending record that only names a
        // general service, any rate of that general service (ADR-0016 B4).
        var rateIds = await RelatedRateIdsAsync(record, cancellationToken);
        var nullableRateIds = rateIds.Select(id => (Guid?)id).ToArray();
        var pendingGeneralServiceId = record.ApprovedServiceId is null && record.ServiceId is null ? record.GeneralServiceId : null;

        // Service income registered by hand (not by any record) for the same professional, service and day.
        var manualIncomes = await _dbContext.IncomeEntries.AsNoTracking()
            .Where(entry => !entry.IsDeleted
                && entry.Kind == IncomeKind.Service
                && nullableRateIds.Contains(entry.ServiceId)
                && entry.StaffProfileId == staffId
                && entry.OccurredOn == date
                && !_dbContext.ServiceRecords.Any(other => other.IncomeEntryId == entry.Id))
            .OrderBy(entry => entry.CreatedAt)
            .Select(entry => new ServiceRecordRelatedIncome(entry.Id, entry.Amount, entry.OccurredOn, entry.CreatedAt))
            .ToListAsync(cancellationToken);

        var otherRecords = await _dbContext.ServiceRecords.AsNoTracking()
            .Where(other => other.Id != record.Id
                && other.Status != ServiceRecordStatus.Rejected
                && (other.ApprovedStaffProfileId ?? other.StaffProfileId) == staffId
                && (nullableRateIds.Contains(other.ApprovedServiceId ?? other.ServiceId)
                    || (pendingGeneralServiceId != null && (other.ApprovedGeneralServiceId ?? other.GeneralServiceId) == pendingGeneralServiceId))
                && (other.ApprovedPerformedOn ?? other.PerformedOn) == date)
            .OrderBy(other => other.RegisteredAt)
            .Select(other => new ServiceRecordRelatedRecord(
                other.Id, other.Status.ToString(), other.ApprovedServiceNameSnapshot ?? other.ServiceNameSnapshot,
                other.ApprovedPerformedOn ?? other.PerformedOn, other.RegisteredAt))
            .ToListAsync(cancellationToken);

        // Membership consumptions of the same service and day, by the same professional or of the customer's memberships;
        // reversed ones are left out.
        var customerMembershipIds = customerId is { } owner
            ? await _dbContext.CustomerMemberships.AsNoTracking()
                .Where(membership => membership.CustomerId == owner)
                .Select(membership => membership.Id)
                .ToListAsync(cancellationToken)
            : [];
        var consumptions = await _dbContext.MembershipConsumptions.AsNoTracking()
            .Where(consumption => rateIds.Contains(consumption.ServiceId)
                && consumption.ServiceDate == date
                && (consumption.StaffProfileId == staffId || customerMembershipIds.Contains(consumption.CustomerMembershipId))
                && !_dbContext.MembershipConsumptionReversals.Any(reversal => reversal.MembershipConsumptionId == consumption.Id)
                && consumption.Id != record.MembershipConsumptionId)
            .OrderBy(consumption => consumption.RegisteredAt)
            .Select(consumption => new ServiceRecordRelatedConsumption(
                consumption.Id,
                consumption.CustomerMembershipId,
                consumption.ServiceNameSnapshot,
                consumption.ServiceDate,
                _dbContext.ServiceRecords.Any(other => other.MembershipConsumptionId == consumption.Id)))
            .ToListAsync(cancellationToken);

        string? appointmentStatus = null;
        if (record.AppointmentId is { } appointmentId)
        {
            appointmentStatus = await _dbContext.Appointments.AsNoTracking()
                .Where(appointment => appointment.Id == appointmentId)
                .Select(appointment => appointment.Status.ToString())
                .SingleOrDefaultAsync(cancellationToken);
        }

        return new ServiceRecordRelatedView(manualIncomes, otherRecords, consumptions, appointmentStatus);
    }

    public async Task<IReadOnlyList<ServiceRecordCustomerOption>> SearchCustomersAsync(string? query, CancellationToken cancellationToken = default)
    {
        var customers = _dbContext.Customers.AsNoTracking().Where(customer => customer.MergedIntoCustomerId == null);
        var term = query?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(term))
        {
            customers = customers.Where(customer =>
                customer.FullName.ToLower().Contains(term) || customer.Phone.Contains(term) || customer.Email.ToLower().Contains(term));
        }

        var found = await customers
            .OrderBy(customer => customer.FullName)
            .Take(MaxCustomerOptions)
            .Select(customer => new { customer.Id, customer.FullName, customer.Phone })
            .ToListAsync(cancellationToken);
        var ids = found.Select(customer => customer.Id).ToArray();
        var memberships = await _dbContext.CustomerMemberships.AsNoTracking()
            .Where(membership => ids.Contains(membership.CustomerId) && membership.Status == MembershipStatus.Active)
            .OrderBy(membership => membership.EndsOn)
            .ToListAsync(cancellationToken);

        return found
            .Select(customer => new ServiceRecordCustomerOption(
                customer.Id,
                customer.FullName,
                ServiceRecordCustomerPrivacy.PhoneHint(customer.Phone),
                memberships
                    .Where(membership => membership.CustomerId == customer.Id)
                    .Select(membership => new ServiceRecordMembershipOption(
                        membership.Id, membership.PlanName, membership.Status.ToString(), membership.StartsOn, membership.EndsOn))
                    .ToArray()))
            .ToArray();
    }

    public async Task<ServiceRecordView> ApproveAsync(
        Guid currentUserId, Guid serviceRecordId, ServiceRecordApproveRequest request, CancellationToken cancellationToken = default)
    {
        var operationType = ValidateApproveShape(request);

        try
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            var record = await LoadLockedAsync(serviceRecordId, cancellationToken);

            if (record.Status != ServiceRecordStatus.Pending)
            {
                // A retried confirmation with the same decision returns the stored one; anything else is a conflict.
                if (record.Status == ServiceRecordStatus.Approved && SameApproval(record, operationType, request))
                {
                    return await GetByIdAsync(record.Id, cancellationToken);
                }

                throw new ConflictException(AlreadyReviewedMessage);
            }

            if (record.Version != request.Version!.Value)
            {
                throw new ConflictException(StaleMessage);
            }

            var approval = await BuildApprovalAsync(currentUserId, record, operationType, request, cancellationToken);
            var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
            record.Approve(approval, currentUserId, utcNow);

            // A paid service marked to accrue credits its points in the same transaction (ADR-0014): the record id
            // is the source, so a retried approval never credits twice. Lock order: record → customer profile.
            if (approval.OperationType == ServiceRecordOperationType.Paid
                && approval.LoyaltyPointsSnapshot is > 0
                && approval.CustomerId is { } customerId)
            {
                await _wallet.AccrueInCurrentTransactionAsync(
                    customerId, LoyaltySourceType.ServiceRecord, record.Id, approval.LoyaltyPointsSnapshot.Value,
                    currentUserId, utcNow, cancellationToken);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(AlreadyReviewedMessage);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConflictException(AlreadyReviewedMessage);
        }

        return await GetByIdAsync(serviceRecordId, cancellationToken);
    }

    public async Task<ServiceRecordView> RejectAsync(
        Guid currentUserId, Guid serviceRecordId, ServiceRecordRejectRequest request, CancellationToken cancellationToken = default)
    {
        var reason = request.Reason?.Trim() ?? string.Empty;
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (reason.Length is < ServiceRecord.MinReasonLength or > ServiceRecord.MaxReasonLength)
        {
            errors["reason"] = [$"El motivo debe tener entre {ServiceRecord.MinReasonLength} y {ServiceRecord.MaxReasonLength} caracteres."];
        }

        if (request.Version is null)
        {
            errors["version"] = ["Falta la versión del registro. Recarga la bandeja e intenta de nuevo."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }

        try
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            var record = await LoadLockedAsync(serviceRecordId, cancellationToken);

            if (record.Status != ServiceRecordStatus.Pending)
            {
                if (record.Status == ServiceRecordStatus.Rejected && record.RejectionReason == reason)
                {
                    return await GetByIdAsync(record.Id, cancellationToken);
                }

                throw new ConflictException(AlreadyReviewedMessage);
            }

            if (record.Version != request.Version!.Value)
            {
                throw new ConflictException(StaleMessage);
            }

            record.Reject(reason, currentUserId, _timeProvider.GetUtcNow().UtcDateTime);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(AlreadyReviewedMessage);
        }

        return await GetByIdAsync(serviceRecordId, cancellationToken);
    }

    public async Task<IReadOnlyList<ServiceRecordRedemptionOption>> GetRedemptionOptionsAsync(
        Guid customerId, Guid? serviceId, CancellationToken cancellationToken = default)
        => await _redemptions.GetServiceRedemptionOptionsAsync(customerId, serviceId, cancellationToken);

    private async Task<ServiceRecordApproval> BuildApprovalAsync(
        Guid currentUserId,
        ServiceRecord record,
        ServiceRecordOperationType operationType,
        ServiceRecordApproveRequest request,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var service = await _dbContext.Services.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == request.ServiceId && !candidate.IsDeleted, cancellationToken);
        if (service is null)
        {
            errors["serviceId"] = ["El servicio no existe en el catálogo."];
        }

        // ADR-0016 D12/F4: the confirmed general service (sent, or else the registered one; for an older record, the
        // general service of its registered rate). The rate chosen must be one of its rates.
        var generalServiceId = request.GeneralServiceId ?? record.GeneralServiceId;
        if (generalServiceId is null && record.ServiceId is { } registeredRateId)
        {
            generalServiceId = await _dbContext.Services.AsNoTracking()
                .Where(rate => rate.Id == registeredRateId)
                .Select(rate => (Guid?)rate.GeneralServiceId)
                .SingleOrDefaultAsync(cancellationToken);
        }

        if (request.GeneralServiceId is { } requestedGeneral
            && !await _dbContext.GeneralServices.AnyAsync(general => general.Id == requestedGeneral && !general.IsDeleted, cancellationToken))
        {
            errors["generalServiceId"] = ["El servicio general no existe o fue eliminado."];
        }

        if (!await _dbContext.StaffProfiles.AnyAsync(staff => staff.Id == request.StaffProfileId, cancellationToken))
        {
            errors["staffProfileId"] = ["El profesional no existe."];
        }

        if (request.CustomerId is { } customerId
            && !await _dbContext.Customers.AnyAsync(customer => customer.Id == customerId && customer.MergedIntoCustomerId == null, cancellationToken))
        {
            errors["customerId"] = ["El cliente no existe o fue fusionado con otro."];
        }

        if (request.PerformedOn > Today())
        {
            errors["performedOn"] = ["La fecha del servicio no puede ser futura."];
        }

        CustomerMembership? membership = null;
        if (operationType == ServiceRecordOperationType.MembershipConsumption && request.CustomerMembershipId is { } membershipId)
        {
            membership = await _dbContext.CustomerMemberships.AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.Id == membershipId, cancellationToken);
            if (membership is null)
            {
                errors["customerMembershipId"] = ["La membresía no existe."];
            }
            else if (membership.CustomerId != request.CustomerId)
            {
                errors["customerMembershipId"] = ["La membresía no pertenece al cliente indicado."];
            }
        }

        if (operationType == ServiceRecordOperationType.LoyaltyReward && request.CustomerId is null)
        {
            errors["customerId"] = ["Elige el cliente que canjeó la recompensa."];
        }

        int? pointsSnapshot = null;
        if (request.AccruesLoyaltyPoints && service is not null)
        {
            if (request.CustomerId is null)
            {
                errors["accruesLoyaltyPoints"] = ["Solo acumula puntos un servicio con cliente identificado."];
            }
            else if (service.LoyaltyPoints <= 0)
            {
                errors["accruesLoyaltyPoints"] = ["El servicio no tiene puntos de fidelización configurados."];
            }
            else if (!await IsLoyaltyEnabledAsync(cancellationToken))
            {
                errors["accruesLoyaltyPoints"] = ["El programa de fidelización está deshabilitado."];
            }
            else
            {
                pointsSnapshot = service.LoyaltyPoints;
            }
        }

        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }

        if (service!.GeneralServiceId != generalServiceId)
        {
            throw new ConflictException(
                "La tarifa elegida no pertenece al servicio general confirmado. Elige una tarifa de ese servicio general.");
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        Guid? incomeEntryId = null;
        Guid? consumptionId = null;
        switch (operationType)
        {
            case ServiceRecordOperationType.Paid:
                // Same snapshots as a manual service income: name, base price and split of the service right now.
                var income = new IncomeEntry(
                    service!.Id, service.Name, service.BasePrice, request.StaffProfileId, request.ChargedAmount!.Value,
                    isPromo: false, request.PerformedOn, currentUserId, utcNow, service.BusinessPercentage);
                _dbContext.IncomeEntries.Add(income);
                incomeEntryId = income.Id;
                break;

            case ServiceRecordOperationType.MembershipConsumption:
                // The record id is the consumption's idempotency key: one record, at most one consumption.
                // Owner and customer were validated before locking, and a merge can move the membership and merge the
                // profile in between. Global lock order (record → customer profiles → memberships, as the merge):
                // the profile is locked BEFORE the membership, never after it. Without it, a merge holding the profile
                // and waiting for the membership deadlocks with this approval, whose save takes a key lock on the same
                // profile (foreign key). Both checks are repeated under the locks; a change is a 409 and the
                // transaction rolls back with no approval and no consumption.
                await _wallet.LockCustomersAsync([request.CustomerId!.Value], cancellationToken);
                if (!await _dbContext.Customers.AsNoTracking()
                        .AnyAsync(customer => customer.Id == request.CustomerId && customer.MergedIntoCustomerId == null, cancellationToken))
                {
                    throw new ConflictException(
                        "El cliente fue fusionado en otro perfil mientras se confirmaba el servicio. Recarga e intenta de nuevo con el perfil que se conserva.");
                }

                var consumption = await _ledger.RegisterInCurrentTransactionAsync(
                    currentUserId,
                    membership!.Id,
                    new MembershipConsumptionRequest(service!.Id, 1, request.PerformedOn, request.StaffProfileId, record.Id),
                    cancellationToken,
                    expectedCustomerId: request.CustomerId);
                consumptionId = consumption.Id;
                break;

            case ServiceRecordOperationType.LoyaltyReward:
                // Delivers the approved redemption (lock order: record → customer profile → redemption) and consumes
                // its reserved points. No income, no consumption, no points: the business covers the commission.
                await _redemptions.DeliverServiceRewardInCurrentTransactionAsync(
                    request.LoyaltyRedemptionId!.Value, request.CustomerId!.Value, service!.Id, record.Id, currentUserId, utcNow,
                    cancellationToken);
                break;
        }

        var isReward = operationType == ServiceRecordOperationType.LoyaltyReward;
        return new ServiceRecordApproval(
            operationType,
            service!.Id,
            service.Name,
            request.StaffProfileId,
            request.CustomerId,
            request.PerformedOn,
            operationType == ServiceRecordOperationType.Paid ? request.ChargedAmount : null,
            incomeEntryId,
            operationType == ServiceRecordOperationType.MembershipConsumption ? membership!.Id : null,
            consumptionId,
            request.AccruesLoyaltyPoints,
            pointsSnapshot,
            request.Notes?.Trim(),
            isReward ? request.LoyaltyRedemptionId : null,
            // Snapshots of the confirmed service right now, as a paid service's income would take them (R14).
            isReward ? service.BasePrice : null,
            isReward ? service.BusinessPercentage : null,
            service.GeneralServiceId);
    }

    private static ServiceRecordOperationType ValidateApproveShape(ServiceRecordApproveRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var operationType = ParseName<ServiceRecordOperationType>(request.OperationType);

        if (operationType is null)
        {
            errors["operationType"] = ["Elige el tipo de operación: servicio pagado, consumo de membresía, cortesía o recompensa."];
        }

        if (request.ServiceId == Guid.Empty)
        {
            errors["serviceId"] = ["Elige la tarifa con la que se liquida el servicio."];
        }

        if (request.GeneralServiceId == Guid.Empty)
        {
            errors["generalServiceId"] = ["Elige el servicio general realizado."];
        }

        if (request.StaffProfileId == Guid.Empty)
        {
            errors["staffProfileId"] = ["Elige el profesional que hizo el servicio."];
        }

        if (request.PerformedOn == default)
        {
            errors["performedOn"] = ["Indica la fecha del servicio."];
        }

        if (operationType == ServiceRecordOperationType.Paid)
        {
            if (request.ChargedAmount is not > 0)
            {
                errors["chargedAmount"] = ["Indica el valor cobrado, mayor que cero. Si no se cobró, es una cortesía."];
            }

            if (request.CustomerMembershipId is not null)
            {
                errors["customerMembershipId"] = ["Un servicio pagado no se descuenta de una membresía."];
            }
        }
        else if (operationType is ServiceRecordOperationType.MembershipConsumption or ServiceRecordOperationType.Courtesy
                 or ServiceRecordOperationType.LoyaltyReward)
        {
            if (request.ChargedAmount is not null)
            {
                errors["chargedAmount"] = ["Solo un servicio pagado lleva valor cobrado."];
            }

            if (request.AccruesLoyaltyPoints)
            {
                errors["accruesLoyaltyPoints"] = ["Solo un servicio pagado puede acumular puntos."];
            }
        }

        if (operationType == ServiceRecordOperationType.MembershipConsumption)
        {
            if (request.CustomerId is null)
            {
                errors["customerId"] = ["Elige el cliente dueño de la membresía."];
            }

            if (request.CustomerMembershipId is null)
            {
                errors["customerMembershipId"] = ["Elige la membresía de la que se descuenta el servicio."];
            }
        }
        else if (operationType == ServiceRecordOperationType.Courtesy && request.CustomerMembershipId is not null)
        {
            errors["customerMembershipId"] = ["Una cortesía no se descuenta de una membresía."];
        }

        if (operationType == ServiceRecordOperationType.LoyaltyReward)
        {
            if (request.LoyaltyRedemptionId is null || request.LoyaltyRedemptionId == Guid.Empty)
            {
                errors["loyaltyRedemptionId"] = ["Elige la solicitud de canje aprobada que se entrega con este servicio."];
            }

            if (request.CustomerMembershipId is not null)
            {
                errors["customerMembershipId"] = ["Una recompensa no se descuenta de una membresía."];
            }
        }
        else if (request.LoyaltyRedemptionId is not null)
        {
            errors["loyaltyRedemptionId"] = ["Solo una recompensa se vincula con una solicitud de canje."];
        }

        if (request.Notes is { Length: > ServiceRecord.MaxNotesLength })
        {
            errors["notes"] = [$"Las notas deben tener {ServiceRecord.MaxNotesLength} caracteres o menos."];
        }

        if (request.Version is null)
        {
            errors["version"] = ["Falta la versión del registro. Recarga la bandeja e intenta de nuevo."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }

        return operationType!.Value;
    }

    private static bool SameApproval(ServiceRecord record, ServiceRecordOperationType operationType, ServiceRecordApproveRequest request)
        => record.OperationType == operationType
            && record.ApprovedServiceId == request.ServiceId
            && (request.GeneralServiceId is null || record.ApprovedGeneralServiceId == request.GeneralServiceId)
            && record.ApprovedStaffProfileId == request.StaffProfileId
            && record.ApprovedCustomerId == request.CustomerId
            && record.ApprovedPerformedOn == request.PerformedOn
            && record.ChargedAmount == request.ChargedAmount
            && record.CustomerMembershipId == request.CustomerMembershipId
            && record.LoyaltyRedemptionId == request.LoyaltyRedemptionId
            && record.AccruesLoyaltyPoints == request.AccruesLoyaltyPoints;

    /// <summary>
    /// Each record with the general service it names: its own, or for an older record the general service its
    /// registered rate belongs to now.
    /// </summary>
    private IQueryable<RegisteredGeneralService> RegisteredGeneralServicesQuery()
        => from record in _dbContext.ServiceRecords.AsNoTracking()
           join rate in _dbContext.Services.AsNoTracking() on record.ServiceId equals (Guid?)rate.Id into rates
           from rate in rates.DefaultIfEmpty()
           select new RegisteredGeneralService
           {
               RecordId = record.Id,
               AppointmentId = record.AppointmentId,
               Status = record.Status,
               GeneralServiceId = record.GeneralServiceId ?? (rate == null ? null : (Guid?)rate.GeneralServiceId),
               ServiceNameSnapshot = record.ServiceNameSnapshot,
           };

    /// <summary>Member-initialized (not positional) so EF can keep filtering after the projection.</summary>
    private sealed class RegisteredGeneralService
    {
        public Guid RecordId { get; init; }
        public Guid? AppointmentId { get; init; }
        public ServiceRecordStatus Status { get; init; }
        public Guid? GeneralServiceId { get; init; }
        public string ServiceNameSnapshot { get; init; } = string.Empty;
    }

    private async Task<Guid[]> RelatedRateIdsAsync(ServiceRecord record, CancellationToken cancellationToken)
    {
        if ((record.ApprovedServiceId ?? record.ServiceId) is { } rateId)
        {
            return [rateId];
        }

        return record.GeneralServiceId is { } generalServiceId
            ? (await _dbContext.Services.AsNoTracking()
                .Where(rate => rate.GeneralServiceId == generalServiceId)
                .Select(rate => rate.Id)
                .ToListAsync(cancellationToken)).ToArray()
            : [];
    }

    private async Task<ServiceRecord> LoadLockedAsync(Guid serviceRecordId, CancellationToken cancellationToken)
    {
        await LockAsync($"SELECT 1 FROM public.service_records WHERE \"Id\" = {serviceRecordId} FOR UPDATE", cancellationToken);
        return await _dbContext.ServiceRecords.SingleOrDefaultAsync(record => record.Id == serviceRecordId, cancellationToken)
            ?? throw new NotFoundException("El servicio realizado no existe.");
    }

    /// <summary>PostgreSQL row lock held until the transaction ends; other providers (unit tests) skip it.</summary>
    private async Task LockAsync(FormattableString lockQuery, CancellationToken cancellationToken)
    {
        if (_dbContext.Database.IsNpgsql())
        {
            await _dbContext.Database.ExecuteSqlInterpolatedAsync(lockQuery, cancellationToken);
        }
    }

    private async Task<bool> IsLoyaltyEnabledAsync(CancellationToken cancellationToken)
        => await _dbContext.LoyaltySettings.AsNoTracking()
            .AnyAsync(settings => settings.Id == LoyaltySettings.SingletonId && settings.IsEnabled, cancellationToken);

    private DateOnly Today() => DateOnly.FromDateTime(BogotaClock.ToLocal(_timeProvider.GetUtcNow().UtcDateTime));

    /// <summary>Accepts only the enum's names (any casing), never numeric strings.</summary>
    private static TEnum? ParseName<TEnum>(string? value)
        where TEnum : struct, Enum
    {
        var name = Enum.GetNames<TEnum>().FirstOrDefault(candidate =>
            string.Equals(candidate, value?.Trim(), StringComparison.OrdinalIgnoreCase));
        return name is null ? null : Enum.Parse<TEnum>(name);
    }

    // ── Mapping ─────────────────────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<ServiceRecordView>> MapAsync(IReadOnlyList<ServiceRecord> records, CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return [];
        }

        var staffIds = records.Select(record => record.StaffProfileId)
            .Concat(records.Where(record => record.ApprovedStaffProfileId is not null).Select(record => record.ApprovedStaffProfileId!.Value))
            .Distinct().ToArray();
        var staffNames = await _dbContext.StaffProfiles.AsNoTracking()
            .Where(staff => staffIds.Contains(staff.Id))
            .ToDictionaryAsync(staff => staff.Id, staff => staff.DisplayName, cancellationToken);

        var customerIds = records.Where(record => record.CustomerId is not null).Select(record => record.CustomerId!.Value)
            .Concat(records.Where(record => record.ApprovedCustomerId is not null).Select(record => record.ApprovedCustomerId!.Value))
            .Distinct().ToArray();
        var customerNames = await _dbContext.Customers.AsNoTracking()
            .Where(customer => customerIds.Contains(customer.Id))
            .ToDictionaryAsync(customer => customer.Id, customer => customer.FullName, cancellationToken);

        var userIds = records.Select(record => record.RegisteredByUserId)
            .Concat(records.Where(record => record.ApprovedByUserId is not null).Select(record => record.ApprovedByUserId!.Value))
            .Concat(records.Where(record => record.RejectedByUserId is not null).Select(record => record.RejectedByUserId!.Value))
            .Distinct().ToArray();
        var userNames = await _dbContext.Users.AsNoTracking()
            .Where(user => userIds.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, user => user.FullName, cancellationToken);

        var appointmentIds = records.Where(record => record.AppointmentId is not null).Select(record => record.AppointmentId!.Value).Distinct().ToArray();
        var appointments = await _dbContext.Appointments.AsNoTracking()
            .Where(appointment => appointmentIds.Contains(appointment.Id))
            .Select(appointment => new { appointment.Id, appointment.StartsAt, appointment.CustomerUserId })
            .ToDictionaryAsync(appointment => appointment.Id, cancellationToken);
        var appointmentStarts = appointments.ToDictionary(item => item.Key, item => item.Value.StartsAt);

        // Profiles now linked to the accounts that booked appointments of pending records without customer.
        var bookingUserIds = records
            .Where(record => record.Status == ServiceRecordStatus.Pending && record.CustomerId is null && record.AppointmentId is not null)
            .Select(record => appointments.GetValueOrDefault(record.AppointmentId!.Value)?.CustomerUserId)
            .Where(userId => userId is not null)
            .Distinct()
            .ToArray();
        var linkedProfiles = bookingUserIds.Length == 0
            ? []
            : await _dbContext.Customers.AsNoTracking()
                .Where(customer => bookingUserIds.Contains(customer.UserId) && customer.MergedIntoCustomerId == null)
                .ToDictionaryAsync(customer => customer.UserId!.Value, customer => new { customer.Id, customer.FullName }, cancellationToken);

        // Approved records marked to accrue: credited (original lot exists) or approved before the wallet existed.
        var accruingIds = records
            .Where(record => record.Status == ServiceRecordStatus.Approved && record.AccruesLoyaltyPoints)
            .Select(record => record.Id)
            .ToArray();
        var creditedIds = accruingIds.Length == 0
            ? []
            : (await _dbContext.LoyaltyLots.AsNoTracking()
                .Where(lot => lot.SourceType == LoyaltySourceType.ServiceRecord && lot.OriginLotId == null && accruingIds.Contains(lot.SourceId))
                .Select(lot => lot.SourceId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        // General services named by the records: registered, confirmed, or (older records) the one of the registered rate.
        var registeredRateIds = records.Where(record => record.GeneralServiceId is null && record.ServiceId is not null)
            .Select(record => record.ServiceId!.Value).Distinct().ToArray();
        var rateGeneralServices = registeredRateIds.Length == 0
            ? new Dictionary<Guid, Guid>()
            : await _dbContext.Services.AsNoTracking()
                .Where(rate => registeredRateIds.Contains(rate.Id))
                .ToDictionaryAsync(rate => rate.Id, rate => rate.GeneralServiceId, cancellationToken);
        Guid? RegisteredGeneralServiceOf(ServiceRecord record)
            => record.GeneralServiceId
               ?? (record.ServiceId is { } rateId && rateGeneralServices.TryGetValue(rateId, out var general) ? general : null);
        var generalServiceIds = records.Select(RegisteredGeneralServiceOf)
            .Concat(records.Select(record => record.ApprovedGeneralServiceId))
            .Where(id => id is not null).Select(id => id!.Value).Distinct().ToArray();
        var generalServiceNames = generalServiceIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await _dbContext.GeneralServices.AsNoTracking()
                .Where(general => generalServiceIds.Contains(general.Id))
                .ToDictionaryAsync(general => general.Id, general => general.Name, cancellationToken);

        var redemptionIds = records.Where(record => record.LoyaltyRedemptionId is not null).Select(record => record.LoyaltyRedemptionId!.Value).ToArray();
        var rewardNames = redemptionIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await _dbContext.LoyaltyRedemptions.AsNoTracking()
                .Where(redemption => redemptionIds.Contains(redemption.Id))
                .ToDictionaryAsync(redemption => redemption.Id, redemption => redemption.RewardNameSnapshot, cancellationToken);

        return records.Select(record =>
        {
            var suggestion = record.Status == ServiceRecordStatus.Pending
                && record.CustomerId is null
                && record.AppointmentId is { } bookedId
                && appointments.GetValueOrDefault(bookedId)?.CustomerUserId is { } bookingUserId
                && linkedProfiles.TryGetValue(bookingUserId, out var linked)
                    ? linked
                    : null;

            ServiceRecordApprovalView? approval = null;
            if (record.Status == ServiceRecordStatus.Approved)
            {
                approval = new ServiceRecordApprovalView(
                    record.OperationType!.Value.ToString(),
                    record.ApprovedServiceId!.Value,
                    record.ApprovedServiceNameSnapshot!,
                    record.ApprovedStaffProfileId!.Value,
                    staffNames.GetValueOrDefault(record.ApprovedStaffProfileId.Value),
                    record.ApprovedCustomerId,
                    record.ApprovedCustomerId is { } approvedCustomer ? customerNames.GetValueOrDefault(approvedCustomer) : null,
                    record.ApprovedPerformedOn!.Value,
                    record.ChargedAmount,
                    record.IncomeEntryId,
                    record.CustomerMembershipId,
                    record.MembershipConsumptionId,
                    record.AccruesLoyaltyPoints,
                    record.LoyaltyPointsSnapshot,
                    record.ReviewNotes,
                    record.ApprovedAt!.Value,
                    userNames.GetValueOrDefault(record.ApprovedByUserId!.Value),
                    record.LoyaltyRedemptionId,
                    record.LoyaltyRedemptionId is { } redemptionId ? rewardNames.GetValueOrDefault(redemptionId) : null,
                    record.RewardBasePriceSnapshot,
                    record.RewardBusinessPercentageSnapshot,
                    record.RewardProfessionalCommission,
                    record.ApprovedGeneralServiceId,
                    record.ApprovedGeneralServiceId is { } approvedGeneral ? generalServiceNames.GetValueOrDefault(approvedGeneral) : null);
            }

            var registeredGeneral = RegisteredGeneralServiceOf(record);

            return new ServiceRecordView(
                record.Id,
                record.Status.ToString(),
                record.AppointmentId,
                record.AppointmentId is { } appointmentId && appointmentStarts.TryGetValue(appointmentId, out var startsAt) ? startsAt : null,
                record.StaffProfileId,
                staffNames.GetValueOrDefault(record.StaffProfileId),
                record.ServiceId,
                record.ServiceNameSnapshot,
                record.PerformedOn,
                record.CustomerId,
                record.CustomerId is { } customerId ? customerNames.GetValueOrDefault(customerId) ?? record.CustomerNameSnapshot : record.CustomerNameSnapshot,
                record.Notes,
                record.RegisteredAt,
                userNames.GetValueOrDefault(record.RegisteredByUserId),
                approval,
                record.RejectionReason,
                record.RejectedAt,
                record.RejectedByUserId is { } rejectedBy ? userNames.GetValueOrDefault(rejectedBy) : null,
                record.Version,
                suggestion?.Id,
                suggestion?.FullName,
                record.Status == ServiceRecordStatus.Approved && record.AccruesLoyaltyPoints
                    ? creditedIds.Contains(record.Id) ? "Credited" : "PredatesWallet"
                    : null,
                registeredGeneral,
                registeredGeneral is { } general ? generalServiceNames.GetValueOrDefault(general) : null);
        }).ToArray();
    }
}
