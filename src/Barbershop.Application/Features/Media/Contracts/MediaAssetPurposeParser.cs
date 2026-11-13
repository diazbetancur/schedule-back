using Barbershop.Domain.Media;

namespace Barbershop.Application.Media;

public static class MediaAssetPurposeParser
{
    public const string InvalidPurposeMessage =
        "Elige un propósito válido: Logo, AppIcon, Banner, StaffPhoto, TipsQr, CustomerReference, Other o ProductImage.";

    /// <summary>
    /// Only the name of a defined purpose is accepted, ignoring case (BUG-098). Enum.TryParse alone also accepts any
    /// number ("999") and flag combinations ("Logo, Banner"), which left files outside the per-role purpose rules.
    /// </summary>
    public static bool TryParse(string? value, out MediaAssetPurpose purpose)
    {
        purpose = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var name = value.Trim();
        foreach (var candidate in Enum.GetValues<MediaAssetPurpose>())
        {
            if (string.Equals(candidate.ToString(), name, StringComparison.OrdinalIgnoreCase))
            {
                purpose = candidate;
                return true;
            }
        }

        return false;
    }
}
