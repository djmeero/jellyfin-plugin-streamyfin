using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json.Serialization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Streamyfin.Configuration;
using Jellyfin.Plugin.Streamyfin.Extensions;
using Jellyfin.Plugin.Streamyfin.PushNotifications;
using Jellyfin.Plugin.Streamyfin.PushNotifications.models;
using Jellyfin.Plugin.Streamyfin.Storage.Models;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Streamyfin.Api;

/// <summary>
/// Flattened Seerr webhook payload. The Seerr Webhook agent must be
/// configured with a custom JSON template that maps to these fields (see the
/// plugin docs / PR description for the exact template).
/// </summary>
public class JellyseerrWebhookPayload
{
  /// <summary>e.g. MEDIA_APPROVED, ISSUE_CREATED, ISSUE_COMMENT, ISSUE_RESOLVED, ISSUE_REOPENED.</summary>
  [JsonPropertyName("notification_type")]
  public string? NotificationType { get; set; }

  [JsonPropertyName("subject")]
  public string? Subject { get; set; }

  [JsonPropertyName("message")]
  public string? Message { get; set; }

  [JsonPropertyName("image")]
  public string? Image { get; set; }

  [JsonPropertyName("issue_id")]
  public string? IssueId { get; set; }

  /// <summary>Jellyfin username of the issue creator.</summary>
  [JsonPropertyName("reported_by")]
  public string? ReportedBy { get; set; }

  /// <summary>Jellyfin username of the comment author (issue comment events).</summary>
  [JsonPropertyName("commented_by")]
  public string? CommentedBy { get; set; }

  /// <summary>Jellyfin username Seerr is targeting (used for request events = the requester).</summary>
  [JsonPropertyName("notify_user")]
  public string? NotifyUser { get; set; }

  /// <summary>TMDB id of the media — used to deep-link to the Jellyfin item.</summary>
  [JsonPropertyName("tmdb_id")]
  public string? TmdbId { get; set; }

  /// <summary>"movie" or "tv".</summary>
  [JsonPropertyName("media_type")]
  public string? MediaType { get; set; }
}

//public class ConfigYamlReq {
//  public string? Value { get; set; }
//}

/// <summary>
/// CollectionImportController.
/// </summary>
[ApiController]
[Route("flixnet")]
public class StreamyfinController : ControllerBase
{
  private readonly ILogger<StreamyfinController> _logger;
  private readonly ILoggerFactory _loggerFactory;
  private readonly IServerConfigurationManager _config;
  private readonly IUserManager _userManager;
  private readonly ILibraryManager _libraryManager;
  private readonly IDtoService _dtoService;
  private readonly SerializationHelper _serializationHelperService;
  private readonly NotificationHelper _notificationHelper;

  public StreamyfinController(
    ILoggerFactory loggerFactory,
    IDtoService dtoService,
    IServerConfigurationManager config,
    IUserManager userManager,
    ILibraryManager libraryManager,
    SerializationHelper serializationHelper,
    NotificationHelper notificationHelper
  )
  {
    _loggerFactory = loggerFactory;
    _logger = loggerFactory.CreateLogger<StreamyfinController>();
    _dtoService = dtoService;
    _config = config;
    _userManager = userManager;
    _libraryManager = libraryManager;
    _serializationHelperService = serializationHelper;
    _notificationHelper = notificationHelper;

    _logger.LogInformation("StreamyfinController Loaded");
  }

  /// <summary>
  /// Post raw FCM push tokens for a specific user & device
  /// </summary>
  /// <param name="deviceToken"></param>
  [HttpPost("device")]
  [Authorize]
  [ProducesResponseType(StatusCodes.Status200OK)]
  public ActionResult PostDeviceToken([FromBody, Required] DeviceToken deviceToken)
  {
    _logger.LogInformation("Posting device token for deviceId: {0}", deviceToken.DeviceId);
    return new JsonResult(
      _serializationHelperService.ToJson(StreamyfinPlugin.Instance!.Database.AddDeviceToken(deviceToken))
    );
  }
  
  /// <summary>
  /// Delete FCM push tokens for a specific device 
  /// </summary>
  /// <param name="deviceId"></param>
  [HttpDelete("device/{deviceId}")]
  [Authorize]
  [ProducesResponseType(StatusCodes.Status200OK)]
  public ActionResult DeleteDeviceToken([FromRoute, Required] Guid? deviceId)
  {
    if (deviceId == null) return BadRequest("Device id is required");

    _logger.LogInformation("Deleting device token for deviceId: {0}", deviceId);
    StreamyfinPlugin.Instance!.Database.RemoveDeviceToken((Guid) deviceId);

    return new OkResult();
  }

