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

using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginHost;

/// <summary>
/// The media tree, with each branch its own facade on the wire.
/// <para>
/// One name per branch rather than one "media" name and the member alone:
/// Transcode and Remux both declare StartAsync, and a single name would leave
/// the broker guessing which capability to check.
/// </para>
/// </summary>
public sealed class RemoteMedia(RemoteCall call) : IPluginMedia
{
    public IPluginMediaProxy Proxy { get; } = new RemoteMediaProxy(call);

    public IPluginMediaTranscode Transcode { get; } = new RemoteMediaTranscode(call);

    public IPluginMediaRemux Remux { get; } = new RemoteMediaRemux(call);

    public IPluginMediaLive Live { get; } = new RemoteMediaLive(call);

    public IPluginRecorder Record { get; } = new RemoteRecorder(call);
}

public sealed class RemoteMediaProxy(RemoteCall call) : IPluginMediaProxy
{
    public async Task<PluginMediaUrl> MintAsync(
        PluginProxyRequest request,
        CancellationToken ct = default
    ) =>
        await call.AskAsync<PluginMediaUrl>(
            "media.proxy",
            nameof(IPluginMediaProxy.MintAsync),
            new { request }
        ) ?? throw new InvalidOperationException("media.proxy.MintAsync answered with no url.");

    public async Task<PluginMediaUrl> MintImageAsync(Uri source, CancellationToken ct = default) =>
        await call.AskAsync<PluginMediaUrl>(
            "media.proxy",
            nameof(IPluginMediaProxy.MintImageAsync),
            new { source }
        )
        ?? throw new InvalidOperationException("media.proxy.MintImageAsync answered with no url.");
}

public sealed class RemoteMediaTranscode(RemoteCall call) : IPluginMediaTranscode
{
    public async Task<PluginMediaUrl> StartAsync(
        PluginRemuxRequest request,
        CancellationToken ct = default
    ) =>
        await call.AskAsync<PluginMediaUrl>(
            "media.transcode",
            nameof(IPluginMediaTranscode.StartAsync),
            new { request }
        )
        ?? throw new InvalidOperationException("media.transcode.StartAsync answered with no url.");
}

public sealed class RemoteMediaRemux(RemoteCall call) : IPluginMediaRemux
{
    public async Task<PluginMediaUrl> StartAsync(
        PluginRemuxRequest request,
        CancellationToken ct = default
    ) =>
        await call.AskAsync<PluginMediaUrl>(
            "media.remux",
            nameof(IPluginMediaRemux.StartAsync),
            new { request }
        ) ?? throw new InvalidOperationException("media.remux.StartAsync answered with no url.");
}

public sealed class RemoteMediaLive(RemoteCall call) : IPluginMediaLive
{
    public Task PublishAsync(
        IReadOnlyList<PluginLiveChannel> channels,
        CancellationToken ct = default
    ) => call.TellAsync("media.live", nameof(IPluginMediaLive.PublishAsync), new { channels });

    public Task PublishGuideAsync(
        IReadOnlyList<PluginEpgProgram> guide,
        CancellationToken ct = default
    ) => call.TellAsync("media.live", nameof(IPluginMediaLive.PublishGuideAsync), new { guide });

    public Task PublishGroupsAsync(
        IReadOnlyList<PluginChannelGroup> groups,
        CancellationToken ct = default
    ) => call.TellAsync("media.live", nameof(IPluginMediaLive.PublishGroupsAsync), new { groups });
}

public sealed class RemoteRecorder(RemoteCall call) : IPluginRecorder
{
    public Task<JobId> ScheduleAsync(
        PluginRecordingRequest request,
        CancellationToken ct = default
    ) =>
        call.AskAsync<JobId>(
            "media.record",
            nameof(IPluginRecorder.ScheduleAsync),
            new { request }
        );

    public Task CancelAsync(JobId recording, CancellationToken ct = default) =>
        call.TellAsync("media.record", nameof(IPluginRecorder.CancelAsync), new { recording });

    public async Task<IReadOnlyList<PluginRecording>> ListAsync(CancellationToken ct = default) =>
        await call.AskAsync<IReadOnlyList<PluginRecording>>(
            "media.record",
            nameof(IPluginRecorder.ListAsync)
        ) ?? [];
}
