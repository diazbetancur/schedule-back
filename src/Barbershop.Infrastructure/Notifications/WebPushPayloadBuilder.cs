using System.Text.Json;
using System.Text.Json.Serialization;
using Barbershop.Application.Notifications;

namespace Barbershop.Infrastructure.Notifications;

/// <summary>
/// Builds the JSON payload in the shape Angular's service worker (ngsw-worker.js) expects.
/// </summary>
internal static class WebPushPayloadBuilder
{
  public const string DefaultUrl = "/";

  // focusLastFocusedOrOpen: if the app is already open, ngsw focuses that window (no reload) and the
  // app's SwPush.notificationClicks handler routes to data.url; if it is closed, ngsw opens data.url.
  // Without data.onActionClick, ngsw does nothing on tap when the app is closed.
  public const string ClickOperation = "focusLastFocusedOrOpen";

  private static readonly JsonSerializerOptions SerializerOptions = new()
  {
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
  };

  public static string Build(PushNotificationMessage message, string iconPath, string badgePath)
  {
    var url = NormalizeUrl(message.Url);

    var notification = new Dictionary<string, object?>
    {
      ["title"] = message.Title,
      ["body"] = message.Body,
      ["icon"] = iconPath,
      ["badge"] = badgePath,
      ["data"] = new Dictionary<string, object?>
      {
        ["url"] = url,
        ["onActionClick"] = new Dictionary<string, object>
        {
          ["default"] = new Dictionary<string, object>
          {
            ["operation"] = ClickOperation,
            ["url"] = url,
          },
        },
      },
    };

    if (!string.IsNullOrWhiteSpace(message.Tag))
    {
      notification["tag"] = message.Tag.Trim();
      notification["renotify"] = true;
    }

    return JsonSerializer.Serialize(new Dictionary<string, object?> { ["notification"] = notification }, SerializerOptions);
  }

  /// <summary>Only app-relative paths are allowed, so a notification can never send the user to another site.</summary>
  public static string NormalizeUrl(string? url)
  {
    if (string.IsNullOrWhiteSpace(url))
    {
      return DefaultUrl;
    }

    var trimmed = url.Trim();
    return trimmed.StartsWith('/') && !trimmed.StartsWith("//", StringComparison.Ordinal) && !trimmed.Contains('\\')
        ? trimmed
        : DefaultUrl;
  }
}
