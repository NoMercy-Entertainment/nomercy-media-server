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

using NoMercy.MediaProcessing.DerivedAudio;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Data.Plugins;

/// <summary>
/// The server side of <see cref="IPluginDerivedAudio" />: a thin forwarder to
/// the server's own <see cref="IDerivedAudioStore" />.
///
/// <para>
/// Deliberately not exposed here: <see cref="IDerivedAudioStore.EvictAsync" />
/// (the cache-cap sweep is the server's policy, not a plugin's to trigger) and
/// <see cref="IDerivedAudioStore.RelativePath" /> (a plugin never learns where
/// a file actually lives - it holds the key, and reads or writes through this
/// facade). A null or empty key is refused here, before it reaches the store,
/// since <c>RelativePath</c> slices the key apart to build a path.
/// </para>
/// </summary>
public sealed class PluginDerivedAudio(IDerivedAudioStore store) : IPluginDerivedAudio
{
    public async Task<string> PutAsync(
        Stream content,
        string contentType,
        CancellationToken ct = default
    )
    {
        DerivedAudioEntry entry = await store.PutAsync(content, contentType, ct);
        return entry.Key;
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        return string.IsNullOrEmpty(key) ? Task.FromResult(false) : store.ExistsAsync(key, ct);
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default)
    {
        return string.IsNullOrEmpty(key)
            ? Task.FromResult<Stream?>(null)
            : store.OpenReadAsync(key, ct);
    }

    public Task TouchAsync(string key, CancellationToken ct = default)
    {
        return string.IsNullOrEmpty(key) ? Task.CompletedTask : store.TouchAsync(key, ct);
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        return string.IsNullOrEmpty(key) ? Task.CompletedTask : store.DeleteAsync(key, ct);
    }
}
