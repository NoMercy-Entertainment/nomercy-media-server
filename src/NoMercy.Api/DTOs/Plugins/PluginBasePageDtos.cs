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
using NoMercy.Api.DTOs.Dashboard;

namespace NoMercy.Api.DTOs.Plugins;

/// <summary>What a plugin is. Open to anyone it is shared with.</summary>
public record PluginInfoPageDto
{
    [JsonProperty("id")]
    public string Id { get; init; } = string.Empty;

    [JsonProperty("name")]
    public string Name { get; init; } = string.Empty;

    [JsonProperty("description")]
    public string Description { get; init; } = string.Empty;

    [JsonProperty("version")]
    public string Version { get; init; } = string.Empty;

    [JsonProperty("author")]
    public string? Author { get; init; }

    [JsonProperty("status")]
    public string Status { get; init; } = string.Empty;

    [JsonProperty("sideloaded")]
    public bool Sideloaded { get; init; }

    [JsonProperty("projectUrl")]
    public string? ProjectUrl { get; init; }
}

/// <summary>
/// One place the server can write, as a picker offers it.
/// <para>
/// The id is the host's own and the only thing a picker may send back. A path
/// would have to exist on the server, and the person choosing is not on it.
/// </para>
/// </summary>
public record PluginLocationDto
{
    [JsonProperty("id")]
    public string Id { get; init; } = string.Empty;

    [JsonProperty("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>local, nfs, smb, s3 or webdav: what the owner would call it.</summary>
    [JsonProperty("kind")]
    public string Kind { get; init; } = string.Empty;

    /// <summary>Whether the server can write here, which the owner should not have to guess.</summary>
    [JsonProperty("writable")]
    public bool Writable { get; init; }
}

/// <summary>
/// One entry inside a location, as a picker offers it.
/// <para>
/// The id is the entry's path relative to the scope, which is what makes it
/// safe to hand back: it cannot name anything outside the folder the owner
/// chose.
/// </para>
/// </summary>
public record PluginLocationEntryDto
{
    [JsonProperty("id")]
    public string Id { get; init; } = string.Empty;

    [JsonProperty("name")]
    public string Name { get; init; } = string.Empty;

    [JsonProperty("isDirectory")]
    public bool IsDirectory { get; init; }

    /// <summary>Zero for a directory.</summary>
    [JsonProperty("sizeBytes")]
    public long SizeBytes { get; init; }
}

/// <summary>
/// Where a plugin's own documentation lives. Open to anyone it is shared with.
/// <para>
/// A link rather than the prose: the host does not fetch and render a page a
/// plugin author controls, because that page would then be running inside the
/// viewer's session.
/// </para>
/// </summary>
public record PluginDocsPageDto
{
    [JsonProperty("url")]
    public string? Url { get; init; }

    [JsonProperty("projectUrl")]
    public string? ProjectUrl { get; init; }
}

/// <summary>
/// What the owner agreed to when they installed this. Open to anyone it is
/// shared with: a license nobody can read before installing is not one.
/// </summary>
public record PluginLicensePageDto
{
    [JsonProperty("license")]
    public string? License { get; init; }

    [JsonProperty("author")]
    public string? Author { get; init; }

    [JsonProperty("projectUrl")]
    public string? ProjectUrl { get; init; }
}

/// <summary>
/// What the plugin may do, what the owner answered, and how thick the walls
/// are. Owner only: it is the page an answer is given on.
/// </summary>
public record PluginPermissionsPageDto
{
    [JsonProperty("isolation")]
    public string Isolation { get; init; } = string.Empty;

    [JsonProperty("noticeKey")]
    public string NoticeKey { get; init; } = string.Empty;

    [JsonProperty("capabilities")]
    public IReadOnlyList<PluginCapabilityStateDto> Capabilities { get; init; } = [];

    [JsonProperty("refusals")]
    public IReadOnlyList<PluginRefusalCountDto> Refusals { get; init; } = [];
}

/// <summary>How often one refusal fired. The code, never what was being done.</summary>
public record PluginRefusalCountDto
{
    [JsonProperty("code")]
    public string Code { get; init; } = string.Empty;

    [JsonProperty("total")]
    public int Total { get; init; }
}

/// <summary>
/// Counts and nothing else.
/// <para>
/// No title, no file and no person appears here. A health page that showed
/// what a plugin had been doing would be a log of what somebody watched,
/// shown under a heading nobody reads as that.
/// </para>
/// </summary>
public record PluginHealthPageDto
{
    [JsonProperty("status")]
    public string Status { get; init; } = string.Empty;

    [JsonProperty("crashes")]
    public int Crashes { get; init; }

    [JsonProperty("ceilingHits")]
    public int CeilingHits { get; init; }

    [JsonProperty("refusals")]
    public IReadOnlyList<PluginRefusalCountDto> Refusals { get; init; } = [];

    [JsonProperty("quota")]
    public PluginQuotaDto Quota { get; init; } = new();

    /// <summary>The last ceiling this plugin went past, or null when it has not.</summary>
    [JsonProperty("lastRefusal")]
    public PluginRefusalCountDto? LastRefusal { get; init; }
}

/// <summary>Whether a newer release exists, and what changed in it.</summary>
public record PluginUpdatePageDto
{
    [JsonProperty("installedVersion")]
    public string InstalledVersion { get; init; } = string.Empty;

    [JsonProperty("availableVersion")]
    public string? AvailableVersion { get; init; }

    [JsonProperty("updateAvailable")]
    public bool UpdateAvailable { get; init; }
}

/// <summary>
/// The plugin's own settings, as the schema describes them.
/// <para>
/// Owner only unless the schema marks a field per-user, in which case a member
/// receives those fields and nothing else.
/// </para>
/// </summary>
public record PluginSettingsPageDto
{
    [JsonProperty("scope")]
    public string Scope { get; init; } = string.Empty;

    [JsonProperty("fields")]
    public IReadOnlyList<PluginSettingsFieldDto> Fields { get; init; } = [];
}

/// <summary>One field, as the host will draw it.</summary>
public record PluginSettingsFieldDto
{
    [JsonProperty("key")]
    public string Key { get; init; } = string.Empty;

    [JsonProperty("labelKey")]
    public string LabelKey { get; init; } = string.Empty;

    [JsonProperty("helpKey")]
    public string? HelpKey { get; init; }

    [JsonProperty("type")]
    public string Type { get; init; } = string.Empty;

    [JsonProperty("scope")]
    public string Scope { get; init; } = string.Empty;

    [JsonProperty("writable")]
    public bool Writable { get; init; }

    /// <summary>
    /// Never carries a value for a password field. A password lives in the
    /// secret store, and a settings page that echoed one back would put it in
    /// every client's memory and every proxy's log.
    /// </summary>
    [JsonProperty("default")]
    public object? Default { get; init; }
}
