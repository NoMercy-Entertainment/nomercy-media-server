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

// ReSharper disable MemberCanBePrivate.Global

using NoMercy.NmSystem.SystemCalls;
using Serilog.Events;

namespace NoMercy.NmSystem.Information;

public static class AppFiles
{
    public static readonly string ApplicationName = "NoMercy MediaServer";

    public static readonly string AppDataPath =
        Environment.OSVersion.Platform == PlatformID.Unix
            ? Path.Combine(
                Environment.GetEnvironmentVariable("HOME") ?? "/home/current",
                ".local/share"
            )
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    // An explicit NOMERCY_APP_PATH override lets each process (notably each
    // parallel test assembly) use a fully isolated app-data root — its own
    // database, cache and logs — so concurrent test processes never collide
    // on shared on-disk state. Unset in production, so behaviour is unchanged.
    public static string AppPath =>
        Environment.GetEnvironmentVariable("NOMERCY_APP_PATH") is { Length: > 0 } appPathOverride
            ? appPathOverride
        : Config.IsTest ? Path.Combine(AppDataPath, "NoMercy_test")
        : Config.IsDev ? Path.Combine(AppDataPath, "NoMercy_dev")
        : Path.Combine(AppDataPath, "NoMercy");

    // ── Config ───────────────────────────────────────────────────────────

    public static string ConfigPath => Path.Combine(AppPath, "config");

    [Obsolete("Tokens are now stored encrypted in app.db. Kept for migration detection only.")]
    public static string TokenFile => Path.Combine(ConfigPath, "token.json");
    public static string AuthKeysFile => Path.Combine(ConfigPath, "auth_keys.json");
    public static string JwksCacheFile => Path.Combine(ConfigPath, "jwks_cache.json");
    public static string ApiKeysFile => Path.Combine(ConfigPath, "api_keys.json");
    public static string TraySettingsFile => Path.Combine(ConfigPath, "tray_settings.json");

    public static string SeedsPath => Path.Combine(ConfigPath, "seeds");
    public static string FolderRootsSeedFile => Path.Combine(SeedsPath, "folderRoots.jsonc");
    public static string LibrariesSeedFile => Path.Combine(SeedsPath, "libraries.jsonc");
    public static string EncoderProfilesSeedFile =>
        Path.Combine(SeedsPath, "encoderProfiles.jsonc");
    public static string EncodingPresetsSeedFile =>
        Path.Combine(SeedsPath, "encodingPresets.jsonc");

    // ── Data & Logs ──────────────────────────────────────────────────────

    public static string DataPath => Path.Combine(AppPath, "data");
    public static string LogPath => Path.Combine(AppPath, "log");

    // AES-128 HLS DRM keys, protected at rest via DataProtection (see
    // NoMercy.NmSystem.Security.DrmKeyStore). Never served as static files —
    // outside CachePath/TranscodePath so it can never land in a published
    // transcode output.
    public static string DrmKeysPath => Path.Combine(DataPath, "drm_keys");

    // ── Cache ────────────────────────────────────────────────────────────

    public static string CachePath => Path.Combine(AppPath, "cache");
    public static string ApiCachePath => Path.Combine(CachePath, "api");
    public static string TempPath => Path.Combine(CachePath, "temp");
    public static string TranscodePath => Path.Combine(CachePath, "transcode");
    public static string EncoderCachePath => Path.Combine(CachePath, "encoder");
    public static string ImagesPath => Path.Combine(CachePath, "images");
    public static string MusicImagesPath => Path.Combine(ImagesPath, "music");
    public static string TempImagesPath => Path.Combine(ImagesPath, "temp");

    // Encoder hardware speed-index cache. Stores per-encoder/codec/resolution
    // FPS measurements so reboots reuse the calibration instead of redoing
    // 20+ minutes of synthetic encodes on every start.
    public static string SpeedIndexCachePath =>
        Path.Combine(CachePath, "encoder", "speed_index.json");

    // ── Browser ──────────────────────────────────────────────────────────

    public static string BrowserPath => Path.Combine(AppPath, "browser");

    // ── Plugins ──────────────────────────────────────────────────────────

    public static string PluginsPath => Path.Combine(AppPath, "plugins");
    public static string PluginConfigPath => Path.Combine(PluginsPath, "configurations");

    // ── Binaries (standalone NoMercy executables for auto-update) ────────

    public static string BinariesPath => Path.Combine(AppPath, "binaries");

