using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Streamyfin.Extensions;
using Jellyfin.Plugin.Streamyfin.PushNotifications.models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Streamyfin.PushNotifications;

public class NotificationHelper
{
    private readonly ILogger<NotificationHelper>? _logger;
    private readonly IUserManager? _userManager;
    private readonly FcmSender _fcmSender;

    public NotificationHelper(
        ILoggerFactory? loggerFactory,
        IUserManager? userManager,
        SerializationHelper serializationHelper)
    {
        _logger = loggerFactory?.CreateLogger<NotificationHelper>();
        _userManager = userManager;
        _fcmSender = new FcmSender(loggerFactory);
    }

    /// <summary>
    /// Ability to send a batch of notifications directly to jellyfin admins
    /// </summary>
    /// <param name="notifications"></param>
    /// <returns></returns>
    public async Task<NotificationResponse?> SendToAdmins(params Notification[] notifications)
    {
        var adminTokens = _userManager.GetAdminTokens();

        _logger?.LogInformation("Attempting to send {0} notifications to admins", notifications.Length);

        // No admin tokens found.
        if (adminTokens.Count == 0)
        {
            _logger?.LogInformation("No admins found");
            return await Task.FromResult<NotificationResponse?>(null).ConfigureAwait(false);
        }

        var requests = notifications.Select(notification =>
        {
            List<String> userDeviceTokens = [];
            var request = notification.ToNotificationRequest();

            // Also send to target user if specified
            if (notification.UserId.HasValue)
            {
                userDeviceTokens = StreamyfinPlugin.Instance?.Database
                    .GetUserDeviceTokens(notification.UserId.Value)
                    .Select(token => token.Token)
                    .ToList() ?? [];
            }

            request.To = adminTokens.Concat(userDeviceTokens).Distinct().ToList();
            return request;
        }).ToArray();

        return await Send(requests).ConfigureAwait(false);
    }

    public async Task<NotificationResponse?> SendToAll(params NotificationRequest[] notifications)
    {
        _logger?.LogInformation("Attempting to send {0} notifications to everyone", notifications.Length);

        var all = StreamyfinPlugin.Instance?.Database
            .GetAllDeviceTokens()
            .Select(token => token.Token)
            .Distinct()
            .ToList() ?? [];

        if (all.Count == 0)
        {
            _logger?.LogInformation("No devices found");
            return await Task.FromResult<NotificationResponse?>(null).ConfigureAwait(false);
        }

        var ready = notifications
            .Select(notification =>
            {
                notification.To = all;
                return notification;
            }).ToArray();

        return await Send(ready).ConfigureAwait(false);
    }

    public async Task<NotificationResponse?> SendToAdmins(
        List<Guid>? excludedUserIds = null,
        params NotificationRequest[] notifications)
    {
        _logger?.LogInformation("Attempting to send {0} notifications to admins", notifications.Length);

        var excludedIds = excludedUserIds ?? Array.Empty<Guid>().ToList();
        var adminTokens = _userManager.GetAdminDeviceTokens()
            .FindAll(deviceToken => !excludedIds.Contains(deviceToken.UserId))
            .Select(deviceToken => deviceToken.Token)
            .Distinct()
            .ToList();

        // No admin tokens found.
        if (adminTokens.Count == 0)
        {
            _logger?.LogInformation("No admins found");
            return await Task.FromResult<NotificationResponse?>(null).ConfigureAwait(false);
        }

        var requests = notifications
            .Select(notification =>
            {
                notification.To = adminTokens;
                return notification;
            }).ToArray();

        return await Send(requests).ConfigureAwait(false);
    }

    /// <summary>
    /// Send notifications straight to FCM using each request's device tokens.
    /// </summary>
    public async Task<NotificationResponse?> Send(params NotificationRequest[] notifications) =>
        await _fcmSender.SendAsync(notifications).ConfigureAwait(false);
}
