// -----------------------------------------------------------------------------
//  Copyright (c) 2024-present NoMercy Entertainment. All rights reserved.
//
//  This file is part of NoMercy MediaServer, source-available software (NOT open
//  source). Personal use and contributions are welcome; distribution, resale,
//  relicensing, and commercial exploitation are prohibited without explicit
//  written consent. See LICENSE for full terms. Distributed WITHOUT ANY WARRANTY.
//
//  SPDX-License-Identifier: LicenseRef-NoMercy-Proprietary
// -----------------------------------------------------------------------------

using System.Data;
using System.Data.Common;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NoMercy.Authorization;
using NoMercy.Database;
using NoMercy.Database.Maintenance;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Storage;
using NoMercy.Database.Models.Users;
using NoMercy.Encoder.Bundle;
using NoMercy.Encoder.Profiles;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Storage;
using NoMercyQueue.Workers;
using Serilog.Events;

namespace NoMercy.Service.Seeds;

public static class DatabaseSeeder
{
    /// <summary>
    /// Phase 1: Create database schema (migrations + EnsureCreated).
    /// Does NOT require authentication — safe to call before auth.
    /// </summary>
    public static async Task InitSchema(IStorage storage)
    {
        Logger.Setup("Initializing database schemas...");

        // 1. AppDbContext first — auth tokens live here, needed before content DB
        AppDbContext appDbContext = new();
        await Migrate(appDbContext);
        await EnsureDatabaseCreated(appDbContext);

        // Migrate Configuration data from media.db to app.db (one-time on update)
        await MigrateConfigurationData(appDbContext, storage);

        // Resolve the server's stable identity BEFORE anything else reads
        // Info.DeviceId (auth, registration, DNS, certs). Must run after the
        // Configuration migration above so a pre-existing "device_identity_id"
        // or "ssl_certificate" row carried over from media.db is visible to it.
        Info.SetResolvedDeviceId(DeviceIdentityResolver.ResolveAndPersist(appDbContext));

        // Decide the default DNS scheme (apex vs srv) exactly once, before
        // BootOrchestrator's Phase 3 registration reads RuntimeServerSettings
        // and sends dns_scheme. Must run in this same pass so it can see the
        // same "prior registration" evidence (auth token / cert rows) the
        // migration above just made visible, and before Phase 2 auth mints a
        // brand-new token that would make a fresh install look pre-registered.
        DnsSchemeResolver.ResolveAndPersist(appDbContext);

        await appDbContext.DisposeAsync();

        // 2. MediaContext — content and metadata
        MediaContext mediaDbContext = new();
        await Migrate(mediaDbContext);
        await EnsureDatabaseCreated(mediaDbContext);

        // 3. QueueContext — background jobs
        QueueContext queueDbContext = new();
        await Migrate(queueDbContext);
        await EnsureDatabaseCreated(queueDbContext);

        CronWorker.SignalDatabaseReady();
        Logger.Setup("Database schemas initialized");
    }

