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

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
/// None of these members has a refusal channel, so both a refused key and a
/// failure inside the server read as an absence: <see cref="ExistsAsync" />
/// answers false, <see cref="OpenReadAsync" /> answers null, and
/// <see cref="TouchAsync" /> and <see cref="DeleteAsync" /> do nothing. Every
/// one of those is logged at Warning first, so the owner still has the reason.
/// <see cref="PutAsync" /> is the exception - it has to return the key of the
/// content it was handed, and there is no key when the store would not take
/// it, so that failure is logged and rethrown. Cancellation is never
/// swallowed anywhere here: a caller that cancelled is owed its
/// <see cref="OperationCanceledException" />.
/// </para>
/// </summary>
public sealed class PluginDerivedAudio(
    IDerivedAudioStore store,
    ILogger<PluginDerivedAudio>? logger = null
) : IPluginDerivedAudio
{
    private readonly ILogger<PluginDerivedAudio> _logger =
        logger ?? NullLogger<PluginDerivedAudio>.Instance;

    public async Task<string> PutAsync(
        Stream content,
        string contentType,
        CancellationToken ct = default
    )
    {
        // The one member with nothing sensible to hand back on failure: a
        // caller asked for the key of content it just produced, and there is
        // no key. Logged here so the reason is on the server's own record
        // whichever way the plugin handles the throw.
        try
        {
            DerivedAudioEntry entry = await store.PutAsync(content, contentType, ct);
            return entry.Key;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "the derived store could not accept a plugin's content");
            throw;
        }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        if (!DerivedAudioKey.IsValid(key))
        {
            return false;
        }

        try
        {
            return await store.ExistsAsync(key, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Warn(exception, nameof(ExistsAsync));
            return false;
        }
    }

    public async Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default)
    {
        if (!DerivedAudioKey.IsValid(key))
        {
            return null;
        }

        try
        {
            return await store.OpenReadAsync(key, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Warn(exception, nameof(OpenReadAsync));
            return null;
        }
    }

    public async Task TouchAsync(string key, CancellationToken ct = default)
    {
        if (!DerivedAudioKey.IsValid(key))
        {
            return;
        }

        try
        {
            await store.TouchAsync(key, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Warn(exception, nameof(TouchAsync));
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        if (!DerivedAudioKey.IsValid(key))
        {
            return;
        }

        try
        {
            await store.DeleteAsync(key, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Warn(exception, nameof(DeleteAsync));
        }
    }

    private void Warn(Exception exception, string member) =>
        _logger.LogWarning(
            exception,
            "the server could not complete a plugin's {Member} call on the derived store",
            member
        );
}
