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

using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using NoMercy.Database;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.PostProcess;
using NoMercy.Encoder.Profiles;
using NoMercy.Encoder.Startup;

namespace NoMercy.Tests.Encoder.Integration;

/// <summary>
/// Command-generator oracle: proves that the full JSON → deserialize →
/// IEncoder.EncodeAsync path emits commands ffmpeg actually accepts.
///
/// Two axes:
///   A — builtin-preset round-trip: every profile in BuiltinPresets.All() is
///       serialized (as the DB seeder stores it) then deserialized (as the
///       controller reconstructs it) and driven through EncodeAsync.
///   B — hand-written user JSON: JSON strings written the way a human types
///       them exercise the deserialization contract. The EncoderProfileService
///       calls (EncoderProfileService.cs) use bare JsonConvert.DeserializeObject
///       with NO JsonSerializerSettings. Under Newtonsoft's defaults this
///       accepts BOTH integer enum values ("Container":0) AND enum member names
///       ("Container":"Mkv") — so a user who hand-writes string enum names, the
///       way the API's StringEnumConverter serialises them, round-trips cleanly.
///       Test B drives integer-enum JSON through a real encode; B2 pins the
///       string-enum-name binding so a regression in it fails loudly.
///
/// Skip logic: a case is skipped only when the required encoder binary is absent
/// from this ffmpeg build — an environment gap, not a code bug.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RealEncode")]
public class JsonProfileCommandOracleTests : IAsyncLifetime
{
    private string _testDir = string.Empty;
    private string _inputFile = string.Empty;
    private string _surroundInputFile = string.Empty;
    private string _subtitleInputFile = string.Empty;
    private string _audioOnlyInputFile = string.Empty;
    private string _opusInputFile = string.Empty;
    private ServiceProvider _serviceProvider = null!;
    private string? _ffmpegPath;
    private string? _ffprobePath;
    private IReadOnlySet<string> _availableEncoders = new HashSet<string>();

    public async Task InitializeAsync()
    {
        _ffmpegPath = NoMercyFfmpegProbe.ResolveFfmpegPath() ?? "ffmpeg";
        _ffprobePath = NoMercyFfmpegProbe.ResolveFfprobePath(_ffmpegPath) ?? "ffprobe";

        _testDir = Path.Combine(
            Path.GetTempPath(),
            "nomercy-json-oracle-" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(_testDir);

        // 1-second stereo video+audio clip (8-bit H.264/yuv420p, AAC stereo).
        _inputFile = Path.Combine(_testDir, "src.mp4");
        int exit = await RunFfmpegAsync([
            "-y",
            "-f",
            "lavfi",
            "-i",
            "testsrc2=size=320x180:rate=25:duration=12",
            "-f",
            "lavfi",
            "-i",
            "sine=frequency=440:duration=12:sample_rate=48000",
            "-c:v",
            "libx264",
            "-preset",
            "ultrafast",
            "-pix_fmt",
            "yuv420p",
            "-c:a",
            "aac",
            _inputFile,
        ]);
        if (exit != 0)
            throw new InvalidOperationException("JSON oracle stereo clip generation failed.");

        // 5.1-channel surround source so downmix profiles have real surround input.
        _surroundInputFile = Path.Combine(_testDir, "src51.mkv");
        int surroundExit = await RunFfmpegAsync([
            "-y",
            "-f",
            "lavfi",
            "-i",
            "testsrc2=size=320x180:rate=25:duration=12",
            "-f",
            "lavfi",
            "-i",
            "sine=frequency=440:duration=12:sample_rate=48000,aformat=channel_layouts=5.1",
            "-c:v",
            "libx264",
            "-preset",
            "ultrafast",
            "-pix_fmt",
            "yuv420p",
            "-c:a",
            "aac",
            _surroundInputFile,
        ]);
        if (surroundExit != 0)
            throw new InvalidOperationException("JSON oracle 5.1 clip generation failed.");

        // Source with an embedded SRT stream for subtitle profile tests.
        string srtPath = Path.Combine(_testDir, "sub.srt");
        await File.WriteAllTextAsync(
            srtPath,
            "1\n00:00:00,000 --> 00:00:12,000\nOracle subtitle line\n"
        );
        _subtitleInputFile = Path.Combine(_testDir, "src_sub.mkv");
        int subExit = await RunFfmpegAsync([
            "-y",
            "-f",
            "lavfi",
            "-i",
            "testsrc2=size=320x180:rate=25:duration=12",
            "-f",
            "lavfi",
            "-i",
            "sine=frequency=440:duration=12:sample_rate=48000",
            "-i",
            srtPath,
            "-c:v",
            "libx264",
            "-preset",
            "ultrafast",
            "-pix_fmt",
            "yuv420p",
            "-c:a",
            "aac",
            "-c:s",
            "srt",
            _subtitleInputFile,
        ]);
        if (subExit != 0)
            throw new InvalidOperationException("JSON oracle subtitle clip generation failed.");

        // Audio-only source for FLAC/AAC/MP3 container profiles that have no video.
        _audioOnlyInputFile = Path.Combine(_testDir, "src_audio.flac");
        int audioExit = await RunFfmpegAsync([
            "-y",
            "-f",
            "lavfi",
            "-i",
            "sine=frequency=440:duration=12:sample_rate=48000",
            "-c:a",
            "flac",
            _audioOnlyInputFile,
        ]);
        if (audioExit != 0)
            throw new InvalidOperationException("JSON oracle audio-only clip generation failed.");

        _availableEncoders = await ProbeAvailableEncodersAsync();

        // H.264 video + Opus stereo audio — proves a Copy-policy audio output
        // carries a codec HlsFmp4 didn't support before this slice (Opus)
        // through a real stream-copy remux, not a re-encode.
        // Generating this fixture itself needs the "libopus" encoder; if this
        // ffmpeg build lacks it, leave _opusInputFile empty — the per-case
        // Skip.IfNot(_availableEncoders.Contains("libopus"), ...) checks already
        // skip every Opus test case instead of failing the whole fixture.
        if (_availableEncoders.Contains("libopus"))
        {
            _opusInputFile = Path.Combine(_testDir, "src_opus.mkv");
            int opusExit = await RunFfmpegAsync([
                "-y",
                "-f",
                "lavfi",
                "-i",
                "testsrc2=size=320x180:rate=25:duration=12",
                "-f",
                "lavfi",
                "-i",
                "sine=frequency=440:duration=12:sample_rate=48000",
                "-c:v",
                "libx264",
                "-preset",
                "ultrafast",
                "-pix_fmt",
                "yuv420p",
                "-c:a",
                "libopus",
                "-b:a",
                "96k",
                _opusInputFile,
            ]);
            if (opusExit != 0)
                throw new InvalidOperationException("JSON oracle Opus clip generation failed.");
        }

        ServiceCollection services = new();
        services.AddLogging();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        services.AddDbContextFactory<MediaContext>(o =>
            o.UseInMemoryDatabase($"json-oracle-media-{suffix}")
        );
        services.AddDbContextFactory<AppDbContext>(o =>
            o.UseInMemoryDatabase($"json-oracle-app-{suffix}")
        );
        services.AddNoMercyEncoder(opts =>
        {
            opts.FfmpegPathOverride = _ffmpegPath;
            opts.FfprobePathOverride = _ffprobePath;
        });
        services.AddSingleton<IHardwareDetector, NullHardwareDetector>();

        // IHostApplicationLifetime is normally added by HostBuilder; stub it
        // here so resolving HardwareInitializationService (which depends on
        // BenchmarkJobTracker) works without a full host.
        services.AddSingleton<IHostApplicationLifetime, TestHostLifetime>();

        _serviceProvider = services.BuildServiceProvider();

        HardwareInitializationService hwInit =
            _serviceProvider.GetRequiredService<HardwareInitializationService>();
        await hwInit.StartAsync(CancellationToken.None);
    }

    private sealed class TestHostLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted { get; } = CancellationToken.None;
        public CancellationToken ApplicationStopping { get; } = CancellationToken.None;
        public CancellationToken ApplicationStopped { get; } = CancellationToken.None;

        public void StopApplication() { }
    }

