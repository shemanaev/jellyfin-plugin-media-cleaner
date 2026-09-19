using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.LeavingSoon;

internal sealed class SqliteLeavingSoonStateStore : ILeavingSoonStateStore
{
    private const int SchemaVersion = 5;
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly ILogger<SqliteLeavingSoonStateStore> _logger;
    private readonly object _migrationLock = new();
    private volatile bool _initialized;

    public SqliteLeavingSoonStateStore(string databasePath, ILogger<SqliteLeavingSoonStateStore> logger)
    {
        _databasePath = databasePath;
        _logger = logger;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public LeavingSoonSnapshot Read() => Execute(ReadSnapshot);

    public long ReadRevision() => Execute(connection =>
    {
        using var transaction = connection.BeginTransaction(deferred: true);
        var revision = ExecuteScalar<long>(connection, transaction, "SELECT revision FROM metadata WHERE singleton=1");
        transaction.Commit();
        return revision;
    });

    public LeavingSoonSnapshot SynchronizeCandidates(IReadOnlyList<LeavingSoonCandidate> candidates, DateTime nowUtc) => Mutate((connection, transaction) =>
    {
        var activeIds = candidates.Select(x => x.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var existingId in ReadWarningIds(connection, transaction).Where(id => !activeIds.Contains(id)))
        {
            ExecuteNonQuery(connection, transaction, "DELETE FROM warning_entries WHERE item_id=$item", ("$item", existingId));
        }

        foreach (var candidate in candidates)
        {
            var existing = ReadWarning(connection, transaction, candidate.ItemId);
            if (existing is not null
                && (!string.Equals(existing.IdentityHash, candidate.IdentityHash, StringComparison.Ordinal)
                    || !string.Equals(existing.NoticeGeneration, candidate.NoticeGeneration, StringComparison.Ordinal)))
            {
                ExecuteNonQuery(connection, transaction, "DELETE FROM warning_entries WHERE item_id=$item", ("$item", candidate.ItemId));
                existing = null;
            }

            if (existing is null)
            {
                ExecuteNonQuery(connection, transaction,
                    """
                    INSERT INTO warning_entries
                        (item_id, kind, status, item_name, item_path, identity_hash, config_fingerprint, notice_generation,
                         first_detected_utc, first_published_utc, not_before_delete_utc, last_seen_utc, notice_days, last_error)
                    VALUES ($item, $kind, $status, $name, $path, $identity, $config, $generation, $detected, NULL, NULL, $seen, $notice, NULL)
                    """,
                    ("$item", candidate.ItemId), ("$kind", (int)candidate.Kind), ("$status", (int)WarningStatus.PendingPublication),
                    ("$name", candidate.Name), ("$path", candidate.Path), ("$identity", candidate.IdentityHash),
                    ("$config", candidate.ConfigurationFingerprint), ("$generation", candidate.NoticeGeneration), ("$detected", Format(nowUtc)),
                    ("$seen", Format(nowUtc)), ("$notice", candidate.NoticeDays));
            }
            else
            {
                var notBefore = existing.NotBeforeDeleteUtc;
                if (existing.FirstPublishedUtc is { } published)
                {
                    var proposed = published.AddDays(candidate.NoticeDays);
                    if (notBefore is null || proposed > notBefore) notBefore = proposed;
                }

                var status = existing.Status == WarningStatus.Ready && notBefore > nowUtc ? WarningStatus.Warning : existing.Status;
                ExecuteNonQuery(connection, transaction,
                    """
                    UPDATE warning_entries SET kind=$kind,item_name=$name,item_path=$path,identity_hash=$identity,
                        config_fingerprint=$config,last_seen_utc=$seen,notice_days=$notice,
                        not_before_delete_utc=$notBefore,status=$status WHERE item_id=$item
                    """,
                    ("$kind", (int)candidate.Kind), ("$name", candidate.Name), ("$path", candidate.Path),
                    ("$identity", candidate.IdentityHash), ("$config", candidate.ConfigurationFingerprint),
                    ("$seen", Format(nowUtc)), ("$notice", candidate.NoticeDays), ("$notBefore", FormatNullable(notBefore)),
                    ("$status", (int)status), ("$item", candidate.ItemId));
            }

            ExecuteNonQuery(connection, transaction, "DELETE FROM warning_entry_rules WHERE item_id=$item", ("$item", candidate.ItemId));
            foreach (var ruleId in candidate.RuleIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ExecuteNonQuery(connection, transaction, "INSERT INTO warning_entry_rules(item_id,rule_id) VALUES ($item,$rule)",
                    ("$item", candidate.ItemId), ("$rule", ruleId));
            }
        }
    });

    public LeavingSoonSnapshot MarkPublished(IReadOnlyCollection<string> itemIds, DateTime nowUtc) => Mutate((connection, transaction) =>
    {
        foreach (var itemId in itemIds)
        {
            var entry = ReadWarning(connection, transaction, itemId);
            if (entry is null) continue;
            var published = entry.FirstPublishedUtc ?? nowUtc;
            var notBefore = entry.NotBeforeDeleteUtc ?? published.AddDays(entry.NoticeDays);
            ExecuteNonQuery(connection, transaction,
                "UPDATE warning_entries SET first_published_utc=$published,not_before_delete_utc=$notBefore,status=$status,last_error=NULL WHERE item_id=$item",
                ("$published", Format(published)), ("$notBefore", Format(notBefore)),
                ("$status", (int)(notBefore <= nowUtc ? WarningStatus.Ready : WarningStatus.Warning)), ("$item", itemId));
        }
    });

    public LeavingSoonSnapshot RecordPublicationError(IReadOnlyCollection<string> itemIds, string error) => Mutate((connection, transaction) =>
    {
        foreach (var itemId in itemIds)
        {
            ExecuteNonQuery(connection, transaction,
                "UPDATE warning_entries SET status=$status,last_error=$error WHERE item_id=$item",
                ("$status", (int)WarningStatus.PendingPublication), ("$error", error), ("$item", itemId));
        }
        ExecuteNonQuery(connection, transaction, "UPDATE metadata SET last_refresh_error=$error WHERE singleton=1", ("$error", error));
    });

    public LeavingSoonSnapshot RecordRefreshSuccess(DateTime nowUtc) => Mutate((connection, transaction) =>
        ExecuteNonQuery(connection, transaction,
            "UPDATE metadata SET last_successful_refresh_utc=$at,last_refresh_error=NULL WHERE singleton=1", ("$at", Format(nowUtc))));

    public LeavingSoonSnapshot InvalidateWarnings() => Mutate((connection, transaction) =>
    {
        ExecuteNonQuery(connection, transaction, "DELETE FROM warning_entries");
        ExecuteNonQuery(connection, transaction, "UPDATE metadata SET last_refresh_error=NULL WHERE singleton=1");
    });

    public LeavingSoonSnapshot SetCollectionIds(Guid? collectionId, Guid? readyCollectionId) => Mutate((connection, transaction) =>
        ExecuteNonQuery(connection, transaction, "UPDATE metadata SET collection_id=$collection,ready_collection_id=$ready WHERE singleton=1",
            ("$collection", collectionId?.ToString("N")), ("$ready", readyCollectionId?.ToString("N"))));

    public ProtectionMutationResult AddProtection(string itemId, string userId, string? identityHash, DateTime nowUtc) =>
        AddProtections([new ViewProtectionSeed(itemId, itemId, identityHash)], userId, nowUtc);

    public ProtectionMutationResult AddProtections(IReadOnlyCollection<ViewProtectionSeed> protections, string userId, DateTime nowUtc) =>
        MutateProtection((connection, transaction) =>
        {
            ViewProtection? first = null;
            var changed = false;
            foreach (var seed in protections)
            {
                var existing = ReadProtectionByItemUser(connection, transaction, seed.ItemId, userId);
                if (existing is not null)
                {
                    var identityChanged = seed.ItemIdentityHash is not null
                        && !string.Equals(existing.ItemIdentityHash, seed.ItemIdentityHash, StringComparison.Ordinal);
                    var noticeChanged = !IdEquals(existing.NoticeItemId, seed.NoticeItemId);
                    if (identityChanged || noticeChanged)
                    {
                        ExecuteNonQuery(connection, transaction,
                            "UPDATE view_protections SET notice_item_id=$notice,item_identity_hash=COALESCE($identity,item_identity_hash),created_at_utc=$at WHERE protection_id=$id",
                            ("$notice", seed.NoticeItemId), ("$identity", seed.ItemIdentityHash),
                            ("$at", identityChanged ? Format(nowUtc) : Format(existing.CreatedAtUtc)), ("$id", existing.ProtectionId));
                        ReplaceProtectionUnits(connection, transaction, existing.ProtectionId, AffectedIds(seed));
                        changed = true;
                    }
                    else
                    {
                        changed |= AddProtectionUnits(connection, transaction, existing.ProtectionId, AffectedIds(seed));
                    }
                    first ??= ReadProtection(connection, transaction, existing.ProtectionId);
                    continue;
                }
                var affectedIds = AffectedIds(seed);
                var protection = new ViewProtection(Guid.NewGuid().ToString("N"), seed.ItemId, seed.NoticeItemId, userId, seed.ItemIdentityHash, nowUtc, affectedIds);
                ExecuteNonQuery(connection, transaction,
                    "INSERT INTO view_protections(protection_id,item_id,notice_item_id,user_id,item_identity_hash,created_at_utc) VALUES ($id,$item,$notice,$user,$identity,$at)",
                    ("$id", protection.ProtectionId), ("$item", seed.ItemId), ("$notice", seed.NoticeItemId), ("$user", userId),
                    ("$identity", seed.ItemIdentityHash), ("$at", Format(nowUtc)));
                AddProtectionUnits(connection, transaction, protection.ProtectionId, affectedIds);
                first ??= protection;
                changed = true;
            }
            return (changed, first);
        });

    public LeavingSoonSnapshot MergeProtectionAffectedItemIds(IReadOnlyDictionary<string, IReadOnlyCollection<string>> affectedItemIds) => Execute(connection =>
    {
        using var transaction = connection.BeginTransaction();
        var changed = false;
        foreach (var pair in affectedItemIds)
            changed |= AddProtectionUnits(connection, transaction, pair.Key, pair.Value);
        if (changed) ExecuteNonQuery(connection, transaction, "UPDATE metadata SET revision=revision+1 WHERE singleton=1");
        transaction.Commit();
        return ReadSnapshot(connection);
    });

    public ProtectionMutationResult RemoveProtection(string protectionId, string? ownerUserId, DateTime nowUtc) =>
        MutateProtection((connection, transaction) =>
        {
            var protection = ReadProtection(connection, transaction, protectionId);
            if (protection is null || (ownerUserId is not null && !IdEquals(protection.UserId, ownerUserId))) return (false, null);
            var affectedIds = AffectedIds(protection);
            ExecuteNonQuery(connection, transaction, "DELETE FROM view_protections WHERE protection_id=$id", ("$id", protectionId));
            foreach (var affectedItemId in affectedIds)
                ResetWarningIfUnprotected(connection, transaction, affectedItemId, nowUtc);
            return (true, protection);
        });

    public ProtectionMutationResult RemoveProtectionsForItem(string itemId, DateTime nowUtc) =>
        MutateProtection((connection, transaction) =>
        {
            var matching = ReadProtections(connection, transaction).Where(x => AffectedIds(x).Any(id => IdEquals(id, itemId))).ToList();
            var first = matching.FirstOrDefault();
            if (first is null) return (false, null);
            foreach (var protection in matching)
                ExecuteNonQuery(connection, transaction, "DELETE FROM view_protections WHERE protection_id=$id", ("$id", protection.ProtectionId));
            foreach (var affectedItemId in matching.SelectMany(AffectedIds).Distinct(StringComparer.OrdinalIgnoreCase))
                ResetWarningIfUnprotected(connection, transaction, affectedItemId, nowUtc);
            return (true, first);
        });

    public ProtectionMutationResult RemoveProtectionsForOwnerItem(string itemId, string ownerUserId, DateTime nowUtc) =>
        MutateProtection((connection, transaction) =>
        {
            var matching = ReadProtections(connection, transaction)
                .Where(x => IdEquals(x.UserId, ownerUserId)
                    && AffectedIds(x).Any(id => IdEquals(id, itemId)))
                .ToList();
            var first = matching.FirstOrDefault();
            if (first is null) return (false, null);
            foreach (var protection in matching)
                ExecuteNonQuery(connection, transaction, "DELETE FROM view_protections WHERE protection_id=$id", ("$id", protection.ProtectionId));
            foreach (var affectedItemId in matching.SelectMany(AffectedIds).Distinct(StringComparer.OrdinalIgnoreCase))
                ResetWarningIfUnprotected(connection, transaction, affectedItemId, nowUtc);
            return (true, first);
        });

    public LeavingSoonSnapshot RemoveCompletedProtections(
        IReadOnlyCollection<string> protectionIds,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> affectedItemIds,
        DateTime nowUtc) => Mutate((connection, transaction) =>
    {
        var resetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var protectionId in protectionIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var protection = ReadProtection(connection, transaction, protectionId);
            if (protection is null) continue;
            resetIds.UnionWith(AffectedIds(protection));
            if (affectedItemIds.TryGetValue(protectionId, out var resolvedIds))
                resetIds.UnionWith(resolvedIds);
            ExecuteNonQuery(connection, transaction, "DELETE FROM view_protections WHERE protection_id=$id", ("$id", protectionId));
        }
        foreach (var itemId in resetIds)
            ResetWarningIfUnprotected(connection, transaction, itemId, nowUtc);
    });

