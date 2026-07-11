using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Streamyfin.PushNotifications.models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.Streamyfin.PushNotifications;

/// <summary>
/// Sends push notifications straight to Firebase Cloud Messaging (HTTP v1) —
/// no Expo relay, tokens in the database are raw FCM registration tokens.
///
/// Credentials come from a Google service-account JSON file placed at
/// {jellyfin-data-dir}/streamyfin-firebase.json (Firebase console → Project
/// settings → Service accounts → Generate new private key). The file is read
/// once and cached; restart Jellyfin (or reload the plugin) after replacing it.
///
/// Messages are sent data-only (title/body/image travel inside the data map)
/// so the client's FirebaseMessagingService always handles them and tap
/// routing works the same in every app state. Tokens FCM reports as
/// UNREGISTERED/invalid — including any leftover ExponentPushToken[...] rows
/// from the retired React Native app — are pruned from the database.
/// </summary>
public class FcmSender
{
    private const string Scope = "https://www.googleapis.com/auth/firebase.messaging";
    private const string CredentialsFileName = "streamyfin-firebase.json";
    private const int MaxParallelSends = 8;

    private static readonly HttpClient _http = new();

    private readonly ILogger<FcmSender>? _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private ServiceAccount? _serviceAccount;
    private bool _serviceAccountLoadAttempted;
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiry = DateTimeOffset.MinValue;

    public FcmSender(ILoggerFactory? loggerFactory)
    {
        _logger = loggerFactory?.CreateLogger<FcmSender>();
    }

    public async Task<NotificationResponse> SendAsync(params NotificationRequest[] notifications)
    {
        var response = new NotificationResponse();

        var account = LoadServiceAccount();
        if (account == null)
        {
            response.Errors.Add(new Errors
            {
                Code = "NO_CREDENTIALS",
                Message = $"FCM service-account file not found/invalid; expected {CredentialsFileName} in the Jellyfin data directory"
            });
            return response;
        }

        string accessToken;
        try
        {
            accessToken = await GetAccessTokenAsync(account).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Failed to obtain an FCM OAuth token");
            response.Errors.Add(new Errors { Code = "AUTH_FAILED", Message = e.Message });
            return response;
        }

        var sendUri = new Uri($"https://fcm.googleapis.com/v1/projects/{account.ProjectId}/messages:send");
        using var throttle = new SemaphoreSlim(MaxParallelSends, MaxParallelSends);

        var tasks = notifications
            .SelectMany(notification =>
            {
                var data = BuildDataPayload(notification);
                return (notification.To ?? []).Distinct().Select(async token =>
                {
                    await throttle.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        return await SendOneAsync(sendUri, accessToken, token, data).ConfigureAwait(false);
                    }
                    finally
                    {
                        throttle.Release();
                    }
                });
            })
            .ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        response.Data.AddRange(results);

        var failed = results.Count(r => r.Status == "error");
        _logger?.LogInformation("FCM send complete: {Ok} delivered, {Failed} failed", results.Length - failed, failed);

