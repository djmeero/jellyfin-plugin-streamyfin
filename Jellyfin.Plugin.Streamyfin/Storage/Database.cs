using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Jellyfin.Plugin.Streamyfin.Storage.Enums;
using Jellyfin.Plugin.Streamyfin.Storage.Models;

namespace Jellyfin.Plugin.Streamyfin.Storage;

public class Database : IDisposable
{
    private readonly string name = "streamyfin_plugin.db";
    private bool _disposed;
    private ReaderWriterLockSlim WriteLock { get; }

    public string DbFilePath { get; set; }

    protected virtual int? CacheSize => null;

    protected virtual string LockingMode => "NORMAL";

    protected virtual string JournalMode => "WAL";

    protected virtual int? JournalSizeLimit => 134_217_728; // 128MiB

    protected virtual int? PageSize => null;

    protected virtual TempStoreMode TempStore => TempStoreMode.Memory;
    
    private const string DeviceTokensTable = "device_tokens";
    private const string IssueParticipantsTable = "issue_participants";

    public Database(string path)
    {
        Directory.CreateDirectory(path);
        DbFilePath = Path.Combine(path, name);
        Initialize(File.Exists(DbFilePath));
        WriteLock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
    }

    private void Initialize(bool fileExists)
    {
        using var connection = CreateConnection();

        string[] queries =
        [
            $"create table if not exists {DeviceTokensTable} (DeviceId GUID PRIMARY KEY, Token TEXT NOT NULL, UserId GUID NOT NULL, Timestamp INTEGER NOT NULL)",
            $"create index if not exists idx_{DeviceTokensTable}_user on {DeviceTokensTable}(UserId)",
            $"create table if not exists {IssueParticipantsTable} (IssueId TEXT NOT NULL, Username TEXT NOT NULL, PRIMARY KEY (IssueId, Username))"
        ];

        connection.RunQueries(queries);
    }

    /// <summary>
    /// Gets all the expo push tokens for the devices a user is currently signed in to
    /// </summary>
    /// <param name="userId">Jellyfin user id</param>
    /// <returns>List of DeviceToken</returns>
    public List<DeviceToken> GetUserDeviceTokens(Guid userId)
    {
        using (WriteLock.Read())
        {
            var tokens = new List<DeviceToken>();
            using var connection = CreateConnection(true);

            using (var statement = connection.PrepareStatement($"select * from {DeviceTokensTable} where UserId = @UserId;"))
            {
                statement.TryBind("@UserId", userId);

                tokens.AddRange(
                    collection: statement.ExecuteQuery()
                        .Select(row =>
                            new DeviceToken
                            {
                                DeviceId = row.GetGuid(0),
                                Token = row.GetString(1),
                                UserId = row.GetGuid(2),
                                Timestamp = row.GetInt64(3)
                            }
                        )
                );
            }

            return tokens;
        }
    }
    
    /// <summary>
    /// Records a participant (by Jellyfin username) for a Jellyseerr issue so that
    /// future comments/resolutions can be routed only to people involved in the issue.
    /// Idempotent — duplicate (issueId, username) pairs are ignored.
    /// </summary>
    /// <param name="issueId">Jellyseerr issue id (stored as text)</param>
    /// <param name="username">Jellyfin username of the participant</param>
    public void AddIssueParticipant(string issueId, string username)
    {
        if (string.IsNullOrWhiteSpace(issueId) || string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        using (WriteLock.Write())
        {
            using var connection = CreateConnection();
            connection.RunInTransaction(db =>
            {
                using var statement = db.PrepareStatement(
                    $"insert or ignore into {IssueParticipantsTable}(IssueId, Username) values (@IssueId, @Username);");
                statement.TryBind("@IssueId", issueId);
                statement.TryBind("@Username", username);
                statement.ExecuteNonQuery();
            });
        }
    }

    /// <summary>
    /// Gets the Jellyfin usernames recorded as participants of a Jellyseerr issue.
    /// </summary>
    /// <param name="issueId">Jellyseerr issue id</param>
    /// <returns>List of Jellyfin usernames</returns>
    public List<string> GetIssueParticipants(string issueId)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(issueId))
        {
            return result;
        }

