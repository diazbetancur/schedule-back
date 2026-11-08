using Barbershop.Application.Auth;
using Barbershop.Domain.Loyalty;

namespace Barbershop.Infrastructure.Loyalty;

/// <summary>
/// Validation and audit shared by the services and membership plans catalogs for their fixed loyalty points.
/// Who may send the points is decided by the endpoints (loyalty.settings.manage, checked against the database).
/// </summary>
internal static class LoyaltyCatalogPoints
{
    public const string FieldName = "loyaltyPoints";

    public static void Validate(int? points, IDictionary<string, string[]> errors)
    {
        if (points is < 0 or > LoyaltySettings.MaxPointsPerItem)
        {
            errors[FieldName] =
            [
                $"Los puntos de fidelización deben ser un número entero entre 0 y {LoyaltySettings.MaxPointsPerItem}. Usa 0 si no otorga puntos."
            ];
        }
    }

    /// <summary>The audit entry for a change of points, or null when the request keeps them as they are.</summary>
    public static LoyaltyPointsChange? Change(
        LoyaltyPointsTarget target,
        Guid targetId,
        int currentPoints,
        int? requestedPoints,
        ICurrentUserAccessor currentUser,
        DateTime changedAt)
    {
        if (requestedPoints is not { } points || points == currentPoints)
        {
            return null;
        }

        var userId = currentUser.UserId
            ?? throw new UnauthorizedAccessException("No se pudo identificar a quien cambia los puntos de fidelización.");
        return new LoyaltyPointsChange(target, targetId, currentPoints, points, userId, changedAt);
    }
}
