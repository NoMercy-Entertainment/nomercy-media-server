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
/// <para>
/// A key is the lowercase hex of a SHA-256 digest - 64 characters from
/// <c>0-9a-f</c> - and only <see cref="PutAsync" /> mints one. Any other
/// string is treated as a key the store does not hold.
/// </para>
/// <para>
/// None of these members but <see cref="PutAsync" /> has a way to say why it
/// could not answer, so none of them throws: a key the store will not take,
/// and a failure inside the server, both read as an absence -
/// <see cref="ExistsAsync" /> false, <see cref="OpenReadAsync" /> null,
/// <see cref="TouchAsync" /> and <see cref="DeleteAsync" /> no-ops - and the
/// server logs the reason on its own side. <see cref="PutAsync" /> has to
/// return a key, so it throws when there is none. A
/// <see cref="OperationCanceledException" /> from the token the caller passed
/// is never swallowed by any of them.
/// </para>
/// </summary>
public interface IPluginDerivedAudio
{
    /// <summary>
    /// Writes a new file and returns the key to read it back with. The one
    /// member here that throws rather than answering with an absence.
    /// </summary>
    Task<string> PutAsync(Stream content, string contentType, CancellationToken ct = default);

    /// <summary>False when the key is not one the store knows, or the server could not answer.</summary>
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);

    /// <summary>Null when the key is not one the store knows, or the server could not answer.</summary>
    Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default);

    /// <summary>Marks a file as still wanted, so a cache sweep does not reclaim it. A no-op for an unknown key.</summary>
    Task TouchAsync(string key, CancellationToken ct = default);

    /// <summary>A no-op for a key the store does not know.</summary>
    Task DeleteAsync(string key, CancellationToken ct = default);
}
