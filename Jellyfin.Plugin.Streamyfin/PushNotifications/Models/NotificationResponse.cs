using System.Collections.Generic;
using Newtonsoft.Json;

namespace Jellyfin.Plugin.Streamyfin.PushNotifications.models;

/// <summary>
/// Aggregated outcome of an FCM send: one <see cref="TicketStatus"/> per
/// (notification, device token) message attempted.
/// </summary>
public class NotificationResponse
{
    [JsonProperty(PropertyName = "data")]
    public List<TicketStatus> Data { get; set; } = [];

    [JsonProperty(PropertyName = "errors")]
    public List<Errors> Errors { get; set; } = [];
}

public class TicketStatus
{
    [JsonProperty(PropertyName = "status")] //"error" | "ok",
    public string Status { get; set; } = "ok";

    /// <summary>FCM message name on success (projects/*/messages/*).</summary>
    [JsonProperty(PropertyName = "id")]
    public string? Id { get; set; }

    [JsonProperty(PropertyName = "message")]
    public string? Message { get; set; }

    [JsonProperty(PropertyName = "details")]
    public object? Details { get; set; }
}

public class Errors
{
    [JsonProperty(PropertyName = "code")]
    public string? Code { get; set; }

    [JsonProperty(PropertyName = "message")]
    public string? Message { get; set; }
}