    /// <summary>
    /// Path for external dependencies (FFmpeg, cloudflared, yt-dlp, etc.).
    /// For installer deployments, this is under the install directory so everything is self-contained.
    /// For standalone deployments, this falls back to BinariesPath in AppData.
    /// </summary>
    public static string DependenciesPath
    {
        get
        {
            string? installDir = Environment.GetEnvironmentVariable("NOMERCY_INSTALL_DIR");
            if (!string.IsNullOrEmpty(installDir))
                return Path.Combine(installDir, "binaries");
            return BinariesPath;
        }
    }

    public static string FfmpegFolder => Path.Combine(DependenciesPath, "ffmpeg");
    public static string FfmpegPath => Path.Combine(FfmpegFolder, "ffmpeg" + Info.ExecSuffix);
    public static string FfProbePath => Path.Combine(FfmpegFolder, "ffprobe" + Info.ExecSuffix);
    public static string FfPlayPath => Path.Combine(FfmpegFolder, "ffplay" + Info.ExecSuffix);

    // shaka-packager lives alongside ffmpeg so EncoderOptions resolves it as the
    // "packager" sibling of the ffmpeg path for CENC/raw-key DRM packaging.
    public static string ShakaPackagerPath =>
        Path.Combine(FfmpegFolder, "packager" + Info.ExecSuffix);

    public static string YtdlpPath => Path.Combine(DependenciesPath, "yt-dlp" + Info.ExecSuffix);

    public static string TesseractFolder => Path.Combine(DependenciesPath, "tesseract");
    public static string TesseractModelsFolder => Path.Combine(TesseractFolder, "tessdata");

    public static string WhisperModel { get; set; } = "ggml-large-v3";

    // The model ships inside the ffmpeg/ subfolder alongside ffmpeg.exe and the
    // libbluray jars — the build output puts everything ffmpeg-runtime there.
    public static string WhisperModelPath => Path.Combine(FfmpegFolder, WhisperModel + ".bin");

    public static string StemsplitModel { get; set; } = "spleeter-2stems-f16";

    // The model ships beside ffmpeg (like the whisper model above) so a relative
    // file name resolves when ffmpeg runs from FfmpegFolder — the analysis job
    // passes the stemsplit filter a relative name because a Windows drive-letter
    // colon in an absolute path splits the filter argument.
    public static string StemsplitModelPath => Path.Combine(FfmpegFolder, StemsplitModel + ".gguf");

    public static string CloudflareDPath =>
        Path.Combine(DependenciesPath, "cloudflared" + Info.ExecSuffix);

    public static string ServerExePath =>
        Path.Combine(BinariesPath, "NoMercyMediaServer" + Info.ExecSuffix);
    public static string AppExePath => Path.Combine(BinariesPath, "NoMercyApp" + Info.ExecSuffix);
    public static string LauncherExePath =>
        Path.Combine(BinariesPath, "NoMercyLauncher" + Info.ExecSuffix);
    public static string CliExePath => Path.Combine(BinariesPath, "nomercy" + Info.ExecSuffix);
    public static string ServerTempExePath =>
        Path.Combine(BinariesPath, "NoMercyMediaServer_temp" + Info.ExecSuffix);
    public static string LauncherTempExePath =>
        Path.Combine(BinariesPath, "NoMercyLauncher_temp" + Info.ExecSuffix);
    public static string CliTempExePath =>
        Path.Combine(BinariesPath, "nomercy_temp" + Info.ExecSuffix);

    // ── Security ─────────────────────────────────────────────────────────

    public static string SecurityPath => Path.Combine(AppPath, "security");

    public static string CertPath => Path.Combine(SecurityPath, "certs");

    [Obsolete("Certs are now stored encrypted in app.db. Kept for migration detection only.")]
    public static string CertFile => Path.Combine(CertPath, "cert.pem");

    [Obsolete("Certs are now stored encrypted in app.db. Kept for migration detection only.")]
    public static string KeyFile => Path.Combine(CertPath, "key.pem");

    [Obsolete("Certs are now stored encrypted in app.db. Kept for migration detection only.")]
    public static string CaFile => Path.Combine(CertPath, "ca.pem");

    public static string SecretsPath => Path.Combine(SecurityPath, "secrets");
    public static string SecretsStore => Path.Combine(SecretsPath, "secrets.bin");
    public static string SecretsKey => Path.Combine(SecretsPath, "secrets.key");

    // ── Misc ─────────────────────────────────────────────────────────────

    public static string AppIcon =>
        Path.Combine(Directory.GetCurrentDirectory(), "Assets/icon" + Info.IconSuffix);

    public static string MediaDatabase => Path.Combine(DataPath, "media.db");
    public static string QueueDatabase => Path.Combine(DataPath, "queue.db");
    public static string AppDatabase => Path.Combine(DataPath, "app.db");

    // ── DataProtection keys ─────────────────────────────────────────────

    public static string DataProtectionKeysDir => Path.Combine(DataPath, "keys");

