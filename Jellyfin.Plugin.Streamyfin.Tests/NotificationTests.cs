using System.Collections.Generic;
using Jellyfin.Plugin.Streamyfin.PushNotifications;
using Jellyfin.Plugin.Streamyfin.PushNotifications.models;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Streamyfin.Tests;

/// <summary>
/// Ensure special types are properly serialized/deserialized when converting between Object - Json - Yaml
/// </summary>
public class NotificationTests(ITestOutputHelper output)
{
    private static readonly SerializationHelper _serializationHelper = new();
    private readonly NotificationHelper _notificationHelper = new(null, null, _serializationHelper);

    // Replace with your own android emulator / ios simulator token
    // Do not use a real devices token. If you do, you can invalidate the token by re-installing streamyfin on your device.
    private const string VirtualToken = "...";

    /// <summary>
    /// Assert we can send a single notification and receive a proper NotificationResponse
    /// </summary>
    [Fact]
    public void SingleFcmPushNotificationTest()
    {
        var request = new NotificationRequest
        {
            To = new List<string> { VirtualToken },
            Title = "FCM Push Test",
            Subtitle = "iOS subtitle",
            Body = "All platforms should see this body",
        };

        var task = _notificationHelper.Send(request);
        task.Wait();

        Assert.NotNull(task.Result);
        output.WriteLine(_serializationHelper.ToJson(task.Result));
    }
    
    /// <summary>
    /// Assert we can send a batch of notifications and receive a proper NotificationResponse
    /// </summary>
    [Fact]
    public void BatchFcmPushNotificationTest()
    {
        var notifications = new List<NotificationRequest>();

        for (var i = 0; i < 5; i++)
        {
            notifications.Add(
                new NotificationRequest
                {
                    To = new List<string> { VirtualToken },
                    Title = $"FCM Push Test {i}",
                    Subtitle = $"iOS subtitle {i}",
                    Body = "All platforms should see this body",
                }
            );
        }

        var task = _notificationHelper.Send(notifications.ToArray());
        task.Wait();

        Assert.NotNull(task.Result);
        Assert.Equal(
            expected: 5,
            actual: task.Result.Data.Count
        );

        output.WriteLine(_serializationHelper.ToJson(task.Result));
    }
}