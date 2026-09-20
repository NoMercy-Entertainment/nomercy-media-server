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
    /// <summary>
    /// Server events by topic, and the plugin's own events. The raw host bus
    /// left the contract: it carried every event the server raises, including
    /// ones about users this plugin has no capability for.
    /// </summary>
    IPluginEvents Events { get; }

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
    /// Running ffmpeg over a track or a derived file, and splitting stems.
    /// <para>
    /// Present only when the plugin declared
    /// <see cref="PluginHookCapability.AudioTools" />; null otherwise, so the
    /// absence is checkable rather than a call that throws.
    /// </para>
    /// </summary>
    IPluginAudioTools? AudioTools => null;

    /// <summary>
    /// The server's own store of files derived from analysis - stems, rendered
    /// transitions - keyed by content rather than by library path.
    /// <para>
    /// Present only when the plugin declared
    /// <see cref="PluginHookCapability.DerivedAudio" />; null otherwise, so the
    /// absence is checkable rather than a call that throws.
    /// </para>
    /// </summary>
    IPluginDerivedAudio? DerivedAudio => null;

    /// <summary>
    /// Writing the DJ analysis record and the stem register.
    /// <para>
    /// Present only when the plugin declared
    /// <see cref="PluginHookCapability.MusicAnalysisWrite" />; null otherwise,
    /// so the absence is checkable rather than a call that throws.
    /// </para>
    /// </summary>
    IPluginMusicAnalysisWriter? MusicAnalysisWriter => null;

    /// <summary>
    /// Sockets, discovery and port mapping, as far as the owner granted them.
    /// <para>
    /// Non-nullable, unlike the v2 members above it. A null facade taught a
    /// plugin author to branch and then quietly do nothing, so the plugin looked
    /// idle and the owner never learned a capability was missing. Reaching a
    /// member without the capability refuses instead, and the refusal names the
    /// line of plugin.json to add.
    /// </para>
    /// </summary>
    IPluginNet Net =>
        throw new PluginRefusedException(
            PluginRefusalMessages.FacadeNotOnThisHost(PluginId.ToString(), "IPluginContext.Net")
        );

    /// <summary>
    /// Every place this plugin may read and write. Replaces the v2
    /// <c>IPluginStorage</c>, which listed the server's folders and offered the
    /// plugin's own as a bare path string.
    /// </summary>
    IPluginStorageV3 Storage =>
        throw new PluginRefusedException(
            PluginRefusalMessages.FacadeNotOnThisHost(PluginId.ToString(), "IPluginContext.Storage")
        );

    /// <summary>Child processes, for the binaries the manifest names.</summary>
    IPluginProcess Process =>
        throw new PluginRefusedException(
            PluginRefusalMessages.FacadeNotOnThisHost(PluginId.ToString(), "IPluginContext.Process")
        );

    /// <summary>What this server is, so a plugin branches on a fact.</summary>
    IPluginServerInfo Server =>
        throw new PluginRefusedException(
            PluginRefusalMessages.FacadeNotOnThisHost(PluginId.ToString(), "IPluginContext.Server")
        );

    /// <summary>Native libraries, gated on the marketplace signature rather than a capability.</summary>
    IPluginNative Native =>
        throw new PluginRefusedException(
            PluginRefusalMessages.FacadeNotOnThisHost(PluginId.ToString(), "IPluginContext.Native")
        );

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
