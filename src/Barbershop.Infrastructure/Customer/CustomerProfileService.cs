using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Customer;
using Barbershop.Application.Media;
using Barbershop.Application.Storage;
using Barbershop.Domain.Media;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Identity;
using Barbershop.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Customer;

internal sealed class CustomerProfileService : ICustomerProfileService
{
    private readonly AppDbContext _dbContext;
    private readonly IPasswordHasher<object> _passwordHasher;
    private readonly IMediaAssetsService _mediaAssetsService;
    private readonly IFileStorageService _fileStorageService;
    private readonly TimeProvider _timeProvider;

    public CustomerProfileService(
        AppDbContext dbContext,
        IPasswordHasher<object> passwordHasher,
        IMediaAssetsService mediaAssetsService,
        IFileStorageService fileStorageService,
        TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _passwordHasher = passwordHasher;
        _mediaAssetsService = mediaAssetsService;
        _fileStorageService = fileStorageService;
        _timeProvider = timeProvider;
    }

    public async Task<CustomerProfileView> GetAsync(Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var user = await LoadActiveUserAsync(currentUserId, cancellationToken);
        return await MapAsync(user, cancellationToken);
    }

    public async Task<CustomerProfileView> UpdateAsync(
        Guid currentUserId,
        CustomerProfileUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateUpdateRequest(request);

        var user = await LoadActiveUserAsync(currentUserId, cancellationToken);
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        user.UpdateCustomerProfile(request.FullName, request.PhoneNumber, request.DateOfBirth, utcNow);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return await MapAsync(user, cancellationToken);
    }

    public async Task<CustomerProfileView> UploadPhotoAsync(
        Guid currentUserId,
        CustomerPhotoUploadRequest request,
        CancellationToken cancellationToken = default)
    {
        var user = await LoadActiveUserAsync(currentUserId, cancellationToken);

        var mediaView = await _mediaAssetsService.UploadAsync(
            currentUserId,
            [RoleNames.Customer],
            new MediaAssetUploadRequest(
                request.FileName,
                request.ContentType,
                request.SizeBytes,
                MediaAssetPurpose.CustomerReference,
                request.Content),
            cancellationToken);

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        if (user.ProfilePhotoMediaAssetId.HasValue)
        {
            await TryDeleteMediaAssetAsync(user.ProfilePhotoMediaAssetId.Value, "Replaced by new customer photo", cancellationToken);
        }

        user.SetProfilePhoto(mediaView.Id, utcNow);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return await MapAsync(user, cancellationToken);
    }

    public async Task<CustomerProfileView> RemovePhotoAsync(Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var user = await LoadActiveUserAsync(currentUserId, cancellationToken);

        if (!user.ProfilePhotoMediaAssetId.HasValue)
        {
            return await MapAsync(user, cancellationToken);
        }

        var assetId = user.ProfilePhotoMediaAssetId.Value;
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        user.SetProfilePhoto(null, utcNow);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await TryDeleteMediaAssetAsync(assetId, "Removed by customer from profile", cancellationToken);
        return await MapAsync(user, cancellationToken);
    }

    public async Task ChangePasswordAsync(
        Guid currentUserId,
        CustomerPasswordChangeRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidatePasswordChangeRequest(request);

        var user = await LoadActiveUserAsync(currentUserId, cancellationToken);

        var verification = _passwordHasher.VerifyHashedPassword(new object(), user.PasswordHash, request.CurrentPassword);
        if (verification == PasswordVerificationResult.Failed)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>
            {
                ["currentPassword"] = ["The current password is incorrect."]
            });
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var newHash = _passwordHasher.HashPassword(new object(), request.NewPassword);
        user.SetPasswordHash(newHash, utcNow);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<User> LoadActiveUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await _dbContext.Users
            .SingleOrDefaultAsync(user => user.Id == userId && user.IsActive, cancellationToken)
            ?? throw new KeyNotFoundException("The current user was not found.");
    }

    private async Task<CustomerProfileView> MapAsync(User user, CancellationToken cancellationToken)
    {
        string? photoUrl = null;

        if (user.ProfilePhotoMediaAssetId.HasValue)
        {
            var asset = await _dbContext.MediaAssets
                .AsNoTracking()
                .Where(a => a.Id == user.ProfilePhotoMediaAssetId.Value)
                .Select(a => new { a.StorageKey, a.PublicUrl })
                .SingleOrDefaultAsync(cancellationToken);

            if (asset is not null)
            {
                photoUrl = asset.PublicUrl ?? _fileStorageService.GetPublicUrl(asset.StorageKey);
            }
        }

        return new CustomerProfileView(
            user.Id,
            user.FullName,
            user.Email,
            user.PhoneNumber,
            user.DateOfBirth,
            photoUrl);
    }

    private async Task TryDeleteMediaAssetAsync(Guid mediaAssetId, string reason, CancellationToken cancellationToken)
    {
        var asset = await _dbContext.MediaAssets
            .SingleOrDefaultAsync(a => a.Id == mediaAssetId, cancellationToken);

        if (asset is null || asset.Status == MediaAssetStatus.Archived)
        {
            return;
        }

        try
        {
            await _fileStorageService.DeleteAsync(asset.StorageKey, cancellationToken);
            asset.Archive(_timeProvider.GetUtcNow().UtcDateTime);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var pending = new Barbershop.Domain.Media.PendingFileDeletion(asset.StorageKey, reason, _timeProvider.GetUtcNow().UtcDateTime);
            _dbContext.PendingFileDeletions.Add(pending);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private static void ValidateUpdateRequest(CustomerProfileUpdateRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(request.FullName))
        {
            errors["fullName"] = ["Full name is required."];
        }
        else if (request.FullName.Length > 120)
        {
            errors["fullName"] = ["Full name must be 120 characters or fewer."];
        }

        if (request.PhoneNumber is not null && request.PhoneNumber.Length > 40)
        {
            errors["phoneNumber"] = ["Phone number must be 40 characters or fewer."];
        }

        ThrowIfAnyErrors(errors);
    }

    private static void ValidatePasswordChangeRequest(CustomerPasswordChangeRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(request.CurrentPassword))
        {
            errors["currentPassword"] = ["Current password is required."];
        }

        if (string.IsNullOrWhiteSpace(request.NewPassword))
        {
            errors["newPassword"] = ["New password is required."];
        }
        else if (request.NewPassword.Length < 8)
        {
            errors["newPassword"] = ["New password must be at least 8 characters."];
        }
        else if (request.NewPassword.Length > 256)
        {
            errors["newPassword"] = ["New password must be 256 characters or fewer."];
        }

        ThrowIfAnyErrors(errors);
    }

    private static void ThrowIfAnyErrors(Dictionary<string, string[]> errors)
    {
        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }
    }
}
