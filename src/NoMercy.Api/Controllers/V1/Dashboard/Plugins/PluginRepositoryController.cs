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

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.NmSystem.Information;
using NoMercy.Plugins;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Verification;
using NoMercy.Storage;
using SemanticVersion = System.Version;

namespace NoMercy.Api.Controllers.V1.Dashboard.Plugins;

/// <summary>
/// The repositories this server pulls plugins from, and what they offer.
/// <para>
/// <see cref="IPluginRepository"/> has been complete since the platform landed
/// — add, remove, refresh, a catalogue with per-version download URLs and
/// checksums, and it survives one repository being unreachable. It had no
/// controller, so from a dashboard none of it existed and the only way to add a
/// plugin was a file you already had.
/// </para>
/// <para>
/// Separate from <see cref="PluginController"/> on purpose: that one is about
/// plugins this server has, this one is about where plugins come from. They
/// share a route prefix because that is what the surface reads like, not
/// because they answer the same question.
/// </para>
/// </summary>
[ApiController]
[Tags("Dashboard Server Plugins")]
[ApiVersion(1.0)]
[Authorize(Policy = "Owner")]
[Route("api/v{version:apiVersion}/dashboard/plugins/repositories", Order = 10)]
public class PluginRepositoryController(
    IPluginRepository repository,
    IPluginManager pluginManager,
    IStorageDriver storageDriver,
    IHttpClientFactory httpClientFactory
) : BaseController
{
    [HttpGet]
    public IActionResult Index() =>
        Ok(
            new DataResponseDto<IEnumerable<PluginRepositoryInfoDto>>
            {
                Data = repository
                    .GetRepositories()
                    .Select(info => new PluginRepositoryInfoDto(info)),
            }
        );

    /// <summary>Adds a repository and reads its index straight away.</summary>
    [HttpPost]
    public async Task<IActionResult> Add(
        [FromBody] PluginRepositoryRequestDto request,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return UnprocessableEntityResponse("A repository needs a name");

        // Parsed rather than pattern-matched, and http(s) only: this URL is
        // fetched by the server, so a file:// or a scheme it does not expect is
        // the server reading its own disk on someone else's instruction.
        if (
            !Uri.TryCreate(request.Url, UriKind.Absolute, out Uri? url)
            || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
        )
            return UnprocessableEntityResponse("A repository URL must be http or https");

        try
        {
            await repository.AddRepositoryAsync(request.Name, url.ToString(), ct);
        }
        catch (InvalidOperationException ex)
        {
            return UnprocessableEntityResponse(ex.Message);
        }

        return Ok(new StatusResponseDto<string> { Status = "ok", Message = "Repository added" });
    }

    [HttpDelete("{name}")]
    public async Task<IActionResult> Remove(string name, CancellationToken ct)
    {
        try
        {
            await repository.RemoveRepositoryAsync(name, ct);
        }
        catch (InvalidOperationException ex)
        {
            return NotFoundResponse(ex.Message);
        }

        return Ok(new StatusResponseDto<string> { Status = "ok", Message = "Repository removed" });
    }

    /// <summary>
    /// Trusts a repository, or takes that back.
    /// <para>
    /// Trust is provenance and nothing more: a plugin from a trusted index
    /// still waits for the owner's consent before it runs. From Phase 3 it
    /// skips the marketplace review hold, which is a delay before a release is
    /// published, and never a decision about the owner's own machine.
    /// </para>
    /// </summary>
    [HttpPatch("{name}")]
    public async Task<IActionResult> SetTrust(
        string name,
        [FromBody] PluginRepositoryTrustRequestDto request,
        CancellationToken ct
    )
    {
        try
        {
            await repository.SetRepositoryTrustAsync(name, request.Trusted, ct);
        }
        catch (InvalidOperationException ex)
        {
            return NotFoundResponse(ex.Message);
        }

        return Ok(
            new StatusResponseDto<string>
            {
                Status = "ok",
                Message = request.Trusted ? "Repository trusted" : "Repository no longer trusted",
            }
        );
    }

    /// <summary>Re-reads every enabled repository's index.</summary>
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        await repository.RefreshAsync(ct);

        return Ok(
            new StatusResponseDto<string> { Status = "ok", Message = "Repositories refreshed" }
        );
    }

    /// <summary>
    /// Everything the catalogue offers, each entry told against what is
    /// installed. The comparison happens here so the web app, the phone and the
    /// TV cannot disagree about whether an update exists.
    /// </summary>
    [HttpGet("available")]
    public IActionResult Available()
    {
        IReadOnlyList<PluginInfo> installed = pluginManager.GetInstalledPlugins() ?? [];

        return Ok(
            new DataResponseDto<IEnumerable<PluginCatalogueEntryDto>>
            {
                Data = repository.GetAvailablePlugins().Select(entry => Describe(entry, installed)),
            }
        );
    }

    /// <summary>
    /// Installs a named version from the catalogue.
    /// <para>
    /// The client names a plugin and a version, never a URL. The server looks
    /// the version up and fetches what the catalogue listed, so an install can
    /// only ever reach somewhere a repository the owner added has published.
    /// </para>
    /// </summary>
    [HttpPost("{pluginId:ulid}/install")]
    public async Task<IActionResult> Install(
        Ulid pluginId,
        [FromQuery] string? version,
        CancellationToken ct
    )
    {
        PluginRepositoryEntry? entry = repository.FindPlugin(pluginId);
        if (entry is null)
            return NotFoundResponse("No repository offers this plugin");

        PluginVersionEntry? target = string.IsNullOrWhiteSpace(version)
            ? Newest(entry)
            : repository.FindVersion(pluginId, version);

        if (target is null)
            return NotFoundResponse("The catalogue does not carry that version");

        string stagingDirectory = storageDriver.CombinePath(
            AppFiles.TempPath,
            $"plugin-fetch-{Ulid.NewUlid():N}"
        );
        // The catalogue names the artifact, and a plugin that ships more than a
        // single assembly publishes an archive. Staging it under the wrong
        // extension would hand a zip to the assembly loader.
        bool isArchive = target.DownloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        string stagedPath = storageDriver.CombinePath(
            stagingDirectory,
            $"{entry.Name}{(isArchive ? ".zip" : ".dll")}"
        );

        try
        {
            storageDriver.CreateDirectory(stagingDirectory);

            HttpClient client = httpClientFactory.CreateClient();
            await using (Stream source = await client.GetStreamAsync(target.DownloadUrl, ct))
            await using (Stream destination = storageDriver.OpenWrite(stagedPath, overwrite: true))
            {
                await source.CopyToAsync(destination, ct);
            }

            // The checksum the catalogue published, enforced before anything is
            // copied into the plugins folder. A repository that publishes none
            // installs unverified, and the dashboard says so before you pick it.
            if (isArchive)
                await pluginManager.InstallPluginArchiveAsync(stagedPath, target.Checksum, ct);
            else
                await pluginManager.InstallPluginAsync(stagedPath, target.Checksum, ct);
        }
        // Not a failure: the new version is unpacked and verified, and the next
        // start applies it. Updating a plugin that is running is the ordinary
        // case, and the file being locked while it runs is the expected outcome
        // on Windows - it used to reach the owner as a 500 with a stack trace.
        catch (PluginUpdatePendingRestartException ex)
        {
            return ConflictResponse(ex.Message);
        }
        catch (PluginVerificationException ex)
        {
            return UnprocessableEntityResponse(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return UnprocessableEntityResponse($"The download failed: {ex.Message}");
        }
        // Anything else the filesystem refuses - a permission, a full disk, a
        // second install running at the same time. The installed plugin is
        // untouched either way, because nothing is written over it until the
        // staged copy is complete.
        catch (IOException ex)
        {
            return UnprocessableEntityResponse($"The install could not be written: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return UnprocessableEntityResponse(ex.Message);
        }
        finally
        {
            if (storageDriver.DirectoryExists(stagingDirectory))
                storageDriver.DeleteDirectory(stagingDirectory, recursive: true);
        }

        return Ok(
            new StatusResponseDto<string>
            {
                Status = "ok",
                Message = "Plugin installed successfully",
            }
        );
    }

    private static PluginCatalogueEntryDto Describe(
        PluginRepositoryEntry entry,
        IReadOnlyList<PluginInfo> installed
    )
    {
        PluginVersionEntry? newest = Newest(entry);
        string? installedVersion = installed
            .FirstOrDefault(plugin => plugin.Id == entry.Id)
            ?.Version.ToString();

        return new()
        {
            Id = entry.Id,
            Name = entry.Name,
            Description = entry.Description,
            Author = entry.Author,
            ProjectUrl = entry.ProjectUrl,
            Versions = entry.Versions.Select(version => new PluginVersionDto(version)).ToList(),
            LatestVersion = newest?.Version,
            InstalledVersion = installedVersion,
            UpdateAvailable = IsNewer(newest?.Version, installedVersion),
        };
    }

    private static PluginVersionEntry? Newest(PluginRepositoryEntry entry) =>
        entry.Versions.OrderByDescending(Sortable).FirstOrDefault();

    /// <summary>
    /// A version that does not parse sorts below every one that does, rather
    /// than throwing. One malformed entry in someone else's index must not take
    /// the whole catalogue down.
    /// </summary>
    private static SemanticVersion Sortable(PluginVersionEntry entry) =>
        SemanticVersion.TryParse(entry.Version, out SemanticVersion? parsed) ? parsed : new(0, 0);

    private static bool IsNewer(string? candidate, string? installed)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(installed))
            return false;

        return SemanticVersion.TryParse(candidate, out SemanticVersion? offered)
            && SemanticVersion.TryParse(installed, out SemanticVersion? present)
            && offered > present;
    }
}
