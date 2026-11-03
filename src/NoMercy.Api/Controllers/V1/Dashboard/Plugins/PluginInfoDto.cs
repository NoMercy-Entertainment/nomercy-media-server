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
using NoMercy.PluginSdk.Abstractions;

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

    /// <summary>
    /// Why the plugin is malfunctioning, when it is. Absent otherwise, so a
    /// client never has to decide whether an empty string means healthy.
    /// </summary>
    [JsonProperty("malfunction", NullValueHandling = NullValueHandling.Ignore)]
    public string? Malfunction { get; init; }

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
    /// Whether the plugin is not loaded because the owner has not approved
    /// it: it was put in the plugins folder by hand, or its files changed
    /// after the last approval. Approve it with <c>POST {id}/approve</c>.
    /// </summary>
    [JsonProperty("awaiting_approval")]
    public bool AwaitingApproval { get; init; }

    /// <summary>Why the plugin waits for approval, when it does. Absent otherwise.</summary>
    [JsonProperty("approval_reason", NullValueHandling = NullValueHandling.Ignore)]
    public string? ApprovalReason { get; init; }

    /// <summary>
    /// What the owner already approved, in the same shape as
    /// <see cref="Capabilities"/>. Null when nothing was approved yet.
    /// <para>
    /// Without it a client can say a plugin widened but not what it now asks
    /// for that it did not before, which is the one thing the owner needs to
    /// answer the question.
    /// </para>
    /// </summary>
    [JsonProperty("consented_capabilities")]
    public PluginCapabilities? ConsentedCapabilities { get; init; }

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
        bool awaitingConsent = false,
        PluginCapabilities? consentedCapabilities = null
    )
    {
        Capabilities = info.Capabilities;
        AwaitingConsent = awaitingConsent;
        AwaitingApproval = info.ApprovalReason is not null;
        ApprovalReason = info.ApprovalReason;
        ConsentedCapabilities = consentedCapabilities;
        RestartRequired = restart?.Required ?? false;
        RestartReasons = restart?.Explain() ?? [];
        Id = info.Id;
        Name = info.Name;
        Description = info.Description;
        Version = info.Version.ToString();
        Status = info.Status.ToString().ToLowerInvariant();
        Malfunction = info.Malfunction;
        Author = info.Author;
        ProjectUrl = info.ProjectUrl;
    }
}
