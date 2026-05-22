using Microsoft.Extensions.Hosting;

namespace Barbershop.Infrastructure.Configuration;

internal static class OptionsValidationHelpers
{
    private static readonly string[] PlaceholderTokens =
    [
        "__CHANGE_ME__",
        "__SET_BY_ENVIRONMENT__",
        "__SET_VIA_ENVIRONMENT__",
        "__SET_VIA_USER_SECRETS__",
        "__REPLACE_ME__"
    ];

    public static bool IsRelaxedEnvironment(IHostEnvironment environment)
        => environment.IsDevelopment() || environment.IsEnvironment("Testing");

    public static bool IsConfigured(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return PlaceholderTokens.All(token => value.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0);
    }
}