        return response;
    }

    /// <summary>
    /// Everything travels in the data map (data-only message): the client builds
    /// the visible notification from data["title"]/data["body"] and forwards the
    /// remaining keys as deep-link extras on tap.
    /// </summary>
    private static Dictionary<string, string> BuildDataPayload(NotificationRequest notification)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal);

        if (notification.Data != null)
        {
            var token = notification.Data as JToken ?? JToken.FromObject(notification.Data);
            if (token is JObject obj)
            {
                foreach (var prop in obj.Properties())
                {
                    if (prop.Value.Type == JTokenType.Null) continue;
                    data[prop.Name] = prop.Value.Type == JTokenType.String
                        ? prop.Value.Value<string>() ?? string.Empty
                        : prop.Value.ToString(Formatting.None);
                }
            }
        }

        var body = notification.Body;
        if (!string.IsNullOrWhiteSpace(notification.Subtitle))
        {
            body = string.IsNullOrWhiteSpace(body) ? notification.Subtitle! : $"{notification.Subtitle}\n{body}";
        }

        if (!string.IsNullOrWhiteSpace(notification.Title)) data["title"] = notification.Title!;
        data["body"] = body;
        if (!string.IsNullOrWhiteSpace(notification.RichContent?.Image)) data["image"] = notification.RichContent!.Image!;

        return data;
    }

    private async Task<TicketStatus> SendOneAsync(
        Uri sendUri,
        string accessToken,
        string deviceToken,
        Dictionary<string, string> data)
    {
        var payload = JsonConvert.SerializeObject(new
        {
            message = new
            {
                token = deviceToken,
                android = new { priority = "HIGH" },
                data
            }
        });

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, sendUri)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");

            using var rawResponse = await _http.SendAsync(request).ConfigureAwait(false);
            var content = await rawResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (rawResponse.IsSuccessStatusCode)
            {
                var name = JObject.Parse(content)["name"]?.Value<string>();
                return new TicketStatus { Status = "ok", Id = name };
            }

            var errorStatus = TryGetErrorStatus(content);

            // UNREGISTERED = app uninstalled / token rotated away.
            // INVALID_ARGUMENT on the token = not an FCM token at all (e.g. a
            // leftover ExponentPushToken from the retired RN app). Both are
            // permanently dead — drop them so we stop retrying forever.
            if (errorStatus == "UNREGISTERED"
                || rawResponse.StatusCode == HttpStatusCode.NotFound
                || errorStatus == "INVALID_ARGUMENT")
            {
                _logger?.LogInformation("Pruning dead device token ({Status}): {Token}...", errorStatus, SafePrefix(deviceToken));
                StreamyfinPlugin.Instance?.Database.RemoveDeviceTokensByToken(deviceToken);
            }
            else
            {
                _logger?.LogWarning("FCM send failed ({HttpStatus} {Status}): {Content}", rawResponse.StatusCode, errorStatus, content);
            }

            return new TicketStatus
            {
                Status = "error",
                Message = errorStatus ?? rawResponse.StatusCode.ToString(),
                Details = new { token = SafePrefix(deviceToken) }
            };
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "FCM send threw for token {Token}...", SafePrefix(deviceToken));
            return new TicketStatus { Status = "error", Message = e.Message };
        }
    }

    private static string? TryGetErrorStatus(string content)
    {
        try
        {
            return JObject.Parse(content)["error"]?["status"]?.Value<string>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string SafePrefix(string token) => token.Length <= 12 ? token : token[..12];

    // region OAuth

    private ServiceAccount? LoadServiceAccount()
    {
        if (_serviceAccount != null || _serviceAccountLoadAttempted) return _serviceAccount;
        _serviceAccountLoadAttempted = true;

        var dataPath = StreamyfinPlugin.Instance?.PluginDataPath;
        if (dataPath == null)
        {
            _logger?.LogError("Plugin instance unavailable; cannot locate the FCM service-account file");
            return null;
        }

        var path = Path.Combine(dataPath, CredentialsFileName);
        if (!File.Exists(path))
        {
            _logger?.LogError("FCM service-account file missing: {Path}", path);
            return null;
        }

        try
        {
            var account = JsonConvert.DeserializeObject<ServiceAccount>(File.ReadAllText(path));
            if (string.IsNullOrWhiteSpace(account?.ProjectId)
                || string.IsNullOrWhiteSpace(account.ClientEmail)
                || string.IsNullOrWhiteSpace(account.PrivateKey))
            {
                _logger?.LogError("FCM service-account file is missing project_id/client_email/private_key: {Path}", path);
                return null;
            }

            _logger?.LogInformation("FCM credentials loaded for project {Project}", account.ProjectId);
            _serviceAccount = account;
            return account;
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Failed to parse the FCM service-account file: {Path}", path);
            return null;
        }
    }

    private async Task<string> GetAccessTokenAsync(ServiceAccount account)
    {
        await _tokenLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Refresh 5 minutes early so an in-flight batch never straddles expiry.
            if (_accessToken != null && DateTimeOffset.UtcNow < _accessTokenExpiry - TimeSpan.FromMinutes(5))
            {
                return _accessToken;
            }

            var now = DateTimeOffset.UtcNow;
            var assertion = CreateSignedJwt(account, now);

            using var request = new HttpRequestMessage(HttpMethod.Post, account.TokenUri)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = assertion
                })
            };

            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Google OAuth token exchange failed ({response.StatusCode}): {content}");
            }

            var parsed = JObject.Parse(content);
            _accessToken = parsed["access_token"]?.Value<string>()
                           ?? throw new InvalidOperationException("Google OAuth response had no access_token");
            var expiresIn = parsed["expires_in"]?.Value<int>() ?? 3600;
            _accessTokenExpiry = now.AddSeconds(expiresIn);

            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static string CreateSignedJwt(ServiceAccount account, DateTimeOffset now)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes(/*lang=json*/ "{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
        var claims = Base64Url(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
        {
            iss = account.ClientEmail,
            scope = Scope,
            aud = account.TokenUri,
            iat = now.ToUnixTimeSeconds(),
            exp = now.AddMinutes(60).ToUnixTimeSeconds()
        })));

        using var rsa = RSA.Create();
        rsa.ImportFromPem(account.PrivateKey);
        var signature = rsa.SignData(
            Encoding.UTF8.GetBytes($"{header}.{claims}"),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return $"{header}.{claims}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class ServiceAccount
    {
        [JsonProperty("project_id")]
        public string? ProjectId { get; set; }

        [JsonProperty("client_email")]
        public string? ClientEmail { get; set; }

        [JsonProperty("private_key")]
        public string? PrivateKey { get; set; }

        [JsonProperty("token_uri")]
        public string TokenUri { get; set; } = "https://oauth2.googleapis.com/token";
    }

    // endregion
}