    private static async Task MigrateConfigurationData(AppDbContext appContext, IStorage storage)
    {
        // Only migrate if app.db has no Configuration rows AND media.db exists with rows
        bool appHasData = await appContext.Configuration.AnyAsync();
        if (appHasData)
            return;

        string mediaDbPath = AppFiles.MediaDatabase;
        if (!storage.Exists(mediaDbPath))
            return;

        try
        {
            // Check if media.db has a Configuration table with rows
            await using SqliteConnection checkConn = new($"Data Source={mediaDbPath}");
            await checkConn.OpenAsync();

            await using SqliteCommand checkCmd = checkConn.CreateCommand();
            checkCmd.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Configuration'";
            long tableExists = (long)(await checkCmd.ExecuteScalarAsync() ?? 0L);

            if (tableExists == 0)
            {
                await checkConn.CloseAsync();
                return;
            }

            await using SqliteCommand countCmd = checkConn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM Configuration";
            long rowCount = (long)(await countCmd.ExecuteScalarAsync() ?? 0L);

            await checkConn.CloseAsync();

            if (rowCount == 0)
                return;

            // Copy rows using ATTACH DATABASE on the app.db connection
            string appDbPath = AppFiles.AppDatabase;
            await using SqliteConnection appConn = new($"Data Source={appDbPath}");
            await appConn.OpenAsync();

            await using SqliteCommand attachCmd = appConn.CreateCommand();
            attachCmd.CommandText = $"ATTACH DATABASE '{mediaDbPath}' AS source";
            await attachCmd.ExecuteNonQueryAsync();

            await using SqliteCommand copyCmd = appConn.CreateCommand();
            copyCmd.CommandText =
                "INSERT OR IGNORE INTO Configuration (Key, Value, ModifiedBy, CreatedAt, UpdatedAt) "
                + "SELECT Key, Value, ModifiedBy, CreatedAt, UpdatedAt FROM source.Configuration";
            int copied = await copyCmd.ExecuteNonQueryAsync();

            await using SqliteCommand detachCmd = appConn.CreateCommand();
            detachCmd.CommandText = "DETACH DATABASE source";
            await detachCmd.ExecuteNonQueryAsync();

            await appConn.CloseAsync();

            Logger.Setup($"Migrated {copied} configuration rows from media.db to app.db");
        }
        catch (Exception ex)
        {
            Logger.Setup(
                $"Configuration migration from media.db failed (non-fatal): {ex.Message}",
                LogEventLevel.Warning
            );
        }
    }

    /// <summary>
    /// Seed truly offline data (config, encoder profiles, libraries).
    /// No network or auth required — safe to call right after InitSchema().
    /// Each seed is individually guarded so one failure doesn't block the rest.
    /// </summary>
    public static async Task SeedOfflineData(IStorage storage, IStorageDriver storageDriver)
    {
        AppDbContext appDbContext = new();
        MediaContext mediaDbContext = new();

        Func<Task>[] offlineSeeds =
        [
            appDbContext.Init,
            () => SeedSystemLocalDriver(mediaDbContext),
            () => V1DriverBridgeSeed.RunAsync(mediaDbContext),
            () => LibrariesSeed.Init(mediaDbContext, storage, storageDriver),
            () => EncodingPresetsSeed.Init(mediaDbContext, storage),
            () => LoadDiskOverlaysAsync(mediaDbContext),
        ];

        foreach (Func<Task> seed in offlineSeeds)
        {
            try
            {
                await seed();
            }
            catch (Exception ex)
            {
                Logger.Setup($"Offline seed failed: {ex.Message}", LogEventLevel.Warning);
            }
        }
    }

    /// <summary>
    /// Seed provider data (TMDB genres, languages, certifications, etc.).
    /// Requires API keys (no auth). Called early in startup before any import jobs.
    /// </summary>
    /// <summary>
    /// Requires the caller to have already run <see cref="SeedOfflineData"/> — the only
    /// caller (<c>ServerBootstrapper</c>) runs it directly beforehand, immediately after
    /// schema init, so the UI has config/library/encoder-preset data before auth
    /// completes. Re-running it here doubled all six sub-seeds (44 SELECTs + 44 upserts
    /// on encoder presets alone) on every boot for no reason.
    /// </summary>
    public static async Task Run(IStorage storage, IStorageDriver storageDriver)
    {
        MediaContext mediaDbContext = new();

        Func<Task>[] seeds =
        [
            () => LanguagesSeed.Init(mediaDbContext),
            () => CountriesSeed.Init(mediaDbContext),
            () => GenresSeed.Init(mediaDbContext),
            () => CertificationsSeed.Init(mediaDbContext),
        ];

        foreach (Func<Task> seed in seeds)
        {
            try
            {
                await seed();
            }
            catch (Exception ex)
            {
                Logger.Setup($"Seed failed: {ex.Message}", LogEventLevel.Warning);
            }
        }

        // Music genres come from MusicBrainz, which serves one request per second and
        // pages 2181 genres. Awaited here it put 77 seconds between a brand-new server
        // starting and its owner being able to log in, measured on a from-zero boot.
        // Nothing in signing in, browsing or playing needs them; the music scan that does
        // is queue work that starts later, and MusicGenresSeed checks the table before it
        // touches the network, so a server that already has them pays nothing either way.
        _ = Task.Run(async () =>
        {
            try
            {
                await MusicGenresSeed.Init(new MediaContext());
            }
            catch (Exception ex)
            {
                Logger.Setup($"Music genres seed failed: {ex.Message}", LogEventLevel.Warning);
            }
        });
    }