  /// <summary>
  /// Generic sender: push a batch of notifications via FCM using persisted
  /// device tokens. Kept for custom/manual pushes (e.g. a backend service).
  /// The Seerr webhook should target <see cref="PostJellyseerrWebhook"/> instead.
  /// </summary>
  /// <param name="notifications"></param>
  /// <returns></returns>
  [HttpPost("push")]
  [Authorize]
  [ProducesResponseType(StatusCodes.Status200OK)]
  [ProducesResponseType(StatusCodes.Status202Accepted)]
  public ActionResult PostNotifications([FromBody, Required] List<Notification> notifications)
  {
    var db = StreamyfinPlugin.Instance?.Database;

    if (db?.TotalDevicesCount() == 0)
    {
      _logger.LogInformation("There are currently no devices setup to receive push notifications");
      return new AcceptedResult();
    }

    List<DeviceToken>? allTokens = null;
    var validNotifications = notifications
      .FindAll(n =>
      {
        var title = n.Title ?? "";
        var body = n.Body ?? "";
        
        // Title and body are both valid
        if (!title.IsNullOrNonWord() && !body.IsNullOrNonWord())
        {
          return true;
        }

        // Title can be empty, body is required.
        return string.IsNullOrEmpty(title) && !body.IsNullOrNonWord();
        // every other scenario is invalid
      })
      .Select(notification =>
      {
        List<DeviceToken> tokens = [];
        var pushNotification = notification.ToNotificationRequest();
        
        // Get tokens for target user
        if (notification.UserId != null || !string.IsNullOrWhiteSpace(notification.Username))
        {
          Guid? userId = null;

          if (notification.UserId != null)
          {
            userId = notification.UserId;
          } 
          else if (notification.Username != null)
          {
            userId = _userManager.GetUsers().ToList().Find(u => u.Username == notification.Username)?.Id;
          }
          if (userId != null)
          {
            _logger.LogInformation("Getting device tokens associated to userId: {0}", userId);
            tokens.AddRange(
              db?.GetUserDeviceTokens((Guid) userId)
              ?? []
            );
          }
        }
        // Get all available tokens
        else if (!notification.IsAdmin)
        {
          _logger.LogInformation("No user target provided. Getting all device tokens...");
          allTokens ??= db?.GetAllDeviceTokens() ?? [];
          tokens.AddRange(allTokens);
          _logger.LogInformation("All known device tokens count: {0}", allTokens.Count);
        }

        // Get all available tokens for admins
        if (notification.IsAdmin)
        {
          _logger.LogInformation("Notification being posted for admins");
          tokens.AddRange(_userManager.GetAdminDeviceTokens());
        }

        pushNotification.To = tokens.Select(t => t.Token).Distinct().ToList();

        return pushNotification;
      })
      .Where(n => n.To.Count > 0)
      .ToArray();

    _logger.LogInformation("Received {0} valid notifications", validNotifications.Length);

    if (validNotifications.Length == 0)
    {
      return new AcceptedResult();
    }

    _logger.LogInformation("Posting notifications...");
    var task = _notificationHelper.Send(validNotifications);
    task.Wait();
    return new JsonResult(_serializationHelperService.ToJson(task.Result));
  }

  /// <summary>
  /// Single entry point for the Seerr Webhook agent (requests AND issues).
  /// Request events notify only the targeted user (the requester). Issue events
  /// notify only the people participating in that issue (creator + commenters),
  /// minus the person who triggered the event. New issues alert Jellyfin admins.
  /// </summary>
  [HttpPost("notification")]
  [Authorize]
  [ProducesResponseType(StatusCodes.Status200OK)]
  [ProducesResponseType(StatusCodes.Status202Accepted)]
  public ActionResult PostJellyseerrWebhook([FromBody, Required] JellyseerrWebhookPayload payload)
  {
    var db = StreamyfinPlugin.Instance?.Database;
    if (db == null)
    {
      return new AcceptedResult();
    }

    var type = payload.NotificationType ?? string.Empty;
    var title = string.IsNullOrWhiteSpace(payload.Subject) ? null : payload.Subject;
    var body = payload.Message ?? string.Empty;

    _logger.LogInformation("Received Jellyseerr webhook: {0}", type);

    // Push Seerr's "Test Notification" to whoever triggered it (the admin),
    // falling back to all Jellyfin admins, so the test is actually verifiable.
    if (type == "TEST_NOTIFICATION")
    {
      if (!string.IsNullOrWhiteSpace(payload.NotifyUser))
      {
        SendToUsernames([payload.NotifyUser!], title, body, payload.Image);
      }
      else
      {
        SendToAdmins(title, body, payload.Image);
      }
      return new OkResult();
    }

    // Non-issue events (media requests) — notify only the targeted user, i.e. the
    // requester. Preserves existing request-notification behaviour.
    if (!type.StartsWith("ISSUE_", StringComparison.Ordinal))
    {
      if (!string.IsNullOrWhiteSpace(payload.NotifyUser))
      {
        SendToUsernames([payload.NotifyUser!], title, body, payload.Image);
      }
      return new OkResult();
    }

    // Issue events.
    var issueId = payload.IssueId;
    if (string.IsNullOrWhiteSpace(issueId))
    {
      _logger.LogWarning("Seerr issue webhook missing issue_id; ignoring");
      return new AcceptedResult();
    }

    // The creator is a participant on every issue event (covers issues created
    // before this plugin started tracking them).
    if (!string.IsNullOrWhiteSpace(payload.ReportedBy))
    {
      db.AddIssueParticipant(issueId!, payload.ReportedBy!);
    }

    // The media title (e.g. "Inception (2010)") goes in the notification title;
    // the body describes the event. Tapping deep-links to the item + opens the
    // issue discussion.
    var item = string.IsNullOrWhiteSpace(payload.Subject) ? "this item" : payload.Subject!;
    var resolved = ResolveJellyfinItem(payload.TmdbId, payload.MediaType);
    object? deepLink = resolved == null
      ? null
      : new { type = "issue", id = resolved.Value.id, itemType = resolved.Value.itemType };

    switch (type)
    {
      case "ISSUE_CREATED":
        // Only the reporter exists so far — alert admins instead of self-notifying.
        // A missing config entry (pre-upgrade XML) counts as enabled.
        if (StreamyfinPlugin.Instance?.Configuration.Config?.notifications?.SeerrIssueCreated is not { Enabled: false })
        {
          SendToAdmins(title, $"A new issue has been opened for {item}", payload.Image, deepLink);
        }
        break;

      case "ISSUE_COMMENT":
        if (!string.IsNullOrWhiteSpace(payload.CommentedBy))
        {
          db.AddIssueParticipant(issueId!, payload.CommentedBy!);
        }

        var replier = string.IsNullOrWhiteSpace(payload.CommentedBy) ? "Someone" : payload.CommentedBy!;
        SendToUsernames(
          db.GetIssueParticipants(issueId!)
            .Where(u => !string.Equals(u, payload.CommentedBy, StringComparison.OrdinalIgnoreCase)),
          title,
          $"{replier} has replied to your issue",
          payload.Image,
          deepLink
        );
        break;

      case "ISSUE_RESOLVED":
        SendToUsernames(db.GetIssueParticipants(issueId!), title, "Issue has been resolved", payload.Image, deepLink);
        break;

      case "ISSUE_REOPENED":
        SendToUsernames(db.GetIssueParticipants(issueId!), title, "Issue has been reopened", payload.Image, deepLink);
        break;

      default:
        _logger.LogInformation("Unhandled Seerr issue type: {0}", type);
        break;
    }

    return new OkResult();
  }

