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

namespace NoMercy.Plugins.Abstractions;

/// <summary>
/// The server's own store for files nothing in a library owns: separated
/// stems, rendered transitions, anything <see cref="IPluginAudioTools" />
/// produces. Addressed by a content key rather than a path, so two plugins
/// that derive the same stem from the same track share one copy instead of
/// each staging its own multi-megabyte file next to a library it does not
/// own.
/// <para>
/// Elevated - see <see cref="PluginHookCapability.DerivedAudio" /> - for the
/// same reason <see cref="IPluginStorage" /> is: it is a place the plugin did
/// not stage itself into, and deleting from it is not harmless.
/// </para>
/// </summary>
public interface IPluginDerivedAudio
{
    /// <summary>Writes a new file and returns the key to read it back with.</summary>
    Task<string> PutAsync(Stream content, string contentType, CancellationToken ct = default);

    Task<bool> ExistsAsync(string key, CancellationToken ct = default);

    /// <summary>Null when the key is not one the store knows.</summary>
    Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default);

    /// <summary>Marks a file as still wanted, so a cache sweep does not reclaim it.</summary>
    Task TouchAsync(string key, CancellationToken ct = default);

    Task DeleteAsync(string key, CancellationToken ct = default);
}
