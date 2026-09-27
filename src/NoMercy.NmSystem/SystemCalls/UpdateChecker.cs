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

using Newtonsoft.Json;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.Status;
using NoMercy.Storage.Drivers.Local;
using Serilog.Events;

namespace NoMercy.NmSystem.SystemCalls;

public interface IUpdateChecker
{
    Task<bool> IsUpdateAvailableAsync();
}

public class UpdateChecker : IUpdateChecker
{
    private const string GithubReleasesUrl =
        "https://api.github.com/repos/NoMercy-Entertainment/nomercy-media-server/releases/latest";

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private readonly IUpdateStatus _updateStatus;
    private readonly RuntimeServerSettings _settings;
    private readonly HttpClient _httpClient;

    public UpdateChecker(IUpdateStatus updateStatus, RuntimeServerSettings settings)
        : this(updateStatus, settings, SharedHttpClient) { }

    /// <summary>
    /// Testing constructor, so a test can answer GitHub's API with a fake handler.
    /// </summary>
    internal UpdateChecker(
        IUpdateStatus updateStatus,
        RuntimeServerSettings settings,
        HttpClient httpClient
    )
    {
        _updateStatus = updateStatus;
        _settings = settings;
        _httpClient = httpClient;
    }

    public async Task<bool> IsUpdateAvailableAsync()
    {
        try
        {
            LatestReleaseInfo? release = await FetchChannelReleaseAsync();

            if (release is null || string.IsNullOrEmpty(release.TagName))
                return false;

            string latestVersion = release.TagName.StartsWith("v")
                ? release.TagName[1..]
                : release.TagName;

            string currentVersion = Software.GetReleaseVersion();

            _updateStatus.LatestVersion = latestVersion;

            if (string.Equals(latestVersion, currentVersion, StringComparison.OrdinalIgnoreCase))
            {
                _updateStatus.RestartNeeded = false;
                _updateStatus.UpdateAvailable = false;

                return false;
            }

            // LOCAL-ONLY: UpdateChecker lives in NmSystem; no reference to NoMercy.Providers.
            string? onDiskVersion = Software.GetFileVersion(
                new LocalStorageDriver(),
                AppFiles.ServerExePath
            );

            // Also check the installed binary (e.g. Program Files) if available
            if (
                onDiskVersion is null
                || !string.Equals(latestVersion, onDiskVersion, StringComparison.OrdinalIgnoreCase)
            )
            {
                string? installDir = Environment.GetEnvironmentVariable("NOMERCY_INSTALL_DIR");

                if (!string.IsNullOrEmpty(installDir))
                {
                    string installedExe = Path.Combine(
                        installDir,
                        "NoMercyMediaServer" + Info.ExecSuffix
                    );

                    string? installedVersion = Software.GetFileVersion(
                        new LocalStorageDriver(),
                        installedExe
                    );

                    if (
                        installedVersion is not null
                        && string.Equals(
                            latestVersion,
                            installedVersion,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        onDiskVersion = installedVersion;
                    }
                }
            }

            _updateStatus.RestartNeeded =
                onDiskVersion is not null
                && string.Equals(latestVersion, onDiskVersion, StringComparison.OrdinalIgnoreCase);

            bool updateAvailable;

            if (
                Version.TryParse(latestVersion, out Version? latest)
                && Version.TryParse(currentVersion, out Version? current)
            )
            {
                updateAvailable = latest > current;
            }
            else
            {
                updateAvailable = !string.Equals(
                    latestVersion,
                    currentVersion,
                    StringComparison.OrdinalIgnoreCase
                );
            }

            _updateStatus.UpdateAvailable = updateAvailable;

            return updateAvailable;
        }
        catch (Exception e)
        {
            Logger.Setup($"Update check failed: {e.Message}", LogEventLevel.Debug);

            return false;
        }
    }

    /// <summary>
    /// Stable reads GitHub's latest release, exactly as before channels existed.
    /// Beta and nightly read the release list, because GitHub's latest release
    /// never includes a prerelease.
    /// </summary>
    private async Task<LatestReleaseInfo?> FetchChannelReleaseAsync()
    {
        if (_settings.UpdateChannel == ReleaseChannel.Stable)
            return JsonConvert.DeserializeObject<LatestReleaseInfo>(
                await GetJsonAsync(GithubReleasesUrl)
            );

        LatestReleaseInfo[] releases =
            JsonConvert.DeserializeObject<LatestReleaseInfo[]>(
                await GetJsonAsync(ReleaseChannelSelector.MediaServerReleaseListUrl)
            ) ?? [];

        return ReleaseChannelSelector.Select(
            releases,
            r => new(r.TagName, r.Draft, r.Prerelease, r.Body),
            _settings.UpdateChannel
        );
    }

    private async Task<string> GetJsonAsync(string url)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new();
        client.DefaultRequestHeaders.Add("User-Agent", ExternalServicesConfig.Current.UserAgent);
        return client;
    }

    private class LatestReleaseInfo
    {
        [JsonProperty("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonProperty("draft")]
        public bool Draft { get; set; }

        [JsonProperty("prerelease")]
        public bool Prerelease { get; set; }

        [JsonProperty("body")]
        public string Body { get; set; } = string.Empty;
    }
}
