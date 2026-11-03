using System.Security.Claims;
using Api.Barbershop.Configuration;
using Barbershop.Application.Auth;
using Barbershop.Domain.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Barbershop.Tests.Features.Authorization;

/// <summary>Exercises the same policy registration the API uses, so a new permission code cannot ship without its policy.</summary>
public sealed class PermissionPoliciesTests
{
  private static readonly Guid UserId = Guid.NewGuid();

  private readonly FakePermissionVerifier _verifier = new();

  private IServiceProvider BuildServices()
  {
    var services = new ServiceCollection();
    services.AddAuthorization(options => options.AddPermissionPolicies());
    services.AddScoped<IAuthorizationHandler, CurrentPermissionAuthorizationHandler>();
    services.AddSingleton<IUserPermissionVerifier>(_verifier);
    services.AddLogging();
    return services.BuildServiceProvider();
  }

  private IAuthorizationService BuildAuthorizationService() => BuildServices().GetRequiredService<IAuthorizationService>();

  private static ClaimsPrincipal PrincipalWith(params Claim[] claims)
      => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId.ToString()), .. claims], "TestAuth"));

  [Fact]
  public void Catalog_IncludesTheLoyaltyPreparationPermissions()
  {
    string[] expected =
    [
      PermissionCodes.SalesRegister,
      PermissionCodes.ServiceRecordsRegister,
      PermissionCodes.ServiceRecordsApprove,
      PermissionCodes.LoyaltySettingsManage,
      PermissionCodes.LoyaltyRewardsManage,
      PermissionCodes.LoyaltyRedemptionsApprove,
      PermissionCodes.LoyaltyRedemptionsDeliver,
      PermissionCodes.LoyaltyPointsAdjust,
      PermissionCodes.MembershipPaymentsConfirm,
      PermissionCodes.CustomerClaimsApprove,
    ];

    Assert.Equal(expected.OrderBy(code => code, StringComparer.Ordinal), PermissionCodes.All.OrderBy(code => code, StringComparer.Ordinal));
    Assert.Equal(PermissionCodes.All.Count, PermissionCodes.All.Distinct(StringComparer.Ordinal).Count());
  }

  [Fact]
  public void Catalog_MarksExactlyTheSensitiveOperationsAsCritical()
  {
    string[] expected =
    [
      PermissionCodes.ServiceRecordsApprove,
      PermissionCodes.LoyaltySettingsManage,
      PermissionCodes.LoyaltyRedemptionsApprove,
      PermissionCodes.LoyaltyRedemptionsDeliver,
      PermissionCodes.LoyaltyPointsAdjust,
      PermissionCodes.MembershipPaymentsConfirm,
      PermissionCodes.CustomerClaimsApprove,
    ];

    Assert.Equal(expected.OrderBy(code => code, StringComparer.Ordinal), PermissionCodes.Critical.OrderBy(code => code, StringComparer.Ordinal));
    Assert.All(PermissionCodes.Critical, code => Assert.Contains(code, PermissionCodes.All));
    // Existing and everyday permissions keep their token-only policies.
    Assert.DoesNotContain(PermissionCodes.SalesRegister, PermissionCodes.Critical);
    Assert.DoesNotContain(PermissionCodes.ServiceRecordsRegister, PermissionCodes.Critical);
    Assert.DoesNotContain(PermissionCodes.LoyaltyRewardsManage, PermissionCodes.Critical);
  }

  [Fact]
  public void EveryPermissionCode_HasItsOwnPolicyName()
  {
    var policyNames = PermissionCodes.All.Select(PermissionPolicyNames.For).ToArray();

    Assert.Equal(policyNames.Length, policyNames.Distinct(StringComparer.Ordinal).Count());
    Assert.Equal("SalesRegisterPolicy", PermissionPolicyNames.For(PermissionCodes.SalesRegister));
    Assert.Throws<ArgumentException>(() => PermissionPolicyNames.For("unknown.permission"));
    // Replaced by separate approve and deliver permissions before it ever shipped.
    Assert.DoesNotContain("loyalty.redemptions.manage", PermissionCodes.All);
  }

  [Fact]
  public async Task OnlyCriticalPolicies_CarryTheDatabaseRequirement()
  {
    var policyProvider = BuildServices().GetRequiredService<IAuthorizationPolicyProvider>();

    foreach (var code in PermissionCodes.All)
    {
      var policy = await policyProvider.GetPolicyAsync(PermissionPolicyNames.For(code));
      Assert.NotNull(policy);

      var databaseRequirements = policy!.Requirements.OfType<CurrentPermissionRequirement>().ToArray();
      if (PermissionCodes.Critical.Contains(code))
      {
        Assert.Equal(code, Assert.Single(databaseRequirements).PermissionCode);
      }
      else
      {
        Assert.Empty(databaseRequirements);
      }
    }
  }

  [Fact]
  public async Task EveryPolicy_SucceedsOnlyWithItsOwnPermissionClaim()
  {
    var authorizationService = BuildAuthorizationService();

    foreach (var code in PermissionCodes.All)
    {
      var holder = PrincipalWith(new Claim(PermissionClaimTypes.Permission, code));

      foreach (var other in PermissionCodes.All)
      {
        var result = await authorizationService.AuthorizeAsync(holder, PermissionPolicyNames.For(other));
        Assert.True(result.Succeeded == (other == code), $"Claim '{code}' against policy for '{other}'.");
      }
    }
  }

  [Fact]
  public async Task CriticalPolicies_FailWhenTheDatabaseNoLongerGrantsThePermission_EvenWithTheClaim()
  {
    var authorizationService = BuildAuthorizationService();
    _verifier.Grants = false;

    foreach (var code in PermissionCodes.Critical)
    {
      var result = await authorizationService.AuthorizeAsync(
          PrincipalWith(new Claim(PermissionClaimTypes.Permission, code)), PermissionPolicyNames.For(code));

      Assert.False(result.Succeeded, code);
    }

    Assert.Equal(
        PermissionCodes.Critical.OrderBy(code => code, StringComparer.Ordinal),
        _verifier.Checks.Select(check => check.PermissionCode).OrderBy(code => code, StringComparer.Ordinal));
    Assert.All(_verifier.Checks, check => Assert.Equal(UserId, check.UserId));
  }

  [Fact]
  public async Task NonCriticalPolicies_AreAuthorizedFromTheTokenAlone()
  {
    var authorizationService = BuildAuthorizationService();
    _verifier.Grants = false;

    foreach (var code in PermissionCodes.All.Except(PermissionCodes.Critical))
    {
      var result = await authorizationService.AuthorizeAsync(
          PrincipalWith(new Claim(PermissionClaimTypes.Permission, code)), PermissionPolicyNames.For(code));

      Assert.True(result.Succeeded, code);
    }

    Assert.Empty(_verifier.Checks);
  }

  [Fact]
  public async Task CriticalPolicies_FailWithoutAUserId()
  {
    var authorizationService = BuildAuthorizationService();
    var code = PermissionCodes.LoyaltyRedemptionsApprove;
    var anonymousHolder = new ClaimsPrincipal(new ClaimsIdentity([new Claim(PermissionClaimTypes.Permission, code)], "TestAuth"));

    var result = await authorizationService.AuthorizeAsync(anonymousHolder, PermissionPolicyNames.For(code));

    Assert.False(result.Succeeded);
    Assert.Empty(_verifier.Checks);
  }

  [Fact]
  public async Task Policies_AreNotSatisfiedByARoleAlone()
  {
    // Admin holds every permission through the seeded role assignments, which travel as claims in its token;
    // the role claim by itself is not a shortcut around a permission policy.
    var authorizationService = BuildAuthorizationService();
    var adminWithoutPermissionClaims = PrincipalWith(new Claim(ClaimTypes.Role, RoleNames.Admin));

    foreach (var code in PermissionCodes.All)
    {
      var result = await authorizationService.AuthorizeAsync(adminWithoutPermissionClaims, PermissionPolicyNames.For(code));
      Assert.False(result.Succeeded);
    }
  }

  [Fact]
  public async Task Policies_IgnoreClaimsOfAnotherType()
  {
    var authorizationService = BuildAuthorizationService();
    var principal = PrincipalWith(new Claim("scope", PermissionCodes.LoyaltyPointsAdjust));

    var result = await authorizationService.AuthorizeAsync(principal, PermissionPolicyNames.For(PermissionCodes.LoyaltyPointsAdjust));

    Assert.False(result.Succeeded);
  }

  private sealed class FakePermissionVerifier : IUserPermissionVerifier
  {
    public bool Grants { get; set; } = true;

    public List<(Guid UserId, string PermissionCode)> Checks { get; } = [];

    public Task<bool> HasPermissionAsync(Guid userId, string permissionCode, CancellationToken cancellationToken = default)
    {
      Checks.Add((userId, permissionCode));
      return Task.FromResult(Grants);
    }
  }
}
