using Barbershop.Application.Customers.Admin;
using Barbershop.Application.ServiceRecords;

namespace Barbershop.Tests.Features.ServiceRecords;

/// <summary>Approvers only see what identifies and selects a customer profile; accounts are never identified.</summary>
public sealed class ServiceRecordCustomerPrivacyTests
{
  [Theory]
  [InlineData("300 765 4321", "•••4321")]
  [InlineData("+57 (300) 123-4567", "•••4567")]
  [InlineData("123", "•••")]
  [InlineData(null, "•••")]
  public void PhoneHint_KeepsOnlyTheLastFourDigits(string? phone, string expected)
      => Assert.Equal(expected, ServiceRecordCustomerPrivacy.PhoneHint(phone));

  [Theory]
  [InlineData("carla.perez@example.com", "c•••@example.com")]
  [InlineData("  Ana@Mail.co ", "A•••@Mail.co")]
  [InlineData("sin-arroba", "•••")]
  [InlineData("", null)]
  public void EmailHint_KeepsTheFirstLetterAndTheDomain(string? email, string? expected)
      => Assert.Equal(expected, ServiceRecordCustomerPrivacy.EmailHint(email));

  [Fact]
  public void ToDuplicate_IdentifiesProfilesOnly_NeverAccounts()
  {
    var profileId = Guid.NewGuid();
    var userId = Guid.NewGuid();

    var profile = ServiceRecordCustomerPrivacy.ToDuplicate(
        new CustomerDuplicateCandidate("customer", profileId, "Carla Pérez", "carla@example.com", "3001234567", ["phone"], true));
    var account = ServiceRecordCustomerPrivacy.ToDuplicate(
        new CustomerDuplicateCandidate("account", userId, "Carla P.", "carla@example.com", null, ["email"], false));

    Assert.Equal(("customer", (Guid?)profileId, "c•••@example.com", "•••4567"), (profile.Kind, profile.CustomerId, profile.EmailHint, profile.PhoneHint));
    Assert.Equal(("account", (Guid?)null, (string?)null), (account.Kind, account.CustomerId, account.PhoneHint));
    Assert.Equal(["email"], account.MatchedOn);
  }
}
