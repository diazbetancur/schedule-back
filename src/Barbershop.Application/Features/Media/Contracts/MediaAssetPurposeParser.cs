using Barbershop.Domain.Media;

namespace Barbershop.Application.Media;

public static class MediaAssetPurposeParser
{
    public static bool TryParse(string? value, out MediaAssetPurpose purpose)
        => Enum.TryParse(value, ignoreCase: true, out purpose);
}