    public LeavingSoonSnapshot MarkDeleted(IReadOnlyCollection<string> itemIds) => Mutate((connection, transaction) =>
    {
        foreach (var itemId in itemIds)
        {
            ExecuteNonQuery(connection, transaction, "DELETE FROM warning_entries WHERE item_id=$item", ("$item", itemId));
            ExecuteNonQuery(connection, transaction, "DELETE FROM view_protections WHERE item_id=$item", ("$item", itemId));
        }
    });

    private static void ResetWarningIfUnprotected(SqliteConnection connection, SqliteTransaction transaction, string itemId, DateTime nowUtc)
    {
        var count = ExecuteScalar<long>(connection, transaction,
            "SELECT COUNT(*) FROM view_protection_units WHERE item_id=$item", ("$item", itemId));
        if (count == 0) ResetWarning(connection, transaction, itemId, nowUtc);
    }

    private static void ResetWarning(SqliteConnection connection, SqliteTransaction transaction, string itemId, DateTime nowUtc) =>
        ExecuteNonQuery(connection, transaction,
            "UPDATE warning_entries SET status=$status,first_detected_utc=$now,first_published_utc=NULL,not_before_delete_utc=NULL,last_error=NULL WHERE item_id=$item",
            ("$status", (int)WarningStatus.PendingPublication), ("$now", Format(nowUtc)), ("$item", itemId));

