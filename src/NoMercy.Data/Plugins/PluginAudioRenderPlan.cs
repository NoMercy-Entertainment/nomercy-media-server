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
using NoMercy.Storage;

namespace NoMercy.Data.Plugins;

/// <summary>
/// A plugin's <see cref="PluginAudioRender" /> checked against everything the
/// host can know before ffmpeg starts: how many inputs and outputs, which
/// pads, which codec settings, and where each file would land.
/// <para>
/// Checked here and not left to ffmpeg, because ffmpeg reads the whole input
/// before it finds out an output cannot be written, and what it then prints
/// is a sentence about its own command line, which the plugin never wrote.
/// </para>
/// </summary>
/// <param name="Targets">The outputs, in the order the plugin gave them. Empty when refused.</param>
/// <param name="Refusal">Why the render cannot run, in words the plugin author can act on. Null when it can.</param>
internal sealed record PluginAudioRenderPlan(
    IReadOnlyList<PluginAudioRenderTarget> Targets,
    string? Refusal
)
{
    /// <summary>
    /// What every file a render is still writing starts with. No finished
    /// file has this name, so anything left under it is a run that was cut
    /// short and is safe to remove.
    /// </summary>
    internal const string TempPrefix = ".nomercy-render-";

    private const int MinOpusBitrateKbps = 6;
    private const int MaxOpusBitrateKbps = 510;

    /// <summary>The only rates libopus encodes at.</summary>
    private static readonly int[] OpusSampleRates = [8000, 12000, 16000, 24000, 48000];

    internal static PluginAudioRenderPlan Build(
        PluginAudioRender? render,
        string? dataFolderPath,
        IStorageDriver driver
    )
    {
        if (render is null)
            return Refused("the render is missing");

        string? refusal = CheckInputs(render.Inputs) ?? CheckGraph(render.GraphText);
        if (refusal is not null)
            return Refused(refusal);

        if (render.Outputs is not { Count: > 0 })
            return Refused("a render needs at least one output");

        if (render.Outputs.Count > PluginAudioRender.MaxOutputs)
            return Refused(
                $"a render writes at most {PluginAudioRender.MaxOutputs} outputs; this one names {render.Outputs.Count}"
            );

        // One batch id for the whole run, so the files of one run are
        // recognisable as belonging together while it is still writing them.
        Ulid batch = Ulid.NewUlid();
        List<PluginAudioRenderTarget> targets = [];

        for (int index = 0; index < render.Outputs.Count; index++)
        {
            PluginAudioOutput? output = render.Outputs[index];
            if (output is null)
                return Refused($"output {index} is missing");

            ResolvedPluginPath path = PluginGraphTokens.ResolvePluginPath(
                output.RelativePath,
                dataFolderPath,
                "an output path"
            );
            if (path.Refusal is not null)
                return Refused(path.Refusal);

            refusal =
                CheckLocation(output.RelativePath, path.FullPath, dataFolderPath!, driver, targets)
                ?? CheckPad(output, targets)
                ?? CheckEncoding(output);
            if (refusal is not null)
                return Refused(refusal);

            string folder = Path.GetDirectoryName(path.FullPath)!;
            targets.Add(
                new(
                    output.Pad,
                    output.RelativePath,
                    path.FullPath,
                    Path.Combine(folder, $"{TempPrefix}{batch}-{index}{Extension(output.Codec)}"),
                    output.Codec,
                    output.BitrateKbps,
                    output.SampleRate
                )
            );
        }

        return new(targets, null);
    }

    /// <summary>The extension ffmpeg picks each codec's container from.</summary>
    internal static string Extension(PluginAudioCodec codec) =>
        codec switch
        {
            PluginAudioCodec.Opus => ".opus",
            PluginAudioCodec.Flac => ".flac",
            _ => ".wav",
        };

    private static PluginAudioRenderPlan Refused(string reason) => new([], reason);

    private static string? CheckInputs(IReadOnlyList<PluginAudioSource>? inputs)
    {
        if (inputs is not { Count: > 0 })
            return "a render needs at least one input";

        if (inputs.Count > PluginAudioRender.MaxInputs)
            return $"a render reads at most {PluginAudioRender.MaxInputs} inputs; this one names {inputs.Count}";

        for (int index = 0; index < inputs.Count; index++)
        {
            PluginAudioSource? source = inputs[index];
            if (source?.Input is null)
                return $"input {index} names no source";

            if (source.Start is { } start && start < TimeSpan.Zero)
                return $"input {index} starts before the beginning of its file";

            if (source.Duration is { } duration && duration <= TimeSpan.Zero)
                return $"input {index} has a duration that is not above zero";
        }

        return null;
    }

    private static string? CheckGraph(string? graphText) =>
        string.IsNullOrWhiteSpace(graphText) ? "the graph is empty" : null;

    private static string? CheckLocation(
        string relativePath,
        string fullPath,
        string dataFolderPath,
        IStorageDriver driver,
        List<PluginAudioRenderTarget> accepted
    )
    {
        if (PluginGraphTokens.PassesThroughALink(dataFolderPath, fullPath, driver))
            return $"plugin file '{relativePath}' leaves the plugin's data folder";

        if (driver.DirectoryExists(fullPath))
            return $"output '{relativePath}' is a folder";

        // Case-insensitive on every platform: two names that differ only in
        // case are one file on Windows and macOS, and a plugin that means two
        // files does not name them that way.
        if (
            accepted.Any(target =>
                string.Equals(target.FinalPath, fullPath, StringComparison.OrdinalIgnoreCase)
            )
        )
            return $"output '{relativePath}' is named more than once";

        return null;
    }

    private static string? CheckPad(
        PluginAudioOutput output,
        List<PluginAudioRenderTarget> accepted
    )
    {
        if (string.IsNullOrWhiteSpace(output.Pad))
            return $"output '{output.RelativePath}' names no pad";

        if (output.Pad.Contains('[') || output.Pad.Contains(']'))
            return $"pad '{output.Pad}' is written without brackets";

        // ffmpeg hands each output pad to one consumer. Mapped twice, it
        // reports the second as a label that does not exist.
        if (accepted.Any(target => target.Pad == output.Pad))
            return $"pad '{output.Pad}' feeds more than one output";

        return null;
    }

    private static string? CheckEncoding(PluginAudioOutput output)
    {
        if (!Enum.IsDefined(output.Codec))
            return $"output '{output.RelativePath}' asks for a codec this host does not encode";

        bool opus = output.Codec == PluginAudioCodec.Opus;

        if (output.BitrateKbps is { } bitrate)
        {
            if (!opus)
                return $"output '{output.RelativePath}' sets a bitrate, which only Opus has";

            if (bitrate is < MinOpusBitrateKbps or > MaxOpusBitrateKbps)
                return $"output '{output.RelativePath}' asks for {bitrate} kbit/s; Opus takes {MinOpusBitrateKbps} to {MaxOpusBitrateKbps}";
        }

        if (output.SampleRate is { } sampleRate)
        {
            if (sampleRate <= 0)
                return $"output '{output.RelativePath}' asks for a sample rate of {sampleRate} Hz";

            if (opus && !OpusSampleRates.Contains(sampleRate))
                return $"output '{output.RelativePath}' asks for {sampleRate} Hz; Opus encodes at {string.Join(", ", OpusSampleRates)} Hz only";
        }

        return null;
    }
}
