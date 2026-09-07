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
using NoMercy.Events;

namespace NoMercy.Plugins.Abstractions;

/// <summary>
/// Everything the host hands a plugin.
/// <para>
/// The members below are the sanctioned routes into the server. Each one exists
/// because the alternative was a plugin reaching around the platform to do the
/// same thing less safely: resolving a data-protection provider to hand-roll
/// secret storage, sharing the EF assembly to read the library, or building a
/// bare <c>HttpClient</c> to reach a host the manifest could not name. A
/// platform is only as strong as its most convenient path.
/// </para>
/// </summary>
public interface IPluginContext
{
    IEventBus EventBus { get; }
    IServiceProvider Services { get; }
    ILogger Logger { get; }
    string DataFolderPath { get; }
    IPluginConfiguration Configuration { get; }

    /// <summary>Bounded by the plugin's granted hosts. See <see cref="Grants"/>.</summary>
    HttpClient HttpClient { get; }

    /// <summary>The plugin's own id, so it can name itself when raising an event or asking for a grant.</summary>
    Ulid PluginId { get; }

    /// <summary>Protected storage for passwords and tokens, scoped to this plugin.</summary>
    IPluginSecretStore Secrets { get; }

    /// <summary>Reading the library. Read-only, so it needs no capability.</summary>
    IPluginLibraryQuery Library { get; }

    /// <summary>
    /// Reading music and its audio analysis. Read-only, so it needs no
    /// capability, for the same reason <see cref="Library" /> needs none.
    ///
    /// Default null so a host built before this member still compiles, and so a
    /// plugin checks for absence rather than catching a throw.
    /// </summary>
    IPluginMusicQuery? Music => null;

    /// <summary>
    /// Everything the plugin can reach that it does not own, by name.
    ///
    /// The general way in. A host grows a capability without this contract
    /// moving, and a plugin asks whether one exists before depending on it.
    /// Null when the host mediates nothing, so existing hosts keep compiling.
    /// </summary>
    IPluginSystem? System => null;

    /// <summary>
    /// Playback, as a typed surface over <see cref="PluginCapability.Player" />.
    /// One capability made convenient, never the only way to reach it.
    /// </summary>
    IPluginPlayer? Player => null;

    /// <summary>
    /// Writing to the library. Present only when the plugin declared
    /// <see cref="PluginHookCapability.LibraryWrite"/> and the owner granted at
    /// least one library; null otherwise, so the absence is checkable rather
    /// than a call that throws.
    /// </summary>
    IPluginLibraryWriter? LibraryWriter { get; }

    /// <summary>
    /// Asking the server to encode a file the plugin has staged.
    /// <para>
    /// Present only when the plugin declared
    /// <see cref="PluginHookCapability.Encoder" />; null otherwise, so the
    /// absence is checkable rather than a call that throws. Without it a plugin
    /// reached the server's own job types by name and drove them with
    /// reflection, which broke silently four times in three days.
    /// </para>
    /// </summary>
    IPluginEncoder? Encoder => null;

    /// <summary>
    /// Learning what became of a job this plugin asked for.
    /// <para>
    /// Beside <see cref="Encoder" /> because asking for work and learning
    /// whether it happened are one story: a plugin that deletes a file once the
    /// encode lands cannot tell a failure from a job still running without this,
    /// and both look like "the library does not have it yet", for ever.
    /// </para>
    /// </summary>
    IPluginJobs? Jobs => null;

    /// <summary>
    /// The places the server can write, rather than a path the owner typed.
    /// <para>
    /// Present only when the plugin declared
    /// <see cref="PluginHookCapability.Storage" />.
    /// </para>
    /// </summary>
    IPluginStorage? Storage => null;

    /// <summary>What the owner has granted this plugin, and how to ask for more.</summary>
    IPluginGrants Grants { get; }

    /// <summary>
    /// Pushing to this plugin's subscribed clients. Present whatever the
    /// capabilities say — a plugin without <c>ws</c> simply has no subscribers,
    /// and reaching nobody is a better answer than a null a plugin has to
    /// branch on for a channel that is part of its contract.
    /// </summary>
    IPluginHubContext Hub { get; }

    /// <summary>
    /// Raises <paramref name="name"/> on the server's event bus in an envelope
    /// the host can subscribe to. A plugin's own event class cannot cross the
    /// load-context boundary; this can.
    /// </summary>
    Task PublishAsync<T>(string name, T payload, CancellationToken ct = default);
}
