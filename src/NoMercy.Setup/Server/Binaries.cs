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

using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.FileSystem;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.NewtonSoftConverters;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Setup.Dto;
using NoMercy.Storage;
using Serilog.Events;
using Downloader = NoMercy.NmSystem.SystemCalls.Download;
using FileAttributes = NoMercy.NmSystem.FileSystem.FileAttributes;
using HttpClient = System.Net.Http.HttpClient;

namespace NoMercy.Setup.Server;

public enum ServerUpdateResult
{
    Downloaded,
    AlreadyUpToDate,
    UseInstaller,

    /// <summary>
    /// Running inside a container, where the image is the unit of update. Swapping the binary
    /// here cannot work: the downloaded file lands in the data volume, and stopping the server
    /// ends PID 1, so the container restarts from the original image and the download is never
    /// executed. Reporting it is the only honest outcome.
    /// </summary>
    UseContainerImage,
    RestartNeeded,
    NoAssetFound,
}

public class Binaries
{
    private readonly IStorageDriver _driver;
    private readonly IStorage _storage;
    private readonly HttpClient _httpClient;

    private const string GithubMediaServerApiUrl =
        "https://api.github.com/repos/NoMercy-Entertainment/nomercy-media-server/releases/latest";
    private const string GithubFfmpegApiUrl =
        "https://api.github.com/repos/NoMercy-Entertainment/nomercy-ffmpeg/releases/latest";
    private const string GithubTesseractApiUrl =
        "https://api.github.com/repos/NoMercy-Entertainment/nomercy-tesseract/releases/latest";
    private const string GithubWhisperModelApiUrl =
        "https://api.github.com/repos/NoMercy-Entertainment/nomercy-whisper-models/releases/latest";
    private const string GithubStemsplitModelApiUrl =
        "https://api.github.com/repos/NoMercy-Entertainment/nomercy-stemsplit-models/releases/latest";

    private const string GithubYtdlpApiUrl =
        "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";
    private const string GithubCloudflaredApiUrl =
        "https://api.github.com/repos/cloudflare/cloudflared/releases/latest";
    private const string GithubShakaPackagerApiUrl =
        "https://api.github.com/repos/shaka-project/shaka-packager/releases/latest";

    // Third-party releases (yt-dlp, cloudflared, shaka-packager) are held to a
    // minimum age before adoption: a compromised or yanked upstream release gets a
    // soak window to surface before a self-hosted server pulls it. NoMercy-owned
    // releases are exempt — they are signature-verified and must ship fast.
    private static readonly TimeSpan ThirdPartyMinReleaseAge = TimeSpan.FromDays(14);

    public Binaries(IStorageDriver driver, IStorage storage)
    {
        _driver = driver;
        _storage = storage;
        _httpClient = new()
        {
            // Default HttpClient.Timeout is 100s which is fine for API calls
            // but binary downloads can be hundreds of MB. Cap at 10 minutes
            // so a stuck CDN connection eventually surfaces as a TaskCanceled
            // instead of pinning the setup phase forever.
            Timeout = TimeSpan.FromMinutes(10),
        };
        _httpClient.DefaultRequestHeaders.Add(
            "User-Agent",
            ExternalServicesConfig.Current.UserAgent
        );
    }

    /// <summary>
    /// Testing constructor — allows injection of a custom <see cref="HttpClient"/> so that
    /// unit tests can supply a fake <see cref="HttpMessageHandler"/> without hitting the network.
    /// </summary>
    internal Binaries(IStorageDriver driver, IStorage storage, HttpClient httpClient)
    {
        _driver = driver;
        _storage = storage;
        _httpClient = httpClient;
    }

    // -------------------------------------------------------------------------
    // Per-instance manifest cache: keyed by API URL so that the same release
    // info is not re-fetched when multiple Download* methods run for the same
    // endpoint in a single session.
    // -------------------------------------------------------------------------
    private readonly Dictionary<
        string,
        (ReleaseManifest? Manifest, bool SignatureVerified, bool SignaturePresent)
    > _manifestCache = new();

    /// <summary>
    /// Fetches (and caches) the <c>manifest.json</c> for <paramref name="releaseInfo"/>.
    /// If <c>manifest.json.sig</c> is also present among the release assets, verifies the
    /// detached signature and records the result in <see cref="_manifestCache"/>.
    /// </summary>
    /// <remarks>
    /// <c>SignaturePresent</c> is tracked separately from <c>SignatureVerified</c> so a
    /// caller can tell "no <c>.sig</c> asset was published for this release" (the signing
    /// rollout has not reached this repo's CI yet — expected, low-noise) apart from "a
    /// <c>.sig</c> asset exists but does not verify against the embedded key" (a real
    /// integrity concern worth an error-level log).
    /// <para>
    /// Internal (not private) so <see cref="TesseractModelDownloader"/> can reuse the exact
    /// same manifest-fetch + signature-verification path — and its per-instance cache — for
    /// the on-demand OCR model pull instead of duplicating the fetch/verify logic.
    /// </para>
    /// </remarks>
    internal async Task<(
        ReleaseManifest? Manifest,
        bool SignatureVerified,
        bool SignaturePresent
    )> GetOrFetchManifestAsync(string apiUrl, GithubReleaseResponse releaseInfo)
    {
        if (
            _manifestCache.TryGetValue(
                apiUrl,
                out (
                    ReleaseManifest? Manifest,
                    bool SignatureVerified,
                    bool SignaturePresent
                ) cached
            )
        )
            return cached;

        Uri? manifestUrl = releaseInfo
            .Assets.FirstOrDefault(a =>
                a.Name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
            )
            ?.BrowserDownloadUrl;

        if (manifestUrl is null)
        {
            _manifestCache[apiUrl] = (null, false, false);
            return (null, false, false);
        }

        try
        {
            string manifestJson = await _httpClient.GetStringAsync(manifestUrl);
            ReleaseManifest? manifest = JsonConvert.DeserializeObject<ReleaseManifest>(
                manifestJson
            );

            bool signatureVerified = false;
            Uri? sigUrl = releaseInfo
                .Assets.FirstOrDefault(a =>
                    a.Name.Equals("manifest.json.sig", StringComparison.OrdinalIgnoreCase)
                )
                ?.BrowserDownloadUrl;
            bool signaturePresent = sigUrl is not null;

            if (sigUrl is not null)
            {
                string armoredSig = await _httpClient.GetStringAsync(sigUrl);
                signatureVerified = BinaryVerification.VerifyManifestSignature(
                    manifestJson,
                    armoredSig
                );

                if (signatureVerified)
                    Logger.Setup("Release manifest signature verified", LogEventLevel.Verbose);
                else
                    Logger.Setup(
                        "Release manifest signature could not be verified",
                        LogEventLevel.Warning
                    );
            }

            (ReleaseManifest? Manifest, bool SignatureVerified, bool SignaturePresent) result = (
                manifest,
                signatureVerified,
                signaturePresent
            );
            _manifestCache[apiUrl] = result;
            return result;
        }
        catch (Exception ex)
        {
            Logger.Setup($"Failed to fetch release manifest: {ex.Message}", LogEventLevel.Warning);
            _manifestCache[apiUrl] = (null, false, false);
            return (null, false, false);
        }
    }

