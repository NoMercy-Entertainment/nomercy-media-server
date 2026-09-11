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

public static class PluginHookCapability
{
    public const string MediaSource = "mediaSource";
    public const string Metadata = "metadata";
    public const string ScheduledTask = "scheduledTask";
    public const string Auth = "auth";
    public const string Encoder = "encoder";
    public const string Ui = "ui";

    /// <summary>
    /// Writing to and deleting from a user's library.
    /// <para>
    /// Its own constant rather than a flag on an existing one, so that
    /// declaring it is a visible line in the manifest. Elevated by definition —
    /// see <see cref="Elevated"/> — so it can never arrive through a baseline
    /// auto-enable, and the owner is asked every time.
    /// </para>
    /// <para>The grant it needs is per-library: see
    /// <see cref="PluginGrantKind.LibraryWrite"/>.</para>
    /// </summary>
    public const string LibraryWrite = "libraryWrite";

    /// <summary>
    /// Writing files anywhere the server can write, through
    /// <see cref="IPluginStorage" />.
    /// <para>
    /// Elevated: a plugin that can write into a library folder can overwrite an
    /// owner's media, which is not a thing to arrive through a baseline
    /// auto-enable.
    /// </para>
    /// </summary>
    public const string Storage = "storage";

    /// <summary>
    /// Running ffmpeg over a track or a derived file, and splitting stems from
    /// it, through <see cref="IPluginAudioTools" />.
    /// <para>
    /// Elevated: an arbitrary filter graph is an arbitrary ffmpeg invocation
    /// against a library file, which is not a thing to arrive through a
    /// baseline auto-enable.
    /// </para>
    /// </summary>
    public const string AudioTools = "audioTools";

    /// <summary>
    /// Reading and writing the server's own store of files derived from
    /// analysis - stems, rendered transitions - through
    /// <see cref="IPluginDerivedAudio" />.
    /// <para>
    /// Elevated for the same reason <see cref="Storage" /> is: it is a place
    /// the plugin did not stage itself into, and deleting from it is not
    /// harmless.
    /// </para>
    /// </summary>
    public const string DerivedAudio = "derivedAudio";

    /// <summary>
    /// Writing the DJ analysis record and the stem register through
    /// <see cref="IPluginMusicAnalysisWriter" />.
    /// <para>
    /// Elevated: it is a write to data every other plugin's
    /// <see cref="IPluginMusicQuery" /> can read, so a plugin declaring it can
    /// shape what the rest of the platform believes about a track.
    /// </para>
    /// </summary>
    public const string MusicAnalysisWrite = "musicAnalysisWrite";

    /// <summary>
    /// Hooks that can never be baseline, whatever else a plugin declares.
    /// <para>Consent classification asks whether every declared hook is
    /// harmless. A hook that can delete a user's media is not, so it is named
    /// here rather than left to the absence of an entry in the baseline set —
    /// the next hook added to that set would otherwise silently become
    /// auto-enabled.</para>
    /// </summary>
    public static IReadOnlySet<string> Elevated { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            LibraryWrite,
            Auth,
            Encoder,
            Storage,
            AudioTools,
            DerivedAudio,
            MusicAnalysisWrite,
        };
}
