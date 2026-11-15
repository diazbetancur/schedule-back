using System.Security.Claims;
using Barbershop.Application.Auth;
using Barbershop.Domain.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Barbershop.Tests.Features.Authorization;

public sealed class NonCriticalPermissionPolicyTests
{
  private static IAuthorizationService BuildAuthorizationService()
  {
    var services = new ServiceCollection();
    services.AddAuthorization(options =>
    {
      options.AddPolicy(PermissionPolicyNames.InventoryView,
          policy => policy.RequireClaim(PermissionClaimTypes.Permission, PermissionCodes.InventoryView));
    });
    services.AddLogging();
    return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
  }

  [Fact]
  public async Task NonCriticalPolicy_Succeeds_ForPrincipalWithClaim()
  {
    var authorizationService = BuildAuthorizationService();
    var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(PermissionClaimTypes.Permission, PermissionCodes.InventoryView)], "TestAuth"));

    var result = await authorizationService.AuthorizeAsync(principal, PermissionPolicyNames.InventoryView);

    Assert.True(result.Succeeded);
  }

  [Fact]
  public async Task NonCriticalPolicy_Fails_ForPrincipalWithoutClaim()
  {
    var authorizationService = BuildAuthorizationService();
    var principal = new ClaimsPrincipal(new ClaimsIdentity([], "TestAuth"));

    var result = await authorizationService.AuthorizeAsync(principal, PermissionPolicyNames.InventoryView);

    Assert.False(result.Succeeded);
  }
}