    /// <summary>
    /// Downloads <paramref name="downloadUrl"/> to a temporary file, verifies its SHA-256
    /// against the release manifest or per-asset sidecar, then atomically replaces
    /// <paramref name="destPath"/> (keeping a <c>.bak</c> backup until the smoke-check
    /// succeeds).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Verification is <em>opportunistic</em>: if no manifest and no <c>.sha256</c> sidecar
    /// are present in the release assets, the file is accepted as-is (preserving backward
    /// compatibility with third-party releases).
    /// </para>
    /// <para>
    /// If a manifest is present but its signature verification failed, a warning is emitted
    /// and the SHA-256 from the manifest is still used for integrity checking.
    /// </para>
    /// </remarks>
    internal async Task<string> DownloadWithVerificationAsync(
        string apiUrl,
        string label,
        Uri downloadUrl,
        string destPath,
        GithubReleaseResponse releaseInfo,
        string assetName,
        bool enforceSignedManifest,
        string? expectedSha256Override = null
    )
    {
        // Resolve the expected SHA-256 in trust order:
        //   1. explicit override (e.g. yt-dlp's own SHA2-256SUMS)
        //   2. signature-verified release manifest (NoMercy-owned repos)
        //   3. per-asset .sha256 sidecar
        //   4. GitHub's server-computed asset digest (universal integrity floor)
        string? expectedSha256 = expectedSha256Override;
        string sha256Source = expectedSha256 is not null ? "upstream checksum" : "none";

        (ReleaseManifest? manifest, bool sigVerified, bool sigPresent) =
            await GetOrFetchManifestAsync(apiUrl, releaseInfo);

        // Owned assets prefer the signature-verified manifest for authenticity. But a
        // signature that does NOT verify — a stale key after a rotation, a CI signing
        // glitch, or a stripped/forged manifest — must never brick the update path: we
        // log loudly and fall through to GitHub's independent asset digest, which still
        // guarantees integrity. Only an actual hash MISMATCH (below) is fatal. A hard
        // throw here would also be illusory security: a repo-write attacker simply omits
        // the manifest and hits the same digest fallback, so failing closed only strands
        // honest servers on the next key rotation.
        bool manifestTrusted = sigVerified || !enforceSignedManifest;

        if (enforceSignedManifest && manifest is not null && sigPresent && !sigVerified)
        {
            // A .sig asset WAS published for this release but did not verify against the
            // embedded key — a genuine signing concern (rotated key, forged manifest, CI
            // glitch) worth surfacing loudly.
            Logger.Setup(
                $"Release manifest signature for {label} did not verify against the embedded NoMercy key — "
                    + "falling back to GitHub asset-digest integrity. Investigate the signing key if this persists.",
                LogEventLevel.Error
            );
        }
        else if (enforceSignedManifest && manifest is not null && !sigPresent)
        {
            // The manifest exists but no .sig asset was published at all — this repo's CI
            // signing rollout has not landed yet (ffmpeg/tesseract/whisper as of writing).
            // Expected during rollout, not a verification failure, so it must not be logged
            // as one: low-noise so a fresh install doesn't read this as a security incident.
            Logger.Setup(
                $"No signature published yet for {label}'s release manifest — accepting on GitHub asset-digest integrity (signing rollout in progress for this repo).",
                LogEventLevel.Debug
            );
        }
        else if (enforceSignedManifest && manifest is null)
        {
            // Visible until every owned repo publishes signed manifests. A missing
            // manifest on an owned release is either the signing rollout still in
            // progress (ffmpeg/tesseract/whisper) or a downgrade attempt — either way
            // it must not be silent.
            Logger.Setup(
                $"No signed manifest published for owned release {label} — accepting on GitHub digest integrity only.",
                LogEventLevel.Warning
            );
        }

        if (expectedSha256 is null && manifest is not null && manifestTrusted)
        {
            expectedSha256 = manifest
                .Assets.FirstOrDefault(a =>
                    a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase)
                )
                ?.Sha256;
            if (expectedSha256 is not null)
                sha256Source = sigVerified ? "signed manifest" : "manifest";
        }

        if (expectedSha256 is null)
        {
            // Fall back to per-asset sidecar (assetName + ".sha256")
            Uri? sidecarUrl = releaseInfo
                .Assets.FirstOrDefault(a =>
                    a.Name.Equals(assetName + ".sha256", StringComparison.OrdinalIgnoreCase)
                )
                ?.BrowserDownloadUrl;

            if (sidecarUrl is not null)
            {
                try
                {
                    expectedSha256 = (await _httpClient.GetStringAsync(sidecarUrl)).Trim();
                    sha256Source = "sidecar";
                }
                catch (Exception ex)
                {
                    Logger.Setup(
                        $"Could not fetch SHA-256 sidecar for {assetName}: {ex.Message}",
                        LogEventLevel.Warning
                    );
                }
            }
        }

