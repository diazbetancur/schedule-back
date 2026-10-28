using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Customers.Admin;
using Barbershop.Domain.Customers;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Customers;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Barbershop.Tests.Features.Customers;

public sealed class CustomerManagementServiceTests : IDisposable
{
  private readonly AppDbContext _dbContext;
  private readonly IAdminCustomersService _service;
  private readonly Guid _adminId;
  private readonly Role _customerRole;
  private readonly Role _staffRole;

  public CustomerManagementServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        // The in-memory provider has no transactions; merge opens one for PostgreSQL.
        .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
        .Options;

    _dbContext = new AppDbContext(options);
    _service = new CustomerManagementService(_dbContext, TimeProvider.System);

    _customerRole = new Role(RoleNames.Customer, isSystemRole: true);
    _staffRole = new Role(RoleNames.Staff, isSystemRole: true);
    _dbContext.Roles.AddRange(_customerRole, _staffRole);

    var admin = new User("Admin Principal", "admin@example.com", "hash", DateTime.UtcNow);
    _dbContext.Users.Add(admin);
    _dbContext.SaveChanges();
    _adminId = admin.Id;
  }

  public void Dispose() => _dbContext.Dispose();

  [Fact]
  public async Task CreateAsync_CreatesProfileWithoutAccount_AndRecordsCreatedEvent()
  {
    var view = await _service.CreateAsync(_adminId, new CustomerCreateRequest("Carlos Pérez", "+57 300 123 4567", "carlos@example.com"));

    Assert.Equal("Carlos Pérez", view.FullName);
    Assert.Equal("+57 300 123 4567", view.Phone);
    Assert.Null(view.LinkedAccount);
    var created = Assert.Single(view.Events);
    Assert.Equal(nameof(CustomerEventType.Created), created.Type);
    Assert.Equal("Admin Principal", created.PerformedByName);
  }

  [Fact]
  public async Task CreateAsync_RequiresNamePhoneAndEmail()
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(_adminId, new CustomerCreateRequest(" ", "12", "no-es-correo")));

    Assert.Contains("fullName", exception.Errors.Keys);
    Assert.Contains("phone", exception.Errors.Keys);
    Assert.Contains("email", exception.Errors.Keys);
  }

  [Fact]
  public async Task SearchAsync_MatchesNameEmailAndPhoneDigits_AndExcludesMergedProfiles()
  {
    var carlos = await CreateCustomerAsync("Carlos Pérez", "300-123-4567", "carlos@example.com");
    await CreateCustomerAsync("Ana Gómez", "3109998888", "ana@example.com");
    var duplicate = await CreateCustomerAsync("Carlos P.", "3001234567", "carlos.p@example.com");

    Assert.Equal(2, (await _service.SearchAsync("carlos")).Count);
    Assert.Single(await _service.SearchAsync("ANA@EXAMPLE"));
    Assert.Equal(2, (await _service.SearchAsync("3001234567")).Count);

    await _service.MergeAsync(_adminId, carlos.Id, new CustomerMergeRequest(duplicate.Id));

    var afterMerge = await _service.SearchAsync("carlos");
    Assert.Equal(carlos.Id, Assert.Single(afterMerge).Id);
  }

  [Fact]
  public async Task FindDuplicatesAsync_MatchesProfilesAndAccountsByEmailOrNormalizedPhone()
  {
    var existing = await CreateCustomerAsync("Carlos Pérez", "+57 300 123 4567", "carlos@example.com");
    await CreateUserAsync("Carlos Cuenta", "CARLOS@example.com", _customerRole, phone: "573001234567");

    var byEmail = await _service.FindDuplicatesAsync("carlos@example.com", null, null);
    Assert.Contains(byEmail, candidate => candidate.Kind == "customer" && candidate.Id == existing.Id);
    Assert.Contains(byEmail, candidate => candidate.Kind == "account" && candidate.MatchedOn.Contains("email"));

    var byPhone = await _service.FindDuplicatesAsync(null, "573001234567", null);
    Assert.Equal(2, byPhone.Count);
    Assert.All(byPhone, candidate => Assert.Contains("phone", candidate.MatchedOn));

    var excludingItself = await _service.FindDuplicatesAsync("carlos@example.com", null, existing.Id);
    Assert.DoesNotContain(excludingItself, candidate => candidate.Id == existing.Id);
  }

  [Fact]
  public async Task FindDuplicatesAsync_TreatsNumbersWithAndWithoutCountryCodeAsTheSamePhone()
  {
    var withCountryCode = await CreateCustomerAsync("Carlos Pérez", "+57 300 123 4567", "carlos@example.com");
    await CreateUserAsync("Carlos Cuenta", "cuenta@example.com", _customerRole, phone: "300 123 4567");

    var matches = await _service.FindDuplicatesAsync(null, "300-123-4567", null);

    Assert.Contains(matches, candidate => candidate.Kind == "customer" && candidate.Id == withCountryCode.Id);
    Assert.Contains(matches, candidate => candidate.Kind == "account");
    Assert.Empty(await _service.FindDuplicatesAsync(null, "300 123 9999", null));
  }

  [Fact]
  public async Task UpdateAsync_ChangesDetails_ButRejectsMergedProfile()
  {
    var target = await CreateCustomerAsync("Carlos Pérez", "3001234567", "carlos@example.com");
    var source = await CreateCustomerAsync("Carlos P.", "3001234567", "carlos.p@example.com");

    var updated = await _service.UpdateAsync(target.Id, new CustomerUpdateRequest("Carlos Andrés Pérez", "3001112233", "carlos.andres@example.com"));
    Assert.Equal("Carlos Andrés Pérez", updated.FullName);
    Assert.Equal("carlos.andres@example.com", updated.Email);

    await _service.MergeAsync(_adminId, target.Id, new CustomerMergeRequest(source.Id));

    await Assert.ThrowsAsync<ConflictException>(() =>
        _service.UpdateAsync(source.Id, new CustomerUpdateRequest("Otro", "3001234567", "otro@example.com")));
  }

  [Fact]
  public async Task GetAccountCandidatesAsync_ReturnsOnlyActiveUnlinkedCustomerAccounts()
  {
    var free = await CreateUserAsync("Cliente Libre", "libre@example.com", _customerRole);
    var linked = await CreateUserAsync("Cliente Vinculado", "vinculado@example.com", _customerRole);
    await CreateUserAsync("Barbero", "barbero@example.com", _staffRole);
    var inactive = await CreateUserAsync("Cliente Inactivo", "inactivo@example.com", _customerRole);
    inactive.Deactivate(DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();

    var customer = await CreateCustomerAsync("Perfil", "3001234567", "perfil@example.com");
    await _service.LinkAccountAsync(_adminId, customer.Id, new CustomerLinkAccountRequest(linked.Id, IdentityVerified: true));

    var candidates = await _service.GetAccountCandidatesAsync(null);

    Assert.Equal(free.Id, Assert.Single(candidates).UserId);
    Assert.Single(await _service.GetAccountCandidatesAsync("LIBRE"));
    Assert.Empty(await _service.GetAccountCandidatesAsync("nadie"));
  }

  [Fact]
  public async Task LinkAccountAsync_RequiresExplicitIdentityVerification()
  {
    var user = await CreateUserAsync("Cliente", "cliente@example.com", _customerRole);
    var customer = await CreateCustomerAsync("Cliente", "3001234567", "cliente@example.com");

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.LinkAccountAsync(_adminId, customer.Id, new CustomerLinkAccountRequest(user.Id, IdentityVerified: false)));

    Assert.Contains("identityVerified", exception.Errors.Keys);
    Assert.Null((await _service.GetByIdAsync(customer.Id)).LinkedAccount);
  }

  [Fact]
  public async Task LinkAccountAsync_LinksAccount_AndRecordsWhoAndWhen()
  {
    var user = await CreateUserAsync("Cliente App", "app@example.com", _customerRole, phone: "3001234567");
    var customer = await CreateCustomerAsync("Cliente", "3001234567", "cliente@example.com");

    var view = await _service.LinkAccountAsync(_adminId, customer.Id, new CustomerLinkAccountRequest(user.Id, IdentityVerified: true));

    Assert.NotNull(view.LinkedAccount);
    Assert.Equal(user.Id, view.LinkedAccount!.UserId);
    Assert.Equal("app@example.com", view.LinkedAccount.Email);
    Assert.Equal("Admin Principal", view.LinkedAccount.LinkedByName);
    var linkedEvent = Assert.Single(view.Events, customerEvent => customerEvent.Type == nameof(CustomerEventType.AccountLinked));
    Assert.Equal(user.Id, linkedEvent.UserId);
    Assert.Equal("app@example.com", linkedEvent.UserEmail);
  }

  [Fact]
  public async Task LinkAccountAsync_RejectsAccountAlreadyLinkedToAnotherProfile()
  {
    var user = await CreateUserAsync("Cliente", "cliente@example.com", _customerRole);
    var first = await CreateCustomerAsync("Primero", "3001234567", "uno@example.com");
    var second = await CreateCustomerAsync("Segundo", "3007654321", "dos@example.com");
    await _service.LinkAccountAsync(_adminId, first.Id, new CustomerLinkAccountRequest(user.Id, true));

    var exception = await Assert.ThrowsAsync<ConflictException>(() =>
        _service.LinkAccountAsync(_adminId, second.Id, new CustomerLinkAccountRequest(user.Id, true)));

    Assert.Equal("Esta cuenta ya está vinculada a otro cliente.", exception.Message);
  }

  [Fact]
  public async Task LinkAccountAsync_RejectsSecondAccountOnSameProfile()
  {
    var userA = await CreateUserAsync("Cliente A", "a@example.com", _customerRole);
    var userB = await CreateUserAsync("Cliente B", "b@example.com", _customerRole);
    var customer = await CreateCustomerAsync("Perfil", "3001234567", "perfil@example.com");
    await _service.LinkAccountAsync(_adminId, customer.Id, new CustomerLinkAccountRequest(userA.Id, true));

    await Assert.ThrowsAsync<ConflictException>(() =>
        _service.LinkAccountAsync(_adminId, customer.Id, new CustomerLinkAccountRequest(userB.Id, true)));
  }

  [Fact]
  public async Task LinkAccountAsync_RejectsAccountsWithoutCustomerRole()
  {
    var staff = await CreateUserAsync("Barbero", "barbero@example.com", _staffRole);
    var customer = await CreateCustomerAsync("Perfil", "3001234567", "perfil@example.com");

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.LinkAccountAsync(_adminId, customer.Id, new CustomerLinkAccountRequest(staff.Id, true)));

    Assert.Contains("userId", exception.Errors.Keys);
  }

  [Fact]
  public async Task UnlinkAccountAsync_RequiresReason()
  {
    var user = await CreateUserAsync("Cliente", "cliente@example.com", _customerRole);
    var customer = await CreateCustomerAsync("Perfil", "3001234567", "perfil@example.com");
    await _service.LinkAccountAsync(_adminId, customer.Id, new CustomerLinkAccountRequest(user.Id, true));

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.UnlinkAccountAsync(_adminId, customer.Id, new CustomerUnlinkAccountRequest("  ")));

    Assert.Contains("reason", exception.Errors.Keys);
    Assert.NotNull((await _service.GetByIdAsync(customer.Id)).LinkedAccount);
  }

  [Fact]
  public async Task UnlinkAccountAsync_KeepsProfileAndAccount_RecordsReason_AndFreesAccountForAnotherProfile()
  {
    var user = await CreateUserAsync("Cliente", "cliente@example.com", _customerRole);
    var wrongProfile = await CreateCustomerAsync("Perfil Equivocado", "3001234567", "equivocado@example.com");
    var rightProfile = await CreateCustomerAsync("Perfil Correcto", "3007654321", "correcto@example.com");
    await _service.LinkAccountAsync(_adminId, wrongProfile.Id, new CustomerLinkAccountRequest(user.Id, true));

    var unlinked = await _service.UnlinkAccountAsync(_adminId, wrongProfile.Id, new CustomerUnlinkAccountRequest("Vinculada por error"));

    Assert.Null(unlinked.LinkedAccount);
    Assert.Equal("Perfil Equivocado", unlinked.FullName);
    var unlinkEvent = Assert.Single(unlinked.Events, customerEvent => customerEvent.Type == nameof(CustomerEventType.AccountUnlinked));
    Assert.Equal("Vinculada por error", unlinkEvent.Reason);
    Assert.Equal(user.Id, unlinkEvent.UserId);
    // The original link event stays in the history.
    Assert.Contains(unlinked.Events, customerEvent => customerEvent.Type == nameof(CustomerEventType.AccountLinked));
    Assert.True(await _dbContext.Users.AnyAsync(candidate => candidate.Id == user.Id && candidate.IsActive));

    var relinked = await _service.LinkAccountAsync(_adminId, rightProfile.Id, new CustomerLinkAccountRequest(user.Id, true));
    Assert.Equal(user.Id, relinked.LinkedAccount!.UserId);
  }

  [Fact]
  public async Task UnlinkAccountAsync_RejectsProfileWithoutAccount()
  {
    var customer = await CreateCustomerAsync("Perfil", "3001234567", "perfil@example.com");

    await Assert.ThrowsAsync<ConflictException>(() =>
        _service.UnlinkAccountAsync(_adminId, customer.Id, new CustomerUnlinkAccountRequest("Sin cuenta")));
  }

  [Fact]
  public async Task MergeAsync_TransfersSourceAccountToTarget_MarksSourceAsMerged_AndAuditsBothSides()
  {
    var user = await CreateUserAsync("Cliente", "cliente@example.com", _customerRole);
    var target = await CreateCustomerAsync("Carlos Pérez", "3001234567", "carlos@example.com");
    var source = await CreateCustomerAsync("Carlos P.", "3001234567", "carlos.p@example.com");
    await _service.LinkAccountAsync(_adminId, source.Id, new CustomerLinkAccountRequest(user.Id, true));

    var preview = await _service.GetMergePreviewAsync(target.Id, source.Id);
    Assert.True(preview.CanMerge);
    Assert.Equal(user.Id, preview.AccountToTransfer!.UserId);

    var merged = await _service.MergeAsync(_adminId, target.Id, new CustomerMergeRequest(source.Id));

    Assert.Equal(user.Id, merged.LinkedAccount!.UserId);
    var mergedFrom = Assert.Single(merged.Events, customerEvent => customerEvent.Type == nameof(CustomerEventType.MergedFrom));
    Assert.Equal(source.Id, mergedFrom.RelatedCustomerId);
    Assert.Equal("Carlos P.", mergedFrom.RelatedCustomerName);
    Assert.Equal(user.Id, mergedFrom.UserId);

    var sourceAfter = await _service.GetByIdAsync(source.Id);
    Assert.Equal(target.Id, sourceAfter.MergedIntoCustomerId);
    Assert.Equal("Carlos Pérez", sourceAfter.MergedIntoCustomerName);
    Assert.Null(sourceAfter.LinkedAccount);
    Assert.Contains(sourceAfter.Events, customerEvent => customerEvent.Type == nameof(CustomerEventType.MergedInto));
    // Nothing is physically deleted.
    Assert.Equal(2, await _dbContext.Customers.CountAsync());
  }

  [Fact]
  public async Task MergeAsync_KeepsTargetAccount_WhenOnlyTargetIsLinked()
  {
    var user = await CreateUserAsync("Cliente", "cliente@example.com", _customerRole);
    var target = await CreateCustomerAsync("Destino", "3001234567", "destino@example.com");
    var source = await CreateCustomerAsync("Origen", "3001234567", "origen@example.com");
    await _service.LinkAccountAsync(_adminId, target.Id, new CustomerLinkAccountRequest(user.Id, true));

    var merged = await _service.MergeAsync(_adminId, target.Id, new CustomerMergeRequest(source.Id));

    Assert.Equal(user.Id, merged.LinkedAccount!.UserId);
    Assert.Null(Assert.Single(merged.Events, customerEvent => customerEvent.Type == nameof(CustomerEventType.MergedFrom)).UserId);
  }

  [Fact]
  public async Task MergeAsync_IsBlocked_WhenProfilesAreLinkedToDifferentAccounts()
  {
    var userA = await CreateUserAsync("Cliente A", "a@example.com", _customerRole);
    var userB = await CreateUserAsync("Cliente B", "b@example.com", _customerRole);
    var target = await CreateCustomerAsync("Destino", "3001234567", "destino@example.com");
    var source = await CreateCustomerAsync("Origen", "3001234567", "origen@example.com");
    await _service.LinkAccountAsync(_adminId, target.Id, new CustomerLinkAccountRequest(userA.Id, true));
    await _service.LinkAccountAsync(_adminId, source.Id, new CustomerLinkAccountRequest(userB.Id, true));

    var preview = await _service.GetMergePreviewAsync(target.Id, source.Id);
    Assert.False(preview.CanMerge);
    Assert.NotEmpty(preview.BlockingReasons);

    await Assert.ThrowsAsync<ConflictException>(() =>
        _service.MergeAsync(_adminId, target.Id, new CustomerMergeRequest(source.Id)));
    Assert.Null((await _service.GetByIdAsync(source.Id)).MergedIntoCustomerId);
  }

  [Fact]
  public async Task MergeAsync_RejectsSelfMerge_AndAlreadyMergedSource()
  {
    var target = await CreateCustomerAsync("Destino", "3001234567", "destino@example.com");
    var source = await CreateCustomerAsync("Origen", "3001234567", "origen@example.com");
    var other = await CreateCustomerAsync("Otro", "3001234567", "otro@example.com");

    await Assert.ThrowsAsync<ConflictException>(() =>
        _service.MergeAsync(_adminId, target.Id, new CustomerMergeRequest(target.Id)));

    await _service.MergeAsync(_adminId, target.Id, new CustomerMergeRequest(source.Id));

    await Assert.ThrowsAsync<ConflictException>(() =>
        _service.MergeAsync(_adminId, other.Id, new CustomerMergeRequest(source.Id)));
    await Assert.ThrowsAsync<ConflictException>(() =>
        _service.MergeAsync(_adminId, source.Id, new CustomerMergeRequest(other.Id)));
  }

  [Fact]
  public async Task CreateFromAccountAsync_CopiesAccountData_LinksIt_AndRecordsBothEvents()
  {
    var user = await CreateUserAsync("Cliente App", "app@example.com", _customerRole, phone: "300 123 4567");

    var view = await _service.CreateFromAccountAsync(_adminId, new CustomerCreateFromAccountRequest(user.Id, null, IdentityVerified: true));

    Assert.Equal("Cliente App", view.FullName);
    Assert.Equal("app@example.com", view.Email);
    Assert.Equal("300 123 4567", view.Phone);
    Assert.Equal(user.Id, view.LinkedAccount!.UserId);
    Assert.Equal("Admin Principal", view.LinkedAccount.LinkedByName);
    Assert.Contains(view.Events, customerEvent => customerEvent.Type == nameof(CustomerEventType.Created));
    Assert.Contains(view.Events, customerEvent => customerEvent.Type == nameof(CustomerEventType.AccountLinked) && customerEvent.UserId == user.Id);
    Assert.Empty(await _service.GetAccountCandidatesAsync(null));
  }

  [Fact]
  public async Task GetByIdAsync_OrdersEventsNewestFirst_AndBreaksTimestampTiesByLifecycleStep()
  {
    // Create-from-account writes "created" and "account linked" with the same timestamp.
    var user = await CreateUserAsync("Cliente App", "app@example.com", _customerRole, phone: "3001234567");
    var created = await _service.CreateFromAccountAsync(_adminId, new CustomerCreateFromAccountRequest(user.Id, null, true));
    await _service.UnlinkAccountAsync(_adminId, created.Id, new CustomerUnlinkAccountRequest("Prueba de orden"));

    for (var attempt = 0; attempt < 5; attempt++)
    {
      var types = (await _service.GetByIdAsync(created.Id)).Events.Select(customerEvent => customerEvent.Type).ToArray();
      Assert.Equal(
          [nameof(CustomerEventType.AccountUnlinked), nameof(CustomerEventType.AccountLinked), nameof(CustomerEventType.Created)],
          types);
    }
  }

  [Fact]
  public async Task CreateFromAccountAsync_RequiresPhone_WhenTheAccountHasNone()
  {
    var user = await CreateUserAsync("Sin Teléfono", "sin@example.com", _customerRole);

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateFromAccountAsync(_adminId, new CustomerCreateFromAccountRequest(user.Id, null, true)));
    Assert.Contains("phone", exception.Errors.Keys);
    Assert.Empty(await _dbContext.Customers.ToListAsync());

    var view = await _service.CreateFromAccountAsync(_adminId, new CustomerCreateFromAccountRequest(user.Id, "3109998888", true));
    Assert.Equal("3109998888", view.Phone);
  }

  [Fact]
  public async Task CreateFromAccountAsync_RequiresIdentityVerification_AndCreatesNothingWithoutIt()
  {
    var user = await CreateUserAsync("Cliente", "cliente@example.com", _customerRole, phone: "3001234567");

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateFromAccountAsync(_adminId, new CustomerCreateFromAccountRequest(user.Id, null, IdentityVerified: false)));

    Assert.Contains("identityVerified", exception.Errors.Keys);
    Assert.Empty(await _dbContext.Customers.ToListAsync());
    Assert.Empty(await _dbContext.CustomerEvents.ToListAsync());
  }

  [Fact]
  public async Task CreateFromAccountAsync_ReusesLinkRules_ForLinkedInactiveAndNonCustomerAccounts()
  {
    var linked = await CreateUserAsync("Vinculado", "vinculado@example.com", _customerRole, phone: "3001234567");
    await _service.CreateFromAccountAsync(_adminId, new CustomerCreateFromAccountRequest(linked.Id, null, true));
    var staff = await CreateUserAsync("Barbero", "barbero@example.com", _staffRole, phone: "3001234567");
    var inactive = await CreateUserAsync("Inactivo", "inactivo@example.com", _customerRole, phone: "3001234567");
    inactive.Deactivate(DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();

    await Assert.ThrowsAsync<ConflictException>(() =>
        _service.CreateFromAccountAsync(_adminId, new CustomerCreateFromAccountRequest(linked.Id, null, true)));
    await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateFromAccountAsync(_adminId, new CustomerCreateFromAccountRequest(staff.Id, null, true)));
    await Assert.ThrowsAsync<ConflictException>(() =>
        _service.CreateFromAccountAsync(_adminId, new CustomerCreateFromAccountRequest(inactive.Id, null, true)));
    await Assert.ThrowsAsync<NotFoundException>(() =>
        _service.CreateFromAccountAsync(_adminId, new CustomerCreateFromAccountRequest(Guid.NewGuid(), null, true)));

    Assert.Single(await _dbContext.Customers.ToListAsync());
  }

  [Fact]
  public async Task FindDuplicatesAsync_ExcludesTheSourceAccount_AndFlagsProfilesThatAlreadyHaveAnAccount()
  {
    var account = await CreateUserAsync("Carlos App", "carlos@example.com", _customerRole, phone: "3001234567");
    var otherAccount = await CreateUserAsync("Otra", "otra@example.com", _customerRole);
    var freeProfile = await CreateCustomerAsync("Carlos Mostrador", "3001234567", "mostrador@example.com");
    var linkedProfile = await CreateCustomerAsync("Carlos Vinculado", "+57 300 123 4567", "vinc@example.com");
    await _service.LinkAccountAsync(_adminId, linkedProfile.Id, new CustomerLinkAccountRequest(otherAccount.Id, true));

    var matches = await _service.FindDuplicatesAsync("carlos@example.com", "3001234567", null, excludeUserId: account.Id);

    Assert.DoesNotContain(matches, candidate => candidate.Kind == "account" && candidate.Id == account.Id);
    Assert.False(Assert.Single(matches, candidate => candidate.Id == freeProfile.Id).IsLinked);
    Assert.True(Assert.Single(matches, candidate => candidate.Id == linkedProfile.Id).IsLinked);
  }

  [Fact]
  public async Task GetByIdAsync_ThrowsNotFound_ForUnknownCustomer()
  {
    await Assert.ThrowsAsync<NotFoundException>(() => _service.GetByIdAsync(Guid.NewGuid()));
  }

  private Task<CustomerDetailView> CreateCustomerAsync(string fullName, string phone, string email)
      => _service.CreateAsync(_adminId, new CustomerCreateRequest(fullName, phone, email));

  private async Task<User> CreateUserAsync(string fullName, string email, Role role, string? phone = null)
  {
    var user = new User(fullName, email, "hash", DateTime.UtcNow, phone);
    user.UserRoles.Add(new UserRole(user.Id, role.Id, DateTime.UtcNow));
    _dbContext.Users.Add(user);
    await _dbContext.SaveChangesAsync();
    return user;
  }
}
