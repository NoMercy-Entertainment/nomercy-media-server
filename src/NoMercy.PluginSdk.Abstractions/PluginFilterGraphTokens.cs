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
    /// <summary>The stemsplit model's file name.</summary>
    public const string StemsModel = "{stemsModel}";

    /// <summary>The whisper model's file name. Refused while the model is not installed. Added in ABI 12.6.</summary>
    public const string WhisperModel = "{whisperModel}";

    /// <summary>The silero VAD model's file name, for whisper's <c>vad_model</c>. Refused while the model is not installed. Added in ABI 12.6.</summary>
    public const string VadModel = "{vadModel}";

    /// <summary>
    /// A file in the calling plugin's own data folder, by relative path. The
    /// host resolves it to an absolute path escaped for both filtergraph
    /// levels, so a Windows drive colon is written as <c>\:</c>. A path that
    /// is absolute, contains <c>..</c>, or leaves the folder is refused. Added
    /// in ABI 12.6.
    /// </summary>
    public static string PluginFile(string relativePath) => "{pluginFile:" + relativePath + "}";
}
