using Barbershop.Domain.Common;

namespace Barbershop.Tests.Features.Auth;

/// <summary>BUG-004 / BUG-017: the rule for an email written into the system.</summary>
public sealed class EmailRulesTests
{
  [Theory]
  [InlineData("ana@example.com")]
  [InlineData("Ana.Maria+citas@correo.example.co")]
  [InlineData("cliente_01@gmail.com")]
  [InlineData("  espacios.fuera@example.com  ")]
  [InlineData("o'brien@example.ie")]
  public void IsValid_AcceptsPlainAsciiAddresses(string email)
  {
    Assert.True(EmailRules.IsValid(email));
  }

  [Theory]
  [InlineData("a b@c.com")]                       // inner space (BUG-004)
  [InlineData("QA Test <usuario@gmail.com>")]     // display name (BUG-004)
  [InlineData("usuario@localhost")]               // no top-level domain (BUG-004)
  [InlineData("usuario@example.c")]               // one-letter top-level domain
  [InlineData("ana​@example.com")]           // zero-width space (BUG-017)
  [InlineData("an‌a@example.com")]           // zero-width non-joiner (BUG-017)
  [InlineData("‮moc.elpmaxe@ana")]           // right-to-left override (BUG-017)
  [InlineData("аna@example.com")]            // Cyrillic «а» (BUG-017)
  [InlineData("ana@exаmple.com")]            // Cyrillic in the domain (BUG-017)
  [InlineData("ana＠example.com")]            // fullwidth at sign
  [InlineData("peña@example.com")]                // non-ASCII letter
  [InlineData("a..b@example.com")]
  [InlineData(".ana@example.com")]
  [InlineData("ana.@example.com")]
  [InlineData("ana@-example.com")]
  [InlineData("ana@example..com")]
  [InlineData("a@@example.com")]
  [InlineData("@example.com")]
  [InlineData("ana@")]
  [InlineData("sinarroba.com")]
  [InlineData("")]
  [InlineData(null)]
  public void IsValid_RejectsMalformedInvisibleAndLookAlikeAddresses(string? email)
  {
    Assert.False(EmailRules.IsValid(email));
  }

  [Fact]
  public void IsValid_RejectsAddressesLongerThan256Characters()
  {
    Assert.False(EmailRules.IsValid(new string('a', 64) + "@" + string.Join(".", Enumerable.Repeat(new string('b', 60), 4)) + ".com"));
  }
}
