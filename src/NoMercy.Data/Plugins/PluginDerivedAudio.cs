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
/// right hex in the wrong case - is refused by the store, which is the only
/// validator: it is not this facade's only caller, so a copy of the rule here
/// would be a second rule to keep in step rather than a second line of
/// defence.
/// </para>
/// <para>
/// None of these members has a refusal channel, so both a refused key and a
/// failure inside the server read as an absence: <see cref="ExistsAsync" />
/// answers false, <see cref="OpenReadAsync" /> answers null, and
/// <see cref="TouchAsync" /> and <see cref="DeleteAsync" /> do nothing. A
/// failure is logged at Warning first, so the owner still has the reason.
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
            // The one log line here the guard does not write, so it carries
            // the same two properties by hand rather than being the one entry
            // a pipeline filtering on them cannot see.
            _logger.LogWarning(
                exception,
                "plugin {PluginId}: {Member} failed inside the server",
                SharedPluginId,
                nameof(PutAsync)
            );
            throw;
        }
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) =>
        PluginCallGuard.RunOrAsync(
            SharedPluginId,
            nameof(ExistsAsync),
            () => store.ExistsAsync(key, ct),
            false,
            _logger
        );

    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default) =>
        PluginCallGuard.RunOrAsync<Stream?>(
            SharedPluginId,
            nameof(OpenReadAsync),
            () => store.OpenReadAsync(key, ct),
            null,
            _logger
        );

    /// <summary>
    /// The store answers whether the key survived to be bumped; the plugin
    /// contract has no channel for that, so the bool is discarded here. A
    /// plugin that needs to know asks <see cref="ExistsAsync" />: the host's
    /// own ffmpeg path is the caller that acts on the answer, and it holds the
    /// store directly.
    /// </summary>
    public Task TouchAsync(string key, CancellationToken ct = default) =>
        PluginCallGuard.RunAsync(
            SharedPluginId,
            nameof(TouchAsync),
            () => store.TouchAsync(key, ct),
            _logger
        );

    public Task DeleteAsync(string key, CancellationToken ct = default) =>
        PluginCallGuard.RunAsync(
            SharedPluginId,
            nameof(DeleteAsync),
            () => store.DeleteAsync(key, ct),
            _logger
        );

    /// <summary>
    /// One facade serves every plugin, so there is no plugin to name in the
    /// guard's warning line. It says so rather than leaving the property out:
    /// a missing field reads as a gap in the pipeline.
    /// </summary>
    private const string SharedPluginId = "shared";
}
