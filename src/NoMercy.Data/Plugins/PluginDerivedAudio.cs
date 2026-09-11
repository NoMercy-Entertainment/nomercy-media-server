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
/// facade). A key that <see cref="IPluginDerivedAudio.PutAsync" /> could not
/// have minted - null, blank, the wrong length, the wrong characters, or the
/// right hex in the wrong case - is refused here, before it reaches the
/// store, since <c>RelativePath</c> slices the key apart to build a path. The
/// store applies the same rule again on its own: this facade is not the only
/// caller it has.
/// </para>
/// <para>
/// None of these members has a refusal channel, so a refusal reads as an
/// absence: <see cref="ExistsAsync" /> is false, <see cref="OpenReadAsync" />
/// is null, and <see cref="TouchAsync" /> and <see cref="DeleteAsync" /> do
/// nothing. <see cref="PutAsync" /> is the exception - it has to return a key,
/// so a failure there is rethrown.
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
        return DerivedAudioKey.IsValid(key) ? store.ExistsAsync(key, ct) : Task.FromResult(false);
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default)
    {
        return DerivedAudioKey.IsValid(key)
            ? store.OpenReadAsync(key, ct)
            : Task.FromResult<Stream?>(null);
    }

    public Task TouchAsync(string key, CancellationToken ct = default)
    {
        return DerivedAudioKey.IsValid(key) ? store.TouchAsync(key, ct) : Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        return DerivedAudioKey.IsValid(key) ? store.DeleteAsync(key, ct) : Task.CompletedTask;
    }
}
