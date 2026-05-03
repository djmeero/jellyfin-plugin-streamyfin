using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Streamyfin.PushNotifications.models;
using Newtonsoft.Json;

public class Notification
{
    /// <summary>
    /// Specific Jellyfin UserId that you want to target with this notification.
    /// This will attempt to notify all streamyfin clients that are logged in under this user.
    /// </summary>
    [JsonProperty(PropertyName = "userId")]
    public Guid? UserId { get; set; }
    
    /// <summary>
    /// Specific Jellyfin Username that you want to target with this notification.
    /// This will attempt to notify all streamyfin clients that are logged in under this username.
    /// </summary>
    [JsonProperty(PropertyName = "username")]
    public string? Username { get; set; }

    /// <summary>
    /// The title to display in the notification. Often displayed above the notification body.
    /// Maps to AndroidNotification.title and aps.alert.title
    /// </summary>
    [JsonProperty(PropertyName = "title", NullValueHandling = NullValueHandling.Ignore)]
    public string? Title { get; set; }

    /// <summary>
    /// iOS Only
    /// The subtitle to display in the notification below the title.
    /// Maps to aps.alert.subtitle.
    /// </summary>
    [JsonProperty(PropertyName = "subtitle")]
    public string? Subtitle { get; set; }

    /// <summary>
    /// The message to display in the notification.
    /// Maps to AndroidNotification.body and aps.alert.body.
    /// </summary>
    [JsonProperty(PropertyName = "body")]
    public string? Body { get; set; }
    
    /// <summary>
    /// Enforce that this notification is for Jellyfin admins only
    /// </summary>
    [JsonProperty(PropertyName = "isAdmin")]
    public bool IsAdmin { get; set; }

    /// <summary>
    /// Optional JSON object delivered to the client app inside the Expo push payload.
    /// Use this to carry deep-link metadata that the client can read on tap (e.g. {"type":"movie","id":"..."},
    /// {"type":"series","seriesId":"..."}, or {"type":"settings","page":"appearance"}).
    /// Forwarded verbatim into ExpoNotificationRequest.data; total payload must stay under ~4KiB.
    /// </summary>
    [JsonProperty(PropertyName = "data", NullValueHandling = NullValueHandling.Ignore)]
    public object? Data { get; set; }

    public ExpoNotificationRequest ToExpoNotification() => new()
    {
        Title = Title,
        Subtitle = Subtitle,
        Body = Body,
        Data = NormalizeJsonValue(Data)
    };

    /// <summary>
    /// ASP.NET Core binds <c>object?</c> request fields as <see cref="JsonElement"/> (System.Text.Json),
    /// but the outbound Expo payload is serialized with Newtonsoft.Json — which doesn't understand
    /// JsonElement and would emit {"ValueKind":1} instead of the actual data. Convert to native CLR
    /// containers so Newtonsoft can serialize the value transparently.
    /// </summary>
    private static object? NormalizeJsonValue(object? value) => value switch
    {
        null               => null,
        JsonElement el     => FromJsonElement(el),
        _                  => value
    };

    private static object? FromJsonElement(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Object => el.EnumerateObject()
            .ToDictionary(p => p.Name, p => FromJsonElement(p.Value)),
        JsonValueKind.Array  => el.EnumerateArray()
            .Select(FromJsonElement).ToList(),
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : (object)el.GetDouble(),
        JsonValueKind.True   => true,
        JsonValueKind.False  => false,
        _                    => null
    };
}