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
using NoMercy.Api.DTOs.Plugins;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.Information;
using NoMercy.Plugins;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;
using NoMercy.Plugins.OutOfProcess;
using NoMercy.Plugins.Sideload;
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
    IStorageDriver storageDriver,
    IPluginDeveloperModeSource developerMode
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
            new DataResponseDto<IEnumerable<PluginInfoDto>> { Data = plugins.Select(Describe) }
        );
    }

    [HttpGet("{id:ulid}")]
    public IActionResult Show(Ulid id)
    {
        PluginInfo? plugin = pluginManager.GetInstalledPlugins().FirstOrDefault(p => p.Id == id);
        if (plugin is null)
            return NotFoundResponse("Plugin not found");

        return Ok(new DataResponseDto<PluginInfoDto> { Data = Describe(plugin) });
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

        consentService.GrantConsent(id, plugin.Capabilities, plugin.Version);

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
        grantStore.RevokeAll(id);

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
    /// An elevated plugin whose recorded consent does not cover what its
    /// manifest now asks for is waiting on the owner, not failing. The
    /// dashboard needs to tell those two apart.
    /// <para>
    /// Not <c>HasConsent</c>: that is true for a record covering a smaller
    /// request, so a plugin that widened showed as plain Disabled with nothing
    /// on screen offering the owner the decision it was actually waiting for.
    /// </para>
    /// </summary>
    /// <summary>
    /// One plugin as the dashboard reads it. Ordered: the consent check is what
    /// upgrades a legacy record, so the consented set is read after it and
    /// reports what the owner approved rather than the empty record it was
    /// held in.
    /// </summary>
    private PluginInfoDto Describe(PluginInfo plugin)
    {
        bool awaiting = AwaitingConsent(plugin);

        return new(
            plugin,
            restartAdvisor.Evaluate(plugin, PluginOperation.Enable),
            awaiting,
            consentService.ConsentedCapabilities(plugin.Id)
        );
    }

    private bool AwaitingConsent(PluginInfo plugin) =>
        !consentService.IsBaseline(plugin.Capabilities)
        && !consentService.ConsentCoversCapabilities(
            plugin.Id,
            plugin.Capabilities,
            plugin.Version
        );

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
    public async Task<IActionResult> Install(
        IFormFile? file,
        CancellationToken ct,
        [FromQuery] Guid? forUser = null
    )
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

        // Before the upload is written anywhere. A file the server will not
        // install has no reason to reach the disk first.
        if (!developerMode.Enabled)
            return UnprocessableEntityResponse(
                new PluginRefusal(
                    PluginRefusalCodes.SideloadDisabled,
                    fileName,
                    "The server did not install the file.",
                    "Installing a plugin from a file is off, because the server cannot check who wrote it.",
                    "Turn on developer mode in server settings, read the warning there, then install the file again.",
                    PluginRefusalSeverity.Blocked
                )
            );

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
            // A guest's plugin is judged against the manifest inside the
            // archive, which only the manager reads. A bare assembly carries
            // no manifest, so there is nothing to judge and no guest install.
            if (forUser is { } guest && guest != Guid.Empty)
            {
                if (!isArchive)
                    return UnprocessableEntityResponse(
                        "A plugin installed for one person is installed from its .zip: a bare .dll carries no manifest to check."
                    );

                await pluginManager.InstallPluginArchiveAsync(stagedPath, null, ct, forUser: guest);
            }
            else if (isArchive)
            {
                await pluginManager.InstallPluginArchiveAsync(stagedPath, null, ct);
            }
            else
            {
                await pluginManager.InstallPluginAsync(stagedPath, ct);
            }

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

    /// <summary>
    /// Removes a plugin, and keeps or purges what the server held for it.
    /// <para>
    /// <c>keepData</c> keeps the plugin's data folder, consent, grants and
    /// secrets. Left out it is true, because a purge cannot be undone and every
    /// client written before the flag existed sends nothing: defaulting the
    /// other way made those clients destroy the owner's plugin data on an
    /// ordinary uninstall, without asking and without a way back. An owner who
    /// wants the data gone says so.
    /// </para>
    /// </summary>
    [HttpDelete("{id:ulid}")]
    public async Task<IActionResult> Uninstall(Ulid id, [FromQuery] bool keepData = true)
    {
        try
        {
            await pluginManager.UninstallPluginAsync(id, keepData);

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

    /// <summary>
    /// Whether this server installs a plugin from a file the owner hands it.
    /// <para>
    /// The refusal a blocked sideload returns says to turn developer mode on in
    /// server settings. Nothing but a text editor could, so the sentence named
    /// a place that did not exist and the owner had no way through.
    /// </para>
    /// </summary>
    [HttpGet("developer-mode")]
    public IActionResult DeveloperMode()
    {
        return Ok(new PluginDeveloperModeDto { Enabled = developerMode.Enabled });
    }

    [HttpPost("developer-mode")]
    public IActionResult DeveloperMode([FromBody] PluginDeveloperModeDto request)
    {
        PluginDeveloperMode saved = new PluginDeveloperModeStore().Write(request.Enabled);

        return Ok(new PluginDeveloperModeDto { Enabled = saved.Enabled });
    }

    /// <summary>
    /// Where plugins run on this server.
    /// <para>
    /// A setting rather than a file somebody edits: an owner deciding to move
    /// their plugins out of the server's process is a decision about their own
    /// machine, and every user-facing choice belongs in the dashboard.
    /// </para>
    /// </summary>
    [HttpGet("runtime-mode")]
    public IActionResult RuntimeMode()
    {
        PluginRuntimeMode mode = PluginRuntimeMode.Load();

        return Ok(
            new PluginRuntimeModeDto
            {
                Isolation = mode.Default.ToString(),
                PerPlugin = mode.PerPlugin ?? new Dictionary<string, PluginIsolation>(),
            }
        );
    }

    [HttpPost("runtime-mode")]
    public IActionResult RuntimeMode([FromBody] PluginRuntimeModeDto request)
    {
        if (!Enum.TryParse(request.Isolation, ignoreCase: true, out PluginIsolation isolation))
            return UnprocessableEntityResponse(
                $"A plugin runs either {nameof(PluginIsolation.InProcess)} or {nameof(PluginIsolation.OutOfProcess)}."
            );

        PluginRuntimeMode mode = new(isolation, request.PerPlugin);
        mode.Save();

        return Ok(
            new PluginRuntimeModeDto
            {
                Isolation = mode.Default.ToString(),
                PerPlugin = mode.PerPlugin ?? new Dictionary<string, PluginIsolation>(),
            }
        );
    }
}
