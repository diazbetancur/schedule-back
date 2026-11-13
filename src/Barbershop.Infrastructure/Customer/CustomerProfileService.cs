using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Customer;
using Barbershop.Application.Media;
using Barbershop.Application.Storage;
using Barbershop.Domain.Common;
using Barbershop.Domain.Media;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Identity;
using Barbershop.Infrastructure.Media;
using Barbershop.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Customer;

internal sealed class CustomerProfileService : ICustomerProfileService
{
    private static readonly DateOnly MinDateOfBirth = new(1900, 1, 1);

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
        ValidateUpdateRequest(request, DateOnly.FromDateTime(BogotaClock.ToLocal(_timeProvider.GetUtcNow().UtcDateTime)));

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
                request.Content,
                ImagesOnly: true),
            cancellationToken);

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var previousPhotoId = user.ProfilePhotoMediaAssetId;

        // Point the profile to the new photo first; only then delete the old file, so a failure in
        // between never leaves the customer pointing to a photo that no longer exists.
        user.SetProfilePhoto(mediaView.Id, utcNow);
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (previousPhotoId is { } oldPhotoId && oldPhotoId != mediaView.Id)
        {
            await ReleasePhotoAsync(currentUserId, oldPhotoId, "Replaced by new customer photo", cancellationToken);
        }

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
        await ReleasePhotoAsync(currentUserId, assetId, "Removed by customer from profile", cancellationToken);
        return await MapAsync(user, cancellationToken);
    }

    public async Task ChangePasswordAsync(
        Guid currentUserId,
        CustomerPasswordChangeRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidatePasswordChangeRequest(request);

        // Changing the password ends every session of the account, including the ones opened by whoever knew the
        // old password (BUG-015). Under the account lock, so a refresh or login committing at the same time cannot
        // leave a session alive. The caller opens a new session for the device that made the change.
        await AccountSessions.InAccountTransactionAsync(_dbContext, async () =>
        {
            await AccountSessions.LockAccountAsync(_dbContext, currentUserId, cancellationToken);

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
            await AccountSessions.RevokeAllRefreshTokensAsync(_dbContext, user.Id, utcNow, cancellationToken);

            await _dbContext.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
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

    /// <summary>
    /// Deletes the previous avatar file only when it is a customer photo this same account uploaded and nothing else
    /// uses it (BUG-019): an avatar that points to someone else's file, or to a staff photo, is only unlinked.
    /// </summary>
    private Task ReleasePhotoAsync(Guid currentUserId, Guid mediaAssetId, string reason, CancellationToken cancellationToken)
        => MediaAssetReferences.ReleaseAsync(
            _dbContext,
            _fileStorageService,
            _timeProvider,
            mediaAssetId,
            asset => Task.FromResult(asset.Purpose == MediaAssetPurpose.CustomerReference && asset.UploadedByUserId == currentUserId),
            reason,
            cancellationToken);

    private static void ValidateUpdateRequest(CustomerProfileUpdateRequest request, DateOnly todayInBogota)
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

        // BUG-059: between 1900-01-01 and today (Bogotá). No minimum age: the legal pages allow profiles of minors
        // managed by an adult, so there is no documented rule to enforce.
        if (request.DateOfBirth is { } dateOfBirth && (dateOfBirth < MinDateOfBirth || dateOfBirth > todayInBogota))
        {
            errors["dateOfBirth"] = ["La fecha de nacimiento debe estar entre el 1 de enero de 1900 y hoy."];
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
        // BUG-018: the policy of registration and reset (8–128), and never the current password again.
        else if (request.NewPassword.Length is < 8 or > 128)
        {
            errors["newPassword"] = ["La nueva contraseña debe tener entre 8 y 128 caracteres."];
        }
        else if (string.Equals(request.NewPassword, request.CurrentPassword, StringComparison.Ordinal))
        {
            errors["newPassword"] = ["La nueva contraseña debe ser distinta de la actual."];
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
