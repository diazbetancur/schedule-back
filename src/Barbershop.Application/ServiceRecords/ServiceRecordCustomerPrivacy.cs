using Barbershop.Application.Customers.Admin;

namespace Barbershop.Application.ServiceRecords;

/// <summary>
/// Approvers (service-records.approve) may not have the Admin role, so the customer data they see while confirming is
/// reduced to what identifies and selects a profile: the name, a hint of the phone and email, and the profile id.
/// Accounts (users) are never identified: only that one matches.
/// </summary>
public static class ServiceRecordCustomerPrivacy
{
    private const char Mask = '•';

    /// <summary>The last four digits only, for example "•••4567".</summary>
    public static string PhoneHint(string? phone)
    {
        var digits = new string((phone ?? string.Empty).Where(char.IsDigit).ToArray());
        return digits.Length <= 4 ? new string(Mask, 3) : $"{new string(Mask, 3)}{digits[^4..]}";
    }

    /// <summary>The first letter and the domain, for example "c•••@example.com".</summary>
    public static string? EmailHint(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        var trimmed = email.Trim();
        var at = trimmed.IndexOf('@');
        return at <= 0 ? new string(Mask, 3) : $"{trimmed[0]}{new string(Mask, 3)}{trimmed[at..]}";
    }

    public static ServiceRecordCustomerDuplicate ToDuplicate(CustomerDuplicateCandidate candidate)
    {
        var isCustomer = string.Equals(candidate.Kind, "customer", StringComparison.Ordinal);
        return new ServiceRecordCustomerDuplicate(
            candidate.Kind,
            isCustomer ? candidate.Id : null,
            candidate.FullName,
            EmailHint(candidate.Email),
            candidate.Phone is null ? null : PhoneHint(candidate.Phone),
            candidate.MatchedOn);
    }
}
