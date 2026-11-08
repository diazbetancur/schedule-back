using Barbershop.Domain.Loyalty;

namespace Barbershop.Tests.Features.Loyalty;

/// <summary>The explicit transition table of a redemption (ADR-0015): pending states, final states and every edge.</summary>
public sealed class LoyaltyRedemptionTransitionTests
{
  private static readonly DateTime Now = new(2026, 11, 4, 17, 0, 0, DateTimeKind.Utc);
  private static readonly DateOnly Today = new(2026, 11, 4);
  private static readonly Guid UserId = Guid.NewGuid();

  [Fact]
  public void TheTableListsExactlyTheApprovedTransitions_AndFinalStatesHaveNone()
  {
    var edges = LoyaltyRedemption.AllowedTransitions
        .SelectMany(pair => pair.Value.Select(target => $"{pair.Key}->{target}"))
        .Order()
        .ToArray();

    Assert.Equal(
        new[]
        {
          "Approved->Cancelled", "Approved->Delivered", "Approved->Expired",
          "Requested->Approved", "Requested->Cancelled", "Requested->Expired", "Requested->Rejected"
        }.Order(),
        edges);
    Assert.All(
        new[] { LoyaltyRedemptionStatus.Delivered, LoyaltyRedemptionStatus.Rejected, LoyaltyRedemptionStatus.Cancelled, LoyaltyRedemptionStatus.Expired },
        status => Assert.Empty(LoyaltyRedemption.AllowedTransitions[status]));
    Assert.All(Enum.GetValues<LoyaltyRedemptionStatus>(), status =>
        Assert.Equal(status is LoyaltyRedemptionStatus.Requested or LoyaltyRedemptionStatus.Approved, LoyaltyRedemption.IsPendingStatus(status)));
  }

  [Fact]
  public void EveryStateRefusesTheTransitionsNotInTheTable()
  {
    foreach (var status in Enum.GetValues<LoyaltyRedemptionStatus>())
    {
      foreach (var target in new[] { LoyaltyRedemptionStatus.Approved, LoyaltyRedemptionStatus.Rejected, LoyaltyRedemptionStatus.Cancelled, LoyaltyRedemptionStatus.Delivered })
      {
        var redemption = InStatus(status);
        void Act()
        {
          switch (target)
          {
            case LoyaltyRedemptionStatus.Approved: redemption.Approve(UserId, Now, Today); break;
            case LoyaltyRedemptionStatus.Rejected: redemption.Reject("Motivo", UserId, Now); break;
            case LoyaltyRedemptionStatus.Cancelled: redemption.Cancel("Motivo", UserId, byCustomer: false, Now); break;
            default: redemption.Deliver(UserId, Now, Today, serviceRecordId: null); break;
          }
        }

        if (LoyaltyRedemption.AllowedTransitions[status].Contains(target))
        {
          Act();
          Assert.Equal(target, redemption.Status);
        }
        else
        {
          Assert.Throws<InvalidOperationException>(Act);
          Assert.Equal(status, redemption.Status);
        }
      }
    }
  }

  [Fact]
  public void DeadlinesAreInclusive_AndExpiryOnlyHappensAfterTheLastDay()
  {
    var redemption = InStatus(LoyaltyRedemptionStatus.Requested, approvalDays: 1);
    Assert.Equal(Today, redemption.ApproveBy);
    Assert.False(redemption.IsPastDeadlineOn(Today));
    Assert.Throws<InvalidOperationException>(() => redemption.Expire(Now, Today));
    Assert.True(redemption.IsPastDeadlineOn(Today.AddDays(1)));
    Assert.Throws<InvalidOperationException>(() => redemption.Approve(UserId, Now, Today.AddDays(1)));
    redemption.Expire(Now, Today.AddDays(1));
    Assert.Equal(LoyaltyRedemptionStatus.Expired, redemption.Status);
  }

  private static LoyaltyRedemption InStatus(LoyaltyRedemptionStatus status, int approvalDays = 30)
  {
    var reward = new LoyaltyReward("Gorra", null, LoyaltyRewardKind.Other, null, 10, UserId, Now);
    var redemption = new LoyaltyRedemption(
        Guid.NewGuid(), reward, null, LoyaltyRedemptionChannel.App, false, Guid.NewGuid(), UserId, Now, Today, approvalDays, 30);
    switch (status)
    {
      case LoyaltyRedemptionStatus.Approved: redemption.Approve(UserId, Now, Today); break;
      case LoyaltyRedemptionStatus.Delivered: redemption.Approve(UserId, Now, Today); redemption.Deliver(UserId, Now, Today, null); break;
      case LoyaltyRedemptionStatus.Rejected: redemption.Reject("Motivo", UserId, Now); break;
      case LoyaltyRedemptionStatus.Cancelled: redemption.Cancel(null, UserId, byCustomer: true, Now); break;
      case LoyaltyRedemptionStatus.Expired: redemption.Expire(Now, Today.AddDays(approvalDays)); break;
    }

    return redemption;
  }
}
