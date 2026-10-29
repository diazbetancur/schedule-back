using Barbershop.Domain.Common;

namespace Barbershop.Domain.Memberships;

/// <summary>
/// A membership sold to a customer. It copies what it needs from its plan at sale time (name, price,
/// mode, terms and, through its allowances, the services and quantities), so later catalog changes
/// never alter it. No income is created here: the sale is recorded by hand in finance and can be linked.
/// </summary>
public sealed class CustomerMembership
{
  private CustomerMembership()
  {
  }

  public CustomerMembership(
      Guid customerId,
      MembershipPlan plan,
      DateOnly soldOn,
      DateOnly startsOn,
      DateOnly endsOn,
      Guid createdByUserId,
      DateTime createdAt)
  {
    if (endsOn < startsOn)
    {
      throw new ArgumentException("The membership cannot end before it starts.", nameof(endsOn));
    }

    CustomerId = customerId;
    OriginalCustomerId = customerId;
    MembershipPlanId = plan.Id;
    PlanName = plan.Name;
    PlanDescription = plan.Description;
    Price = plan.Price;
    Mode = plan.Mode;
    Terms = plan.Terms;
    SpecificConditions = plan.SpecificConditions;
    SoldOn = soldOn;
    StartsOn = startsOn;
    EndsOn = endsOn;
    Status = MembershipStatus.PendingActivation;
    CreatedByUserId = createdByUserId;
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
  }

  public Guid Id { get; private set; } = Guid.NewGuid();

  /// <summary>Current holder; changes only when duplicate customer profiles are merged.</summary>
  public Guid CustomerId { get; private set; }

  /// <summary>Holder at sale time; never changes.</summary>
  public Guid OriginalCustomerId { get; private set; }

  public Guid MembershipPlanId { get; private set; }
  public string PlanName { get; private set; } = string.Empty;
  public string? PlanDescription { get; private set; }
  public int Price { get; private set; }
  public MembershipPlanMode Mode { get; private set; }
  public string Terms { get; private set; } = string.Empty;
  public string? SpecificConditions { get; private set; }
  public DateOnly SoldOn { get; private set; }
  public DateOnly StartsOn { get; private set; }

  /// <summary>Last valid day, inclusive (Bogotá date).</summary>
  public DateOnly EndsOn { get; private set; }

  public MembershipStatus Status { get; private set; }
  public DateTime? ActivatedAt { get; private set; }
  public Guid? ActivatedByUserId { get; private set; }
  public DateTime? AnnulledAt { get; private set; }
  public Guid? AnnulledByUserId { get; private set; }
  public string? AnnulmentReason { get; private set; }

  /// <summary>Optional link to the membership-sale income that paid for it; one income may cover several memberships.</summary>
  public Guid? IncomeEntryId { get; private set; }

  public Guid CreatedByUserId { get; private set; }
  public DateTime CreatedAt { get; private set; }
  public DateTime? UpdatedAt { get; private set; }

  /// <summary>Mapped to PostgreSQL's xmin system column; used as an optimistic concurrency token.</summary>
  public uint Version { get; private set; }

  public ICollection<MembershipAllowance> Allowances { get; } = [];
  public ICollection<MembershipGift> Gifts { get; } = [];

  public bool IsExpiredOn(DateOnly today) => today > EndsOn;

  public void Activate(Guid activatedByUserId, DateTime activatedAt)
  {
    if (Status != MembershipStatus.PendingActivation)
    {
      throw new InvalidOperationException("Only a membership pending activation can be activated.");
    }

    Status = MembershipStatus.Active;
    ActivatedByUserId = activatedByUserId;
    ActivatedAt = DomainValidation.EnsureUtc(activatedAt, nameof(activatedAt));
    UpdatedAt = ActivatedAt;
  }

  public void Annul(Guid annulledByUserId, string reason, DateTime annulledAt)
  {
    if (Status == MembershipStatus.Annulled)
    {
      throw new InvalidOperationException("The membership is already annulled.");
    }

    Status = MembershipStatus.Annulled;
    AnnulledByUserId = annulledByUserId;
    AnnulmentReason = DomainValidation.Required(reason, nameof(reason), 500, 3);
    AnnulledAt = DomainValidation.EnsureUtc(annulledAt, nameof(annulledAt));
    UpdatedAt = AnnulledAt;
  }

  public void LinkIncome(Guid incomeEntryId, DateTime updatedAt)
  {
    if (IncomeEntryId is not null)
    {
      throw new InvalidOperationException("The membership already has a linked income.");
    }

    IncomeEntryId = incomeEntryId;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  /// <summary>Only drops the reference: balances, gifts and history are untouched.</summary>
  public Guid UnlinkIncome(DateTime updatedAt)
  {
    var incomeEntryId = IncomeEntryId ?? throw new InvalidOperationException("The membership has no linked income.");
    IncomeEntryId = null;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
    return incomeEntryId;
  }

  public void MoveToCustomer(Guid customerId, DateTime updatedAt)
  {
    CustomerId = customerId;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }
}

public enum MembershipStatus
{
  PendingActivation = 1,
  Active = 2,
  Annulled = 3
}
