using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Loyalty.Admin;
using Barbershop.Domain.Loyalty;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Loyalty;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Tests.Features.Loyalty;

/// <summary>Rules of the global loyalty settings. Concurrency against real PostgreSQL is in AdminLoyaltyHttpIntegrationTests.</summary>
public sealed class LoyaltySettingsServiceTests : IDisposable
{
  private readonly AppDbContext _dbContext;
  private readonly IAdminLoyaltySettingsService _service;
  private readonly User _admin;

  public LoyaltySettingsServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options;

    _dbContext = new AppDbContext(options);
    _dbContext.Database.EnsureCreated(); // applies the seeded settings row, as the migration does
    _admin = new User("Admin Fidelización", $"admin-{Guid.NewGuid():N}@example.com", "hash", DateTime.UtcNow);
    _dbContext.Users.Add(_admin);
    _dbContext.SaveChanges();
    _service = new LoyaltySettingsService(_dbContext, TimeProvider.System);
  }

  public void Dispose() => _dbContext.Dispose();

  [Fact]
  public async Task GetAsync_StartsDisabled_WithTheDefaultGlobalValidity()
  {
    var settings = await _service.GetAsync();

    Assert.False(settings.IsEnabled);
    Assert.Equal(LoyaltySettings.DefaultPointsValidityDays, settings.PointsValidityDays);
    Assert.Null(settings.UpdatedAt);
    Assert.Null(settings.UpdatedByName);
  }

  [Fact]
  public async Task UpdateAsync_ChangesTheGlobalValidityAndStatus_AndAuditsPreviousAndNewValues()
  {
    var current = await _service.GetAsync();

    var updated = await _service.UpdateAsync(_admin.Id, new LoyaltySettingsUpdateRequest(true, 90, current.Version));

    Assert.True(updated.IsEnabled);
    Assert.Equal(90, updated.PointsValidityDays);
    Assert.NotNull(updated.UpdatedAt);
    Assert.Equal(_admin.FullName, updated.UpdatedByName);

    var change = Assert.Single((await _service.GetChangesAsync()).Items);
    Assert.False(change.PreviousIsEnabled);
    Assert.True(change.NewIsEnabled);
    Assert.Equal(LoyaltySettings.DefaultPointsValidityDays, change.PreviousPointsValidityDays);
    Assert.Equal(90, change.NewPointsValidityDays);
    Assert.Equal(_admin.Id, change.ChangedByUserId);
    Assert.Equal(_admin.FullName, change.ChangedByName);
  }

  [Fact]
  public async Task UpdateAsync_WithTheSameValues_WritesNoAuditEntry()
  {
    var current = await _service.GetAsync();

    await _service.UpdateAsync(_admin.Id, new LoyaltySettingsUpdateRequest(current.IsEnabled, current.PointsValidityDays, current.Version));

    Assert.Empty((await _service.GetChangesAsync()).Items);
    Assert.Null((await _service.GetAsync()).UpdatedAt);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(-30)]
  [InlineData(LoyaltySettings.MaxPointsValidityDays + 1)]
  public async Task UpdateAsync_RejectsAValidityOutsideTheAllowedDays(int days)
  {
    var current = await _service.GetAsync();

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.UpdateAsync(_admin.Id, new LoyaltySettingsUpdateRequest(true, days, current.Version)));

    Assert.Contains("pointsValidityDays", exception.Errors.Keys);
    Assert.Empty((await _service.GetChangesAsync()).Items);
  }

  [Theory]
  [InlineData(LoyaltySettings.MinPointsValidityDays)]
  [InlineData(LoyaltySettings.MaxPointsValidityDays)]
  public async Task UpdateAsync_AcceptsTheValidityLimits(int days)
  {
    var current = await _service.GetAsync();

    var updated = await _service.UpdateAsync(_admin.Id, new LoyaltySettingsUpdateRequest(false, days, current.Version));

    Assert.Equal(days, updated.PointsValidityDays);
  }

  [Fact]
  public async Task UpdateAsync_RequiresTheVersion()
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.UpdateAsync(_admin.Id, new LoyaltySettingsUpdateRequest(true, 30, null)));

    Assert.Contains("version", exception.Errors.Keys);
  }

  [Fact]
  public async Task UpdateAsync_WithAStaleVersion_IsAConflict_AndChangesNothing()
  {
    var current = await _service.GetAsync();

    await Assert.ThrowsAsync<ConflictException>(() =>
        _service.UpdateAsync(_admin.Id, new LoyaltySettingsUpdateRequest(true, 30, current.Version + 1)));

    var after = await _service.GetAsync();
    Assert.False(after.IsEnabled);
    Assert.Equal(LoyaltySettings.DefaultPointsValidityDays, after.PointsValidityDays);
    Assert.Empty((await _service.GetChangesAsync()).Items);
  }

  [Fact]
  public async Task GetChangesAsync_ListsTheMostRecentFirst()
  {
    var version = (await _service.GetAsync()).Version;
    foreach (var days in new[] { 30, 60, 120 })
    {
      version = (await _service.UpdateAsync(_admin.Id, new LoyaltySettingsUpdateRequest(true, days, version))).Version;
    }

    var changes = (await _service.GetChangesAsync()).Items;

    Assert.Equal([120, 60, 30], changes.Select(change => change.NewPointsValidityDays));
    Assert.Equal([60, 30, LoyaltySettings.DefaultPointsValidityDays], changes.Select(change => change.PreviousPointsValidityDays));
  }

  [Fact]
  public void Domain_RejectsInvalidValidityDirectly()
  {
    var settings = new LoyaltySettings(false, LoyaltySettings.DefaultPointsValidityDays, DateTime.UtcNow);

    Assert.Throws<ArgumentOutOfRangeException>(() => settings.Update(true, 0, Guid.NewGuid(), DateTime.UtcNow));
    Assert.Throws<ArgumentOutOfRangeException>(() => new LoyaltySettings(false, LoyaltySettings.MaxPointsValidityDays + 1, DateTime.UtcNow));
  }
}