        using (WriteLock.Read())
        {
            using var connection = CreateConnection(true);
            using var statement = connection.PrepareStatement(
                $"select Username from {IssueParticipantsTable} where IssueId = @IssueId;");
            statement.TryBind("@IssueId", issueId);
            result.AddRange(statement.ExecuteQuery().Select(row => row.GetString(0)));
            return result;
        }
    }

    /// <summary>
    /// Gets all known device tokens
    /// </summary>
    /// <returns>List of DeviceToken</returns>
    public List<DeviceToken> GetAllDeviceTokens()
    {
        using (WriteLock.Read())
        {
            List<DeviceToken> tokens = [];
            using var connection = CreateConnection(true);

            using (var statement = connection.PrepareStatement($"select * from {DeviceTokensTable};"))
            {
                tokens.AddRange(
                    collection: statement.ExecuteQuery()
                        .Select(row => 
                            new DeviceToken
                            {
                                DeviceId = row.GetGuid(0), 
                                Token = row.GetString(1), 
                                UserId = row.GetGuid(2), 
                                Timestamp = row.GetInt64(3)
                            }
                        )
                    );
            }

            return tokens;
        }
    }

    /// <summary>
    /// Gets the specific expo push token for a device
    /// </summary>
    /// <param name="deviceId">Device id generated from streamyfin</param>
    /// <returns>DeviceToken?</returns>
    public DeviceToken? GetDeviceTokenForDeviceId(Guid deviceId)
    {
        using (WriteLock.Read())
        {
            using var connection = CreateConnection(true);

            using (var statement = connection.PrepareStatement($"select * from {DeviceTokensTable} where DeviceId = @DeviceId;"))
            {
                statement.TryBind("@DeviceId", deviceId);
                return statement.ExecuteQuery()
                    .Select(row => 
                        new DeviceToken
                        {
                            DeviceId = row.GetGuid(0), 
                            Token = row.GetString(1), 
                            UserId = row.GetGuid(2), 
                            Timestamp = row.GetInt64(3)
                        }
                    ).FirstOrDefault();
            }
        }
    }

    /// <summary>
    /// Adds a device token, unique to every deviceId.
    /// </summary>
    /// <param name="token"></param>
    /// <returns>DeviceToken</returns>
    public DeviceToken AddDeviceToken(DeviceToken token)
    {
        using (WriteLock.Write())
        {
            using var connection = CreateConnection();
            return connection.RunInTransaction(db =>
            {
                long timestamp = DateTime.UtcNow.ToFileTime();

                using (var statement = db.PrepareStatement($"delete from {DeviceTokensTable} where DeviceId=@DeviceId;"))
                {
                    statement.TryBind("@DeviceId", token.DeviceId);
                    statement.ExecuteNonQuery();
                }

                using (var statement = db.PrepareStatement($"insert into {DeviceTokensTable}(DeviceId, Token, UserId, Timestamp) values (@DeviceId, @Token, @UserId, @Timestamp);"))
                {
                    statement.TryBind("@DeviceId", token.DeviceId);
                    statement.TryBind("@Token", token.Token);
                    statement.TryBind("@UserId", token.UserId);
                    statement.TryBind("@Timestamp", timestamp);
                    statement.ExecuteNonQuery();
                }

                token.Timestamp = timestamp;
                return token;
            });
        }
    }
    
    /// <summary>
    /// Get the total record count for DeviceTokensTable
    /// </summary>
    /// <returns>Total count for tokens</returns>
    public Int64 TotalDevicesCount()
    {
        using (WriteLock.Read())
        {
            using var connection = CreateConnection();
            return connection.RunInTransaction(db =>
            {
                using (var statement = db.PrepareStatement($"select count(*) from {DeviceTokensTable};"))
                {
                    return (Int64)(statement.ExecuteScalar() ?? 0);
                }
            });
        }
    }

    /// <summary>
    /// Removes a device token using the device id
    /// </summary>
    /// <param name="deviceId">Device id generated from streamyfin</param>
    /// <returns>DeviceToken</returns>
    public void RemoveDeviceToken(Guid deviceId)
    {
        using (WriteLock.Write())
        {
            using var connection = CreateConnection();
            connection.RunInTransaction(db =>
            {
                using var statement = db.PrepareStatement($"delete from {DeviceTokensTable} where DeviceId=@DeviceId;");

                statement.TryBind("@DeviceId", deviceId);
                statement.ExecuteNonQuery();
            });
        }
    }

    /// <summary>
    /// Removes every row holding the given push token, regardless of device id.
    /// Used to prune tokens FCM reports as unregistered/invalid.
    /// </summary>
    /// <param name="token">Raw FCM registration token</param>
    public void RemoveDeviceTokensByToken(string token)
    {
        using (WriteLock.Write())
        {
            using var connection = CreateConnection();
            connection.RunInTransaction(db =>
            {
                using var statement = db.PrepareStatement($"delete from {DeviceTokensTable} where Token=@Token;");

                statement.TryBind("@Token", token);
                statement.ExecuteNonQuery();
            });
        }
    }

    private SqliteConnection CreateConnection(bool isReadOnly = false)
    {
        var connection = new SqliteConnection($"Filename={DbFilePath}");
        connection.Open();

        if (CacheSize.HasValue)
        {
            connection.Execute("PRAGMA cache_size=" + CacheSize.Value);
        }

        if (!string.IsNullOrWhiteSpace(LockingMode))
        {
            connection.Execute("PRAGMA locking_mode=" + LockingMode);
        }

        if (!string.IsNullOrWhiteSpace(JournalMode))
        {
            connection.Execute("PRAGMA journal_mode=" + JournalMode);
        }

        if (JournalSizeLimit.HasValue)
        {
            connection.Execute("PRAGMA journal_size_limit=" + JournalSizeLimit.Value);
        }

        if (PageSize.HasValue)
        {
            connection.Execute("PRAGMA page_size=" + PageSize.Value);
        }

        connection.Execute("PRAGMA temp_store=" + (int)TempStore);

        return connection;
    }

    /// <summary>
    /// Clear up all tables
    /// </summary>
    public void Purge()
    {
        using (WriteLock.Write())
        {
            using var connection = CreateConnection();
            connection.RunInTransaction(db =>
            {
                using var statement = db.PrepareStatement($"delete from {DeviceTokensTable};");
                statement.ExecuteNonQuery();
            });
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}