    private LeavingSoonSnapshot Mutate(Action<SqliteConnection, SqliteTransaction> mutation) => Execute(connection =>
    {
        using var transaction = connection.BeginTransaction();
        mutation(connection, transaction);
        ExecuteNonQuery(connection, transaction, "UPDATE metadata SET revision=revision+1 WHERE singleton=1");
        transaction.Commit();
        return ReadSnapshot(connection);
    });

    private ProtectionMutationResult MutateProtection(Func<SqliteConnection, SqliteTransaction, (bool Changed, ViewProtection? Protection)> mutation) => Execute(connection =>
    {
        using var transaction = connection.BeginTransaction();
        var result = mutation(connection, transaction);
        if (result.Changed) ExecuteNonQuery(connection, transaction, "UPDATE metadata SET revision=revision+1 WHERE singleton=1");
        transaction.Commit();
        return new ProtectionMutationResult(result.Changed, ReadSnapshot(connection), result.Protection);
    });

    private T Execute<T>(Func<SqliteConnection, T> action)
    {
        EnsureInitialized();
        try
        {
            using var connection = OpenConnection();
            return action(connection);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Leaving Soon state operation failed; deletion will remain fail-closed. Database: {DatabasePath}", _databasePath);
            throw;
        }
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_migrationLock)
        {
            if (_initialized) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            ExecuteNonQuery(connection, transaction,
                """
                CREATE TABLE IF NOT EXISTS metadata (
                    singleton INTEGER PRIMARY KEY CHECK(singleton=1),schema_version INTEGER NOT NULL,revision INTEGER NOT NULL,
                    collection_id TEXT NULL,ready_collection_id TEXT NULL,last_successful_refresh_utc TEXT NULL,last_refresh_error TEXT NULL);
                CREATE TABLE IF NOT EXISTS warning_entries (
                    item_id TEXT PRIMARY KEY,kind INTEGER NOT NULL,status INTEGER NOT NULL,item_name TEXT NOT NULL,item_path TEXT NULL,
                    identity_hash TEXT NOT NULL,config_fingerprint TEXT NOT NULL,notice_generation TEXT NOT NULL DEFAULT 'initial',first_detected_utc TEXT NOT NULL,
                    first_published_utc TEXT NULL,not_before_delete_utc TEXT NULL,last_seen_utc TEXT NOT NULL,
                    notice_days INTEGER NOT NULL CHECK(notice_days >= 0),last_error TEXT NULL);
                CREATE TABLE IF NOT EXISTS warning_entry_rules (
                    item_id TEXT NOT NULL REFERENCES warning_entries(item_id) ON DELETE CASCADE,rule_id TEXT NOT NULL,
                    PRIMARY KEY(item_id,rule_id));
                INSERT OR IGNORE INTO metadata(singleton,schema_version,revision) VALUES (1,5,0);
                """);
            var version = ExecuteScalar<long>(connection, transaction, "SELECT schema_version FROM metadata WHERE singleton=1");
            if (version is 1 or 2)
            {
                ExecuteNonQuery(connection, transaction, "ALTER TABLE metadata ADD COLUMN last_successful_refresh_utc TEXT NULL");
                ExecuteNonQuery(connection, transaction, "ALTER TABLE metadata ADD COLUMN last_refresh_error TEXT NULL");
                CreateProtectionTable(connection, transaction);
                var where = version == 2 ? "WHERE k.status IN (0,1)" : string.Empty;
                ExecuteNonQuery(connection, transaction,
                    $"""
                    INSERT OR IGNORE INTO view_protections(protection_id,item_id,notice_item_id,user_id,item_identity_hash,created_at_utc)
                    SELECT lower(hex(randomblob(16))),k.item_id,k.item_id,k.requested_by_user_id,w.identity_hash,k.requested_at_utc
                    FROM keep_requests k LEFT JOIN warning_entries w ON w.item_id=k.item_id {where};
                    DROP TABLE IF EXISTS keep_request_events;
                    DROP TABLE keep_requests;
                    UPDATE warning_entries SET status=0,first_published_utc=NULL,not_before_delete_utc=NULL,last_error=NULL;
                    UPDATE metadata SET schema_version=4 WHERE singleton=1;
                    """);
                version = 4;
            }
            if (version == 3)
            {
                ExecuteNonQuery(connection, transaction, "ALTER TABLE view_protections ADD COLUMN notice_item_id TEXT NULL");
                ExecuteNonQuery(connection, transaction, "UPDATE view_protections SET notice_item_id=item_id WHERE notice_item_id IS NULL");
                ExecuteNonQuery(connection, transaction, "UPDATE metadata SET schema_version=4 WHERE singleton=1");
                version = 4;
            }
            if (version == 4)
            {
                ExecuteNonQuery(connection, transaction, "ALTER TABLE warning_entries ADD COLUMN notice_generation TEXT NOT NULL DEFAULT 'initial'");
                CreateProtectionTable(connection, transaction);
                CreateProtectionUnitsTable(connection, transaction);
                ExecuteNonQuery(connection, transaction,
                    "INSERT OR IGNORE INTO view_protection_units(protection_id,item_id) SELECT protection_id,item_id FROM view_protections");
                ExecuteNonQuery(connection, transaction,
                    "INSERT OR IGNORE INTO view_protection_units(protection_id,item_id) SELECT protection_id,notice_item_id FROM view_protections");
                ExecuteNonQuery(connection, transaction, "UPDATE metadata SET schema_version=5 WHERE singleton=1");
                version = 5;
            }
            if (version != SchemaVersion) throw new InvalidOperationException($"Unsupported Leaving Soon state schema {version}; expected {SchemaVersion}.");
            CreateProtectionTable(connection, transaction);
            CreateProtectionUnitsTable(connection, transaction);
            var integrity = ExecuteScalar<string>(connection, transaction, "PRAGMA integrity_check");
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Leaving Soon state integrity check failed: {integrity}");
            transaction.Commit();
            _initialized = true;
        }
    }

    private static void CreateProtectionTable(SqliteConnection connection, SqliteTransaction transaction) => ExecuteNonQuery(connection, transaction,
        """
        CREATE TABLE IF NOT EXISTS view_protections (
            protection_id TEXT NOT NULL PRIMARY KEY,item_id TEXT NOT NULL,notice_item_id TEXT NOT NULL,user_id TEXT NOT NULL,
            item_identity_hash TEXT NULL,created_at_utc TEXT NOT NULL,UNIQUE(item_id,user_id));
        CREATE INDEX IF NOT EXISTS ix_view_protections_item ON view_protections(item_id);
        CREATE INDEX IF NOT EXISTS ix_view_protections_user ON view_protections(user_id,created_at_utc);
        """);

    private static void CreateProtectionUnitsTable(SqliteConnection connection, SqliteTransaction transaction) => ExecuteNonQuery(connection, transaction,
        """
        CREATE TABLE IF NOT EXISTS view_protection_units (
            protection_id TEXT NOT NULL REFERENCES view_protections(protection_id) ON DELETE CASCADE,
            item_id TEXT NOT NULL,PRIMARY KEY(protection_id,item_id));
        CREATE INDEX IF NOT EXISTS ix_view_protection_units_item ON view_protection_units(item_id);
        """);

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static LeavingSoonSnapshot ReadSnapshot(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction(deferred: true);
        long revision; Guid? collectionId; Guid? readyCollectionId; DateTime? lastSuccess; string? lastError;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT revision,collection_id,ready_collection_id,last_successful_refresh_utc,last_refresh_error FROM metadata WHERE singleton=1";
            using var reader = command.ExecuteReader(); reader.Read();
            revision = reader.GetInt64(0); collectionId = ParseGuid(reader.IsDBNull(1) ? null : reader.GetString(1));
            readyCollectionId = ParseGuid(reader.IsDBNull(2) ? null : reader.GetString(2));
            lastSuccess = reader.IsDBNull(3) ? null : Parse(reader.GetString(3)); lastError = reader.IsDBNull(4) ? null : reader.GetString(4);
        }
        var warnings = ReadWarningIds(connection, transaction)
            .Select(id => ReadWarning(connection, transaction, id))
            .Where(entry => entry is not null)
            .Cast<WarningEntry>()
            .ToList();
        var protections = ReadProtections(connection, transaction);
        var snapshot = new LeavingSoonSnapshot(revision, collectionId, readyCollectionId, lastSuccess, lastError, warnings, protections);
        transaction.Commit();
        return snapshot;
    }

    private static IReadOnlyList<string> ReadWarningIds(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT item_id FROM warning_entries ORDER BY first_detected_utc,item_id";
        using var reader = command.ExecuteReader(); var result = new List<string>(); while (reader.Read()) result.Add(reader.GetString(0)); return result;
    }

    private static WarningEntry? ReadWarning(SqliteConnection connection, SqliteTransaction? transaction, string itemId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT kind,status,item_name,item_path,identity_hash,config_fingerprint,notice_generation,first_detected_utc,first_published_utc,not_before_delete_utc,last_seen_utc,notice_days,last_error FROM warning_entries WHERE item_id=$item";
        command.Parameters.AddWithValue("$item", itemId); using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        var entry = new WarningEntry(itemId, (Core.MediaItemKind)reader.GetInt32(0), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4), reader.GetString(5), (WarningStatus)reader.GetInt32(1), Parse(reader.GetString(7)),
            reader.IsDBNull(8) ? null : Parse(reader.GetString(8)), reader.IsDBNull(9) ? null : Parse(reader.GetString(9)),
            Parse(reader.GetString(10)), reader.GetInt32(11), [], reader.IsDBNull(12) ? null : reader.GetString(12), reader.GetString(6));
        reader.Close();
        using var ruleCommand = connection.CreateCommand(); ruleCommand.Transaction = transaction;
        ruleCommand.CommandText = "SELECT rule_id FROM warning_entry_rules WHERE item_id=$item ORDER BY rule_id";
        ruleCommand.Parameters.AddWithValue("$item", itemId); using var ruleReader = ruleCommand.ExecuteReader();
        var rules = new List<string>(); while (ruleReader.Read()) rules.Add(ruleReader.GetString(0)); return entry with { RuleIds = rules };
    }

    private static List<ViewProtection> ReadProtections(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT protection_id,item_id,notice_item_id,user_id,item_identity_hash,created_at_utc FROM view_protections ORDER BY created_at_utc,protection_id";
        using var reader = command.ExecuteReader(); var rows = new List<ViewProtection>();
        while (reader.Read()) rows.Add(new ViewProtection(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? reader.GetString(1) : reader.GetString(2),
            reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), Parse(reader.GetString(5))));
        reader.Close();
        return rows.Select(protection => protection with
        {
            AffectedItemIds = ReadProtectionUnits(connection, transaction, protection.ProtectionId),
        }).ToList();
    }

    private static IReadOnlyList<string> ReadProtectionUnits(SqliteConnection connection, SqliteTransaction? transaction, string protectionId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT item_id FROM view_protection_units WHERE protection_id=$id ORDER BY item_id";
        command.Parameters.AddWithValue("$id", protectionId);
        using var reader = command.ExecuteReader(); var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    private static bool AddProtectionUnits(SqliteConnection connection, SqliteTransaction transaction, string protectionId, IEnumerable<string> itemIds)
    {
        var changed = false;
        foreach (var itemId in itemIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT OR IGNORE INTO view_protection_units(protection_id,item_id) VALUES ($id,$item)";
            command.Parameters.AddWithValue("$id", protectionId); command.Parameters.AddWithValue("$item", itemId);
            changed |= command.ExecuteNonQuery() > 0;
        }
        return changed;
    }

    private static void ReplaceProtectionUnits(SqliteConnection connection, SqliteTransaction transaction, string protectionId, IEnumerable<string> itemIds)
    {
        ExecuteNonQuery(connection, transaction, "DELETE FROM view_protection_units WHERE protection_id=$id", ("$id", protectionId));
        AddProtectionUnits(connection, transaction, protectionId, itemIds);
    }

    private static IReadOnlyList<string> AffectedIds(ViewProtectionSeed protection) =>
        (protection.AffectedItemIds ?? []).Append(protection.ItemId).Append(protection.NoticeItemId)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static IReadOnlyList<string> AffectedIds(ViewProtection protection) =>
        (protection.AffectedItemIds ?? []).Append(protection.ItemId).Append(protection.NoticeItemId)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static ViewProtection? ReadProtection(SqliteConnection connection, SqliteTransaction transaction, string id) =>
        ReadProtections(connection, transaction).FirstOrDefault(x => string.Equals(x.ProtectionId, id, StringComparison.OrdinalIgnoreCase));
    private static ViewProtection? ReadProtectionByItemUser(SqliteConnection connection, SqliteTransaction transaction, string item, string user) =>
        ReadProtections(connection, transaction).FirstOrDefault(x => IdEquals(x.ItemId, item) && IdEquals(x.UserId, user));

    private static void ExecuteNonQuery(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static T ExecuteScalar<T>(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static string Format(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static object FormatNullable(DateTime? value) => value is null ? DBNull.Value : Format(value.Value);
    private static DateTime Parse(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
    private static Guid? ParseGuid(string? value) => Guid.TryParse(value, out var id) ? id : null;
    private static bool IdEquals(string left, string right) => Guid.TryParse(left, out var l) && Guid.TryParse(right, out var r) ? l == r : string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
