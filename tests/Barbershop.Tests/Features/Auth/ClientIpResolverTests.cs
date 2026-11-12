using System.Net;
using Api.Barbershop.Configuration;
using Microsoft.Extensions.Primitives;

namespace Barbershop.Tests.Features.Auth;

/// <summary>
/// BUG-001: the auth rate limiter keyed by the Cloudflare edge hop, so one client was spread over many buckets.
/// The inputs are what is left after UseForwardedHeaders consumed the rightmost X-Forwarded-For entry (the edge).
/// </summary>
public sealed class ClientIpResolverTests
{
  [Fact]
  public void ClientBehindCloudflare_IsTheKey_WhateverEdgeItArrivesThrough()
  {
    var first = ClientIpResolver.Resolve(IPAddress.Parse("104.16.3.4"), "203.0.113.50");
    var second = ClientIpResolver.Resolve(IPAddress.Parse("172.68.9.9"), "203.0.113.50");
    var ipv6Edge = ClientIpResolver.Resolve(IPAddress.Parse("2606:4700::1"), "203.0.113.50");

    Assert.Equal("203.0.113.50", first);
    Assert.Equal(first, second);
    Assert.Equal(first, ipv6Edge);
  }

  [Theory]
  [InlineData("1.2.3.4, 203.0.113.50")]
  [InlineData("9.9.9.9, 8.8.8.8, 203.0.113.50")]
  [InlineData("104.16.0.1, 203.0.113.50")]
  [InlineData("10.0.0.1, 203.0.113.50")]
  [InlineData("not-an-ip, 203.0.113.50")]
  public void EntriesForgedByTheClient_AreIgnored(string forwardedFor)
  {
    Assert.Equal("203.0.113.50", ClientIpResolver.Resolve(IPAddress.Parse("104.16.3.4"), forwardedFor));
  }

  [Fact]
  public void InternalHopsBetweenCloudflareAndTheApp_AreSkipped()
  {
    Assert.Equal("203.0.113.50", ClientIpResolver.Resolve(IPAddress.Parse("10.1.2.3"), "203.0.113.50, 104.16.3.4"));
  }

  [Fact]
  public void SeveralHeaderValues_AreReadAsOneChain()
  {
    Assert.Equal("203.0.113.50", ClientIpResolver.Resolve(IPAddress.Parse("104.16.3.4"), new StringValues(["1.1.1.1", "203.0.113.50"])));
  }

  [Fact]
  public void DifferentClients_GetDifferentKeys()
  {
    Assert.NotEqual(
        ClientIpResolver.Resolve(IPAddress.Parse("104.16.3.4"), "203.0.113.50"),
        ClientIpResolver.Resolve(IPAddress.Parse("104.16.3.4"), "198.51.100.7"));
  }

  [Fact]
  public void WithoutForwardingHeaders_TheConnectionAddressIsTheKey()
  {
    Assert.Equal("203.0.113.9", ClientIpResolver.Resolve(IPAddress.Parse("203.0.113.9"), StringValues.Empty));
    Assert.Equal("unknown", ClientIpResolver.Resolve(null, StringValues.Empty));
  }

  [Fact]
  public void IPv4MappedAndPortForms_AreNormalized()
  {
    Assert.Equal("203.0.113.50", ClientIpResolver.Resolve(IPAddress.Parse("::ffff:104.16.3.4"), "203.0.113.50:4711"));
    Assert.Equal("2001:db8::7", ClientIpResolver.Resolve(IPAddress.Parse("104.16.3.4"), "[2001:db8::7]:443"));
  }

  [Fact]
  public void ALongChainOfTrustedAddresses_StopsAfterTheHopLimit()
  {
    // Edge + three more "proxy" entries forged by someone inside a trusted range: the key stops at the hop limit
    // instead of walking on to an attacker-chosen value.
    var key = ClientIpResolver.Resolve(IPAddress.Parse("104.16.3.4"), "6.6.6.6, 10.0.0.3, 10.0.0.2, 10.0.0.1");

    Assert.Equal("10.0.0.3", key);
  }
}
