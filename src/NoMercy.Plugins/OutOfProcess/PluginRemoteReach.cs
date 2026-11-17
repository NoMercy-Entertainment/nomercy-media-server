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

namespace NoMercy.PluginSdk.OutOfProcess;

/// <summary>
/// What the out-of-process runtime carries across the boundary, and what it
/// does not. The one list.
/// <para>
/// A plugin moved into its own process gets the context <c>RemotePluginContext</c>
/// builds in NoMercy.PluginHost, and the server reaches it through
/// <see cref="RemotePlugin" />. Whatever neither of those carries comes back
/// null, refuses, or is never called, and the plugin looks broken on the one
/// install where the owner chose isolation. This table says which, so the
/// dashboard can name it instead of the plugin failing quietly.
/// </para>
/// <para>
/// Tests in NoMercy.Tests.PluginHost hold it to the code: a facade added to
/// <see cref="IPluginContext" />, a facade carried by <c>RemotePluginContext</c>,
/// a new hook, or an entry point <see cref="RemotePlugin" /> starts forwarding
/// fails a test until this table says so too.
/// </para>
/// </summary>
public static class PluginRemoteReach
{
    /// <summary>
    /// The <see cref="IPluginContext" /> members <c>RemotePluginContext</c>
    /// leaves at the interface default, each with the hook whose declaration
    /// means the plugin will reach for it.
    /// <para>
    /// Null where nothing in a manifest says so: reading music, playback,
    /// the browser, cast and the marketplace need no hook, so a plugin's use
    /// of them cannot be known before it runs.
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, string?> UncarriedFacades { get; } =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [nameof(IPluginContext.Music)] = null,
            [nameof(IPluginContext.Player)] = null,
            [nameof(IPluginContext.Encoder)] = PluginHookCapability.Encoder,
            // Handed out with the encoder in process, so declared by the same hook.
            [nameof(IPluginContext.Jobs)] = PluginHookCapability.Encoder,
            [nameof(IPluginContext.AudioTools)] = PluginHookCapability.AudioTools,
            [nameof(IPluginContext.DerivedAudio)] = PluginHookCapability.DerivedAudio,
            [nameof(IPluginContext.MusicAnalysisWriter)] = PluginHookCapability.MusicAnalysisWrite,
            [nameof(IPluginContext.Lyrics)] = PluginHookCapability.LyricsWrite,
            [nameof(IPluginContext.Browser)] = null,
            [nameof(IPluginContext.Cast)] = null,
            [nameof(IPluginContext.Marketplace)] = null,
        };

    /// <summary>
    /// Hooks whose facade the runtime does carry, so declaring one asks for
    /// nothing the other process lacks.
    /// </summary>
    public static IReadOnlySet<string> CarriedHooks { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            PluginHookCapability.LibraryWrite,
            PluginHookCapability.Storage,
        };

    /// <summary>
    /// Hooks that are an entry point the server calls on the plugin, each with
    /// the interface it calls through.
    /// </summary>
    public static IReadOnlyDictionary<string, Type> EntryPointHooks { get; } =
        new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
        {
            [PluginHookCapability.Ui] = typeof(IUiPlugin),
            [PluginHookCapability.MediaSource] = typeof(IMediaSourcePlugin),
            [PluginHookCapability.Metadata] = typeof(IMetadataPlugin),
            [PluginHookCapability.ScheduledTask] = typeof(IScheduledTaskPlugin),
            [PluginHookCapability.Auth] = typeof(IAuthPlugin),
        };

    /// <summary>
    /// The entry-point hooks the server forwards to a plugin in another
    /// process.
    /// <para>
    /// None yet. The host can dispatch five of them, but <see cref="RemotePlugin" />
    /// implements <see cref="IPlugin" /> only, and every caller in the server
    /// finds a plugin's entry point by casting the instance it holds.
    /// </para>
    /// </summary>
    public static IReadOnlySet<string> ForwardedHooks { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a plugin's MVC controllers answer when it runs elsewhere. They
    /// do not: routes are built from assemblies loaded into the server, and
    /// nothing dispatches a request across the channel.
    /// </summary>
    public static bool ForwardsControllers => false;
}
