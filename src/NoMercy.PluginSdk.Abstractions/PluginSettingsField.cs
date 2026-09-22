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

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// One field on the host-rendered settings page.
/// <para>
/// The host draws it, so it is drawn the same on web, phone and television,
/// and a plugin cannot ship a settings page a remote control cannot reach.
/// </para>
/// </summary>
public sealed record PluginSettingsField
{
    public required string Key { get; init; }

    /// <summary>A key, not a sentence: the host renders it in the reader's language.</summary>
    public required string LabelKey { get; init; }

    /// <summary>One of <see cref="PluginFormFieldType.All" />.</summary>
    public required string Type { get; init; }

    public string? HelpKey { get; init; }

    public PluginSettingsScope Scope { get; init; } = PluginSettingsScope.Server;

    /// <summary>
    /// False by default. A field the plugin writes to without the owner asking
    /// is one the owner cannot keep set, so saying so is opt-in.
    /// </summary>
    public bool Writable { get; init; }

    public object? Default { get; init; }

    public IReadOnlyList<PluginFormOption> Options { get; init; } = [];

    /// <summary>
    /// A password is held in the secret store, never in settings, so it is not
    /// in the settings file, the export or the log line that dumps them.
    /// </summary>
    public bool BackedBySecrets =>
        string.Equals(Type, PluginFormFieldType.Password, StringComparison.Ordinal);
}
