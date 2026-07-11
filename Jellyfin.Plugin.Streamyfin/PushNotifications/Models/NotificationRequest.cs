using System.Collections.Generic;
using Newtonsoft.Json;

namespace Jellyfin.Plugin.Streamyfin.PushNotifications.models;

/// <summary>
/// A push notification destined for one or more raw FCM device tokens.
/// Delivered as an FCM HTTP v1 data-only message: the client app builds the
/// visible notification itself from the data payload (title/body/image plus
/// any deep-link fields), which keeps tap routing identical whether the app
/// is in the foreground, background or killed.
/// </summary>
public class NotificationRequest
{
    /// <summary>
    /// Raw FCM registration tokens of the recipient devices.
    /// </summary>
    [JsonProperty("to", DefaultValueHandling = DefaultValueHandling.Ignore)]
    public List<string> To { get; set; } = [];

    /// <summary>
    /// The title to display in the notification.
    /// </summary>
    [JsonProperty(PropertyName = "title")]
    public string? Title { get; set; }

    /// <summary>
    /// Optional secondary line shown under the title (client renders it as part of the body).
    /// </summary>
    [JsonProperty(PropertyName = "subtitle", DefaultValueHandling = DefaultValueHandling.Ignore)]
    public string? Subtitle { get; set; }

    /// <summary>
    /// The message to display in the notification.
    /// </summary>
    [JsonProperty(PropertyName = "body")]
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// Optional JSON object delivered to the client app inside the FCM data payload.
    /// Use this to carry deep-link metadata the client reads on tap (e.g. {"type":"movie","id":"..."}).
    /// Every value is stringified before sending — FCM data maps are string→string.
    /// </summary>
    [JsonProperty("data", DefaultValueHandling = DefaultValueHandling.Ignore)]
    public object? Data { get; set; }

    /// <summary>
    /// Optional rich-media attachment (e.g. a movie poster). Forwarded to the client
    /// as data["image"]; the URL must be publicly reachable over HTTPS.
    /// </summary>
    [JsonProperty(PropertyName = "richContent", DefaultValueHandling = DefaultValueHandling.Ignore)]
    public RichContent? RichContent { get; set; }
}

/// <summary>
/// Rich-media attachment for a push notification. A single publicly reachable
/// HTTPS image URL.
/// </summary>
public class RichContent
{
    [JsonProperty(PropertyName = "image", DefaultValueHandling = DefaultValueHandling.Ignore)]
    public string? Image { get; set; }
}
