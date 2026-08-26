using System.Security.Claims;
using Barbershop.Application.Auth;
using Barbershop.Domain.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Barbershop.Tests.Features.Authorization;

public sealed class SalesRegisterPolicyTests
{
  private static IAuthorizationService BuildAuthorizationService()
  {
    var services = new ServiceCollection();
    services.AddAuthorization(options =>
    {
      options.AddPolicy(PermissionPolicyNames.SalesRegister,
          policy => policy.RequireClaim(PermissionClaimTypes.Permission, PermissionCodes.SalesRegister));
    });
    services.AddLogging();
    return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
  }

  [Fact]
  public async Task SalesRegisterPolicy_Succeeds_ForPrincipalWithClaim()
  {
    var authorizationService = BuildAuthorizationService();
    var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(PermissionClaimTypes.Permission, PermissionCodes.SalesRegister)], "TestAuth"));

    var result = await authorizationService.AuthorizeAsync(principal, PermissionPolicyNames.SalesRegister);

    Assert.True(result.Succeeded);
  }

  [Fact]
  public async Task SalesRegisterPolicy_Fails_ForPrincipalWithoutClaim()
  {
    var authorizationService = BuildAuthorizationService();
    var principal = new ClaimsPrincipal(new ClaimsIdentity([], "TestAuth"));

    var result = await authorizationService.AuthorizeAsync(principal, PermissionPolicyNames.SalesRegister);

    Assert.False(result.Succeeded);
  }
}