        if (expectedSha256 is null)
        {
            // GitHub returns "sha256:<hex>" for every release asset — a zero-cost
            // integrity check against what GitHub stored, even for third-party binaries.
            string? digest = releaseInfo
                .Assets.FirstOrDefault(a =>
                    a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase)
                )
                ?.Digest;
            expectedSha256 = BinaryVerification.ExtractSha256FromDigest(digest);
            if (expectedSha256 is not null)
                sha256Source = "github digest";
        }

        // --- Download to a temp file -----------------------------------------------------------
        string tempPath = destPath + ".tmp";

        // Clean up any leftover temp from a previous aborted download.
        if (_driver.FileExists(tempPath))
            _driver.DeleteFile(tempPath);

        string? directory = Path.GetDirectoryName(destPath);
        if (directory is not null && !_driver.DirectoryExists(directory))
            _storage.CreateDirectory(directory);

        Logger.Setup($"Downloading {label}", LogEventLevel.Verbose);

        using (
            HttpResponseMessage response = await _httpClient.GetAsync(
                downloadUrl,
                HttpCompletionOption.ResponseHeadersRead
            )
        )
        {
            response.EnsureSuccessStatusCode();

            await using Stream contentStream = await response.Content.ReadAsStreamAsync();
            await using Stream fileStream = _driver.OpenWrite(tempPath, overwrite: true);
            await contentStream.CopyToAsync(fileStream);
            await fileStream.FlushAsync();
        }

        if (!_driver.FileExists(tempPath) || _storage.SizeOrZero(tempPath) == 0)
        {
            if (_driver.FileExists(tempPath))
                _driver.DeleteFile(tempPath);
            throw new IOException(
                $"Download of {label} produced an empty or missing file at {tempPath}"
            );
        }

        // --- SHA-256 verification --------------------------------------------------------------
        if (expectedSha256 is not null)
        {
            bool hashOk = await BinaryVerification.VerifyFileSha256Async(tempPath, expectedSha256);
            if (!hashOk)
            {
                _driver.DeleteFile(tempPath);
                throw new InvalidDataException(
                    $"SHA-256 mismatch for {label}: the downloaded file does not match the expected checksum"
                );
            }

            Logger.Setup($"SHA-256 verified for {label} ({sha256Source})", LogEventLevel.Verbose);
        }
        else
        {
            // No manifest, sidecar, or digest resolved. For NoMercy-owned assets this
            // is unexpected (every current release carries at least a digest) and worth
            // surfacing; for third-party it is tolerated by policy.
            Logger.Setup(
                $"No SHA-256 available for {label} — skipping integrity check",
                enforceSignedManifest ? LogEventLevel.Warning : LogEventLevel.Debug
            );
        }

        // --- Atomic replace with backup --------------------------------------------------------
        string backupPath = destPath + ".bak";

        if (_driver.FileExists(backupPath))
            _driver.DeleteFile(backupPath);

        if (_driver.FileExists(destPath))
            _driver.MoveFile(destPath, backupPath);

        _driver.MoveFile(tempPath, destPath);

        // Smoke-check: destination must exist and be non-zero after the move.
        if (!_driver.FileExists(destPath) || _storage.SizeOrZero(destPath) == 0)
        {
            // Restore backup if the smoke-check fails.
            if (_driver.FileExists(backupPath))
            {
                if (_driver.FileExists(destPath))
                    _driver.DeleteFile(destPath);
                _driver.MoveFile(backupPath, destPath);
            }

            throw new IOException(
                $"Post-download smoke-check failed for {label}: file missing or empty at {destPath}"
            );
        }

        // Backup no longer needed.
        if (_driver.FileExists(backupPath))
            _driver.DeleteFile(backupPath);

        Logger.Setup(
            $"Downloaded {label} to {destPath} ({_storage.SizeOrZero(destPath)} bytes)",
            LogEventLevel.Verbose
        );

        return destPath;
    }

    /// <summary>
    /// Returns true when the binary exists in an installer directory (not the binaries path).
    /// This prevents redundant downloads when binaries were installed by an installer.
    /// </summary>
    // Internal (not private): NoMercy.Tests.Setup exercises the platform/asset-resolution
    // branches of each Download* method directly (installer-directory short-circuit,
    // per-platform asset name selection, "no asset found" fallbacks) without needing to
    // drive the entire DownloadAll() sequence — the same testability rationale already
    // documented on DownloadFfmpeg below.
    internal bool ExistsInInstalledDirectory(string executableName)
    {
        // Check the Launcher's install directory (set for installer deployments only)
        string? installDir = Environment.GetEnvironmentVariable("NOMERCY_INSTALL_DIR");
        if (
            !string.IsNullOrEmpty(installDir)
            && _driver.FileExists(Path.Combine(installDir, executableName))
        )
            return true;

        // Also check the server's own directory, but only if it differs from the binaries path
        // (otherwise this is a standalone deployment and we DO want to download updates)
        string? ownDir = Path.GetDirectoryName(
            Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location
        );

        if (
            ownDir is not null
            && !string.Equals(
                Path.GetFullPath(ownDir),
                Path.GetFullPath(AppFiles.BinariesPath),
                StringComparison.OrdinalIgnoreCase
            )
            && _driver.FileExists(Path.Combine(ownDir, executableName))
        )
            return true;

        return false;
    }

    private readonly List<string> _binaryReport = new();

    public Task DownloadAll()
    {
        return Task.Run(async () =>
        {
            Logger.Setup("Downloading Binaries");

            // Scoped to exactly this DownloadAll() run: App/Launcher/CLI/ServerUpdate all
            // resolve the SAME "nomercy-media-server/releases/latest" URL below, which used
            // to mean 4 identical api.github.com requests for one repo, out of ~10 total per
            // boot against GitHub's 60/hr anonymous limit. The flag is reset in the finally
            // block below rather than left on: TesseractModelDownloader is a DI singleton
            // that reuses ITS OWN long-lived Binaries instance for on-demand, days-apart
            // per-language pulls — memoizing across THAT lifetime would mean a new signed
            // tesseract release never gets picked up again after the first call.
            _memoizeReleaseInfoForThisRun = true;
            try
            {
                await DownloadApp();
                await DownloadLauncher();
                await DownloadCli();
                await DownloadServerUpdate();
                await DownloadFfmpeg();
                await DownloadCloudflared();
                await DownloadYtdlp();
                await DownloadShakaPackager();
                await DownloadWhisperModels(AppFiles.WhisperModel);
                await DownloadStemsplitModel(AppFiles.StemsplitModel);

                List<string> tesseractLanguages = ["eng", "jpn"];
                if (!CultureInfo.CurrentCulture.Equals(CultureInfo.InvariantCulture))
                {
                    string currentCulture = CultureInfo.CurrentCulture.EnglishLanguageTag();
                    if (
                        !string.IsNullOrEmpty(currentCulture)
                        && !tesseractLanguages.Contains(currentCulture)
                    )
                        tesseractLanguages.Add(currentCulture);
                }
                await DownloadTesseractData(tesseractLanguages);
            }
            finally
            {
                _memoizeReleaseInfoForThisRun = false;
                _releaseInfoMemo.Clear();
            }

            if (_binaryReport.Count > 0)
            {
                const int columns = 3;
                int rows = (_binaryReport.Count + columns - 1) / columns;
                int[] columnWidth = new int[columns];
                for (int i = 0; i < _binaryReport.Count; i++)
                {
                    int col = i % columns;
                    if (_binaryReport[i].Length > columnWidth[col])
                        columnWidth[col] = _binaryReport[i].Length;
                }

                StringBuilder report = new();
                report.Append($"Binaries up to date ({_binaryReport.Count}):");
                for (int row = 0; row < rows; row++)
                {
                    report.Append('\n').Append("  ");
                    for (int col = 0; col < columns; col++)
                    {
                        int index = (row * columns) + col;
                        if (index >= _binaryReport.Count)
                            break;

                        bool last = col == columns - 1 || index == _binaryReport.Count - 1;
                        report.Append(
                            last
                                ? _binaryReport[index]
                                : _binaryReport[index].PadRight(columnWidth[col] + 2)
                        );
                    }
                }

                Logger.Setup(report.ToString(), LogEventLevel.Verbose);
            }
        });
    }

    internal bool CheckLocalVersion(
        GithubReleaseResponse releaseInfo,
        string destination,
        out string version
    )
    {
        version = releaseInfo.TagName.StartsWith("v")
            ? releaseInfo.TagName[1..]
            : releaseInfo.TagName;

        bool fileExists = _storage.Exists(destination);
        if (!fileExists)
            return false;

        DateTime creationTime = _storage.LastModified(destination).UtcDateTime;
        DateTimeOffset releaseDate =
            releaseInfo.PublishedAt != DateTimeOffset.MinValue
                ? releaseInfo.PublishedAt.UtcDateTime
                : DateTimeOffset.Now;

        return creationTime >= releaseDate;
    }

    private bool InstalledFfmpegMatchesProcessArchitecture()
    {
        if (!_storage.Exists(AppFiles.FfmpegPath))
            return true;

        try
        {
            using Stream stream = _storage.OpenRead(AppFiles.FfmpegPath);
            return ExecutableArchitecture.MatchesProcess(stream);
        }
        catch (Exception ex)
        {
            Logger.Setup(
                $"Could not probe installed ffmpeg architecture: {ex.Unwrap()}",
                LogEventLevel.Verbose
            );
            return true;
        }
    }

    // A rate-limited fetch must never block a boot/recovery tick for the full GitHub
    // hourly reset window (up to 60 minutes) — that starves every other startup
    // dependency and, for the degraded-mode recovery loop, means the process never
    // gets a chance to fall back to cached metadata at all. Once either cap is hit,
    // GetLatestReleaseInfo stops waiting and serves the last-known-good cache instead.
    private const int MaxRateLimitedAttempts = 3;
    private static readonly TimeSpan MaxRateLimitWait = TimeSpan.FromMinutes(2);

    // Per-run memo keyed on the API URL, active only while DownloadAll() is executing
    // (see _memoizeReleaseInfoForThisRun there). DownloadAll's App/Launcher/CLI/
    // ServerUpdate steps all resolve the SAME "nomercy-media-server/releases/latest"
    // URL, so one run made 4 identical api.github.com requests for it alone (part of
    // ~10 total per boot against GitHub's 60/hr anonymous limit). NOT active for a
    // direct caller like TesseractModelDownloader — a DI singleton that reuses its own
    // long-lived Binaries instance for on-demand, days-apart per-language pulls, where
    // memoizing forever would mean a new signed release never gets picked up again.
    // Deliberately not a persisted or TTL cache either: that would make
    // CheckLocalVersion's self-correcting mtime-vs-published_at behavior depend on a
    // stale claim about a prior run instead of this run's own live/fallback result.
    private bool _memoizeReleaseInfoForThisRun;
    private readonly Dictionary<string, GithubReleaseResponse> _releaseInfoMemo = new();

    internal async Task<GithubReleaseResponse> GetLatestReleaseInfo(string apiUrl)
    {
        if (
            _memoizeReleaseInfoForThisRun
            && _releaseInfoMemo.TryGetValue(apiUrl, out GithubReleaseResponse? memoized)
        )
            return memoized;

        GithubReleaseResponse result = await FetchLatestReleaseInfoUncachedAsync(apiUrl);
        if (_memoizeReleaseInfoForThisRun)
            _releaseInfoMemo[apiUrl] = result;
        return result;
    }

    private async Task<GithubReleaseResponse> FetchLatestReleaseInfoUncachedAsync(string apiUrl)
    {
        int attempt = 0;
        int rateLimitedAttempts = 0;
        TimeSpan backoff = TimeSpan.FromSeconds(30);

        while (true)
        {
            attempt++;
            try
            {
                using HttpResponseMessage response = await _httpClient.GetAsync(apiUrl);

                if (
                    response.StatusCode
                    is HttpStatusCode.Forbidden
                        or HttpStatusCode.TooManyRequests
                )
                {
                    rateLimitedAttempts++;
                    TimeSpan waitTime = ResolveRateLimitWaitTime(response, backoff);

                    if (ExceedsRateLimitBudget(waitTime, rateLimitedAttempts))
                        return await ResolveRateLimitedFallbackAsync(apiUrl, waitTime);

                    Logger.Setup(
                        $"GitHub API rate limited (attempt {attempt}), waiting {waitTime.TotalSeconds:F0}s to retry: {apiUrl}",
                        LogEventLevel.Warning
                    );

                    await Task.Delay(waitTime);
                    backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 600));
                    continue;
                }

                response.EnsureSuccessStatusCode();

                string jsonResponse = await response.Content.ReadAsStringAsync();

                return await ParseAndCacheReleaseInfoAsync(apiUrl, jsonResponse);
            }
            catch (Exception e)
            {
                Logger.Setup(
                    $"Error fetching release info from {apiUrl}: {e.Message}",
                    LogEventLevel.Warning
                );
                return await ResolveCachedOrEmptyAsync(apiUrl);
            }
        }
    }

    private static TimeSpan ResolveRateLimitWaitTime(HttpResponseMessage response, TimeSpan backoff)
    {
        if (!response.Headers.TryGetValues("X-RateLimit-Reset", out IEnumerable<string>? values))
            return backoff;

        string? resetValue = values.FirstOrDefault();
        if (resetValue is null || !long.TryParse(resetValue, out long resetUnix))
            return backoff;

        DateTimeOffset resetTime = DateTimeOffset.FromUnixTimeSeconds(resetUnix);
        TimeSpan untilReset = resetTime - DateTimeOffset.UtcNow;
        return untilReset > TimeSpan.Zero ? untilReset + TimeSpan.FromSeconds(2) : backoff;
    }

    private static bool ExceedsRateLimitBudget(TimeSpan waitTime, int rateLimitedAttempts) =>
        waitTime > MaxRateLimitWait || rateLimitedAttempts >= MaxRateLimitedAttempts;

    private async Task<GithubReleaseResponse> ResolveRateLimitedFallbackAsync(
        string apiUrl,
        TimeSpan waitTime
    )
    {
        GithubReleaseResponse? cached = await TryLoadCachedReleaseInfoAsync(apiUrl);
        if (cached is not null)
        {
            Logger.Setup(
                $"GitHub API rate limited for {apiUrl} — serving cached release metadata "
                    + $"instead of waiting {waitTime.TotalSeconds:F0}s for the reset.",
                LogEventLevel.Information
            );
            return cached;
        }

        Logger.Setup(
            $"GitHub API rate limited for {apiUrl} and no cached release metadata is available.",
            LogEventLevel.Warning
        );
        return new();
    }

    private async Task<GithubReleaseResponse> ResolveCachedOrEmptyAsync(string apiUrl)
    {
        GithubReleaseResponse? cached = await TryLoadCachedReleaseInfoAsync(apiUrl);
        if (cached is not null)
        {
            Logger.Setup(
                $"Serving cached release metadata for {apiUrl} because the live fetch failed.",
                LogEventLevel.Information
            );
            return cached;
        }

        return new();
    }

    private async Task<GithubReleaseResponse> ParseAndCacheReleaseInfoAsync(
        string apiUrl,
        string jsonResponse
    )
    {
        GithubReleaseResponse? parsed = jsonResponse.FromJson<GithubReleaseResponse>();
        if (parsed is null)
            return new();

        await WriteReleaseCacheAsync(apiUrl, jsonResponse);
        return parsed;
    }

    // -------------------------------------------------------------------------
    // Last-known-good release metadata cache: written on every successful fetch,
    // read back only when GitHub's API is unavailable (403/429/exception). This
    // is what lets ffmpeg provisioning survive a GitHub API rate-limit without
    // ever downloading an asset from stale/untrusted data — the asset download
    // itself always uses BrowserDownloadUrl from this (possibly cached) release
    // info, and that CDN endpoint is not subject to the API's rate limit.
    // -------------------------------------------------------------------------

    private static string ReleaseCacheDirectory =>
        Path.Combine(AppFiles.CachePath, "releases-cache");

    private static string ReleaseCacheFilePath(string apiUrl) =>
        Path.Combine(ReleaseCacheDirectory, ReleaseCacheFileName(apiUrl));

    private static string ReleaseCacheFileName(string apiUrl)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(apiUrl));
        return Convert.ToHexString(hash).ToLowerInvariant() + ".json";
    }

    private async Task WriteReleaseCacheAsync(string apiUrl, string json)
    {
        try
        {
            string directory = ReleaseCacheDirectory;
            if (!_driver.DirectoryExists(directory))
                _storage.CreateDirectory(directory);

            await _storage.WriteAllTextAsync(
                ReleaseCacheFilePath(apiUrl),
                json,
                CancellationToken.None
            );
        }
        catch (Exception ex)
        {
            Logger.Setup(
                $"Failed to cache release metadata for {apiUrl}: {ex.Message}",
                LogEventLevel.Debug
            );
        }
    }

    private async Task<GithubReleaseResponse?> TryLoadCachedReleaseInfoAsync(string apiUrl)
    {
        string path = ReleaseCacheFilePath(apiUrl);
        if (!_storage.Exists(path))
            return null;

        try
        {
            string json = await _storage.ReadAllTextAsync(path, CancellationToken.None);
            GithubReleaseResponse? cached = json.FromJson<GithubReleaseResponse>();
            return cached is { Assets.Length: > 0 } ? cached : null;
        }
        catch (Exception ex)
        {
            Logger.Setup(
                $"Failed to read cached release metadata for {apiUrl}: {ex.Message}",
                LogEventLevel.Debug
            );
            return null;
        }
    }

    /// <summary>
    /// Returns the newest published (non-draft, non-prerelease) release that is at
    /// least <paramref name="minAge"/> old. Used for third-party dependencies whose
    /// authenticity we cannot vouch for: a minimum age gives a bad upstream release
    /// time to be caught and pulled before a self-hosted server adopts it.
    /// </summary>
    /// <returns>
    /// The qualifying release, or an empty <see cref="GithubReleaseResponse"/> when
    /// none is old enough yet (the caller then keeps whatever is already installed).
    /// </returns>
    internal async Task<GithubReleaseResponse> GetLatestReleaseInfoOlderThan(
        string latestApiUrl,
        TimeSpan minAge
    )
    {
        // Derive the list endpoint from the "…/releases/latest" URL.
        const string latestSuffix = "/releases/latest";
        string listUrl = latestApiUrl.EndsWith(latestSuffix, StringComparison.OrdinalIgnoreCase)
            ? string.Concat(
                latestApiUrl.AsSpan(0, latestApiUrl.Length - "/latest".Length),
                "?per_page=30"
            )
            : latestApiUrl;

        DateTimeOffset cutoff = DateTimeOffset.UtcNow - minAge;

        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(listUrl);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync();

            GithubReleaseResponse[] releases = json.FromJson<GithubReleaseResponse[]>() ?? [];

            GithubReleaseResponse? qualifying = SelectNewestPublishedBefore(releases, cutoff);

            if (qualifying is null)
            {
                Logger.Setup(
                    $"No release older than {minAge.TotalDays:F0} days found at {listUrl} — keeping current binary.",
                    LogEventLevel.Debug
                );
                return new();
            }

            return qualifying;
        }
        catch (Exception e)
        {
            Logger.Setup(
                $"Error fetching release list from {listUrl}: {e.Message}",
                LogEventLevel.Warning
            );
            return new();
        }
    }

    /// <summary>
    /// Selects the newest published (non-draft, non-prerelease) release whose
    /// <c>published_at</c> is at or before <paramref name="cutoff"/>. Pure so the
    /// age-gate policy can be unit-tested without the network.
    /// </summary>
    internal static GithubReleaseResponse? SelectNewestPublishedBefore(
        IEnumerable<GithubReleaseResponse> releases,
        DateTimeOffset cutoff
    )
    {
        return releases
            .Where(r => r is { Draft: false, Prerelease: false })
            .Where(r => r.PublishedAt != DateTimeOffset.MinValue && r.PublishedAt <= cutoff)
            .MaxBy(r => r.PublishedAt);
    }

    /// <summary>
    /// Fails closed when <paramref name="asset"/> carries a GitHub SHA-256 digest and
    /// the file at <paramref name="path"/> does not match it. A missing digest (older
    /// releases) is tolerated. Used for third-party binaries downloaded outside
    /// <see cref="DownloadWithVerificationAsync"/> (which handle their own extraction).
    /// </summary>
    internal async Task VerifyAssetDigestOrThrow(string path, Asset? asset, string label)
    {
        string? expected = BinaryVerification.ExtractSha256FromDigest(asset?.Digest);
        if (expected is null)
        {
            Logger.Setup(
                $"No GitHub digest for {label} — skipping integrity check",
                LogEventLevel.Debug
            );
            return;
        }

        if (!await BinaryVerification.VerifyFileSha256Async(path, expected))
        {
            _storage.Delete(path);
            throw new InvalidDataException(
                $"SHA-256 mismatch for {label}: the downloaded file does not match GitHub's asset digest"
            );
        }

        Logger.Setup($"SHA-256 verified for {label} (github digest)", LogEventLevel.Verbose);
    }

    internal async Task DownloadApp()
    {
        if (ExistsInInstalledDirectory("NoMercyApp" + Info.ExecSuffix))
        {
            Logger.Setup(
                "App found in installed directory, skipping download",
                LogEventLevel.Verbose
            );
            return;
        }

        GithubReleaseResponse releaseInfo = await GetLatestReleaseInfo(GithubMediaServerApiUrl);
        if (releaseInfo.Assets.Length == 0)
        {
            Logger.Setup("No assets found for App release.", LogEventLevel.Warning);
            return;
        }

        if (CheckLocalVersion(releaseInfo, AppFiles.AppExePath, out string version))
        {
            _binaryReport.Add($"App = {version}");
            return;
        }

        await Downloader.DeleteSourceDownload(_storage, AppFiles.AppExePath);

        Uri? downloadUrl = null;
        string? assetName = null;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            assetName = "NoMercyApp-windows-x64.exe";
            downloadUrl = releaseInfo
                .Assets.FirstOrDefault(a =>
                    a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase)
                )
                ?.BrowserDownloadUrl;
        }
        else if (
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        )
        {
            assetName = "NoMercyApp-linux-arm64";
            downloadUrl = releaseInfo
                .Assets.FirstOrDefault(a =>
                    a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase)
                )
                ?.BrowserDownloadUrl;
        }
        else if (
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            && RuntimeInformation.ProcessArchitecture == Architecture.X64
        )
        {
            assetName = "NoMercyApp-linux-x64";
            downloadUrl = releaseInfo
                .Assets.FirstOrDefault(a =>
                    a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase)
                )
                ?.BrowserDownloadUrl;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            assetName = "NoMercyApp-macos-x64.dmg";
            downloadUrl = releaseInfo
                .Assets.FirstOrDefault(a =>
                    a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase)
                )
                ?.BrowserDownloadUrl;
        }

        if (downloadUrl == null || assetName is null)
        {
            Logger.Setup(
                "No suitable NoMercyApp asset found for the current platform.",
                LogEventLevel.Warning
            );
            return;
        }

        string path = await DownloadWithVerificationAsync(
            GithubMediaServerApiUrl,
            "NoMercyApp",
            downloadUrl,
            AppFiles.AppExePath,
            releaseInfo,
            assetName,
            enforceSignedManifest: true
        );

        await FileAttributes.SetCreatedAttribute(path, releaseInfo.PublishedAt);

        await FilePermissions.SetExecutionPermissions(path);
    }

    internal async Task DownloadLauncher()
    {
        if (ExistsInInstalledDirectory("NoMercyLauncher" + Info.ExecSuffix))
        {
            Logger.Setup(
                "Launcher found in installed directory, skipping download",
                LogEventLevel.Verbose
            );
            return;
        }

        GithubReleaseResponse releaseInfo = await GetLatestReleaseInfo(GithubMediaServerApiUrl);
        if (releaseInfo.Assets.Length == 0)
        {
            Logger.Setup("No assets found for Launcher release.", LogEventLevel.Warning);
            return;
        }

        if (CheckLocalVersion(releaseInfo, AppFiles.LauncherExePath, out string version))
        {
            _binaryReport.Add($"Launcher = {version}");
            return;
        }

        await Downloader.DeleteSourceDownload(_storage, AppFiles.LauncherExePath);

        Uri? downloadUrl = null;
        string? assetName = null;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            assetName = "NoMercyLauncher-windows-x64.exe";
            downloadUrl = releaseInfo
                .Assets.FirstOrDefault(a =>
                    a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase)
                )
                ?.BrowserDownloadUrl;
        }
        else if (
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        )
        {
            assetName = "NoMercyLauncher-linux-arm64";
            downloadUrl = releaseInfo
                .Assets.FirstOrDefault(a =>
                    a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase)
                )
                ?.BrowserDownloadUrl;
        }
        else if (
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            && RuntimeInformation.ProcessArchitecture == Architecture.X64
        )
        {
            assetName = "NoMercyLauncher-linux-x64";
            downloadUrl = releaseInfo
                .Assets.FirstOrDefault(a =>
                    a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase)
                )
                ?.BrowserDownloadUrl;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            assetName = "NoMercyLauncher-macos-x64";
            downloadUrl = releaseInfo
                .Assets.FirstOrDefault(a =>
                    a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase)
                )
                ?.BrowserDownloadUrl;
        }

        if (downloadUrl == null || assetName is null)
        {
            Logger.Setup(
                "No suitable NoMercyLauncher asset found for the current platform.",
                LogEventLevel.Warning
            );
            return;
        }

        string path = await DownloadWithVerificationAsync(
            GithubMediaServerApiUrl,
            "NoMercyLauncher",
            downloadUrl,
            AppFiles.LauncherExePath,
            releaseInfo,
            assetName,
            enforceSignedManifest: true
        );

        await FileAttributes.SetCreatedAttribute(path, releaseInfo.PublishedAt);

        await FilePermissions.SetExecutionPermissions(path);
    }

    internal async Task DownloadCli()
    {
        if (ExistsInInstalledDirectory("nomercy" + Info.ExecSuffix))
        {
            Logger.Setup(
                "CLI found in installed directory, skipping download",
                LogEventLevel.Verbose
            );
            return;
        }

        GithubReleaseResponse releaseInfo = await GetLatestReleaseInfo(GithubMediaServerApiUrl);
        if (releaseInfo.Assets.Length == 0)
        {
            Logger.Setup("No assets found for CLI release.", LogEventLevel.Warning);
            return;
        }

        if (CheckLocalVersion(releaseInfo, AppFiles.CliExePath, out string version))
        {
            _binaryReport.Add($"CLI = {version}");
            return;
        }

        await Downloader.DeleteSourceDownload(_storage, AppFiles.CliExePath);

        Uri? downloadUrl = null;
        string? assetName = null;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            assetName = "nomercy-windows-x64.exe";
            downloadUrl = releaseInfo
                .Assets.FirstOrDefault(a =>
                    a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase)
                )
                ?.BrowserDownloadUrl;
        }
        else if (
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        )
        {
            assetName = "nomercy-linux-arm64";
            downloadUrl = releaseInfo
                .Assets.FirstOrDefault(a =>
                    a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase)
                )
                ?.BrowserDownloadUrl;
        }
        else if (
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            && RuntimeInformation.ProcessArchitecture == Architecture.X64
        )
        {
            assetName = "nomercy-linux-x64";
            downloadUrl = releaseInfo
                .Assets.FirstOrDefault(a =>
                    a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase)
                )
                ?.BrowserDownloadUrl;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            assetName = "nomercy-macos-x64";
            downloadUrl = releaseInfo
                .Assets.FirstOrDefault(a =>
                    a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase)
                )
                ?.BrowserDownloadUrl;
        }

        if (downloadUrl == null || assetName is null)
        {
            Logger.Setup(
                "No suitable nomercy CLI asset found for the current platform.",
                LogEventLevel.Warning
            );
            return;
        }

        string path = await DownloadWithVerificationAsync(
            GithubMediaServerApiUrl,
            "nomercy",
            downloadUrl,
            AppFiles.CliExePath,
            releaseInfo,
            assetName,
            enforceSignedManifest: true
        );

        await FileAttributes.SetCreatedAttribute(path, releaseInfo.PublishedAt);

        await FilePermissions.SetExecutionPermissions(path);
    }

    public async Task<ServerUpdateResult> DownloadServerUpdate()
    {
        GithubReleaseResponse releaseInfo = await GetLatestReleaseInfo(GithubMediaServerApiUrl);
        if (releaseInfo.Assets.Length == 0)
        {
            Logger.Setup("No assets found for Server release.", LogEventLevel.Warning);
            return ServerUpdateResult.NoAssetFound;
        }

        string latestVersion = releaseInfo.TagName.StartsWith("v")
            ? releaseInfo.TagName[1..]
            : releaseInfo.TagName;

        string currentVersion = Software.GetReleaseVersion();

        if (string.Equals(latestVersion, currentVersion, StringComparison.OrdinalIgnoreCase))
        {
            _binaryReport.Add($"Server = {currentVersion}");
            return ServerUpdateResult.AlreadyUpToDate;
        }

        if (
            Version.TryParse(latestVersion, out Version? latest)
            && Version.TryParse(currentVersion, out Version? current)
            && latest <= current
        )
        {
            _binaryReport.Add($"Server = {currentVersion}");
            return ServerUpdateResult.AlreadyUpToDate;
        }

        // Container deployment: checked before the installer branch because a container never
        // has NOMERCY_INSTALL_DIR set and would otherwise fall through to staging a binary that
        // can never run.
        if (Screen.IsDocker)
        {
            Logger.Setup(
                $"Server update available: {currentVersion} -> {latestVersion} (pull the new container image to apply it)"
            );
            return ServerUpdateResult.UseContainerImage;
        }

        // Installer deployment: the installer handles updates, don't download to binaries path
        string? installDir = Environment.GetEnvironmentVariable("NOMERCY_INSTALL_DIR");
        if (!string.IsNullOrEmpty(installDir))
        {
            Logger.Setup(
                $"Server update available: {currentVersion} -> {latestVersion} (use installer to update)"
            );
            return ServerUpdateResult.UseInstaller;
        }

        string? onDiskVersion = Software.GetFileVersion(_driver, AppFiles.ServerExePath);
        if (
            onDiskVersion is not null
            && string.Equals(latestVersion, onDiskVersion, StringComparison.OrdinalIgnoreCase)
        )
        {
            Logger.Setup(
                $"Server binary on disk is already {onDiskVersion} (running {currentVersion}), restart needed to apply"
            );
            return ServerUpdateResult.RestartNeeded;
        }

        Logger.Setup($"Server update available: {currentVersion} -> {latestVersion}");

        await Downloader.DeleteSourceDownload(_storage, AppFiles.ServerTempExePath);

        Uri? downloadUrl = null;
        string? assetName = null;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            assetName = "NoMercyMediaServer-windows-x64.exe";
            downloadUrl = releaseInfo
                .Assets.FirstOrDefault(a =>
                    a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase)
                )
                ?.BrowserDownloadUrl;
        }
        else if (
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        )
        {
            assetName = "NoMercyMediaServer-linux-arm64";
            downloadUrl = releaseInfo
                .Assets.FirstOrDefault(a =>
                    a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase)
                )
                ?.BrowserDownloadUrl;
        }
        else if (
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            && RuntimeInformation.ProcessArchitecture == Architecture.X64
        )
        {
            assetName = "NoMercyMediaServer-linux-x64";
            downloadUrl = releaseInfo
                .Assets.FirstOrDefault(a =>
                    a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase)
                )
                ?.BrowserDownloadUrl;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            assetName = "NoMercyMediaServer-macos-x64";
            downloadUrl = releaseInfo
                .Assets.FirstOrDefault(a =>
                    a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase)
                )
                ?.BrowserDownloadUrl;
        }

        if (downloadUrl == null || assetName is null)
        {
            Logger.Setup(
                "No suitable NoMercyMediaServer asset found for the current platform.",
                LogEventLevel.Warning
            );
            return ServerUpdateResult.NoAssetFound;
        }

        string path = await DownloadWithVerificationAsync(
            GithubMediaServerApiUrl,
            "NoMercyMediaServer Update",
            downloadUrl,
            AppFiles.ServerTempExePath,
            releaseInfo,
            assetName,
            enforceSignedManifest: true
        );

        // Wait for the file to become available (antivirus scanning can briefly lock/quarantine it)
        bool fileReady = false;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (_storage.Exists(path) && _storage.SizeOrZero(path) > 0)
            {
                fileReady = true;
                break;
            }

            Logger.Setup(
                $"Waiting for staged update file to become available (attempt {attempt + 1}/5)...",
                LogEventLevel.Debug
            );
            await Task.Delay(1000);
        }

        if (!fileReady)
        {
            Logger.Setup(
                $"Staged update file not available at {path} after download",
                LogEventLevel.Error
            );
            return ServerUpdateResult.NoAssetFound;
        }

        await FileAttributes.SetCreatedAttribute(path, releaseInfo.PublishedAt);

        await FilePermissions.SetExecutionPermissions(path);

        Logger.Setup($"Server update staged at {path} ({_storage.SizeOrZero(path)} bytes)");
        return ServerUpdateResult.Downloaded;
    }

    /// <summary>
    /// Internal (not private) so the degraded-mode recovery loop
    /// (<see cref="Boot.DegradedModeRecovery.TryProvisionBinariesAsync"/>) can retry
    /// ffmpeg provisioning alone instead of re-running <see cref="DownloadAll"/> — which
    /// would re-query all ten dependency repos' <c>releases/latest</c> endpoints on every
    /// backoff tick and, on a single rate-limited hiccup, keep re-triggering the same
    /// GitHub 403 for every other binary too.
    /// </summary>
    internal async Task DownloadFfmpeg()
    {
        GithubReleaseResponse releaseInfo = await GetLatestReleaseInfo(GithubFfmpegApiUrl);
        if (releaseInfo.Assets.Length == 0)
        {
            if (!_storage.Exists(AppFiles.FfmpegPath))
                throw new InvalidOperationException(
                    "FFmpeg is not installed and release info could not be fetched. Will retry."
                );

            // A wrong-architecture ffmpeg can never start, so "keep the existing
            // binaries" is only safe when they actually match this machine. Without
            // this check, a rate-limited/offline fetch would silently re-accept a
            // stuck ARM64-on-x64 binary forever, since the replacement logic below
            // never runs. Throwing here routes into DegradedModeRecovery's retry
            // loop instead of leaving the server unable to encode with no signal why.
            if (!InstalledFfmpegMatchesProcessArchitecture())
                throw new InvalidOperationException(
                    $"Installed ffmpeg does not match this machine's CPU architecture ({RuntimeInformation.ProcessArchitecture}) "
                        + "and release info could not be fetched to replace it. Will retry."
                );

            Logger.Setup(
                "No assets found for FFMpeg release, keeping existing binaries.",
                LogEventLevel.Warning
            );
            return;
        }

        if (CheckLocalVersion(releaseInfo, AppFiles.FfmpegPath, out string version))
        {
            if (InstalledFfmpegMatchesProcessArchitecture())
            {
                _binaryReport.Add($"Ffmpeg = {version}");
                return;
            }

            // A release shipped through an arch-blind picker left ARM64 binaries on
            // x64 machines. They pass the version check but can never start, so a
            // mismatch here falls through to a fresh download of the correct build.
            Logger.Setup(
                $"Installed ffmpeg does not match this machine's CPU architecture ({RuntimeInformation.ProcessArchitecture}) — replacing it with the correct build.",
                LogEventLevel.Warning
            );
        }

        // Skip the update when ffmpeg is locked by a running encode — the zip
        // extraction would fail mid-way, leave AppFiles.FfmpegFolder in a partial
        // state, and trip the Phase 4 "required startup task failed" alert. The
        // update will land on the next boot when no encode is in flight.
        if (_storage.Exists(AppFiles.FfmpegPath) && Locking.IsFileLocked(AppFiles.FfmpegPath))
        {
            Logger.Setup(
                "FFmpeg binary is in use by a running encode — deferring update to next boot.",
                LogEventLevel.Information
            );
            return;
        }

        await Downloader.DeleteSourceDownload(_storage, AppFiles.FfmpegPath);
        await Downloader.DeleteSourceDownload(_storage, AppFiles.FfProbePath);
        await Downloader.DeleteSourceDownload(_storage, AppFiles.FfPlayPath);

        Asset? selectedAsset = null;

        if (
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        )
        {
            selectedAsset = releaseInfo.Assets.FirstOrDefault(a =>
                a.Name.Contains("windows") && a.Name.Contains("aarch64")
            );
        }
        else if (
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            && RuntimeInformation.ProcessArchitecture == Architecture.X64
        )
        {
            selectedAsset = releaseInfo.Assets.FirstOrDefault(a =>
                a.Name.Contains("windows") && a.Name.Contains("x86_64")
            );
        }
        else if (
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        )
        {
            selectedAsset = releaseInfo.Assets.FirstOrDefault(a =>
                a.Name.Contains("linux") && a.Name.Contains("aarch64")
            );
        }
        else if (
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            && RuntimeInformation.ProcessArchitecture == Architecture.X64
        )
        {
            selectedAsset = releaseInfo.Assets.FirstOrDefault(a =>
                a.Name.Contains("linux") && a.Name.Contains("x86_64")
            );
        }
        else if (
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        )
        {
            selectedAsset = releaseInfo.Assets.FirstOrDefault(a =>
                a.Name.Contains("darwin") && a.Name.Contains("arm64")
            );
        }
        else if (
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            && RuntimeInformation.ProcessArchitecture == Architecture.X64
        )
        {
            selectedAsset = releaseInfo.Assets.FirstOrDefault(a =>
                a.Name.Contains("darwin") && a.Name.Contains("x86_64")
            );
        }

        if (selectedAsset is null)
        {
            Logger.Setup(
                "No suitable FFMpeg asset found for the current platform.",
                LogEventLevel.Warning
            );
            return;
        }

        string archiveDestPath = Path.Combine(AppFiles.DependenciesPath, selectedAsset.Name);

        string path = await DownloadWithVerificationAsync(
            GithubFfmpegApiUrl,
            "FFMpeg",
            selectedAsset.BrowserDownloadUrl,
            archiveDestPath,
            releaseInfo,
            selectedAsset.Name,
            enforceSignedManifest: true
        );

        // Re-check the lock right before extraction — the encoder worker may have
        // reserved a job and started running ffmpeg between the pre-download check
        // and the multi-minute zip download finishing. Without this gate, extraction
        // races the encode: ExtractToFile deletes ffmpeg.exe, in-flight Process.Start
        // hits ERROR_FILE_NOT_FOUND, encode fails. Keep the downloaded zip so the
        // update lands on the next boot when no encode is in flight.
        if (_storage.Exists(AppFiles.FfmpegPath) && Locking.IsFileLocked(AppFiles.FfmpegPath))
        {
            Logger.Setup(
                "FFmpeg binary became locked by a running encode while the update downloaded — "
                    + "deferring extraction to next boot.",
                LogEventLevel.Information
            );
            return;
        }

        List<string> files = await Archiving.ExtractArchive(_storage, path, AppFiles.FfmpegFolder);
        foreach (string file in files)
        {
            await FileAttributes.SetCreatedAttribute(file, releaseInfo.PublishedAt);
            await FilePermissions.SetExecutionPermissions(file);
        }

        await Downloader.DeleteSourceDownload(_storage, path);
    }

    /// <summary>
    /// Fetches an upstream checksum manifest (e.g. yt-dlp's <c>SHA2-256SUMS</c>) from
    /// the release assets and returns the hex SHA-256 for <paramref name="targetAssetName"/>.
    /// The file format is one <c>"&lt;hash&gt;  &lt;filename&gt;"</c> entry per line.
    /// </summary>
    /// <returns>The hex digest, or <c>null</c> when the sums file or entry is absent.</returns>
    internal async Task<string?> ResolveUpstreamSha256Async(
        GithubReleaseResponse releaseInfo,
        string sumsAssetName,
        string targetAssetName
    )
    {
        Uri? sumsUrl = releaseInfo
            .Assets.FirstOrDefault(a =>
                a.Name.Equals(sumsAssetName, StringComparison.OrdinalIgnoreCase)
            )
            ?.BrowserDownloadUrl;

        if (sumsUrl is null)
            return null;

        try
        {
            string sums = await _httpClient.GetStringAsync(sumsUrl);
            return BinaryVerification.ParseSha256Sums(sums, targetAssetName);
        }
        catch (Exception ex)
        {
            Logger.Setup(
                $"Could not fetch upstream {sumsAssetName} for {targetAssetName}: {ex.Message}",
                LogEventLevel.Warning
            );
        }

        return null;
    }

    internal async Task DownloadYtdlp()
    {
        GithubReleaseResponse releaseInfo = await GetLatestReleaseInfoOlderThan(
            GithubYtdlpApiUrl,
            ThirdPartyMinReleaseAge
        );
        if (releaseInfo.Assets.Length == 0)
        {
            Logger.Setup("No assets found for yt-dlp release.", LogEventLevel.Warning);
            return;
        }

        if (CheckLocalVersion(releaseInfo, AppFiles.YtdlpPath, out string version))
        {
            _binaryReport.Add($"Yt-dlp = {version}");
            return;
        }

        await Downloader.DeleteSourceDownload(_storage, AppFiles.YtdlpPath);

        string? assetName = null;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            assetName = "yt-dlp_x86.exe";
        else if (
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        )
            assetName = "yt-dlp_linux_aarch64";
        else if (
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            && RuntimeInformation.ProcessArchitecture == Architecture.X64
        )
            assetName = "yt-dlp_linux";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            assetName = "yt-dlp_macos";

        Uri? downloadUrl = assetName is null
            ? null
            : releaseInfo
                .Assets.FirstOrDefault(a =>
                    a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase)
                )
                ?.BrowserDownloadUrl;

        if (downloadUrl is null || assetName is null)
        {
            Logger.Setup(
                "No suitable yt-dlp asset found for the current platform.",
                LogEventLevel.Warning
            );
            return;
        }

        // yt-dlp publishes its own SHA2-256SUMS — enforce the download against the
        // hash the yt-dlp maintainers listed for this exact asset. When that file is
        // unavailable, DownloadWithVerificationAsync still falls back to GitHub's
        // asset digest, so the binary is never accepted unverified.
        string? expectedSha256 = await ResolveUpstreamSha256Async(
            releaseInfo,
            "SHA2-256SUMS",
            assetName
        );

        string outputPath = await DownloadWithVerificationAsync(
            GithubYtdlpApiUrl,
            "yt-dlp",
            downloadUrl,
            AppFiles.YtdlpPath,
            releaseInfo,
            assetName,
            enforceSignedManifest: false,
            expectedSha256Override: expectedSha256
        );

        await FileAttributes.SetCreatedAttribute(outputPath, releaseInfo.PublishedAt);

        await FilePermissions.SetExecutionPermissions(outputPath);

        Logger.Setup($"Downloaded yt-dlp to {outputPath}");
    }

    internal async Task DownloadShakaPackager()
    {
        GithubReleaseResponse releaseInfo = await GetLatestReleaseInfoOlderThan(
            GithubShakaPackagerApiUrl,
            ThirdPartyMinReleaseAge
        );
        if (releaseInfo.Assets.Length == 0)
        {
            Logger.Setup("No assets found for shaka-packager release.", LogEventLevel.Warning);
            return;
        }

        if (CheckLocalVersion(releaseInfo, AppFiles.ShakaPackagerPath, out string version))
        {
            _binaryReport.Add($"shaka-packager = {version}");
            return;
        }

        await Downloader.DeleteSourceDownload(_storage, AppFiles.ShakaPackagerPath);

        // shaka-project release asset names per platform.
        string? assetName = null;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            assetName = "packager-win-x64.exe";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            assetName =
                RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                    ? "packager-linux-arm64"
                    : "packager-linux-x64";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            assetName =
                RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                    ? "packager-osx-arm64"
                    : "packager-osx-x64";

        Uri? downloadUrl = assetName is null
            ? null
            : releaseInfo
                .Assets.FirstOrDefault(a =>
                    a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase)
                )
                ?.BrowserDownloadUrl;

        if (downloadUrl is null || assetName is null)
        {
            Logger.Setup(
                "No suitable shaka-packager asset found for the current platform.",
                LogEventLevel.Warning
            );
            return;
        }

        string outputPath = await DownloadWithVerificationAsync(
            GithubShakaPackagerApiUrl,
            "shaka-packager",
            downloadUrl,
            AppFiles.ShakaPackagerPath,
            releaseInfo,
            assetName,
            enforceSignedManifest: false
        );

        await FileAttributes.SetCreatedAttribute(outputPath, releaseInfo.PublishedAt);
        await FilePermissions.SetExecutionPermissions(outputPath);

        Logger.Setup($"Downloaded shaka-packager to {outputPath}");
    }

    internal async Task DownloadCloudflared()
    {
        string destinationPath = AppFiles.CloudflareDPath;

        GithubReleaseResponse releaseInfo = await GetLatestReleaseInfoOlderThan(
            GithubCloudflaredApiUrl,
            ThirdPartyMinReleaseAge
        );
        if (releaseInfo.Assets.Length == 0)
        {
            Logger.Setup("No assets found for cloudflared release.", LogEventLevel.Warning);
            return;
        }

        if (CheckLocalVersion(releaseInfo, destinationPath, out string version))
        {
            _binaryReport.Add($"Cloudflared = {version}");
            return;
        }

        Asset? selectedAsset = null;
        bool needsExtraction = false;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            selectedAsset = releaseInfo.Assets.FirstOrDefault(a =>
                a.Name.Equals("cloudflared-windows-amd64.exe")
            );
        }
        else if (
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        )
        {
            // cloudflared-linux-arm is the 32-bit build. Picking it on arm64 leaves a binary
            // that may not execute at all, and the tunnel is the only remote transport a
            // CGNAT user has.
            selectedAsset = releaseInfo.Assets.FirstOrDefault(a =>
                a.Name.Equals("cloudflared-linux-arm64")
            );
        }
        else if (
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            && RuntimeInformation.ProcessArchitecture == Architecture.X64
        )
        {
            selectedAsset = releaseInfo.Assets.FirstOrDefault(a =>
                a.Name.Equals("cloudflared-linux-amd64")
            );
        }
        else if (
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        )
        {
            selectedAsset = releaseInfo.Assets.FirstOrDefault(a =>
                a.Name.Equals("cloudflared-darwin-arm64.tgz")
            );
            needsExtraction = true;
        }
        else if (
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            && RuntimeInformation.ProcessArchitecture == Architecture.X64
        )
        {
            selectedAsset = releaseInfo.Assets.FirstOrDefault(a =>
                a.Name.Equals("cloudflared-darwin-amd64.tgz")
            );
            needsExtraction = true;
        }

        if (selectedAsset is null)
        {
            Logger.Setup(
                "No suitable cloudflared asset found for the current platform.",
                LogEventLevel.Warning
            );
            return;
        }

        string path = await Downloader.DownloadFile(
            _storage,
            "cloudflared",
            selectedAsset.BrowserDownloadUrl
        );

        // cloudflared publishes no checksums, so authenticity rests on the 14-day
        // release-age gate. We can still reject a corrupt or tampered download by
        // matching GitHub's own asset digest before we install or extract it.
        await VerifyAssetDigestOrThrow(path, selectedAsset, "cloudflared");

        Logger.Setup($"Downloaded cloudflared to {path}");

        if (needsExtraction)
        {
            List<string> files = await Archiving.ExtractArchive(
                _storage,
                path,
                AppFiles.DependenciesPath
            );
            foreach (string file in files)
            {
                await FileAttributes.SetCreatedAttribute(file, releaseInfo.PublishedAt);
                await FilePermissions.SetExecutionPermissions(file);
            }
            await Downloader.DeleteSourceDownload(_storage, path);
        }
        else
        {
            if (_storage.Exists(destinationPath))
                _storage.Delete(destinationPath);

            _storage.Move(path, destinationPath);

            await FileAttributes.SetCreatedAttribute(destinationPath, releaseInfo.PublishedAt);

            await FilePermissions.SetExecutionPermissions(destinationPath);
        }
    }

    internal async Task DownloadWhisperModels(string modelName = "ggml-large-v3")
    {
        string destinationPath = Path.Combine(AppFiles.FfmpegFolder, modelName + ".bin");

        GithubReleaseResponse releaseInfo = await GetLatestReleaseInfo(GithubWhisperModelApiUrl);
        if (releaseInfo.Assets.Length == 0)
        {
            Logger.Setup(
                "No assets found for nomercy-whisper-models release.",
                LogEventLevel.Warning
            );
            return;
        }

        if (CheckLocalVersion(releaseInfo, destinationPath, out string version))
        {
            _binaryReport.Add($"Whisper = {version}");
            return;
        }

        await Downloader.DeleteSourceDownload(_storage, destinationPath);

        List<Asset> modelAssets = releaseInfo
            .Assets.Where(a => a.Name.Contains(modelName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (modelAssets.Count == 0)
        {
            Logger.Setup(
                $"No assets found for model {modelName} in nomercy-whisper-models release.",
                LogEventLevel.Warning
            );
            return;
        }

        List<string> paths = [];
        foreach (Asset asset in modelAssets)
        {
            string partDestPath = Path.Combine(AppFiles.DependenciesPath, asset.Name);
            paths.Add(
                await DownloadWithVerificationAsync(
                    GithubWhisperModelApiUrl,
                    "nomercy-whisper-models",
                    asset.BrowserDownloadUrl,
                    partDestPath,
                    releaseInfo,
                    asset.Name,
                    enforceSignedManifest: true
                )
            );
        }

        if (modelAssets.Count > 1)
        {
            string outputPath = await ConcatenateModelParts(modelName, paths);

            foreach (string path in paths)
            {
                await Downloader.DeleteSourceDownload(_storage, path);
            }

            await FileAttributes.SetCreatedAttribute(outputPath, releaseInfo.PublishedAt);

            Logger.Setup($"Downloaded and concatenated Whisper model parts to {outputPath}");
        }
        else
        {
            // Single-asset releases still land at DependenciesPath/{assetName} (see
            // DownloadWithVerificationAsync's destPath argument above) — without this
            // move+stamp, CheckLocalVersion's next-boot check reads FfmpegFolder/{model}.bin,
            // finds nothing, and re-downloads the ~2GB model on every single boot forever.
            await MoveDownloadedModelIntoPlaceAsync(
                paths[0],
                destinationPath,
                releaseInfo.PublishedAt,
                "Whisper model"
            );
        }
    }

    /// <summary>
    /// Moves a downloaded, hash-verified single-asset model from its staging path
    /// (<see cref="AppFiles.DependenciesPath"/>) into its final resting place beside
    /// ffmpeg, then stamps <see cref="FileAttributes.SetCreatedAttribute"/> so the next
    /// boot's <see cref="CheckLocalVersion"/> sees it as current instead of re-downloading
    /// it forever. Shared by <see cref="DownloadWhisperModels"/>'s single-asset branch and
    /// <see cref="DownloadStemsplitModel"/> — both models ship as one GitHub asset that
    /// lands beside ffmpeg under a different extension.
    /// </summary>
    /// <remarks>
    /// FfmpegFolder normally already exists (DownloadFfmpeg's extraction step creates it,
    /// and runs earlier in DownloadAll's sequence) — mirrors ConcatenateModelParts' own
    /// guard for a caller that invokes one of these Download* methods directly.
    /// </remarks>
    private async Task MoveDownloadedModelIntoPlaceAsync(
        string sourcePath,
        string destinationPath,
        DateTimeOffset publishedAt,
        string label
    )
    {
        if (!_driver.DirectoryExists(AppFiles.FfmpegFolder))
            _storage.CreateDirectory(AppFiles.FfmpegFolder);

        if (_driver.FileExists(destinationPath))
            _driver.DeleteFile(destinationPath);

        _driver.MoveFile(sourcePath, destinationPath);

        await FileAttributes.SetCreatedAttribute(destinationPath, publishedAt);

        Logger.Setup($"Downloaded {label} to {destinationPath}");
    }

    /// <summary>
    /// Downloads and verifies the stemsplit GGUF model (Spleeter 2stems on ggml) that the
    /// ffmpeg <c>stemsplit</c> filter needs at runtime, from the dedicated
    /// nomercy-stemsplit-models release. Mirrors <see cref="DownloadWhisperModels"/>'s
    /// single-asset branch: unlike whisper's largest models, the stemsplit model always
    /// ships as exactly one ~39 MB asset, so there is no multi-part concatenation path here.
    /// </summary>
    internal async Task DownloadStemsplitModel(string modelName = "spleeter-2stems-f16")
    {
        string destinationPath = Path.Combine(AppFiles.FfmpegFolder, modelName + ".gguf");

        GithubReleaseResponse releaseInfo = await GetLatestReleaseInfo(GithubStemsplitModelApiUrl);
        if (releaseInfo.Assets.Length == 0)
        {
            Logger.Setup(
                "No assets found for nomercy-stemsplit-models release.",
                LogEventLevel.Warning
            );
            return;
        }

        if (CheckLocalVersion(releaseInfo, destinationPath, out string version))
        {
            _binaryReport.Add($"Stemsplit = {version}");
            return;
        }

        await Downloader.DeleteSourceDownload(_storage, destinationPath);

        string assetName = modelName + ".gguf";
        Asset? asset = releaseInfo.Assets.FirstOrDefault(a =>
            a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase)
        );

        if (asset is null)
        {
            Logger.Setup(
                $"No asset found for model {modelName} in nomercy-stemsplit-models release.",
                LogEventLevel.Warning
            );
            return;
        }

        string partDestPath = Path.Combine(AppFiles.DependenciesPath, asset.Name);
        string downloadedPath = await DownloadWithVerificationAsync(
            GithubStemsplitModelApiUrl,
            "nomercy-stemsplit-models",
            asset.BrowserDownloadUrl,
            partDestPath,
            releaseInfo,
            asset.Name,
            enforceSignedManifest: true
        );

        await MoveDownloadedModelIntoPlaceAsync(
            downloadedPath,
            destinationPath,
            releaseInfo.PublishedAt,
            "Stemsplit model"
        );
    }

    internal async Task<string> ConcatenateModelParts(
        string modelName,
        IEnumerable<string> partPaths
    )
    {
        string destinationPath = Path.Combine(AppFiles.FfmpegFolder, modelName + ".bin");

        // Mirrors DownloadWithVerificationAsync's own directory-creation guard: normally
        // FfmpegFolder already exists (DownloadFfmpeg's extraction step creates it, and
        // runs before this in DownloadAll's sequence), but nothing enforces that
        // ordering for a caller that only ever invokes DownloadWhisperModels directly.
        if (!_driver.DirectoryExists(AppFiles.FfmpegFolder))
            _storage.CreateDirectory(AppFiles.FfmpegFolder);

        await using Stream destinationStream = _driver.OpenWrite(destinationPath, overwrite: true);

        // Concatenate the exact local files DownloadWithVerificationAsync already
        // hash-verified — never re-derive paths from URLs.
        foreach (string partPath in partPaths)
        {
            await using Stream partStream = _driver.OpenRead(partPath);
            await partStream.CopyToAsync(destinationStream);
        }

        Logger.Setup($"Concatenated model parts into {destinationPath}", LogEventLevel.Verbose);

        return destinationPath;
    }

    internal async Task DownloadTesseractData(IEnumerable<string> languages)
    {
        GithubReleaseResponse releaseInfo = await GetLatestReleaseInfo(GithubTesseractApiUrl);
        if (releaseInfo.Assets.Length == 0)
        {
            Logger.Setup("No assets found for TesseractData release.", LogEventLevel.Warning);
            return;
        }

        foreach (string lang in languages)
        {
            Uri? downloadUrl = releaseInfo
                .Assets.FirstOrDefault(a =>
                    a.Name.Equals($"{lang}.traineddata", StringComparison.OrdinalIgnoreCase)
                )
                ?.BrowserDownloadUrl;

            if (downloadUrl == null)
            {
                Logger.Setup(
                    $"No asset found for language {lang} in TesseractData release.",
                    LogEventLevel.Warning
                );
                continue;
            }

            string destinationPath = Path.Combine(
                AppFiles.TesseractModelsFolder,
                $"{lang}.traineddata"
            );

            if (CheckLocalVersion(releaseInfo, destinationPath, out string version))
            {
                _binaryReport.Add($"Tesseract[{lang}] = {version}");
                continue;
            }

            await Downloader.DeleteSourceDownload(_storage, destinationPath);

            string assetName = $"{lang}.traineddata";
            string path = await DownloadWithVerificationAsync(
                GithubTesseractApiUrl,
                $"Tesseract data for {lang}",
                downloadUrl,
                destinationPath,
                releaseInfo,
                assetName,
                enforceSignedManifest: true
            );

            await FileAttributes.SetCreatedAttribute(path, releaseInfo.PublishedAt);

            Logger.Setup($"Downloaded Tesseract data for {lang} to {destinationPath}");
        }
    }
}
