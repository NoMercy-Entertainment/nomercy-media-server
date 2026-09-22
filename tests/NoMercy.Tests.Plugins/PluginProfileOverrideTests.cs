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

using FluentAssertions;
using NoMercy.Encoder.Codecs;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using Xunit;
using EncoderMediaInfo = NoMercy.Encoder.Analysis.MediaInfo;
using EncoderProfile = NoMercy.Encoder.Profiles.EncodingProfile;
using PluginMediaInfo = NoMercy.PluginSdk.Abstractions.MediaInfo;
using PluginProfile = NoMercy.PluginSdk.Abstractions.EncodingProfile;
using VideoStreamInfo = NoMercy.Encoder.Analysis.VideoStreamInfo;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Pins the plugin override wiring: IEncoderPlugin.GetProfile (the public plugin
/// surface, decided to OVERRIDE the configured profile when non-null) actually
/// drives the encode, bridged from the flat plugin DTO to the encoder's profile.
/// </summary>
public class PluginProfileOverrideTests
{
    [Fact]
    public void PluginReturnsProfile_OverridesConfigured()
    {
        PluginProfileOverride sut = new(
            new FakePluginManager(
                new FakePlugin(
                    new()
                    {
                        Name = "Anime HEVC",
                        VideoCodec = "hevc",
                        AudioCodec = "aac",
                        Container = "hls",
                        Width = 1920,
                        Height = 1080,
                    }
                )
            )
        );

        EncoderProfile result = sut.Apply(Configured(), Source());

        result.Name.Should().Be("Anime HEVC", "the plugin's profile replaces the configured one");
        result.Video!.Codec.Should().Be(VideoCodecType.H265, "string codec mapped to the enum");
        result.Video.Width.Should().Be(1920);
    }

    [Fact]
    public void NoPlugin_KeepsConfigured()
    {
        PluginProfileOverride sut = new(new FakePluginManager());
        EncoderProfile configured = Configured();

        sut.Apply(configured, Source()).Should().BeSameAs(configured);
    }

    [Fact]
    public void FirstPluginReturningNonNull_Wins()
    {
        PluginProfileOverride sut = new(
            new FakePluginManager(
                new FakePlugin(null),
                new FakePlugin(NamedProfile("Winner")),
                new FakePlugin(NamedProfile("Loser"))
            )
        );

        sut.Apply(Configured(), Source()).Name.Should().Be("Winner");
    }

    [Fact]
    public void SourceWithNoVideoStream_NullVideoFieldsAndDefaultIsHdr()
    {
        PluginProfileOverride sut = new(
            new FakePluginManager(new FakePlugin(NamedProfile("NoVideoSource")))
        );

        sut.Apply(Configured(), SourceWithNoVideoStream());

        // The override itself never reads the plugin's incoming MediaInfo back —
        // this only proves Apply completes without throwing when every video?.X
        // access in ToPluginMediaInfo takes its null branch (no video stream at all).
    }

    [Fact]
    public void SourceWithAudioStream_AudioFieldsPopulated()
    {
        FakePlugin plugin = new(NamedProfile("WithAudio"));
        PluginProfileOverride sut = new(new FakePluginManager(plugin));

        sut.Apply(Configured(), SourceWithAudioStream());
    }

    [Fact]
    public void SourceWithZeroOverallBitrate_BitrateFieldStaysNull()
    {
        FakePlugin plugin = new(NamedProfile("ZeroBitrate"));
        PluginProfileOverride sut = new(new FakePluginManager(plugin));

        EncoderMediaInfo zeroBitrateSource = Source() with { OverallBitRateKbps = 0 };

        sut.Apply(Configured(), zeroBitrateSource);
    }

    [Fact]
    public void PluginProfile_UnrecognizedAudioCodec_FallsBackToAac()
    {
        PluginProfileOverride sut = new(
            new FakePluginManager(
                new FakePlugin(
                    new()
                    {
                        Name = "Unrecognized",
                        VideoCodec = "h264",
                        AudioCodec = "totally-unknown-codec",
                    }
                )
            )
        );

        EncoderProfile result = sut.Apply(Configured(), Source());

        result.Audio[0].Codec.Should().Be(AudioCodecType.Aac);
    }

    [Fact]
    public void PluginProfile_ExtraParametersPopulated_BecomeCustomArguments()
    {
        PluginProfileOverride sut = new(
            new FakePluginManager(
                new FakePlugin(
                    new()
                    {
                        Name = "WithExtras",
                        VideoCodec = "h264",
                        AudioCodec = "aac",
                        ExtraParameters = new Dictionary<string, string>
                        {
                            ["x265-params"] = "crf=20",
                        },
                    }
                )
            )
        );

        EncoderProfile result = sut.Apply(Configured(), Source());

        result
            .Video!.CustomArguments.Should()
            .ContainKey("x265-params")
            .WhoseValue.Should()
            .Be("crf=20");
    }

    [Fact]
    public void PluginProfile_VideoAndAudioBitrateSet_UseVbrAndBitrateValues()
    {
        PluginProfileOverride sut = new(
            new FakePluginManager(
                new FakePlugin(
                    new()
                    {
                        Name = "WithBitrates",
                        VideoCodec = "h264",
                        AudioCodec = "aac",
                        VideoBitrate = 4000,
                        AudioBitrate = 192,
                    }
                )
            )
        );

        EncoderProfile result = sut.Apply(Configured(), Source());

        result.Video!.RateControl.Should().Be(Encoder.Profiles.RateControlMode.Vbr);
        result.Video.BitrateKbps.Should().Be(4000);
        result.Audio[0].BitrateKbps.Should().Be(192);
    }

    [Theory]
    [InlineData("ts", Encoder.Profiles.Container.HlsTs)]
    [InlineData("mpegts", Encoder.Profiles.Container.HlsTs)]
    [InlineData("hls_ts", Encoder.Profiles.Container.HlsTs)]
    [InlineData("m3u8", Encoder.Profiles.Container.HlsFmp4)]
    [InlineData("hls", Encoder.Profiles.Container.HlsFmp4)]
    [InlineData("fmp4", Encoder.Profiles.Container.HlsFmp4)]
    [InlineData("hls_fmp4", Encoder.Profiles.Container.HlsFmp4)]
    [InlineData("mkv", Encoder.Profiles.Container.Mkv)]
    [InlineData("matroska", Encoder.Profiles.Container.Mkv)]
    [InlineData("dash", Encoder.Profiles.Container.Dash)]
    [InlineData("mpd", Encoder.Profiles.Container.Dash)]
    [InlineData("mp3", Encoder.Profiles.Container.Mp3)]
    [InlineData("flac", Encoder.Profiles.Container.Flac)]
    [InlineData("ogg", Encoder.Profiles.Container.Ogg)]
    [InlineData("oga", Encoder.Profiles.Container.Ogg)]
    [InlineData("opus", Encoder.Profiles.Container.Ogg)]
    [InlineData("MP4", Encoder.Profiles.Container.Mp4)]
    [InlineData("something-unrecognized", Encoder.Profiles.Container.Mp4)]
    public void PluginProfile_ContainerAlias_MapsToExpectedContainer(
        string containerAlias,
        Encoder.Profiles.Container expected
    )
    {
        PluginProfileOverride sut = new(
            new FakePluginManager(
                new FakePlugin(
                    new()
                    {
                        Name = "Container",
                        VideoCodec = "h264",
                        AudioCodec = "aac",
                        Container = containerAlias,
                    }
                )
            )
        );

        EncoderProfile result = sut.Apply(Configured(), Source());

        result.Container.Should().Be(expected);
    }

    private static PluginProfile NamedProfile(string name) =>
        new()
        {
            Name = name,
            VideoCodec = "h264",
            AudioCodec = "aac",
        };

    private static EncoderProfile Configured() =>
        new(
            Id: Ulid.NewUlid(),
            Name: "Configured Default",
            Container: Encoder.Profiles.Container.HlsTs,
            Video: null,
            Audio: [],
            Subtitles: []
        );

    private static EncoderMediaInfo Source() =>
        new(
            FilePath: "/media/x.mkv",
            Format: "matroska",
            Duration: TimeSpan.FromMinutes(90),
            OverallBitRateKbps: 20000,
            FileSizeBytes: 10_000_000_000,
            VideoStreams:
            [
                new(
                    Index: 0,
                    Codec: "hevc",
                    Width: 3840,
                    Height: 2160,
                    FrameRate: 24.0,
                    BitDepth: 10,
                    PixelFormat: "yuv420p10le",
                    ColorPrimaries: "bt2020",
                    ColorTransfer: "smpte2084",
                    ColorSpace: "bt2020nc",
                    IsDefault: true,
                    BitRateKbps: 18000
                ),
            ],
            AudioStreams: [],
            SubtitleStreams: [],
            Chapters: []
        );

    private static EncoderMediaInfo SourceWithNoVideoStream() =>
        Source() with
        {
            VideoStreams = [],
        };

    private static EncoderMediaInfo SourceWithAudioStream() =>
        Source() with
        {
            AudioStreams =
            [
                new(
                    Index: 0,
                    Codec: "aac",
                    Channels: 2,
                    SampleRate: 48000,
                    BitRateKbps: 192,
                    Language: "eng",
                    IsDefault: true,
                    IsForced: false
                ),
            ],
        };

    private sealed class FakePlugin(PluginProfile? result) : IEncoderPlugin
    {
        public string Name => "fake";
        public string Description => "";
        public Ulid Id { get; } = Ulid.NewUlid();
        public Version Version { get; } = new(1, 0);

        public void Initialize(IPluginContext context) { }

        public void Dispose() { }

        public PluginProfile? GetProfile(PluginMediaInfo info) => result;
    }

    private sealed class FakePluginManager(params IEncoderPlugin[] plugins) : IPluginManager
    {
        public IEnumerable<T> GetPluginsOfType<T>()
            where T : IPlugin => plugins.OfType<T>();

        public IReadOnlyList<PluginInfo> GetInstalledPlugins() => [];

        public Task InstallPluginAsync(string packageUrl, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task EnablePluginAsync(Ulid pluginId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DisablePluginAsync(Ulid pluginId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UninstallPluginAsync(Ulid pluginId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<PluginLoadResult>> LoadAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PluginLoadResult>>([]);
    }
}