    public Task DisposeAsync()
    {
        _serviceProvider.Dispose();
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch
        {
            // best-effort
        }
        return Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // Test A — builtin preset round-trip
    // -------------------------------------------------------------------------

    /// <summary>
    /// Produces one test case per builtin preset: the preset is serialized to
    /// JSON (as the DB seeder writes it), then deserialized (as the controller
    /// reads it), then driven through IEncoder.EncodeAsync. This proves the
    /// full seeder → controller → command-generator round-trip for every preset
    /// a user inherits on fresh install.
    /// </summary>
    public static IEnumerable<object[]> BuiltinPresetCases()
    {
        foreach (EncodingProfile profile in BuiltinPresets.All())
            yield return [profile.Name, JsonConvert.SerializeObject(profile)];
    }

    [SkippableTheory]
    [MemberData(nameof(BuiltinPresetCases))]
    public async Task BuiltinPreset_SerializeDeserializeEncode_FfmpegAcceptsCommand(
        string presetName,
        string serializedJson
    )
    {
        EncodingProfile? deserialized = JsonConvert.DeserializeObject<EncodingProfile>(
            serializedJson
        );

        deserialized
            .Should()
            .NotBeNull(
                $"builtin preset '{presetName}' must survive a JsonConvert round-trip "
                    + "with no JsonSerializerSettings (the path used by EncoderProfileService)"
            );

        EncodingProfile profile = deserialized!;

        // Skip if the required video encoder is absent from this ffmpeg build.
        if (profile.Video is not null)
        {
            string videoEncoder = VideoEncoderName(profile.Video.Codec);
            if (!string.IsNullOrEmpty(videoEncoder))
                Skip.IfNot(
                    _availableEncoders.Contains(videoEncoder),
                    $"preset '{presetName}': video encoder '{videoEncoder}' absent from this ffmpeg build"
                );
        }

        // Skip if any required audio encoder is absent.
        foreach (AudioOutput audioOutput in profile.Audio)
        {
            string audioEncoder = AudioEncoderName(audioOutput.Codec);
            if (!string.IsNullOrEmpty(audioEncoder))
                Skip.IfNot(
                    _availableEncoders.Contains(audioEncoder),
                    $"preset '{presetName}': audio encoder '{audioEncoder}' absent from this ffmpeg build"
                );
        }

        // Pick the appropriate synthetic source for this profile.
        string inputPath = ChooseInputPath(profile);

        string outputDir = Path.Combine(
            _testDir,
            "builtin_" + SanitizeName(presetName) + "_" + Guid.NewGuid().ToString("N")[..4]
        );
        Directory.CreateDirectory(outputDir);

        EncodingResult result = await RunEncodeAsync(profile, inputPath, outputDir);

        result
            .Success.Should()
            .BeTrue(
                $"ffmpeg must accept the command generated from builtin preset '{presetName}' "
                    + $"after a bare JsonConvert round-trip. "
                    + $"Error: {result.Error?.Message} | stderr: {result.Error?.FfmpegStderr}"
            );
    }

    // -------------------------------------------------------------------------
    // Test B — hand-written user JSON (integer enums — the correct contract)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns (caseName, profileJson, inputFileKey) triples.
    /// inputFileKey: "stereo" | "surround" | "subtitle" | "audio-only"
    ///
    /// All enums are integers because bare JsonConvert.DeserializeObject uses
    /// Newtonsoft's default settings (no StringEnumConverter). String enum
    /// names such as "Container":"Mkv" would throw JsonSerializationException —
    /// which IS the real behaviour and IS a bug for a user who types strings.
    /// These cases document the WORKING integer contract.
    /// </summary>
    public static IEnumerable<object[]> HandWrittenJsonCases()
    {
        // Case 1 — MKV H.264 + AAC stereo (the contract spec example, integers)
        // Container.Mkv=0, StreamPolicy.Transcode=0, VideoCodecType.H264=0,
        // RateControlMode.Crf=0, CodecProfile.High=4, AudioCodecType.Aac=0,
        // SubtitlePolicy.Extract=0, SubtitleCodecType.WebVtt=0
        yield return
        [
            "MkvH264Aac",
            """
                {
                  "Id": "01ARZ3NDEKTSV4RRFFQ69G5FAV",
                  "Name": "Test MKV H264+AAC",
                  "Container": 0,
                  "Video": {
                    "Policy": 0,
                    "Codec": 0,
                    "Width": 320,
                    "Height": 180,
                    "RateControl": 0,
                    "Crf": 22,
                    "BitrateKbps": 0,
                    "MaxBitrateKbps": null,
                    "BufferSizeKbps": null,
                    "Preset": "ultrafast",
                    "CodecProfile": 4,
                    "Level": "4.0",
                    "Tune": null,
                    "BitDepth": 8,
                    "PixelFormat": "yuv420p",
                    "KeyframeIntervalSeconds": 4,
                    "ConvertHdrToSdr": false,
                    "SegmentNameTemplate": "video_{label}/video_{label}",
                    "PlaylistNameTemplate": "video_{label}/video_{label}",
                    "CustomArguments": null
                  },
                  "Audio": [
                    {
                      "Policy": 0,
                      "Codec": 0,
                      "BitrateKbps": 192,
                      "Channels": 2,
                      "SampleRateHz": 48000,
                      "AllowedLanguages": ["*"],
                      "DefaultLanguage": null,
                      "Loudness": null,
                      "Downmix": null,
                      "SegmentNameTemplate": "audio_{lang}_{codec}/audio_{lang}_{codec}",
                      "PlaylistNameTemplate": "audio_{lang}_{codec}/audio_{lang}_{codec}",
                      "CustomArguments": null
                    }
                  ],
                  "Subtitles": [],
                  "ThumbnailOutput": null,
                  "LadderConfig": null,
                  "HlsConfig": null,
                  "HlsDerivatives": null,
                  "DashConfig": null,
                  "DrmConfig": null,
                  "SegmentDurationSeconds": 6,
                  "EncodeMode": 0,
                  "AutoDetectCrop": false,
                  "SchemaVersion": 2,
                  "Description": "Hand-written MKV H.264 AAC stereo",
                  "IsBuiltin": false,
                  "HardwarePreference": 3,
                  "BitDepthPolicy": 0,
                  "HdrPolicy": 0,
                  "HdrOptions": null,
                  "ClientCompatibility": 15,
                  "SubtitleAcquisition": null,
                  "CustomArguments": null,
                  "Mezzanine": null
                }
                """,
            "stereo",
        ];

        // Case 2 — MKV H.265 10-bit with explicit Main10 CodecProfile
        // Container.Mkv=0, VideoCodecType.H265=1, CodecProfile.Main10=3,
        // AudioCodecType.Aac=0
        yield return
        [
            "MkvH265_10bit",
            """
                {
                  "Id": "01ARZ3NDEKTSV4RRFFQ69G5FAW",
                  "Name": "Test MKV H265 10-bit",
                  "Container": 0,
                  "Video": {
                    "Policy": 0,
                    "Codec": 1,
                    "Width": 320,
                    "Height": 180,
                    "RateControl": 0,
                    "Crf": 28,
                    "BitrateKbps": 0,
                    "MaxBitrateKbps": null,
                    "BufferSizeKbps": null,
                    "Preset": "ultrafast",
                    "CodecProfile": 3,
                    "Level": null,
                    "Tune": null,
                    "BitDepth": 10,
                    "PixelFormat": "yuv420p10le",
                    "KeyframeIntervalSeconds": 4,
                    "ConvertHdrToSdr": false,
                    "SegmentNameTemplate": "video_{label}/video_{label}",
                    "PlaylistNameTemplate": "video_{label}/video_{label}",
                    "CustomArguments": null
                  },
                  "Audio": [
                    {
                      "Policy": 0,
                      "Codec": 0,
                      "BitrateKbps": 192,
                      "Channels": 2,
                      "SampleRateHz": 48000,
                      "AllowedLanguages": ["*"],
                      "DefaultLanguage": null,
                      "Loudness": null,
                      "Downmix": null,
                      "SegmentNameTemplate": "audio_{lang}_{codec}/audio_{lang}_{codec}",
                      "PlaylistNameTemplate": "audio_{lang}_{codec}/audio_{lang}_{codec}",
                      "CustomArguments": null
                    }
                  ],
                  "Subtitles": [],
                  "ThumbnailOutput": null,
                  "LadderConfig": null,
                  "HlsConfig": null,
                  "HlsDerivatives": null,
                  "DashConfig": null,
                  "DrmConfig": null,
                  "SegmentDurationSeconds": 6,
                  "EncodeMode": 0,
                  "AutoDetectCrop": false,
                  "SchemaVersion": 2,
                  "Description": "Hand-written MKV H.265 10-bit",
                  "IsBuiltin": false,
                  "HardwarePreference": 3,
                  "BitDepthPolicy": 0,
                  "HdrPolicy": 0,
                  "HdrOptions": null,
                  "ClientCompatibility": 15,
                  "SubtitleAcquisition": null,
                  "CustomArguments": null,
                  "Mezzanine": null
                }
                """,
            "stereo",
        ];

        // Case 3 — MP4 H.264 + AC3 (5.1 surround passthrough)
        // Container.Mp4=1, VideoCodecType.H264=0, AudioCodecType.Ac3=3
        yield return
        [
            "Mp4H264Ac3",
            """
                {
                  "Id": "01ARZ3NDEKTSV4RRFFQ69G5FAX",
                  "Name": "Test MP4 H264+AC3",
                  "Container": 1,
                  "Video": {
                    "Policy": 0,
                    "Codec": 0,
                    "Width": 320,
                    "Height": 180,
                    "RateControl": 0,
                    "Crf": 28,
                    "BitrateKbps": 0,
                    "MaxBitrateKbps": null,
                    "BufferSizeKbps": null,
                    "Preset": "ultrafast",
                    "CodecProfile": 0,
                    "Level": null,
                    "Tune": null,
                    "BitDepth": 8,
                    "PixelFormat": "yuv420p",
                    "KeyframeIntervalSeconds": 4,
                    "ConvertHdrToSdr": false,
                    "SegmentNameTemplate": "video_{label}/video_{label}",
                    "PlaylistNameTemplate": "video_{label}/video_{label}",
                    "CustomArguments": null
                  },
                  "Audio": [
                    {
                      "Policy": 0,
                      "Codec": 3,
                      "BitrateKbps": 384,
                      "Channels": 6,
                      "SampleRateHz": 48000,
                      "AllowedLanguages": ["*"],
                      "DefaultLanguage": null,
                      "Loudness": null,
                      "Downmix": null,
                      "SegmentNameTemplate": "audio_{lang}_{codec}/audio_{lang}_{codec}",
                      "PlaylistNameTemplate": "audio_{lang}_{codec}/audio_{lang}_{codec}",
                      "CustomArguments": null
                    }
                  ],
                  "Subtitles": [],
                  "ThumbnailOutput": null,
                  "LadderConfig": null,
                  "HlsConfig": null,
                  "HlsDerivatives": null,
                  "DashConfig": null,
                  "DrmConfig": null,
                  "SegmentDurationSeconds": 6,
                  "EncodeMode": 0,
                  "AutoDetectCrop": false,
                  "SchemaVersion": 2,
                  "Description": "Hand-written MP4 H.264 AC3 5.1",
                  "IsBuiltin": false,
                  "HardwarePreference": 3,
                  "BitDepthPolicy": 0,
                  "HdrPolicy": 0,
                  "HdrOptions": null,
                  "ClientCompatibility": 15,
                  "SubtitleAcquisition": null,
                  "CustomArguments": null,
                  "Mezzanine": null
                }
                """,
            "surround",
        ];

        // Case 4 — MKV H.264 + AAC + SRT subtitle extract
        // Container.Mkv=0, SubtitlePolicy.Extract=0, SubtitleCodecType.Srt=1
        yield return
        [
            "MkvH264AacSrt",
            """
                {
                  "Id": "01ARZ3NDEKTSV4RRFFQ69G5FAY",
                  "Name": "Test MKV H264+AAC+SRT",
                  "Container": 0,
                  "Video": {
                    "Policy": 0,
                    "Codec": 0,
                    "Width": 320,
                    "Height": 180,
                    "RateControl": 0,
                    "Crf": 28,
                    "BitrateKbps": 0,
                    "MaxBitrateKbps": null,
                    "BufferSizeKbps": null,
                    "Preset": "ultrafast",
                    "CodecProfile": 0,
                    "Level": null,
                    "Tune": null,
                    "BitDepth": 8,
                    "PixelFormat": "yuv420p",
                    "KeyframeIntervalSeconds": 4,
                    "ConvertHdrToSdr": false,
                    "SegmentNameTemplate": "video_{label}/video_{label}",
                    "PlaylistNameTemplate": "video_{label}/video_{label}",
                    "CustomArguments": null
                  },
                  "Audio": [
                    {
                      "Policy": 0,
                      "Codec": 0,
                      "BitrateKbps": 192,
                      "Channels": 2,
                      "SampleRateHz": 48000,
                      "AllowedLanguages": ["*"],
                      "DefaultLanguage": null,
                      "Loudness": null,
                      "Downmix": null,
                      "SegmentNameTemplate": "audio_{lang}_{codec}/audio_{lang}_{codec}",
                      "PlaylistNameTemplate": "audio_{lang}_{codec}/audio_{lang}_{codec}",
                      "CustomArguments": null
                    }
                  ],
                  "Subtitles": [
                    {
                      "Policy": 0,
                      "Codec": 1,
                      "AllowedLanguages": ["*"],
                      "IncludeForced": true,
                      "OcrLanguage": null,
                      "PlaylistNameTemplate": "subtitles/{filename}.{lang}.{type}",
                      "CustomArguments": null
                    }
                  ],
                  "ThumbnailOutput": null,
                  "LadderConfig": null,
                  "HlsConfig": null,
                  "HlsDerivatives": null,
                  "DashConfig": null,
                  "DrmConfig": null,
                  "SegmentDurationSeconds": 6,
                  "EncodeMode": 0,
                  "AutoDetectCrop": false,
                  "SchemaVersion": 2,
                  "Description": "Hand-written MKV H.264 AAC SRT subtitle extract",
                  "IsBuiltin": false,
                  "HardwarePreference": 3,
                  "BitDepthPolicy": 0,
                  "HdrPolicy": 0,
                  "HdrOptions": null,
                  "ClientCompatibility": 15,
                  "SubtitleAcquisition": null,
                  "CustomArguments": null,
                  "Mezzanine": null
                }
                """,
            "subtitle",
        ];

        // Case 5 — HLS fMP4 stream-COPY video + sprite thumbnails (the copy+sprite
        // fix under test): Container.HlsFmp4=3, StreamPolicy.Copy=1,
        // VideoCodecType.Copy=4, AudioCodecType.Aac=0. HlsDerivatives is left null
        // so it defaults to GenerateSpriteVtt=true — proving the two-command
        // remux (main copy playlist + separate sprite command) survives a real
        // ffmpeg run instead of aborting with exit -22.
        yield return
        [
            "HlsFmp4CopyVideoWithSprite",
            """
                {
                  "Id": "01ARZ3NDEKTSV4RRFFQ69G5FB0",
                  "Name": "Test HLS fMP4 copy+sprite",
                  "Container": 3,
                  "Video": {
                    "Policy": 1,
                    "Codec": 4,
                    "Width": null,
                    "Height": null,
                    "RateControl": 0,
                    "Crf": 0,
                    "BitrateKbps": 0,
                    "MaxBitrateKbps": null,
                    "BufferSizeKbps": null,
                    "Preset": null,
                    "CodecProfile": 0,
                    "Level": null,
                    "Tune": null,
                    "BitDepth": 8,
                    "PixelFormat": null,
                    "KeyframeIntervalSeconds": 4,
                    "ConvertHdrToSdr": false,
                    "SegmentNameTemplate": "video/video",
                    "PlaylistNameTemplate": "video/video",
                    "CustomArguments": null
                  },
                  "Audio": [
                    {
                      "Policy": 0,
                      "Codec": 0,
                      "BitrateKbps": 192,
                      "Channels": 2,
                      "SampleRateHz": 48000,
                      "AllowedLanguages": ["*"],
                      "DefaultLanguage": null,
                      "Loudness": null,
                      "Downmix": null,
                      "SegmentNameTemplate": "audio_{lang}_{codec}/audio_{lang}_{codec}",
                      "PlaylistNameTemplate": "audio_{lang}_{codec}/audio_{lang}_{codec}",
                      "CustomArguments": null
                    }
                  ],
                  "Subtitles": [],
                  "ThumbnailOutput": null,
                  "LadderConfig": null,
                  "HlsConfig": null,
                  "HlsDerivatives": null,
                  "DashConfig": null,
                  "DrmConfig": null,
                  "SegmentDurationSeconds": 6,
                  "EncodeMode": 0,
                  "AutoDetectCrop": false,
                  "SchemaVersion": 2,
                  "Description": "Hand-written HLS fMP4 copy+sprite",
                  "IsBuiltin": false,
                  "HardwarePreference": 3,
                  "BitDepthPolicy": 0,
                  "HdrPolicy": 0,
                  "HdrOptions": null,
                  "ClientCompatibility": 15,
                  "SubtitleAcquisition": null,
                  "CustomArguments": null,
                  "Mezzanine": null
                }
                """,
            "stereo",
        ];

        // Case 6 — HLS fMP4 stream-COPY video + stream-COPY Opus audio (the
        // audio smart-copy slice under test): Container.HlsFmp4=3,
        // StreamPolicy.Copy=1, VideoCodecType.Copy=4, AudioCodecType.Copy=9.
        // Opus was not in HlsFmp4's ContainerCompatibility matrix before this
        // slice — proves the fork muxes a copied Opus track into fMP4 HLS
        // through a real ffmpeg run, with both streams mapped directly
        // ("-c:v copy" / "-c:a copy", no filtergraph — a bracket-labeled map
        // would force filter_complex, which Copy-policy output plans never
        // route through, see PlanStage's MapLabel comment).
        yield return
        [
            "HlsFmp4CopyVideoCopyOpusAudio",
            """
                {
                  "Id": "01ARZ3NDEKTSV4RRFFQ69G5FB1",
                  "Name": "Test HLS fMP4 copy video + copy Opus audio",
                  "Container": 3,
                  "Video": {
                    "Policy": 1,
                    "Codec": 4,
                    "Width": null,
                    "Height": null,
                    "RateControl": 0,
                    "Crf": 0,
                    "BitrateKbps": 0,
                    "MaxBitrateKbps": null,
                    "BufferSizeKbps": null,
                    "Preset": null,
                    "CodecProfile": 0,
                    "Level": null,
                    "Tune": null,
                    "BitDepth": 8,
                    "PixelFormat": null,
                    "KeyframeIntervalSeconds": 4,
                    "ConvertHdrToSdr": false,
                    "SegmentNameTemplate": "video/video",
                    "PlaylistNameTemplate": "video/video",
                    "CustomArguments": null
                  },
                  "Audio": [
                    {
                      "Policy": 1,
                      "Codec": 9,
                      "BitrateKbps": 0,
                      "Channels": 0,
                      "SampleRateHz": 0,
                      "AllowedLanguages": [],
                      "DefaultLanguage": null,
                      "Loudness": null,
                      "Downmix": null,
                      "SegmentNameTemplate": "audio_{lang}_{codec}/audio_{lang}_{codec}",
                      "PlaylistNameTemplate": "audio_{lang}_{codec}/audio_{lang}_{codec}",
                      "CustomArguments": null
                    }
                  ],
                  "Subtitles": [],
                  "ThumbnailOutput": null,
                  "LadderConfig": null,
                  "HlsConfig": null,
                  "HlsDerivatives": {
                    "GenerateSpriteVtt": false,
                    "GenerateThumbnailTrack": false,
                    "GenerateFontsJson": false
                  },
                  "DashConfig": null,
                  "DrmConfig": null,
                  "SegmentDurationSeconds": 6,
                  "EncodeMode": 0,
                  "AutoDetectCrop": false,
                  "SchemaVersion": 2,
                  "Description": "Hand-written HLS fMP4 copy video + copy Opus audio",
                  "IsBuiltin": false,
                  "HardwarePreference": 3,
                  "BitDepthPolicy": 0,
                  "HdrPolicy": 0,
                  "HdrOptions": null,
                  "ClientCompatibility": 15,
                  "SubtitleAcquisition": null,
                  "CustomArguments": null,
                  "Mezzanine": null
                }
                """,
            "opus",
        ];
    }

    [SkippableTheory]
    [MemberData(nameof(HandWrittenJsonCases))]
    public async Task HandWrittenJson_IntegerEnums_DeserializesAndFfmpegAcceptsCommand(
        string caseName,
        string profileJson,
        string inputFileKey
    )
    {
        // Bare deserialize — same code path as EncoderProfileService lines 159 / 292.
        // No JsonSerializerSettings, so Newtonsoft default settings apply.
        EncodingProfile? profile = JsonConvert.DeserializeObject<EncodingProfile>(profileJson);

        profile
            .Should()
            .NotBeNull(
                $"hand-written JSON case '{caseName}' must deserialize cleanly under "
                    + "Newtonsoft default settings (integer enums, PascalCase fields)"
            );

        if (profile!.Video is not null)
        {
            string videoEncoder = VideoEncoderName(profile.Video.Codec);
            if (!string.IsNullOrEmpty(videoEncoder))
                Skip.IfNot(
                    _availableEncoders.Contains(videoEncoder),
                    $"case '{caseName}': video encoder '{videoEncoder}' absent from this ffmpeg build"
                );
        }

        foreach (AudioOutput audioOutput in profile.Audio)
        {
            string audioEncoder = AudioEncoderName(audioOutput.Codec);
            if (!string.IsNullOrEmpty(audioEncoder))
                Skip.IfNot(
                    _availableEncoders.Contains(audioEncoder),
                    $"case '{caseName}': audio encoder '{audioEncoder}' absent from this ffmpeg build"
                );
        }

        // The "opus" fixture is itself only generated when libopus is available
        // (see InitializeAsync); a Copy-policy audio output has no audioEncoder
        // to gate on above, so this case needs its own skip when the fixture
        // never got made.
        if (inputFileKey == "opus")
            Skip.If(
                string.IsNullOrEmpty(_opusInputFile),
                $"case '{caseName}': Opus source fixture unavailable, libopus absent from this ffmpeg build"
            );

        string inputPath = inputFileKey switch
        {
            "surround" => _surroundInputFile,
            "subtitle" => _subtitleInputFile,
            "audio-only" => _audioOnlyInputFile,
            "opus" => _opusInputFile,
            _ => _inputFile,
        };

        string outputDir = Path.Combine(
            _testDir,
            "handwritten_" + SanitizeName(caseName) + "_" + Guid.NewGuid().ToString("N")[..4]
        );
        Directory.CreateDirectory(outputDir);

        EncodingResult result = await RunEncodeAsync(profile, inputPath, outputDir);

        result
            .Success.Should()
            .BeTrue(
                $"ffmpeg must accept the command generated from hand-written JSON case '{caseName}'. "
                    + $"Error: {result.Error?.Message} | stderr: {result.Error?.FfmpegStderr}"
            );

        // The copy+sprite case has its sprite generated by a SEPARATE ffmpeg
        // command (see BuildStage.AllVideoOutputsAreCopy) that runs as a
        // post-processing step — ExecuteStage treats post-process command
        // failures as non-fatal, so `result.Success` alone would stay true even
        // if that second command silently failed to produce anything. Assert
        // the actual sprite + VTT files landed on disk so this oracle proves
        // the two-command remux against real ffmpeg, not just that the main
        // copy playlist encoded.
        if (caseName == "HlsFmp4CopyVideoWithSprite")
        {
            // Found by pattern rather than by a spelled-out name: the height in
            // that name follows the source's aspect, so pinning the literal ties
            // this oracle to one test clip's shape. The width is asserted
            // separately, because that is the part the profile decides.
            string[] sheets = Directory.GetFiles(outputDir, "thumbs_*.webp");
            sheets
                .Should()
                .ContainSingle(
                    "the separate sprite command must produce exactly one sheet for a copy-video HLS plan"
                );

            string spritePath = sheets[0];
            string vttPath = Path.ChangeExtension(spritePath, ".vtt");

            SpriteSheet
                .ReadTileWidth(Path.GetFileName(spritePath))
                .Should()
                .Be(
                    SpriteSheet.MinimumWidth,
                    "the sheet must be rendered at the profile's tile width"
                );
            File.Exists(vttPath)
                .Should()
                .BeTrue($"the separate sprite command must produce {vttPath} alongside the sprite");
            new FileInfo(spritePath)
                .Length.Should()
                .BeGreaterThan(0, "the sprite sheet must contain real image data");
            new FileInfo(vttPath)
                .Length.Should()
                .BeGreaterThan(0, "the VTT cue file must not be empty");
        }

        // A successful exit alone doesn't prove "-c:v copy" / "-c:a copy" were
        // actually used instead of a silent re-encode — ffprobe the produced
        // fMP4 segments and pin the codec identity: h264 in, h264 out, opus in,
        // opus out. A transcode to some other codec (or a filtergraph mapping
        // failure ffmpeg tolerated) would flip these and fail loudly here.
        if (caseName == "HlsFmp4CopyVideoCopyOpusAudio")
        {
            // fMP4 HLS init segments (named "init.mp4" — see HlsOutputStrategy's
            // "-hls_fmp4_init_filename") carry the full moov/stsd box, unlike a
            // bare numbered .m4s media fragment which ffprobe can't reliably
            // identify standalone.
            string[] initSegments = Directory.GetFiles(
                outputDir,
                "init.mp4",
                SearchOption.AllDirectories
            );

            // Match against the immediate parent directory name only — the
            // outputDir prefix itself contains both "Video" and "Audio"
            // substrings (from the case name), so matching the full path
            // would find every init.mp4 under either filter.
            string[] videoTargets =
            [
                .. initSegments.Where(p =>
                    Path.GetFileName(Path.GetDirectoryName(p))!
                        .Contains("video", StringComparison.OrdinalIgnoreCase)
                ),
            ];
            string[] audioTargets =
            [
                .. initSegments.Where(p =>
                    Path.GetFileName(Path.GetDirectoryName(p))!
                        .Contains("audio", StringComparison.OrdinalIgnoreCase)
                ),
            ];

            videoTargets.Should().NotBeEmpty("the copy-video output must write segment files");
            audioTargets.Should().NotBeEmpty("the copy-audio output must write segment files");

            string[] videoCodecs = await ProbeCodecNamesAsync(videoTargets[0]);
            string[] audioCodecs = await ProbeCodecNamesAsync(audioTargets[0]);

            videoCodecs
                .Should()
                .Contain("h264", "-c:v copy must preserve the source codec, never re-encode it");
            audioCodecs
                .Should()
                .Contain(
                    "opus",
                    "-c:a copy must preserve the source Opus codec, never re-encode it to aac/eac3"
                );
        }
    }

    // -------------------------------------------------------------------------
    // Test B2 — string enum names deserialize (the real, user-friendly contract)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Proves the bare <see cref="JsonConvert.DeserializeObject{T}(string)"/> call
    /// used by EncoderProfileService (no JsonSerializerSettings) ACCEPTS enum
    /// values written as their string names — Newtonsoft's default behaviour maps
    /// "Mkv" to <see cref="Container.Mkv"/>, "H264" to
    /// <see cref="NoMercy.Encoder.Codecs.VideoCodecType.H264"/>, and so on. A user
    /// who hand-writes "container":"Mkv" (as the dashboard serializes it, since
    /// the API registers a StringEnumConverter) round-trips correctly. This test
    /// pins that guarantee: if it starts failing, the enum-name binding a user
    /// relies on has regressed.
    /// </summary>
    [Fact]
    public void HandWrittenJson_StringEnumNames_DeserializeToCorrectEnumMembers()
    {
        const string JsonWithStringEnums = """
            {
              "Id": "01ARZ3NDEKTSV4RRFFQ69G5FAZ",
              "Name": "String enum test",
              "Container": "Mkv",
              "Video": {
                "Policy": "Transcode",
                "Codec": "H264",
                "Width": 320,
                "Height": 180,
                "RateControl": "Crf",
                "Crf": 22,
                "BitrateKbps": 0,
                "Preset": "ultrafast",
                "CodecProfile": "High",
                "BitDepth": 8,
                "KeyframeIntervalSeconds": 4,
                "ConvertHdrToSdr": false,
                "SegmentNameTemplate": "v",
                "PlaylistNameTemplate": "v"
              },
              "Audio": [],
              "Subtitles": []
            }
            """;

        EncodingProfile? profile = JsonConvert.DeserializeObject<EncodingProfile>(
            JsonWithStringEnums
        );

        profile.Should().NotBeNull("Newtonsoft accepts enum member names by default");
        profile!.Container.Should().Be(Container.Mkv, "\"Mkv\" maps to Container.Mkv");
        profile
            .Video!.Codec.Should()
            .Be(NoMercy.Encoder.Codecs.VideoCodecType.H264, "\"H264\" maps to VideoCodecType.H264");
        profile
            .Video.CodecProfile.Should()
            .Be(CodecProfile.High, "\"High\" maps to CodecProfile.High");
        profile
            .Video.RateControl.Should()
            .Be(RateControlMode.Crf, "\"Crf\" maps to RateControlMode.Crf");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task<EncodingResult> RunEncodeAsync(
        EncodingProfile profile,
        string inputPath,
        string outputDir
    )
    {
        EncodingRequest request = new(
            InputPath: inputPath,
            OutputDirectory: outputDir,
            Profile: profile
        );
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        IEncoder encoder = _serviceProvider.GetRequiredService<IEncoder>();
        return await encoder.EncodeAsync(request, progress: null, cts.Token);
    }

    /// <summary>
    /// Chooses the most appropriate synthetic source for a profile:
    /// audio-only for profiles with no video, surround for profiles that
    /// have a 5.1 audio output, subtitle source when the profile has subtitle
    /// outputs, and the standard stereo clip otherwise.
    /// </summary>
    private string ChooseInputPath(EncodingProfile profile)
    {
        if (profile.Video is null)
            return _audioOnlyInputFile;

        if (profile.Subtitles.Length > 0)
            return _subtitleInputFile;

        bool hasSurroundAudio = profile.Audio.Any(a => a.Channels > 2);
        if (hasSurroundAudio)
            return _surroundInputFile;

        return _inputFile;
    }

    private static string VideoEncoderName(NoMercy.Encoder.Codecs.VideoCodecType codec) =>
        codec switch
        {
            NoMercy.Encoder.Codecs.VideoCodecType.H264 => "libx264",
            NoMercy.Encoder.Codecs.VideoCodecType.H265 => "libx265",
            NoMercy.Encoder.Codecs.VideoCodecType.Av1 => "libsvtav1",
            NoMercy.Encoder.Codecs.VideoCodecType.Vp9 => "libvpx-vp9",
            NoMercy.Encoder.Codecs.VideoCodecType.Copy => string.Empty,
            _ => string.Empty,
        };

    private static string AudioEncoderName(NoMercy.Encoder.Codecs.AudioCodecType codec) =>
        codec switch
        {
            NoMercy.Encoder.Codecs.AudioCodecType.Aac => "aac",
            NoMercy.Encoder.Codecs.AudioCodecType.Flac => "flac",
            NoMercy.Encoder.Codecs.AudioCodecType.Opus => "libopus",
            NoMercy.Encoder.Codecs.AudioCodecType.Ac3 => "ac3",
            NoMercy.Encoder.Codecs.AudioCodecType.Eac3 => "eac3",
            NoMercy.Encoder.Codecs.AudioCodecType.Mp3 => "libmp3lame",
            NoMercy.Encoder.Codecs.AudioCodecType.Vorbis => "libvorbis",
            NoMercy.Encoder.Codecs.AudioCodecType.TrueHd => "truehd",
            NoMercy.Encoder.Codecs.AudioCodecType.Dts => "dca",
            NoMercy.Encoder.Codecs.AudioCodecType.Copy => string.Empty,
            _ => string.Empty,
        };

    /// <summary>
    /// Replaces characters that are not valid in directory names with underscores
    /// so output directories can be created cleanly for each test case.
    /// </summary>
    private static string SanitizeName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name, @"[^A-Za-z0-9_\-]", "_");

    private async Task<IReadOnlySet<string>> ProbeAvailableEncodersAsync()
    {
        HashSet<string> encoders = new(StringComparer.Ordinal);
        ProcessStartInfo psi = new()
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-encoders");

        using Process process = Process.Start(psi)!;
        string stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        foreach (string line in stdout.Split('\n'))
        {
            string trimmed = line.Trim();
            string[] parts = trimmed.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );
            if (
                parts is [{ Length: 6 }, _, ..]
                && parts[0].All(c => c is 'V' or 'A' or 'S' or 'F' or 'X' or 'B' or 'D' or '.')
            )
                encoders.Add(parts[1]);
        }
        return encoders;
    }

    private async Task<int> RunFfmpegAsync(string[] args)
    {
        ProcessStartInfo psi = new()
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        using Process process = Process.Start(psi)!;
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        await stderr;
        await stdout;
        return process.ExitCode;
    }

    /// <summary>
    /// Returns the <c>codec_name</c> of every stream in <paramref name="mediaPath"/>
    /// via ffprobe, so a test can pin the real codec identity of a produced
    /// artifact instead of trusting the command-builder's intent alone.
    /// </summary>
    private async Task<string[]> ProbeCodecNamesAsync(string mediaPath)
    {
        ProcessStartInfo psi = new()
        {
            FileName = _ffprobePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("stream=codec_name");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
        psi.ArgumentList.Add(mediaPath);

        using Process process = Process.Start(psi)!;
        string stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        return
        [
            .. stdout.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            ),
        ];
    }
}
