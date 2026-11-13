using System.Net.Mail;
using Barbershop.Application.Auth;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Media;
using Barbershop.Application.Staff;
using Barbershop.Application.Staff.Admin;
using Barbershop.Application.Staff.SelfService;
using Barbershop.Application.Storage;
using Barbershop.Domain.Common;
using Barbershop.Domain.Media;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Identity;
using Barbershop.Infrastructure.Media;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Staff;

internal sealed class StaffManagementService : IAdminStaffService, IStaffProfileService
{
    private const int DefaultAppointmentDurationMinutes = 30;
    private const int MinimumAppointmentDurationMinutes = 10;
    private const int MaximumAppointmentDurationMinutes = 240;

    private readonly AppDbContext _dbContext;
    private readonly IIdentitySeedService _identitySeedService;
    private readonly IPasswordHasher<object> _passwordHasher;
    private readonly TimeProvider _timeProvider;
    private readonly IMediaAssetsService _mediaAssetsService;
    private readonly IFileStorageService _fileStorageService;
    private readonly ICurrentUserAccessor? _currentUser;

    public StaffManagementService(
        AppDbContext dbContext,
        IIdentitySeedService identitySeedService,
        IPasswordHasher<object> passwordHasher,
        TimeProvider timeProvider,
        IMediaAssetsService mediaAssetsService,
        IFileStorageService fileStorageService,
        ICurrentUserAccessor? currentUser = null)
    {
        _dbContext = dbContext;
        _identitySeedService = identitySeedService;
        _passwordHasher = passwordHasher;
        _timeProvider = timeProvider;
        _mediaAssetsService = mediaAssetsService;
        _fileStorageService = fileStorageService;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlyList<StaffManagementView>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var staffProfiles = await _dbContext.StaffProfiles
            .Include(staffProfile => staffProfile.User)
            .OrderBy(staffProfile => staffProfile.DisplayName)
            .ToListAsync(cancellationToken);

        return await MapManyAsync(staffProfiles, cancellationToken);
    }

    public async Task<StaffManagementView> GetByIdAsync(Guid staffProfileId, CancellationToken cancellationToken = default)
    {
        var staffProfile = await LoadStaffProfileAsync(staffProfileId, cancellationToken);
        return await MapAsync(staffProfile, cancellationToken);
    }

    public async Task<StaffManagementView> CreateAsync(AdminStaffCreateRequest request, CancellationToken cancellationToken = default)
    {
        ValidateCreateRequest(request);
        await _identitySeedService.EnsureSeededAsync(cancellationToken);

        return await MediaAssetReferences.InTransactionAsync(_dbContext, async () =>
        {
            await LockRequestedMediaAsync(request.PhotoMediaAssetId, request.TipsQrMediaAssetId, cancellationToken);
            await ValidateMediaAssignmentAsync(null, request.PhotoMediaAssetId, request.TipsQrMediaAssetId, null, cancellationToken);
            return await CreateStaffAsync(request, cancellationToken);
        }, cancellationToken);
    }