    public static async Task LoadDiskOverlaysAsync(MediaContext context)
    {
        string overlayDir = Path.Combine(AppFiles.DataPath, "profiles");
        Directory.CreateDirectory(overlayDir);

        DiskOverlayLoader.LoadResult overlay = DiskOverlayLoader.Load(overlayDir);

        foreach (string error in overlay.Errors)
            Logger.Setup($"Disk overlay load error: {error}", LogEventLevel.Warning);

        foreach (DiskOverlayLoader.LoadedPreset entry in overlay.Loaded)
        {
            EncodingProfile p = entry.Profile;
            Database.Models.Media.EncodingPreset? existing =
                await context.EncodingPresets.FirstOrDefaultAsync(x => x.Id == p.Id);

            if (existing is null)
            {
                context.EncodingPresets.Add(
                    new()
                    {
                        Id = p.Id,
                        Name = p.Name,
                        Description = entry.Description,
                        ProfileJson = Newtonsoft.Json.JsonConvert.SerializeObject(p),
                        ParentPresetId = entry.ParentPresetId,
                        IsBuiltIn = false,
                        Source = $"disk:{Path.GetFileName(entry.SourcePath)}",
                    }
                );
            }
            else if (existing.IsBuiltIn)
            {
                Logger.Setup(
                    $"Disk overlay '{entry.SourcePath}' has Ulid {p.Id} that collides with a built-in preset '{existing.Name}'. "
                        + "Built-ins are immutable; disk overlay rejected. Use a different Ulid to coexist.",
                    LogEventLevel.Warning
                );
            }
            else
            {
                existing.Name = p.Name;
                existing.Description = entry.Description ?? existing.Description;
                existing.ProfileJson = Newtonsoft.Json.JsonConvert.SerializeObject(p);
                existing.ParentPresetId = entry.ParentPresetId;
                existing.Source = $"disk:{Path.GetFileName(entry.SourcePath)}";
            }
        }

        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Ensures the built-in system local driver row exists. Idempotent — skips
    /// if the row is already present. The driver uses an empty rootPath so
    /// StorageFactory builds each folder's guard from the folder's own subPath,
    /// giving per-folder path isolation without a separate driver per folder.
    /// </summary>
    public static async Task SeedSystemLocalDriver(MediaContext mediaContext)
    {
        bool exists = await mediaContext.Drivers.AnyAsync(d => d.Id == Driver.SystemLocalDriverId);
        if (exists)
            return;

        Driver systemLocalDriver = new()
        {
            Id = Driver.SystemLocalDriverId,
            Name = "Local",
            Type = "local",
            Config = "{\"rootPath\":\"\"}",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        mediaContext.Drivers.Add(systemLocalDriver);
        await mediaContext.SaveChangesAsync();

        Logger.Setup("Seeded system local driver", LogEventLevel.Verbose);
    }

    /// <summary>
    /// Runs the bundle-slug rename pass after DI is available. Must be called
    /// after <see cref="Run"/> so the <see cref="IStorageFactory"/> singleton
    /// is fully configured with driver config resolvers.
    /// </summary>
    public static async Task RunBundleSlugRenamePassAsync(
        IStorageFactory storageFactory,
        ILogger<BundleSlugRenamer> logger
    )
    {
        if (BuiltinPresetRenames.SlugRenames.Count == 0)
            return;

        try
        {
            MediaContext context = new();
            BundleSlugRenamer renamer = new(
                BuiltinPresetRenames.SlugRenames,
                storageFactory,
                context,
                logger
            );
            await renamer.RunAsync();
            Logger.Setup("Bundle slug rename pass complete", LogEventLevel.Verbose);
        }
        catch (Exception ex)
        {
            Logger.Setup($"Bundle slug rename pass failed: {ex.Message}", LogEventLevel.Warning);
        }
    }

    /// <summary>
    /// Seed auth-dependent data (users, library assignment, claims).
    /// Called after auth completes via BootOrchestrator.
    /// </summary>
    public static async Task SeedAuthData(IStorage storage, string? accessToken)
    {
        MediaContext mediaDbContext = new();

        Func<Task>[] seeds =
        [
            () => mediaDbContext.Init(storage, accessToken),
            () => AssignOwnerToUnassignedLibraries(mediaDbContext),
            () => UserCache.Current.InitializeAsync(mediaDbContext),
        ];

        foreach (Func<Task> seed in seeds)
        {
            try
            {
                await seed();
            }
            catch (Exception ex)
            {
                Logger.Setup($"Auth seed failed: {ex.Message}", LogEventLevel.Warning);
            }
        }
    }

    private static async Task AssignOwnerToUnassignedLibraries(MediaContext mediaContext)
    {
        try
        {
            User? owner = await mediaContext.Users.FirstOrDefaultAsync(u => u.Owner);
            if (owner is null)
                return;

            List<Ulid> assignedLibraryIds = await mediaContext
                .LibraryUser.Select(lu => lu.LibraryId)
                .Distinct()
                .ToListAsync();

            List<Library> unassigned = await mediaContext
                .Libraries.Where(l => !assignedLibraryIds.Contains(l.Id))
                .ToListAsync();

            if (unassigned.Count == 0)
                return;

            foreach (Library library in unassigned)
            {
                mediaContext.LibraryUser.Add(new(library.Id, owner.Id));
            }

            await mediaContext.SaveChangesAsync();
            Logger.Setup($"Assigned {unassigned.Count} libraries to owner {owner.Name}");
        }
        catch (Exception ex)
        {
            Logger.Setup(
                $"Failed to assign libraries to owner: {ex.Message}",
                LogEventLevel.Warning
            );
        }
    }

    private static Task Migrate(DbContext context)
    {
        string contextName = context.GetType().Name;

        // Check if migration history table exists to determine DB state.
        // Do NOT run PRAGMA commands first — they create an empty .db file
        // which causes CanConnect() to return true on a fresh installation.
        // NOTE: Must use raw ADO.NET here — ExecuteSqlRaw returns rows-affected (-1 for SELECT),
        // not the query result, so it can't be used to read a scalar value.
        bool migrationTableExists = false;
        try
        {
            DbConnection connection = context.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
                connection.Open();
            using DbCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory'";
            migrationTableExists = Convert.ToInt64(command.ExecuteScalar()) > 0;
        }
        catch
        {
            // Could not check — assume table doesn't exist
        }

        List<string> availableMigrations = context.Database.GetMigrations().ToList();

        // Self-heal: if the migration-history table says a migration has been
        // applied but its physical tables are missing, EF's GetPendingMigrations()
        // returns empty and we skip Migrate(). That's how a half-applied
        // schema gets stuck. Scan the applied list for migrations whose
        // expected tables don't exist and unstamp them so the next
        // GetPendingMigrations() reports them as pending.
        if (migrationTableExists)
        {
            UnstampMigrationsMissingTables(context, contextName);
        }

        // Use pending list as the source of truth — count equality was too
        // weak (same count by coincidence silently skipped new migrations).
        List<string> pendingMigrations = migrationTableExists
            ? context.Database.GetPendingMigrations().ToList()
            : availableMigrations;

        if (pendingMigrations.Count == 0)
        {
            Logger.Setup(
                $"{contextName}: Database is up to date ({availableMigrations.Count} migrations applied).",
                LogEventLevel.Verbose
            );
        }
        else
        {
            Logger.Setup(
                $"{contextName}: Applying {pendingMigrations.Count} migration(s): {string.Join(", ", pendingMigrations)}",
                LogEventLevel.Verbose
            );

            string? dbPath = context.Database.GetDbConnection().DataSource;
            if (!string.IsNullOrEmpty(dbPath))
                DatabaseBackupService.BackupBeforeMigration(dbPath, pendingMigrations.Count);

            // Rows whose parent was deleted before cascade rules existed make EF's
            // SQLite table-rebuild migrations throw "FOREIGN KEY constraint failed"
            // when they copy the orphan. Clear them first (the backup above is the
            // safety net) so those migrations can apply. Best-effort: a cleanup
            // failure must not block the migration — the catch below still reports
            // any orphan that survives.
            try
            {
                IReadOnlyDictionary<string, int> removedOrphans = ForeignKeyOrphanCleaner.Clean(
                    context.Database.GetDbConnection(),
                    contextName
                );
                if (removedOrphans.Count > 0)
                    Logger.Setup(
                        $"{contextName}: Removed {removedOrphans.Values.Sum()} foreign-key-orphaned row(s) before migration: "
                            + string.Join(
                                ", ",
                                removedOrphans.Select(entry => $"{entry.Key}={entry.Value}")
                            ),
                        LogEventLevel.Warning
                    );
            }
            catch (Exception ex)
            {
                Logger.Setup(
                    $"{contextName}: Orphan pre-flight failed (non-fatal): {ex.Message}",
                    LogEventLevel.Warning
                );
            }

            try
            {
                context.Database.Migrate();
                Logger.Setup(
                    $"{contextName}: Migrations applied successfully.",
                    LogEventLevel.Verbose
                );
            }
            catch (Exception ex) when (ex.Message.Contains("already exists"))
            {
                RecoverExistingMigration(context, pendingMigrations, ex);
            }
            catch (Exception ex) when (ex.Message.Contains("FOREIGN KEY constraint failed"))
            {
                IReadOnlyList<string> violations = ForeignKeyOrphanCleaner.DescribeViolations(
                    context.Database.GetDbConnection()
                );
                Logger.Setup(
                    $"{contextName}: Migration failed on a foreign-key constraint. "
                        + (
                            violations.Count > 0
                                ? $"Orphaned rows: {string.Join("; ", violations)}"
                                : "No pre-existing orphans remain — the violation is in a migration's own data step."
                        ),
                    LogEventLevel.Fatal
                );
                throw;
            }
        }

        // Configure SQLite pragmas after schema exists
        context.Database.ExecuteSqlRaw("PRAGMA journal_mode = WAL;");
        context.Database.ExecuteSqlRaw("PRAGMA encoding = 'UTF-8'");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Detects migrations that are marked as applied in __EFMigrationsHistory
    /// but whose <c>CREATE TABLE</c> statements never actually ran — the
    /// physical table is missing. Unstamps those rows so the next
    /// <c>Migrate()</c> call sees them as pending and applies them.
    ///
    /// How this state happens: older versions' migration-history sync (triggered
    /// by an "already exists" catch on an unrelated migration) could mark
    /// every pending migration as applied even though only one of them
    /// actually ran. Future runs then short-circuit because history says
    /// "all applied."
    /// </summary>
    private static void UnstampMigrationsMissingTables(DbContext context, string contextName)
    {
        IReadOnlyDictionary<string, string> migrationTables = GetExpectedTablesPerMigration(
            context
        );
        if (migrationTables.Count == 0)
            return;

        HashSet<string> existingTables = GetExistingTables(context);
        List<string> appliedMigrations = context.Database.GetAppliedMigrations().ToList();

        List<string> toUnstamp = [];
        foreach (string migrationId in appliedMigrations)
        {
            if (!migrationTables.TryGetValue(migrationId, out string? expectedTable))
                continue;
            if (string.IsNullOrEmpty(expectedTable))
                continue;
            if (existingTables.Contains(expectedTable))
                continue;

            toUnstamp.Add(migrationId);
        }

        if (toUnstamp.Count == 0)
            return;

        Logger.Setup(
            $"{contextName}: Detected {toUnstamp.Count} stamped-but-missing migration(s), "
                + $"unstamping so they re-apply: {string.Join(", ", toUnstamp)}",
            LogEventLevel.Warning
        );

        foreach (string migrationId in toUnstamp)
        {
            try
            {
                context.Database.ExecuteSqlRaw(
                    "DELETE FROM __EFMigrationsHistory WHERE MigrationId = {0}",
                    migrationId
                );
            }
            catch (Exception ex)
            {
                Logger.Setup(
                    $"{contextName}: Could not unstamp {migrationId}: {ex.Message}",
                    LogEventLevel.Warning
                );
            }
        }
    }

    /// <summary>
    /// Walks the model snapshot to produce a map of <c>migrationId → primary
    /// table name</c>. We only need the table name a migration creates;
    /// if multiple tables land in one migration we track the first one
    /// (usually enough to detect the "stamped but not run" state).
    /// </summary>
    private static IReadOnlyDictionary<string, string> GetExpectedTablesPerMigration(
        DbContext context
    )
    {
        Dictionary<string, string> result = new()
        {
            // We can't reliably ask EF for the Up/Down operations per migration
            // without reflection on generated types. Instead, keep a small curated
            // lookup for migrations whose primary table is the failure surface —
            // this list grows as new tables are added. The fallback is "unknown
            // migration" which we treat as safe (don't unstamp).
            ["20260416210105_AddEncodingHistoryTable"] = "EncodingHistory",
            ["20260417010426_AddEncodingPresetTable"] = "EncodingPresets",
            ["20260417011900_AddContentSegmentTable"] = "ContentSegments",
        };

        // Context-type filtering: only return entries whose migration name
        // appears in the context's migration set.
        HashSet<string> known = context.Database.GetMigrations().ToHashSet();
        return result
            .Where(kv => known.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private static HashSet<string> GetExistingTables(DbContext context)
    {
        HashSet<string> tables = new(StringComparer.OrdinalIgnoreCase);

        try
        {
            DbConnection connection = context.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
                connection.Open();

            using DbCommand command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            using DbDataReader reader = command.ExecuteReader();
            while (reader.Read())
                tables.Add(reader.GetString(0));
        }
        catch
        {
            // Best-effort — on failure we just skip the self-heal step.
        }

        return tables;
    }

    private static void RecoverExistingMigration(
        DbContext context,
        List<string> pendingMigrations,
        Exception collision
    )
    {
        IMigrationsAssembly assembly = context.GetService<IMigrationsAssembly>();
        foreach (string migrationId in pendingMigrations)
        {
            if (context.Database.GetAppliedMigrations().Contains(migrationId))
                continue;

            if (
                !assembly.Migrations.TryGetValue(migrationId, out TypeInfo? migrationType)
                || !MigrationObjectsExist(
                    context,
                    assembly.CreateMigration(migrationType, context.Database.ProviderName!)
                )
            )
                throw collision;

            context.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (MigrationId TEXT NOT NULL CONSTRAINT PK___EFMigrationsHistory PRIMARY KEY, ProductVersion TEXT NOT NULL)"
            );
            string version = context.GetType().Assembly.GetName().Version?.ToString() ?? "1.0.0";
            context.Database.ExecuteSqlRaw(
                "INSERT OR IGNORE INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ({0}, {1})",
                [migrationId, version]
            );
            Logger.Setup(
                $"Verified existing schema for {migrationId}; retrying remaining migrations.",
                LogEventLevel.Warning
            );

            try
            {
                context.Database.Migrate();
                return;
            }
            catch (Exception ex) when (ex.Message.Contains("already exists"))
            {
                collision = ex;
            }
        }

        throw collision;
    }

    private static bool MigrationObjectsExist(DbContext context, Migration migration)
    {
        DbConnection connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            connection.Open();

        if (migration.UpOperations.Count == 0)
            return false;

        foreach (MigrationOperation operation in migration.UpOperations)
        {
            switch (operation)
            {
                case CreateTableOperation table
                    when table.ForeignKeys.Count == 0
                        && table.UniqueConstraints.Count == 0
                        && table.CheckConstraints.Count == 0:
                    if (!TableMatches(connection, table))
                        return false;
                    break;
                case AddColumnOperation column
                    when column.DefaultValue is null && column.DefaultValueSql is null:
                    if (!ColumnExists(connection, column.Table, column))
                        return false;
                    break;
                case CreateIndexOperation index when index.Filter is null:
                    if (!IndexMatches(connection, index))
                        return false;
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private static bool TableMatches(DbConnection connection, CreateTableOperation table)
    {
        if (!ObjectExists(connection, "table", table.Name))
            return false;

        foreach (AddColumnOperation column in table.Columns)
        {
            if (
                column.DefaultValue is not null
                || column.DefaultValueSql is not null
                || !ColumnExists(connection, table.Name, column)
            )
                return false;
        }

        using (DbCommand command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA table_info(\"{table.Name.Replace("\"", "\"\"")}\")";
            using DbDataReader reader = command.ExecuteReader();
            Dictionary<int, string> keyColumns = new();
            while (reader.Read())
            {
                int keyOrder = reader.GetInt32(5);
                if (keyOrder > 0)
                    keyColumns.Add(keyOrder, reader.GetString(1));
            }

            IEnumerable<string> expectedKey = table.PrimaryKey?.Columns ?? [];
            if (
                !keyColumns
                    .OrderBy(entry => entry.Key)
                    .Select(entry => entry.Value)
                    .SequenceEqual(expectedKey, StringComparer.OrdinalIgnoreCase)
            )
                return false;
        }

        return true;
    }

    private static bool ColumnExists(
        DbConnection connection,
        string table,
        AddColumnOperation column
    )
    {
        using DbCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\")";
        using DbDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (
                reader.GetString(1).Equals(column.Name, StringComparison.OrdinalIgnoreCase)
                && (
                    column.ColumnType is null
                    || reader
                        .GetString(2)
                        .Equals(column.ColumnType, StringComparison.OrdinalIgnoreCase)
                )
                && (reader.GetInt32(3) == 0) == column.IsNullable
            )
                return true;
        }

        return false;
    }

    private static bool IndexMatches(DbConnection connection, CreateIndexOperation index)
    {
        if (!ObjectExists(connection, "index", index.Name))
            return false;

        List<string> columns = [];
        using (DbCommand command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA index_info(\"{index.Name.Replace("\"", "\"\"")}\")";
            using DbDataReader reader = command.ExecuteReader();
            while (reader.Read())
                columns.Add(reader.GetString(2));
        }
        if (!columns.SequenceEqual(index.Columns, StringComparer.OrdinalIgnoreCase))
            return false;

        using DbCommand listCommand = connection.CreateCommand();
        listCommand.CommandText = $"PRAGMA index_list(\"{index.Table.Replace("\"", "\"\"")}\")";
        using DbDataReader listReader = listCommand.ExecuteReader();
        while (listReader.Read())
        {
            if (listReader.GetString(1).Equals(index.Name, StringComparison.OrdinalIgnoreCase))
                return (listReader.GetInt32(2) != 0) == index.IsUnique;
        }

        return false;
    }

    private static bool ObjectExists(DbConnection connection, string type, string name)
    {
        using DbCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = @type AND name = @name";
        DbParameter typeParameter = command.CreateParameter();
        typeParameter.ParameterName = "@type";
        typeParameter.Value = type;
        command.Parameters.Add(typeParameter);
        DbParameter nameParameter = command.CreateParameter();
        nameParameter.ParameterName = "@name";
        nameParameter.Value = name;
        command.Parameters.Add(nameParameter);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    private static async Task EnsureDatabaseCreated(DbContext context)
    {
        Logger.Setup(
            $"Ensuring database is created for {context.GetType().Name}",
            LogEventLevel.Verbose
        );
        await context.Database.EnsureCreatedAsync();
    }
}