  /// <summary>
  /// Resolve a TMDB id to a Jellyfin library item, returning its id (dashless
  /// GUID, as the app routes use) and whether it's a "Movie" or "Series".
  /// </summary>
  private (string id, string itemType)? ResolveJellyfinItem(string? tmdbId, string? mediaType)
  {
    if (string.IsNullOrWhiteSpace(tmdbId))
    {
      return null;
    }

    var isTv = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase);
    var match = _libraryManager.GetItemList(new InternalItemsQuery
    {
      IncludeItemTypes = isTv ? new[] { BaseItemKind.Series } : new[] { BaseItemKind.Movie },
      HasAnyProviderId = new Dictionary<string, string> { { "Tmdb", tmdbId! } },
      Recursive = true,
      Limit = 1
    }).FirstOrDefault();

    if (match == null)
    {
      _logger.LogInformation("No Netflix item found for tmdb {0} ({1})", tmdbId, mediaType);
      return null;
    }

    return (match.Id.ToString("N"), isTv ? "Series" : "Movie");
  }

  /// <summary>
  /// Resolve a set of Jellyfin usernames to their device tokens and push a single
  /// notification to all of them.
  /// </summary>
  private void SendToUsernames(IEnumerable<string> usernames, string? title, string body, string? image, object? data = null)
  {
    var db = StreamyfinPlugin.Instance?.Database;
    if (db == null) return;

    var users = _userManager.GetUsers().ToList();
    var tokens = new List<string>();

    foreach (var username in usernames
               .Where(u => !string.IsNullOrWhiteSpace(u))
               .Distinct(StringComparer.OrdinalIgnoreCase))
    {
      var userId = users.Find(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase))?.Id;
      if (userId == null)
      {
        _logger.LogInformation("No Netflix user matched username '{0}'", username);
        continue;
      }

      tokens.AddRange(db.GetUserDeviceTokens(userId.Value).Select(t => t.Token));
    }

    SendTokens(tokens, title, body, image, data);
  }

  /// <summary>
  /// Push a single notification to every Jellyfin admin's device tokens.
  /// </summary>
  private void SendToAdmins(string? title, string body, string? image, object? data = null)
  {
    SendTokens(
      _userManager.GetAdminDeviceTokens().Select(t => t.Token).ToList(),
      title,
      body,
      image,
      data
    );
  }

  private void SendTokens(List<string> tokens, string? title, string body, string? image, object? data = null)
  {
    var distinct = tokens.Distinct().ToList();
    if (distinct.Count == 0)
    {
      _logger.LogInformation("No device tokens for recipients; nothing to send");
      return;
    }

    if (body.IsNullOrNonWord() && (title?.IsNullOrNonWord() ?? true))
    {
      _logger.LogInformation("Notification has no usable content; skipping");
      return;
    }

    var notification = new NotificationRequest
    {
      Title = title,
      Body = body,
      To = distinct,
      RichContent = string.IsNullOrWhiteSpace(image) ? null : new RichContent { Image = image },
      Data = data
    };

    _notificationHelper.Send(notification).Wait();
  }
}
