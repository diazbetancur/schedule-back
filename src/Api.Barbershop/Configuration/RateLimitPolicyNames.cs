namespace Api.Barbershop.Configuration;

public static class RateLimitPolicyNames
{
    public const string AuthCredentials = "auth-credentials";
    public const string AuthPasswordReset = "auth-password-reset";
    public const string AuthRefresh = "auth-refresh";

    /// <summary>BUG-112: POST /notifications/test, per signed-in user.</summary>
    public const string NotificationTest = "notification-test";

    /// <summary>BUG-112: admin broadcasts, per signed-in user.</summary>
    public const string NotificationBroadcast = "notification-broadcast";
}
