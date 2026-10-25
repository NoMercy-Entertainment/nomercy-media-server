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

using System.Text.RegularExpressions;
using NoMercy.NmSystem.Information;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Data.Plugins;

/// <summary>
/// Replaces the <see cref="PluginFilterGraphTokens" /> in a plugin's filter
/// text with the files they stand for. The model tokens become bare file
/// names, because ffmpeg runs with <see cref="AppFiles.FfmpegFolder" /> as its
/// working directory. A plugin file is outside that folder, so it becomes an
/// absolute path, escaped for both filtergraph levels, and only ever a path
/// inside the plugin's own data folder.
/// </summary>
internal static partial class PluginGraphTokens
{
    /// <summary>The stemsplit model file's own name.</summary>
    internal static string StemsModelFileName => AppFiles.StemsplitModel + ".gguf";

    /// <summary>The whisper model file's own name.</summary>
    internal static string WhisperModelFileName => AppFiles.WhisperModel + ".bin";

    /// <summary>The silero VAD model file's own name.</summary>
    internal static string VadModelFileName => AppFiles.VadModel + ".bin";

    /// <summary>Whether the text names the whisper model, so the host can check it is installed before ffmpeg reads the whole input.</summary>
    internal static bool UsesWhisperModel(string text) =>
        text.Contains(PluginFilterGraphTokens.WhisperModel, StringComparison.Ordinal);

    /// <summary>Whether the text names the VAD model.</summary>
    internal static bool UsesVadModel(string text) =>
        text.Contains(PluginFilterGraphTokens.VadModel, StringComparison.Ordinal);

    /// <summary>
    /// The text with every token replaced, or the reason one could not be.
    /// <paramref name="dataFolderPath" /> is the plugin's own folder; null
    /// when the host does not know it, which refuses every plugin-file token.
    /// </summary>
    internal static ResolvedGraphText Resolve(string text, string? dataFolderPath)
    {
        string resolved = text.Replace(
                PluginFilterGraphTokens.StemsModel,
                StemsModelFileName,
                StringComparison.Ordinal
            )
            .Replace(
                PluginFilterGraphTokens.WhisperModel,
                WhisperModelFileName,
                StringComparison.Ordinal
            )
            .Replace(PluginFilterGraphTokens.VadModel, VadModelFileName, StringComparison.Ordinal);

        string? refusal = null;
        resolved = PluginFileToken()
            .Replace(
                resolved,
                match =>
                {
                    if (refusal is not null)
                        return match.Value;

                    ResolvedGraphText file = ResolvePluginFile(
                        match.Groups[1].Value,
                        dataFolderPath
                    );
                    refusal = file.Refusal;
                    return file.Text;
                }
            );

        return refusal is null ? new(resolved, null) : new(text, refusal);
    }

    private static ResolvedGraphText ResolvePluginFile(string relativePath, string? dataFolderPath)
    {
        if (dataFolderPath is null)
            return new(
                string.Empty,
                "a plugin file token needs the plugin's data folder, which this host did not provide"
            );

        if (string.IsNullOrWhiteSpace(relativePath))
            return new(string.Empty, "a plugin file token names no file");

        // Checked the same way on every platform: a Windows drive letter is
        // not rooted to a Linux host, but it is still not a relative path.
        if (
            Path.IsPathRooted(relativePath)
            || relativePath.StartsWith('/')
            || relativePath.StartsWith('\\')
            || HasDriveLetter(relativePath)
        )
            return new(
                string.Empty,
                $"plugin file '{relativePath}' is an absolute path; only a path inside the plugin's data folder is allowed"
            );

        if (relativePath.Split('/', '\\').Any(segment => segment == ".."))
            return new(
                string.Empty,
                $"plugin file '{relativePath}' leaves the plugin's data folder"
            );

        string root = Path.GetFullPath(dataFolderPath);
        string full = Path.GetFullPath(Path.Combine(root, relativePath));
        string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            return new(
                string.Empty,
                $"plugin file '{relativePath}' leaves the plugin's data folder"
            );

        return new(EscapeForFilterGraph(full), null);
    }

    /// <summary>
    /// A filter option value is unescaped twice: once as the option, once as
    /// part of the graph. A colon ends an option at the first level, so it is
    /// written as <c>\\:</c>, which the first pass turns into <c>\:</c> and the
    /// second into <c>:</c>. Backslashes become forward slashes first, which
    /// every platform's ffmpeg opens, so they never need escaping at all.
    /// </summary>
    private static bool HasDriveLetter(string path) =>
        path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';

    internal static string EscapeForFilterGraph(string path) =>
        path.Replace('\\', '/').Replace(":", "\\\\:", StringComparison.Ordinal);

    [GeneratedRegex(@"\{pluginFile:([^}]*)\}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PluginFileToken();
}
