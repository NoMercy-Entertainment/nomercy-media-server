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