    private async Task<StaffManagementView> CreateStaffAsync(AdminStaffCreateRequest request, CancellationToken cancellationToken)
    {
        var normalizedEmail = NormalizeEmail(request.Email);
        var duplicateEmailExists = await _dbContext.Users.AnyAsync(user => user.NormalizedEmail == normalizedEmail, cancellationToken);
        if (duplicateEmailExists)
        {
            throw new ConflictException("A user with this email already exists.");
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var durationMinutes = request.DefaultAppointmentDurationMinutes is null or 0
            ? DefaultAppointmentDurationMinutes
            : request.DefaultAppointmentDurationMinutes.Value;
        var isActive = request.IsActive ?? true;

        var passwordHash = _passwordHasher.HashPassword(new object(), request.InitialPassword);
        var user = new User(request.FullName, request.Email, passwordHash, utcNow, request.PhoneNumber);
        user.UpdateProfile(request.FullName, request.PhoneNumber, request.PhotoMediaAssetId, utcNow);

        if (!isActive)
        {
            user.Deactivate(utcNow);
        }

        var staffRole = await _dbContext.Roles.SingleAsync(role => role.NormalizedName == RoleNames.Staff.ToUpperInvariant(), cancellationToken);
        user.UserRoles.Add(new UserRole(user.Id, staffRole.Id, utcNow));

        var staffProfile = new StaffProfile(user.Id, request.DisplayName, durationMinutes, utcNow);
        staffProfile.UpdateDetails(
            request.DisplayName,
            request.Bio,
            request.PhoneNumber,
            request.PhotoMediaAssetId,
            request.TipsQrMediaAssetId,
            durationMinutes,
            isActive,
            utcNow);

        _dbContext.Users.Add(user);
        _dbContext.StaffProfiles.Add(staffProfile);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return await MapAsync(staffProfile, cancellationToken);
    }

    public async Task<StaffManagementView> UpdateAsync(Guid staffProfileId, AdminStaffUpdateRequest request, CancellationToken cancellationToken = default)
    {
        ValidateUpdateRequest(request);

        return await MediaAssetReferences.InTransactionAsync(_dbContext, async () =>
        {
            await LockRequestedMediaAsync(request.PhotoMediaAssetId, request.TipsQrMediaAssetId, cancellationToken);
            var staffProfile = await LoadStaffProfileAsync(staffProfileId, cancellationToken);
            await ValidateMediaAssignmentAsync(staffProfile, request.PhotoMediaAssetId, request.TipsQrMediaAssetId, null, cancellationToken);
            return await UpdateStaffAsync(staffProfile, request, cancellationToken);
        }, cancellationToken);
    }

    private async Task<StaffManagementView> UpdateStaffAsync(StaffProfile staffProfile, AdminStaffUpdateRequest request, CancellationToken cancellationToken)
    {
        var normalizedEmail = NormalizeEmail(request.Email);
        var duplicateEmailExists = await _dbContext.Users.AnyAsync(
            user => user.Id != staffProfile.UserId && user.NormalizedEmail == normalizedEmail,
            cancellationToken);

        if (duplicateEmailExists)
        {
            throw new ConflictException("A user with this email already exists.");
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var durationMinutes = request.DefaultAppointmentDurationMinutes is null or 0
            ? staffProfile.DefaultAppointmentDurationMinutes
            : request.DefaultAppointmentDurationMinutes.Value;
        var isActive = request.IsActive ?? staffProfile.IsActive;
        var deactivatesAccount = staffProfile.User.IsActive && !isActive;

        staffProfile.User.SetEmail(request.Email);
        staffProfile.User.UpdateProfile(request.FullName, request.PhoneNumber, request.PhotoMediaAssetId, utcNow);

        if (isActive)
        {
            staffProfile.User.Activate(utcNow);
        }
        else
        {
            staffProfile.User.Deactivate(utcNow);
        }

        staffProfile.UpdateDetails(
            request.DisplayName,
            request.Bio,
            request.PhoneNumber,
            request.PhotoMediaAssetId,
            request.TipsQrMediaAssetId,
            durationMinutes,
            isActive,
            utcNow);

        await SaveAsync(deactivatesAccount, staffProfile.UserId, cancellationToken);
        return await MapAsync(staffProfile, cancellationToken);
    }

    public async Task<StaffManagementView> UpdateStatusAsync(Guid staffProfileId, StaffStatusUpdateRequest request, CancellationToken cancellationToken = default)
    {
        var staffProfile = await LoadStaffProfileAsync(staffProfileId, cancellationToken);
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var deactivatesAccount = staffProfile.User.IsActive && !request.IsActive;

        if (request.IsActive)
        {
            staffProfile.User.Activate(utcNow);
        }
        else
        {
            staffProfile.User.Deactivate(utcNow);
        }

        staffProfile.UpdateDetails(
            staffProfile.DisplayName,
            staffProfile.Bio,
            staffProfile.PhoneNumber,
            staffProfile.PhotoMediaAssetId,
            staffProfile.TipsQrMediaAssetId,
            staffProfile.DefaultAppointmentDurationMinutes,
            request.IsActive,
            utcNow);

        await SaveAsync(deactivatesAccount, staffProfile.UserId, cancellationToken);
        return await MapAsync(staffProfile, cancellationToken);
    }

    /// <summary>
    /// Deactivating a professional deactivates their account, which may be an administrator's (BUG-076): the same
    /// rules as DELETE /admin/users apply (not one's own account, never the last active Admin).
    /// </summary>
    private Task<int> SaveAsync(bool deactivatesAccount, Guid userId, CancellationToken cancellationToken)
        => deactivatesAccount
            ? AdminAccountGuard.DeactivateAsync(
                _dbContext, userId, _currentUser?.UserId, () => _dbContext.SaveChangesAsync(cancellationToken), cancellationToken)
            : _dbContext.SaveChangesAsync(cancellationToken);

    public async Task<StaffManagementView> EnableProfessionalForCurrentUserAsync(
        Guid currentUserId,
        EnableProfessionalProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateEnableRequest(request);
        await _identitySeedService.EnsureSeededAsync(cancellationToken);

        var user = await _dbContext.Users
            .Include(candidate => candidate.UserRoles)
            .ThenInclude(userRole => userRole.Role)
            .Include(candidate => candidate.StaffProfile)
            .SingleOrDefaultAsync(candidate => candidate.Id == currentUserId, cancellationToken)
            ?? throw new KeyNotFoundException("The current user was not found.");

        if (user.StaffProfile is not null)
        {
            throw new ConflictException("The professional profile is already active.");
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var durationMinutes = request.DefaultAppointmentDurationMinutes is null or 0
            ? DefaultAppointmentDurationMinutes
            : request.DefaultAppointmentDurationMinutes.Value;

        var alreadyStaff = user.UserRoles.Any(assignment => assignment.Role.Name == RoleNames.Staff);
        if (!alreadyStaff)
        {
            var staffRole = await _dbContext.Roles.SingleAsync(
                role => role.NormalizedName == RoleNames.Staff.ToUpperInvariant(), cancellationToken);
            user.UserRoles.Add(new UserRole(user.Id, staffRole.Id, utcNow));
        }

        var staffProfile = new StaffProfile(user.Id, request.DisplayName, durationMinutes, utcNow);
        staffProfile.UpdateDetails(
            request.DisplayName,
            null,
            user.PhoneNumber,
            null,
            null,
            durationMinutes,
            true,
            utcNow);

        _dbContext.StaffProfiles.Add(staffProfile);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return await MapAsync(staffProfile, cancellationToken);
    }

    public async Task<StaffManagementView> GetCurrentAsync(Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var staffProfile = await LoadStaffProfileByUserIdAsync(currentUserId, cancellationToken);
        return await MapAsync(staffProfile, cancellationToken);
    }

    public async Task<StaffManagementView> UpdateCurrentAsync(Guid currentUserId, StaffProfileUpdateRequest request, CancellationToken cancellationToken = default)
    {
        ValidateSelfUpdateRequest(request);

        // A professional may only link files they uploaded for that slot (BUG-019); re-sending the current ids keeps
        // saving the form working.
        return await MediaAssetReferences.InTransactionAsync(_dbContext, async () =>
        {
            await LockRequestedMediaAsync(request.PhotoMediaAssetId, request.TipsQrMediaAssetId, cancellationToken);
            var staffProfile = await LoadStaffProfileByUserIdAsync(currentUserId, cancellationToken);
            await ValidateMediaAssignmentAsync(staffProfile, request.PhotoMediaAssetId, request.TipsQrMediaAssetId, currentUserId, cancellationToken);
            return await UpdateCurrentStaffAsync(staffProfile, request, cancellationToken);
        }, cancellationToken);
    }

    private async Task<StaffManagementView> UpdateCurrentStaffAsync(StaffProfile staffProfile, StaffProfileUpdateRequest request, CancellationToken cancellationToken)
    {
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var durationMinutes = request.DefaultAppointmentDurationMinutes is null or 0
            ? staffProfile.DefaultAppointmentDurationMinutes
            : request.DefaultAppointmentDurationMinutes.Value;

        staffProfile.User.UpdateProfile(
            staffProfile.User.FullName,
            request.PhoneNumber,
            request.PhotoMediaAssetId,
            utcNow);

        staffProfile.UpdateDetails(
            request.DisplayName,
            request.Bio,
            request.PhoneNumber,
            request.PhotoMediaAssetId,
            request.TipsQrMediaAssetId,
            durationMinutes,
            staffProfile.IsActive,
            utcNow,
            request.InstagramUrl,
            request.FacebookUrl,
            request.TikTokUrl,
            request.YoutubeUrl,
            request.XUrl);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return await MapAsync(staffProfile, cancellationToken);
    }

    public async Task<StaffManagementView> UploadPhotoAsync(Guid currentUserId, StaffMediaUploadRequest request, CancellationToken cancellationToken = default)
    {
        var staffProfile = await LoadStaffProfileByUserIdAsync(currentUserId, cancellationToken);
        var roles = new[] { RoleNames.Staff };
        var mediaView = await _mediaAssetsService.UploadAsync(
            currentUserId, roles,
            new Barbershop.Application.Media.MediaAssetUploadRequest(
                request.FileName, request.ContentType, request.SizeBytes,
                MediaAssetPurpose.StaffPhoto, request.Content),
            cancellationToken);

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var previousPhotoId = staffProfile.PhotoMediaAssetId;

        staffProfile.SetPhoto(mediaView.Id, utcNow);
        staffProfile.User.SetProfilePhoto(mediaView.Id, utcNow);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await ReleaseSlotMediaAsync(previousPhotoId, mediaView.Id, MediaAssetPurpose.StaffPhoto, staffProfile.UserId, "Replaced by new staff photo", cancellationToken);
        return await MapAsync(staffProfile, cancellationToken);
    }

    public async Task<StaffManagementView> RemovePhotoAsync(Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var staffProfile = await LoadStaffProfileByUserIdAsync(currentUserId, cancellationToken);
        if (!staffProfile.PhotoMediaAssetId.HasValue)
        {
            return await MapAsync(staffProfile, cancellationToken);
        }

        var assetId = staffProfile.PhotoMediaAssetId.Value;
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        staffProfile.SetPhoto(null, utcNow);
        staffProfile.User.SetProfilePhoto(null, utcNow);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await ReleaseSlotMediaAsync(assetId, null, MediaAssetPurpose.StaffPhoto, staffProfile.UserId, "Removed by staff from profile", cancellationToken);
        return await MapAsync(staffProfile, cancellationToken);
    }

    public async Task<StaffManagementView> UploadTipsQrAsync(Guid currentUserId, StaffMediaUploadRequest request, CancellationToken cancellationToken = default)
    {
        var staffProfile = await LoadStaffProfileByUserIdAsync(currentUserId, cancellationToken);
        var roles = new[] { RoleNames.Staff };
        var mediaView = await _mediaAssetsService.UploadAsync(
            currentUserId, roles,
            new Barbershop.Application.Media.MediaAssetUploadRequest(
                request.FileName, request.ContentType, request.SizeBytes,
                MediaAssetPurpose.TipsQr, request.Content),
            cancellationToken);

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var previousTipsQrId = staffProfile.TipsQrMediaAssetId;

        staffProfile.SetTipsQr(mediaView.Id, utcNow);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await ReleaseSlotMediaAsync(previousTipsQrId, mediaView.Id, MediaAssetPurpose.TipsQr, staffProfile.UserId, "Replaced by new tips QR", cancellationToken);
        return await MapAsync(staffProfile, cancellationToken);
    }

    public async Task<StaffManagementView> RemoveTipsQrAsync(Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var staffProfile = await LoadStaffProfileByUserIdAsync(currentUserId, cancellationToken);
        if (!staffProfile.TipsQrMediaAssetId.HasValue)
        {
            return await MapAsync(staffProfile, cancellationToken);
        }

        var assetId = staffProfile.TipsQrMediaAssetId.Value;
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        staffProfile.SetTipsQr(null, utcNow);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await ReleaseSlotMediaAsync(assetId, null, MediaAssetPurpose.TipsQr, staffProfile.UserId, "Removed by staff from profile", cancellationToken);
        return await MapAsync(staffProfile, cancellationToken);
    }

    public async Task<StaffManagementView> UploadPhotoAsync(Guid staffProfileId, Guid uploadedByUserId, StaffMediaUploadRequest request, CancellationToken cancellationToken = default)
    {
        var staffProfile = await LoadStaffProfileAsync(staffProfileId, cancellationToken);
        var roles = new[] { RoleNames.Admin };
        var mediaView = await _mediaAssetsService.UploadAsync(
            uploadedByUserId, roles,
            new Barbershop.Application.Media.MediaAssetUploadRequest(
                request.FileName, request.ContentType, request.SizeBytes,
                MediaAssetPurpose.StaffPhoto, request.Content),
            cancellationToken);

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var previousPhotoId = staffProfile.PhotoMediaAssetId;

        staffProfile.SetPhoto(mediaView.Id, utcNow);
        staffProfile.User.SetProfilePhoto(mediaView.Id, utcNow);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await ReleaseSlotMediaAsync(previousPhotoId, mediaView.Id, MediaAssetPurpose.StaffPhoto, staffProfile.UserId, "Replaced by admin-uploaded staff photo", cancellationToken);
        return await MapAsync(staffProfile, cancellationToken);
    }

    async Task<StaffManagementView> IAdminStaffService.RemovePhotoAsync(Guid staffProfileId, CancellationToken cancellationToken)
    {
        var staffProfile = await LoadStaffProfileAsync(staffProfileId, cancellationToken);
        if (!staffProfile.PhotoMediaAssetId.HasValue)
        {
            return await MapAsync(staffProfile, cancellationToken);
        }

        var assetId = staffProfile.PhotoMediaAssetId.Value;
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        staffProfile.SetPhoto(null, utcNow);
        staffProfile.User.SetProfilePhoto(null, utcNow);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await ReleaseSlotMediaAsync(assetId, null, MediaAssetPurpose.StaffPhoto, staffProfile.UserId, "Removed by admin from staff profile", cancellationToken);
        return await MapAsync(staffProfile, cancellationToken);
    }

    public async Task<StaffManagementView> UploadTipsQrAsync(Guid staffProfileId, Guid uploadedByUserId, StaffMediaUploadRequest request, CancellationToken cancellationToken = default)
    {
        var staffProfile = await LoadStaffProfileAsync(staffProfileId, cancellationToken);
        var roles = new[] { RoleNames.Admin };
        var mediaView = await _mediaAssetsService.UploadAsync(
            uploadedByUserId, roles,
            new Barbershop.Application.Media.MediaAssetUploadRequest(
                request.FileName, request.ContentType, request.SizeBytes,
                MediaAssetPurpose.TipsQr, request.Content),
            cancellationToken);

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var previousTipsQrId = staffProfile.TipsQrMediaAssetId;

        staffProfile.SetTipsQr(mediaView.Id, utcNow);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await ReleaseSlotMediaAsync(previousTipsQrId, mediaView.Id, MediaAssetPurpose.TipsQr, staffProfile.UserId, "Replaced by admin-uploaded tips QR", cancellationToken);
        return await MapAsync(staffProfile, cancellationToken);
    }

    async Task<StaffManagementView> IAdminStaffService.RemoveTipsQrAsync(Guid staffProfileId, CancellationToken cancellationToken)
    {
        var staffProfile = await LoadStaffProfileAsync(staffProfileId, cancellationToken);
        if (!staffProfile.TipsQrMediaAssetId.HasValue)
        {
            return await MapAsync(staffProfile, cancellationToken);
        }

        var assetId = staffProfile.TipsQrMediaAssetId.Value;
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        staffProfile.SetTipsQr(null, utcNow);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await ReleaseSlotMediaAsync(assetId, null, MediaAssetPurpose.TipsQr, staffProfile.UserId, "Removed by admin from staff profile", cancellationToken);
        return await MapAsync(staffProfile, cancellationToken);
    }

    /// <summary>
    /// Deletes the file a photo/QR slot no longer uses, only when it is a file of that kind uploaded by the
    /// professional or by an administrator and nothing else references it (BUG-019). A file linked by mistake
    /// (another kind, someone else's, or still in use) is only unlinked, never deleted.
    /// </summary>
    private async Task ReleaseSlotMediaAsync(
        Guid? previousMediaAssetId,
        Guid? currentMediaAssetId,
        MediaAssetPurpose slotPurpose,
        Guid staffUserId,
        string reason,
        CancellationToken cancellationToken)
    {
        if (previousMediaAssetId is not { } mediaAssetId || mediaAssetId == currentMediaAssetId)
        {
            return;
        }

        await MediaAssetReferences.ReleaseAsync(
            _dbContext,
            _fileStorageService,
            _timeProvider,
            mediaAssetId,
            async asset => asset.Purpose == slotPurpose
                && (asset.UploadedByUserId == staffUserId
                    || await MediaAssetReferences.IsAdminAsync(_dbContext, asset.UploadedByUserId, cancellationToken)),
            reason,
            cancellationToken);
    }

    private async Task<StaffProfile> LoadStaffProfileAsync(Guid staffProfileId, CancellationToken cancellationToken)
    {
        return await _dbContext.StaffProfiles
            .Include(staffProfile => staffProfile.User)
            .SingleOrDefaultAsync(staffProfile => staffProfile.Id == staffProfileId, cancellationToken)
            ?? throw new KeyNotFoundException("The staff profile was not found.");
    }

    private async Task<StaffProfile> LoadStaffProfileByUserIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await _dbContext.StaffProfiles
            .Include(staffProfile => staffProfile.User)
            .SingleOrDefaultAsync(staffProfile => staffProfile.UserId == userId, cancellationToken)
            ?? throw new KeyNotFoundException("The staff profile was not found for the current user.");
    }

    private async Task<IReadOnlyList<StaffManagementView>> MapManyAsync(IReadOnlyCollection<StaffProfile> staffProfiles, CancellationToken cancellationToken)
    {
        var mediaAssetIds = staffProfiles
            .SelectMany(staffProfile => new[] { staffProfile.PhotoMediaAssetId, staffProfile.TipsQrMediaAssetId })
            .Where(mediaAssetId => mediaAssetId.HasValue)
            .Select(mediaAssetId => mediaAssetId!.Value)
            .Distinct()
            .ToArray();

        Dictionary<Guid, string?> mediaUrls;
        if (mediaAssetIds.Length == 0)
        {
            mediaUrls = [];
        }
        else
        {
            var assets = await _dbContext.MediaAssets
                .Where(asset => mediaAssetIds.Contains(asset.Id))
                .Select(asset => new { asset.Id, asset.StorageKey, asset.PublicUrl })
                .ToListAsync(cancellationToken);

            mediaUrls = assets.ToDictionary(
                asset => asset.Id,
                asset => (string?)(asset.PublicUrl ?? _fileStorageService.GetPublicUrl(asset.StorageKey)));
        }

        return staffProfiles
            .Select(staffProfile => Map(staffProfile, mediaUrls))
            .ToArray();
    }

    private async Task<StaffManagementView> MapAsync(StaffProfile staffProfile, CancellationToken cancellationToken)
    {
        var assets = await _dbContext.MediaAssets
            .Where(asset => asset.Id == staffProfile.PhotoMediaAssetId || asset.Id == staffProfile.TipsQrMediaAssetId)
            .Select(asset => new { asset.Id, asset.StorageKey, asset.PublicUrl })
            .ToListAsync(cancellationToken);

        var mediaUrls = assets.ToDictionary(
            asset => asset.Id,
            asset => (string?)(asset.PublicUrl ?? _fileStorageService.GetPublicUrl(asset.StorageKey)));

        return Map(staffProfile, mediaUrls);
    }

    private static StaffManagementView Map(StaffProfile staffProfile, IReadOnlyDictionary<Guid, string?> mediaUrls)
    {
        mediaUrls.TryGetValue(staffProfile.PhotoMediaAssetId ?? Guid.Empty, out var photoUrl);
        mediaUrls.TryGetValue(staffProfile.TipsQrMediaAssetId ?? Guid.Empty, out var tipsQrUrl);

        return new StaffManagementView(
            staffProfile.Id,
            staffProfile.UserId,
            staffProfile.User.FullName,
            staffProfile.User.Email,
            staffProfile.PhoneNumber ?? staffProfile.User.PhoneNumber,
            staffProfile.DisplayName,
            staffProfile.Bio,
            staffProfile.DefaultAppointmentDurationMinutes,
            staffProfile.PhotoMediaAssetId,
            photoUrl,
            staffProfile.TipsQrMediaAssetId,
            tipsQrUrl,
            staffProfile.InstagramUrl,
            staffProfile.FacebookUrl,
            staffProfile.TikTokUrl,
            staffProfile.YoutubeUrl,
            staffProfile.XUrl,
            staffProfile.User.IsActive && staffProfile.IsActive,
            staffProfile.CreatedAt,
            staffProfile.UpdatedAt);
    }

    private Task LockRequestedMediaAsync(Guid? photoMediaAssetId, Guid? tipsQrMediaAssetId, CancellationToken cancellationToken)
    {
        var requestedIds = new[] { photoMediaAssetId, tipsQrMediaAssetId }
            .Where(mediaAssetId => mediaAssetId.HasValue)
            .Select(mediaAssetId => mediaAssetId!.Value)
            .ToArray();

        return MediaAssetReferences.LockAsync(_dbContext, requestedIds, cancellationToken);
    }

    /// <summary>
    /// Which files a photo/QR slot may point to (BUG-019). Keeping the current file or clearing the slot is always
    /// allowed (also for a file linked by mistake before this rule, so the form can still be saved or the link
    /// removed). A new file must exist, be ready, be of the slot's kind, not be used by anything else and, when the
    /// professional edits their own profile, have been uploaded by them. The error never says whose the file is.
    /// </summary>
    private async Task ValidateMediaAssignmentAsync(
        StaffProfile? staffProfile,
        Guid? photoMediaAssetId,
        Guid? tipsQrMediaAssetId,
        Guid? selfServiceUserId,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (!await IsAssignableAsync(photoMediaAssetId, staffProfile?.PhotoMediaAssetId, MediaAssetPurpose.StaffPhoto, staffProfile, selfServiceUserId, cancellationToken))
        {
            errors["photoMediaAssetId"] = ["The selected photo is not available for this profile. Upload it from the profile instead."];
        }

        if (!await IsAssignableAsync(tipsQrMediaAssetId, staffProfile?.TipsQrMediaAssetId, MediaAssetPurpose.TipsQr, staffProfile, selfServiceUserId, cancellationToken))
        {
            errors["tipsQrMediaAssetId"] = ["The selected tips QR is not available for this profile. Upload it from the profile instead."];
        }

        ThrowIfAnyErrors(errors);
    }

    private async Task<bool> IsAssignableAsync(
        Guid? requestedId,
        Guid? currentId,
        MediaAssetPurpose slotPurpose,
        StaffProfile? staffProfile,
        Guid? selfServiceUserId,
        CancellationToken cancellationToken)
    {
        if (requestedId is not { } mediaAssetId || mediaAssetId == currentId)
        {
            return true;
        }

        var asset = await _dbContext.MediaAssets
            .AsNoTracking()
            .Where(candidate => candidate.Id == mediaAssetId)
            .Select(candidate => new { candidate.Status, candidate.Purpose, candidate.UploadedByUserId })
            .SingleOrDefaultAsync(cancellationToken);

        if (asset is null
            || asset.Status != MediaAssetStatus.Ready
            || asset.Purpose != slotPurpose
            || (selfServiceUserId is { } ownerId && asset.UploadedByUserId != ownerId))
        {
            return false;
        }

        // The professional's own account avatar mirrors the photo slot, so it does not count as another use.
        return !await MediaAssetReferences.IsReferencedAsync(
            _dbContext, mediaAssetId, cancellationToken, staffProfile?.UserId, staffProfile?.Id);
    }

    private static void ValidateCreateRequest(AdminStaffCreateRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        ValidateCommonAdminFields(errors, request.FullName, request.Email, request.DisplayName, request.PhoneNumber, request.Bio, request.DefaultAppointmentDurationMinutes);
        AddErrorIf(errors, "initialPassword", string.IsNullOrWhiteSpace(request.InitialPassword) || request.InitialPassword.Length is < 8 or > 128,
            "InitialPassword must be between 8 and 128 characters.");
        ThrowIfAnyErrors(errors);
    }

    private static void ValidateUpdateRequest(AdminStaffUpdateRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        ValidateCommonAdminFields(errors, request.FullName, request.Email, request.DisplayName, request.PhoneNumber, request.Bio, request.DefaultAppointmentDurationMinutes);
        ThrowIfAnyErrors(errors);
    }

    private static void ValidateSelfUpdateRequest(StaffProfileUpdateRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        AddErrorIf(errors, "displayName", string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length is < 2 or > 120,
            "DisplayName must be between 2 and 120 characters.");
        AddErrorIf(errors, "phoneNumber", !string.IsNullOrWhiteSpace(request.PhoneNumber) && request.PhoneNumber.Trim().Length > 40,
            "PhoneNumber must be 40 characters or fewer.");
        AddErrorIf(errors, "bio", !string.IsNullOrWhiteSpace(request.Bio) && request.Bio.Trim().Length > 2000,
            "Bio must be 2000 characters or fewer.");
        AddErrorIf(errors, "defaultAppointmentDurationMinutes",
            request.DefaultAppointmentDurationMinutes is not null and not 0 &&
            (request.DefaultAppointmentDurationMinutes < MinimumAppointmentDurationMinutes || request.DefaultAppointmentDurationMinutes > MaximumAppointmentDurationMinutes),
            $"DefaultAppointmentDurationMinutes must be between {MinimumAppointmentDurationMinutes} and {MaximumAppointmentDurationMinutes}.");
        AddErrorIf(errors, "instagramUrl", !string.IsNullOrWhiteSpace(request.InstagramUrl) && request.InstagramUrl.Trim().Length > 2048,
            "InstagramUrl must be 2048 characters or fewer.");
        AddErrorIf(errors, "facebookUrl", !string.IsNullOrWhiteSpace(request.FacebookUrl) && request.FacebookUrl.Trim().Length > 2048,
            "FacebookUrl must be 2048 characters or fewer.");
        AddErrorIf(errors, "tikTokUrl", !string.IsNullOrWhiteSpace(request.TikTokUrl) && request.TikTokUrl.Trim().Length > 2048,
            "TikTokUrl must be 2048 characters or fewer.");
        AddErrorIf(errors, "youtubeUrl", !string.IsNullOrWhiteSpace(request.YoutubeUrl) && request.YoutubeUrl.Trim().Length > 2048,
            "YoutubeUrl must be 2048 characters or fewer.");
        AddErrorIf(errors, "xUrl", !string.IsNullOrWhiteSpace(request.XUrl) && request.XUrl.Trim().Length > 2048,
            "XUrl must be 2048 characters or fewer.");

        // BUG-047: the public profile publishes these links to anyone; only absolute http/https addresses.
        AddSocialUrlError(errors, "instagramUrl", "Instagram", request.InstagramUrl, "https://instagram.com/tu_usuario");
        AddSocialUrlError(errors, "facebookUrl", "Facebook", request.FacebookUrl, "https://facebook.com/tu_pagina");
        AddSocialUrlError(errors, "tikTokUrl", "TikTok", request.TikTokUrl, "https://tiktok.com/@tu_usuario");
        AddSocialUrlError(errors, "youtubeUrl", "YouTube", request.YoutubeUrl, "https://youtube.com/@tu_canal");
        AddSocialUrlError(errors, "xUrl", "X", request.XUrl, "https://x.com/tu_usuario");

        ThrowIfAnyErrors(errors);
    }

    private static void AddSocialUrlError(
        Dictionary<string, string[]> errors, string field, string network, string? value, string example)
    {
        if (!errors.ContainsKey(field) && !string.IsNullOrWhiteSpace(value) && !WebUrls.IsHttpUrl(value))
        {
            errors[field] =
            [
                $"El enlace de {network} debe ser una dirección web completa que empiece por https:// (por ejemplo {example})."
            ];
        }
    }

    private static void ValidateEnableRequest(EnableProfessionalProfileRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        AddErrorIf(errors, "displayName",
            string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length is < 2 or > 120,
            "DisplayName must be between 2 and 120 characters.");
        AddErrorIf(errors, "defaultAppointmentDurationMinutes",
            request.DefaultAppointmentDurationMinutes is not null and not 0 &&
            (request.DefaultAppointmentDurationMinutes < MinimumAppointmentDurationMinutes || request.DefaultAppointmentDurationMinutes > MaximumAppointmentDurationMinutes),
            $"DefaultAppointmentDurationMinutes must be between {MinimumAppointmentDurationMinutes} and {MaximumAppointmentDurationMinutes}.");

        ThrowIfAnyErrors(errors);
    }

    private static void ValidateCommonAdminFields(
        IDictionary<string, string[]> errors,
        string fullName,
        string email,
        string displayName,
        string? phoneNumber,
        string? bio,
        int? defaultAppointmentDurationMinutes)
    {
        AddErrorIf(errors, "fullName", string.IsNullOrWhiteSpace(fullName) || fullName.Trim().Length is < 2 or > 120,
            "FullName must be between 2 and 120 characters.");
        AddErrorIf(errors, "email", !IsValidEmail(email), "Email must be a valid email address.");
        AddErrorIf(errors, "displayName", string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length is < 2 or > 120,
            "DisplayName must be between 2 and 120 characters.");
        AddErrorIf(errors, "phoneNumber", !string.IsNullOrWhiteSpace(phoneNumber) && phoneNumber.Trim().Length > 40,
            "PhoneNumber must be 40 characters or fewer.");
        AddErrorIf(errors, "bio", !string.IsNullOrWhiteSpace(bio) && bio.Trim().Length > 2000,
            "Bio must be 2000 characters or fewer.");
        AddErrorIf(errors, "defaultAppointmentDurationMinutes",
            defaultAppointmentDurationMinutes is not null and not 0 &&
            (defaultAppointmentDurationMinutes < MinimumAppointmentDurationMinutes || defaultAppointmentDurationMinutes > MaximumAppointmentDurationMinutes),
            $"DefaultAppointmentDurationMinutes must be between {MinimumAppointmentDurationMinutes} and {MaximumAppointmentDurationMinutes}.");
    }

    private static bool IsValidEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        try
        {
            _ = new MailAddress(email.Trim());
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string NormalizeEmail(string value) => value.Trim().ToUpperInvariant();

    private static void AddErrorIf(IDictionary<string, string[]> errors, string key, bool condition, string message)
    {
        if (condition)
        {
            errors[key] = [message];
        }
    }

    private static void ThrowIfAnyErrors(Dictionary<string, string[]> errors)
    {
        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }
    }
}
