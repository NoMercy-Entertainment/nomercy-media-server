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

using System.Globalization;
using NoMercy.Encoder.Profiles;

namespace NoMercy.Encoder.Commands;

public class FfmpegCommandBuilder
{
    private GlobalOptions _globalOptions = new();
    private Dictionary<string, string>? _globalExtraFlags;
    private readonly List<InputOptions> _inputs = [];
    private string? _filterComplex;
    private readonly List<OutputOptions> _outputs = [];

    public FfmpegCommandBuilder WithGlobalOptions(GlobalOptions options)
    {
        _globalOptions = options;
        return this;
    }

    // Profile-level CustomArguments injected as global ffmpeg options (before the
    // -i input). Null is a no-op so non-customized profiles emit unchanged commands.
    public FfmpegCommandBuilder WithGlobalExtraFlags(Dictionary<string, string>? flags)
    {
        _globalExtraFlags = flags;
        return this;
    }

    public FfmpegCommandBuilder AddInput(InputOptions input)
    {
        _inputs.Add(input);
        return this;
    }

    public FfmpegCommandBuilder WithFilterComplex(string filterGraph)
    {
        _filterComplex = filterGraph;
        return this;
    }

    public FfmpegCommandBuilder AddOutput(OutputOptions output)
    {
        _outputs.Add(output);
        return this;
    }

    /// <summary>
    /// Whether anything has been added for this command to write. ffmpeg refuses
    /// to run without one ("At least one output file must be specified"), so a
    /// caller that may end up with nothing to encode must check before building.
    /// </summary>
    public bool HasOutputs => _outputs.Count > 0;

    public FfmpegCommand Build(string ffmpegPath, string? workingDirectory = null)
    {
        List<string> args = [];

        // Global options
        if (_globalOptions.Overwrite)
            args.Add("-y");
        if (_globalOptions.HideBanner)
            args.Add("-hide_banner");
        if (_globalOptions.ProgressPipe)
        {
            args.Add("-progress");
            args.Add("pipe:1");
        }
        if (_globalOptions.Threads.HasValue)
        {
            args.Add("-threads");
            args.Add(_globalOptions.Threads.Value.ToString());
        }
        if (_globalOptions.ProbeSizeBytes.HasValue)
        {
            args.Add("-probesize");
            args.Add(_globalOptions.ProbeSizeBytes.Value.ToString());
        }
        if (_globalOptions.AnalyzeDurationUs.HasValue)
        {
            args.Add("-analyzeduration");
            args.Add(_globalOptions.AnalyzeDurationUs.Value.ToString());
        }

        // Profile-level custom args — global escape hatch, emitted before inputs.
        if (_globalExtraFlags is not null)
        {
            foreach (KeyValuePair<string, string> flag in _globalExtraFlags)
            {
                if (CustomArgumentGuard.ShouldDrop(flag.Key, flag.Value, false, "global flags"))
                    continue;

                args.Add(flag.Key);
                // An empty value marks a bare boolean flag (e.g. "-an") — emitting
                // it anyway adds a stray empty argv token ffmpeg treats as an
                // unmapped output URL.
                if (flag.Value.Length > 0)
                    args.Add(flag.Value);
            }
        }

        // Inputs
        foreach (InputOptions input in _inputs)
        {
            if (input.HwAccelDevice is not null)
            {
                args.Add("-hwaccel");
                args.Add(input.HwAccelDevice);
            }
            if (input.HwAccelOutputFormat is not null)
            {
                args.Add("-hwaccel_output_format");
                args.Add(input.HwAccelOutputFormat);
            }
            if (input.SeekTo.HasValue)
            {
                args.Add("-ss");
                args.Add(
                    input.SeekTo.Value.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)
                );
            }
            if (input.Duration.HasValue)
            {
                args.Add("-t");
                args.Add(
                    input.Duration.Value.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)
                );
            }
            args.Add("-i");
            args.Add(input.FilePath);
        }

        // Filter complex
        if (_filterComplex is not null)
        {
            args.Add("-filter_complex");
            args.Add(_filterComplex);
        }

        // Outputs
        foreach (OutputOptions output in _outputs)
        {
            foreach (string map in output.MapStreams ?? [])
            {
                args.Add("-map");
                args.Add(map);
            }
            if (output.VideoCodec is not null)
            {
                args.Add("-c:v");
                args.Add(output.VideoCodec);
            }
            if (output.AudioCodec is not null)
            {
                args.Add("-c:a");
                args.Add(output.AudioCodec);
            }
            if (output.SubtitleCodec is not null)
            {
                args.Add("-c:s");
                args.Add(output.SubtitleCodec);
            }
            if (output.Preset is not null)
            {
                args.Add("-preset");
                args.Add(output.Preset);
            }
            if (output.Profile is not null)
            {
                args.Add("-profile:v");
                args.Add(output.Profile);
            }
            if (output.Level is not null)
            {
                args.Add("-level");
                args.Add(output.Level);
            }
            if (output.PixelFormat is not null)
            {
                args.Add("-pix_fmt");
                args.Add(output.PixelFormat);
            }
            if (output.Crf.HasValue)
            {
                args.Add("-crf");
                args.Add(output.Crf.Value.ToString());
            }
            if (output.VideoBitrateKbps.HasValue)
            {
                args.Add("-b:v");
                args.Add($"{output.VideoBitrateKbps.Value}k");
            }
            if (output.AudioBitrateKbps.HasValue)
            {
                args.Add("-b:a");
                args.Add($"{output.AudioBitrateKbps.Value}k");
            }
            if (output.AudioChannels is not null)
            {
                args.Add("-ac");
                args.Add(output.AudioChannels);
            }
            if (output.AudioSampleRate.HasValue)
            {
                args.Add("-ar");
                args.Add(output.AudioSampleRate.Value.ToString());
            }
            if (output.KeyframeInterval.HasValue)
            {
                args.Add("-g");
                args.Add(output.KeyframeInterval.Value.ToString());
            }
            if (output.ExtraFlags is not null)
            {
                foreach (KeyValuePair<string, string> flag in output.ExtraFlags)
                {
                    if (CustomArgumentGuard.ShouldDrop(flag.Key, flag.Value, false, "output flags"))
                        continue;

                    args.Add(flag.Key);
                    // An empty value marks a bare boolean flag (e.g. "-an") — emitting
                    // it anyway adds a stray empty argv token ffmpeg treats as an
                    // unmapped output URL.
                    if (flag.Value.Length > 0)
                        args.Add(flag.Value);
                }
            }

            // Metadata must sit with the output it applies to, after its codec and
            // filter flags and before its filepath — ffmpeg binds an output option
            // to the URL that follows it.
            if (output.StripSourceMetadata)
            {
                args.Add("-map_metadata");
                args.Add("-1");
            }

            foreach (OutputStreamTag tag in output.StreamMetadata ?? [])
            {
                args.Add($"-metadata:{tag.StreamSpecifier}");
                // Unlike ExtraFlags, an empty value is meaningful here: "key=" tells
                // ffmpeg to drop the tag the source stream carried.
                args.Add($"{tag.Key}={tag.Value}");
            }

            args.Add(output.FilePath);
        }

        return new(ffmpegPath, args.ToArray(), workingDirectory);
    }
}
