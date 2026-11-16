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

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// The tokens a <see cref="PluginFilterGraph.Text" /> may carry where a file
/// belongs. The host replaces each one before the graph runs, so a plugin never
/// hard-codes a path that only exists on the machine running the server.
/// </summary>
public static class PluginFilterGraphTokens
{
    /// <summary>
    /// The Spleeter model's file name, for <c>stemsplit=model={stemsModel}</c>.
    /// Always Spleeter, whatever the machine: a plugin that wants the better
    /// RoFormer stems where the machine can run them writes
    /// <see cref="StemsSource" /> instead.
    /// </summary>
    public const string StemsModel = "{stemsModel}";

    /// <summary>
    /// The whole source of a stem split, written as <c>stemsplit={stemsSource}</c>
    /// with the plugin's own options after it (<c>stemsplit={stemsSource}:stem=all</c>).
    /// <para>
    /// The host fills it in per run: RoFormer's <c>preset=full:cpu_fallback=0</c>
    /// on a machine whose GPU the filter takes, <c>model=</c> and the Spleeter
    /// file everywhere else. When the filter refuses RoFormer on this machine
    /// the host runs the same graph again with Spleeter, in the same call, and
    /// remembers the answer until the ffmpeg build, the GPU list or a GPU
    /// driver changes; the plugin sees one run. A graph that also has a
    /// <c>whisper</c> filter gets Spleeter: RoFormer switches the GPU backend
    /// of the whole process to full precision, which makes whisper on the GPU
    /// several times slower. While RoFormer splits, the run is judged by the
    /// filter's progress instead of a fixed time. Needs the Spleeter model like
    /// <see cref="StemsModel" />, since that is the fall back. Added in ABI 12.10.
    /// </para>
    /// </summary>
    public const string StemsSource = "{stemsSource}";

    /// <summary>
    /// <see cref="StemsSource" /> for a graph that only cares about the vocals:
    /// RoFormer's <c>preset=vocal_full:cpu_fallback=0</c>, two models instead
    /// of four, and the accompaniment as the mixture minus the vocals. Half the
    /// memory, so it runs on cards that cannot hold all four models, and lyric
    /// line times come out the same. Spleeter otherwise, with the same fall
    /// back. Added in ABI 12.10.
    /// </summary>
    public const string StemsVocalSource = "{stemsVocalSource}";

    /// <summary>The whisper model's file name. Refused while the model is not installed. Added in ABI 12.6.</summary>
    public const string WhisperModel = "{whisperModel}";

    /// <summary>The silero VAD model's file name, for whisper's <c>vad_model</c>. Refused while the model is not installed. Added in ABI 12.6.</summary>
    public const string VadModel = "{vadModel}";

    /// <summary>
    /// The lyricsalign model's file name, for the <c>lyricsalign</c> filter's
    /// <c>model</c> option: the CTC model that places a known text on the
    /// audio. Refused while the model is not installed, or while the server's
    /// ffmpeg is older than the release that has the filter. The model is
    /// large and only fetched once a graph names this token, so the first
    /// such graph is refused and the server starts the download. Added in
    /// ABI 12.8.
    /// </summary>
    public const string AlignModel = "{alignModel}";

    /// <summary>
    /// A file in the calling plugin's own data folder, by relative path. The
    /// host resolves it to an absolute path escaped for both filtergraph
    /// levels, so a Windows drive colon is written as <c>\:</c>. A path that
    /// is absolute, contains <c>..</c>, or leaves the folder is refused. Added
    /// in ABI 12.6.
    /// </summary>
    public static string PluginFile(string relativePath) => "{pluginFile:" + relativePath + "}";
}
