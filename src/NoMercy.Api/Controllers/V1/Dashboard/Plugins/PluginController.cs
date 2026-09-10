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
using Newtonsoft.Json;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.Information;
using NoMercy.Plugins;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;
using NoMercy.Plugins.Verification;
using NoMercy.Storage;

namespace NoMercy.Api.Controllers.V1.Dashboard.Plugins;

[ApiController]
[Tags("Dashboard Server Plugins")]
[ApiVersion(1.0)]
[Authorize(Policy = "Owner")]
[Route("api/v{version:apiVersion}/dashboard/plugins", Order = 10)]
public class PluginController(
    IPluginManager pluginManager,
    IPluginConsentService consentService,
    IPluginGrantStore grantStore,
    IPluginRestartAdvisor restartAdvisor,
    IStorageDriver storageDriver
) : BaseController
{
    private const long MaximumUploadBytes = 64L * 1024 * 1024;
    private const string PluginAssemblyExtension = ".dll";
    private const string PluginArchiveExtension = ".zip";

    [HttpGet]
    public IActionResult Index()
    {
        IReadOnlyList<PluginInfo> plugins = pluginManager.GetInstalledPlugins();

        return Ok(
            new DataResponseDto<IEnumerable<PluginInfoDto>>
            {
                Data = plugins.Select(p => new PluginInfoDto(
                    p,
                    restartAdvisor.Evaluate(p, PluginOperation.Enable),
                    AwaitingConsent(p)
                )),
            }
        );
    }

    [HttpGet("{id:ulid}")]
    public IActionResult Show(Ulid id)
    {
        PluginInfo? plugin = pluginManager.GetInstalledPlugins().FirstOrDefault(p => p.Id == id);
        if (plugin is null)
            return NotFoundResponse("Plugin not found");

        return Ok(
            new DataResponseDto<PluginInfoDto>
            {
                Data = new(
                    plugin,
                    restartAdvisor.Evaluate(plugin, PluginOperation.Enable),
                    AwaitingConsent(plugin)
                ),
            }
        );
    }

    /// <summary>
    /// Records the owner's consent to a plugin's declared capabilities, then
    /// enables it.
    /// <para>
    /// An elevated plugin — anything declaring rest, ws, network or an elevated
    /// hook — installs disabled and cannot enable itself. That part was right;
    /// what was missing is this. <c>GrantConsent</c> existed with no caller, so
    /// "installs disabled pending consent" was a dead end rather than a state
    /// with a way out, and every plugin needing outbound access was stuck at
    /// first run.
    /// </para>
    /// </summary>
    [HttpPost("{id:ulid}/consent")]
    public async Task<IActionResult> Consent(Ulid id, [FromBody] PluginConsentRequestDto? request)
    {
        PluginInfo? plugin = pluginManager.GetInstalledPlugins().FirstOrDefault(p => p.Id == id);
        if (plugin is null)
            return NotFoundResponse("Plugin not found");

        consentService.GrantConsent(id);

        // Grants named in the same call, so consenting to a plugin that needs a
        // library or a host is one decision for the owner rather than three
        // prompts they learn to click through.
        foreach (PluginGrantDto grant in request?.Grants ?? [])
        {
            if (string.IsNullOrWhiteSpace(grant.Kind) || string.IsNullOrWhiteSpace(grant.Value))
                continue;

            grantStore.Grant(id, grant.Kind, grant.Value);
        }

        try
        {
            await pluginManager.EnablePluginAsync(id);
        }
        catch (InvalidOperationException ex)
        {
            return NotFoundResponse(ex.Message);
        }

        return Ok(
            new StatusResponseDto<string> { Status = "ok", Message = "Plugin consent granted" }
        );
    }

    /// <summary>Withdraws consent and disables the plugin.</summary>
    [HttpDelete("{id:ulid}/consent")]
    public async Task<IActionResult> RevokeConsent(Ulid id)
    {
        consentService.RevokeConsent(id);

        foreach (string kind in AllGrantKinds)
        foreach (string value in grantStore.Granted(id, kind))
            grantStore.Revoke(id, kind, value);

        try
        {
            await pluginManager.DisablePluginAsync(id);
        }
        catch (InvalidOperationException)
        {
            // Already gone or never loaded. The consent record is what this
            // route is responsible for, and that is now withdrawn.
        }

        return Ok(
            new StatusResponseDto<string> { Status = "ok", Message = "Plugin consent revoked" }
        );
    }

    /// <summary>Everything plugins have asked the owner for and not yet been given.</summary>
    [HttpGet("grants/pending")]
    public IActionResult PendingGrants() =>
        Ok(
            new DataResponseDto<IEnumerable<PluginGrantRequestDto>>
            {
                Data = grantStore
                    .PendingRequests()
                    .Select(request => new PluginGrantRequestDto(request)),
            }
        );

    /// <summary>Answers one pending request. Denying clears it rather than recording a denial.</summary>
    [HttpPost("{id:ulid}/grants")]
    public IActionResult ResolveGrant(Ulid id, [FromBody] PluginGrantDecisionDto decision)
    {
        if (string.IsNullOrWhiteSpace(decision.Kind) || string.IsNullOrWhiteSpace(decision.Value))
            return UnprocessableEntityResponse("A grant needs both a kind and a value");

        if (decision.Granted)
            grantStore.Grant(id, decision.Kind, decision.Value);
        else
            grantStore.ClearRequest(id, decision.Kind, decision.Value);

        return Ok(
            new StatusResponseDto<string>
            {
                Status = "ok",
                Message = decision.Granted ? "Grant given" : "Grant denied",
            }
        );
    }

    /// <summary>
    /// An elevated plugin with no recorded consent is waiting on the owner, not
    /// failing. The dashboard needs to tell those two apart.
    /// </summary>
    private bool AwaitingConsent(PluginInfo plugin) =>
        !consentService.IsBaseline(plugin.Capabilities) && !consentService.HasConsent(plugin.Id);

    private static readonly string[] AllGrantKinds =
    [
        PluginGrantKind.Capability,
        PluginGrantKind.NetworkHost,
        PluginGrantKind.LibraryWrite,
    ];

    [HttpPost("{id:ulid}/enable")]
    public async Task<IActionResult> Enable(Ulid id)
    {
        try
        {
            await pluginManager.EnablePluginAsync(id);

            return Ok(
                new StatusResponseDto<string>
                {
                    Status = "ok",
                    Message = "Plugin enabled successfully",
                }
            );
        }
        catch (InvalidOperationException ex)
        {
            return NotFoundResponse(ex.Message);
        }
    }

    [HttpPost("{id:ulid}/disable")]
    public async Task<IActionResult> Disable(Ulid id)
    {
        try
        {
            await pluginManager.DisablePluginAsync(id);

            return Ok(
                new StatusResponseDto<string>
                {
                    Status = "ok",
                    Message = "Plugin disabled successfully",
                }
            );
        }
        catch (InvalidOperationException ex)
        {
            return NotFoundResponse(ex.Message);
        }
    }

    /// <summary>
    /// Disables then enables a plugin in one request. Neither half needs the
    /// server restarted, so a single Restart action never asks the owner to do
    /// what the platform can just do for them.
    /// </summary>
    [HttpPost("{id:ulid}/restart")]
    public async Task<IActionResult> Restart(Ulid id)
    {
        try
        {
            await pluginManager.RestartPluginAsync(id);

            return Ok(
                new StatusResponseDto<string>
                {
                    Status = "ok",
                    Message = "Plugin restarted successfully",
                }
            );
        }
        catch (InvalidOperationException ex)
        {
            return NotFoundResponse(ex.Message);
        }
    }

    /// <summary>
    /// Installs a plugin from a file the owner uploaded.
    /// <para>
    /// <see cref="IPluginManager.InstallPluginAsync(string, CancellationToken)"/>
    /// has existed since the platform landed and had no caller: the only way to
    /// add a plugin was to put a file in the plugins folder on the server and
    /// restart it. Anyone who can do that does not need a dashboard, so this
    /// takes the file over the wire and stages it where the manager expects.
    /// </para>
    /// <para>
    /// The upload lands in a per-request staging directory, never in the plugins
    /// folder. Copying it into place is the manager's decision and happens only
    /// after verification passes, so a rejected file is never somewhere the
    /// loader will find it on the next start.
    /// </para>
    /// </summary>
    [HttpPost("install")]
    [RequestSizeLimit(MaximumUploadBytes)]
    public async Task<IActionResult> Install(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return UnprocessableEntityResponse("No file was uploaded");

        string fileName = BareFileName(file.FileName);

        if (string.IsNullOrWhiteSpace(fileName))
            return UnprocessableEntityResponse("The uploaded file has no name");

        bool isArchive = fileName.EndsWith(
            PluginArchiveExtension,
            StringComparison.OrdinalIgnoreCase
        );

        if (
            !isArchive
            && !fileName.EndsWith(PluginAssemblyExtension, StringComparison.OrdinalIgnoreCase)
        )
            return UnprocessableEntityResponse("A plugin is installed from its .zip or its .dll");

        string stagingDirectory = Path.Combine(
            AppFiles.TempPath,
            $"plugin-install-{Ulid.NewUlid():N}"
        );
        string stagedPath = Path.Combine(stagingDirectory, fileName);

        try
        {
            storageDriver.CreateDirectory(stagingDirectory);

            await using (Stream destination = storageDriver.OpenWrite(stagedPath, overwrite: true))
            {
                await file.CopyToAsync(destination, ct);
            }

            // An archive carries the manifest and everything the plugin ships
            // with; a bare assembly is one file and no manifest at all. They are
            // different installs, not one install with a flag.
            if (isArchive)
                await pluginManager.InstallPluginArchiveAsync(stagedPath, null, ct);
            else
                await pluginManager.InstallPluginAsync(stagedPath, ct);

            return Ok(
                new StatusResponseDto<string>
                {
                    Status = "ok",
                    Message = "Plugin installed successfully",
                }
            );
        }
        catch (PluginVerificationException ex)
        {
            return UnprocessableEntityResponse(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return UnprocessableEntityResponse(ex.Message);
        }
        finally
        {
            // The manager copied what it accepted; the upload itself is spent
            // either way, and leaving it behind grows the cache on every retry.
            if (storageDriver.DirectoryExists(stagingDirectory))
                storageDriver.DeleteDirectory(stagingDirectory, recursive: true);
        }
    }

    /// <summary>
    /// The last segment of a client-supplied name, on either separator.
    /// <para>
    /// Not <see cref="Path.GetFileName(string)"/>: that asks the platform, and
    /// on Linux a backslash is an ordinary character — so an upload from a
    /// Windows client reaching a Linux server keeps its whole path as one file
    /// name there and loses it on Windows. The two hosts then disagree about
    /// what was uploaded, and a rule about where bytes land cannot depend on
    /// which machine is serving.
    /// </para>
    /// </summary>
    private static string BareFileName(string candidate)
    {
        int lastSeparator = candidate.LastIndexOfAny(['/', '\\']);

        return lastSeparator < 0 ? candidate : candidate[(lastSeparator + 1)..];
    }

    [HttpDelete("{id:ulid}")]
    public async Task<IActionResult> Uninstall(Ulid id)
    {
        try
        {
            await pluginManager.UninstallPluginAsync(id);

            return Ok(
                new StatusResponseDto<string>
                {
                    Status = "ok",
                    Message = "Plugin uninstalled successfully",
                }
            );
        }
        catch (InvalidOperationException ex)
        {
            return NotFoundResponse(ex.Message);
        }
    }

    [HttpGet]
    [Route("credentials")]
    public IActionResult Credentials()
    {
        UserPass? aniDb = CredentialManager.Credential("AniDb");

        if (aniDb == null)
            return NotFoundResponse("No credentials found for AniDb");

        return Ok(
            new AniDbCredentialsResponseDto
            {
                Key = "AniDb",
                Username = aniDb.Username,
                ApiKey = aniDb.ApiKey,
            }
        );
    }

    [HttpPost]
    [Route("credentials")]
    public IActionResult Credentials([FromBody] AniDbCredentialsRequestDto requestDto)
    {
        UserPass? aniDb = CredentialManager.Credential(requestDto.Key);
        CredentialManager.SetCredentials(
            requestDto.Key,
            requestDto.Username,
            requestDto.Password ?? (aniDb?.Password).OrEmpty(),
            requestDto.ApiKey
        );

        return Ok(
            new StatusResponseDto<string>
            {
                Status = "ok",
                Message = "Credentials set successfully for {0}",
                Args = [requestDto.Key],
            }
        );
    }
}

public record PluginInfoDto
{
    [JsonProperty("id")]
    public Ulid Id { get; init; }

    [JsonProperty("name")]
    public string Name { get; init; } = null!;

    [JsonProperty("description")]
    public string Description { get; init; } = null!;

    [JsonProperty("version")]
    public string Version { get; init; } = null!;

    [JsonProperty("status")]
    public string Status { get; init; } = null!;

    [JsonProperty("author")]
    public string? Author { get; init; }

    [JsonProperty("project_url")]
    public string? ProjectUrl { get; init; }

    /// <summary>
    /// What the plugin declared it needs. The owner is being asked to consent
    /// to this, so it has to be visible before they do.
    /// </summary>
    [JsonProperty("capabilities")]
    public PluginCapabilities? Capabilities { get; init; }

    /// <summary>Whether an elevated plugin is waiting on the owner rather than broken.</summary>
    [JsonProperty("awaiting_consent")]
    public bool AwaitingConsent { get; init; }

    /// <summary>
    /// Whether enabling this needs the server restarted, and why. Empty means
    /// it takes effect immediately, which is the usual answer and the one worth
    /// stating — an owner told nothing either way restarts after everything.
    /// </summary>
    [JsonProperty("restart_required")]
    public bool RestartRequired { get; init; }

    [JsonProperty("restart_reasons")]
    public IReadOnlyList<string> RestartReasons { get; init; } = [];

    public PluginInfoDto() { }

    public PluginInfoDto(
        PluginInfo info,
        PluginRestartRequirement? restart = null,
        bool awaitingConsent = false
    )
    {
        Capabilities = info.Capabilities;
        AwaitingConsent = awaitingConsent;
        RestartRequired = restart?.Required ?? false;
        RestartReasons = restart?.Explain() ?? [];
        Id = info.Id;
        Name = info.Name;
        Description = info.Description;
        Version = info.Version.ToString();
        Status = info.Status.ToString().ToLowerInvariant();
        Author = info.Author;
        ProjectUrl = info.ProjectUrl;
    }
}