    // ── Directory management ─────────────────────────────────────────────

    public static IEnumerable<string> AllPaths()
    {
        return
        [
            AppDataPath,
            AppPath,
            BinariesPath,
            BrowserPath,
            CachePath,
            ApiCachePath,
            EncoderCachePath,
            CertPath,
            ConfigPath,
            SeedsPath,
            DataPath,
            DataProtectionKeysDir,
            DependenciesPath,
            ImagesPath,
            LogPath,
            MusicImagesPath,
            PluginConfigPath,
            PluginsPath,
            SecretsPath,
            SecurityPath,
            TempImagesPath,
            TempPath,
            TesseractModelsFolder,
            TranscodePath,
        ];
    }

    public static Task CreateAppFolders()
    {
        if (!Directory.Exists(AppPath))
            Directory.CreateDirectory(AppPath);

        MigrateOldPaths();

        // DataProtection keys need restrictive permissions (700, not 755)
        if (!Directory.Exists(DataProtectionKeysDir))
        {
            Directory.CreateDirectory(DataProtectionKeysDir);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                DirectoryInfo keysDir = new(DataProtectionKeysDir)
                {
                    UnixFileMode =
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                };
            }
        }

        foreach (string path in AllPaths().Where(path => !Directory.Exists(path)))
        {
            Logger.Setup($"Creating directory: {path}", LogEventLevel.Verbose);
            Directory.CreateDirectory(path);
            if (Environment.OSVersion.Platform == PlatformID.Unix)
            {
                // Set appropriate Unix permissions (755)
                DirectoryInfo dirInfo = new(path);
                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    dirInfo.UnixFileMode =
                        UnixFileMode.UserRead
                        | UnixFileMode.UserWrite
                        | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead
                        | UnixFileMode.GroupExecute
                        | UnixFileMode.OtherRead
                        | UnixFileMode.OtherExecute;
                }
            }
        }

        // app.db should have 600 permissions (owner read/write only — contains secrets)
        if (File.Exists(AppDatabase) && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        {
            File.SetUnixFileMode(AppDatabase, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return Task.CompletedTask;
    }

    private static void MigrateOldPaths()
    {
        string oldRoot = Path.Combine(AppPath, "root");
        if (!Directory.Exists(oldRoot))
            return;

        Logger.Setup("Migrating legacy folder structure...");

        // root/binaries → binaries
        MigrateDirectory(Path.Combine(oldRoot, "binaries"), BinariesPath);

        // root/certs → security/certs
        MigrateDirectory(Path.Combine(oldRoot, "certs"), CertPath);

        // root/secrets → security/secrets
        MigrateDirectory(Path.Combine(oldRoot, "secrets"), SecretsPath);

        // cache/apiData → cache/api
        MigrateDirectory(Path.Combine(CachePath, "apiData"), ApiCachePath);

        // config seed files → config/seeds/
        Directory.CreateDirectory(SeedsPath);
        MigrateFile(Path.Combine(ConfigPath, "folderRootsSeed.jsonc"), FolderRootsSeedFile);
        MigrateFile(Path.Combine(ConfigPath, "librariesSeed.jsonc"), LibrariesSeedFile);
        MigrateFile(Path.Combine(ConfigPath, "encoderProfilesSeed.jsonc"), EncoderProfilesSeedFile);

        // Clean up empty root directory
        try
        {
            if (Directory.Exists(oldRoot) && !Directory.EnumerateFileSystemEntries(oldRoot).Any())
            {
                Directory.Delete(oldRoot);
                Logger.Setup("Removed empty legacy root/ directory");
            }
        }
        catch
        {
            // Best-effort cleanup
        }

        Logger.Setup("Migration complete");
    }

    private static void MigrateDirectory(string oldPath, string newPath)
    {
        try
        {
            if (!Directory.Exists(oldPath))
                return;

            if (Directory.Exists(newPath) && Directory.EnumerateFileSystemEntries(newPath).Any())
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            Directory.Move(oldPath, newPath);
            Logger.Setup($"Migrated {oldPath} → {newPath}");
        }
        catch (Exception ex)
        {
            Logger.Setup($"Failed to migrate {oldPath}: {ex.Message}", LogEventLevel.Warning);
        }
    }

    private static void MigrateFile(string oldPath, string newPath)
    {
        try
        {
            if (!File.Exists(oldPath) || File.Exists(newPath))
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            File.Move(oldPath, newPath);
            Logger.Setup($"Migrated {oldPath} → {newPath}");
        }
        catch (Exception ex)
        {
            Logger.Setup($"Failed to migrate {oldPath}: {ex.Message}", LogEventLevel.Warning);
        }
    }
}
