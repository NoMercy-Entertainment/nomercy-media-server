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

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Errors;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Encoder.Startup;
using NoMercy.Storage;

namespace NoMercy.Tests.Encoder.Startup;

public class FfmpegCapabilityProbeTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static FfmpegCapabilityProbe BuildProbe(
        Mock<IFfmpegCapabilities> caps,
        Mock<IProcessRunner> runner,
        Mock<IStorage> storage,
        EncoderOptions? options = null
    ) =>
        new(
            caps.Object,
            runner.Object,
            storage.Object,
            options ?? new EncoderOptions(),
            NullLogger<FfmpegCapabilityProbe>.Instance
        );

    /// <summary>
    /// Configures <paramref name="runner"/> to return <paramref name="stdout"/>
    /// for the given <paramref name="firstArg"/> (first element of the args array).
    /// </summary>
    private static void SetupMuxerOutput(Mock<IProcessRunner> runner, string stdout)
    {
        runner
            .Setup(r =>
                r.RunAsync(
                    It.IsAny<string>(),
                    It.Is<string[]>(a =>
                        a.Length == 2 && a[0] == "-hide_banner" && a[1] == "-muxers"
                    ),
                    null,
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new ProcessResult(0, stdout, "", TimeSpan.Zero));
    }

    private static void SetupFpcalc(Mock<IProcessRunner> runner, int exitCode)
    {
        runner
            .Setup(r =>
                r.RunAsync(
                    It.IsAny<string>(),
                    It.Is<string[]>(a => a.Length == 1 && a[0] == "--version"),
                    null,
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new ProcessResult(exitCode, "", "", TimeSpan.Zero));
    }

    private static Mock<IFfmpegCapabilities> CapsWithProtocol(bool bluray, bool dvdread)
    {
        Mock<IFfmpegCapabilities> caps = new();
        caps.Setup(c => c.AvailableEncoders).Returns(new HashSet<string> { "libx264" });
        caps.Setup(c => c.AvailableDecoders).Returns(new HashSet<string>());
        caps.Setup(c => c.AvailableFilters).Returns(new HashSet<string>());
        caps.Setup(c => c.AvailableProtocols).Returns(new HashSet<string>());
        caps.Setup(c => c.HasProtocol("bluray")).Returns(bluray);
        caps.Setup(c => c.HasProtocol("dvdread")).Returns(dvdread);
        // ffmpeg ≥ 6.1 routes libdvdread through the `dvdvideo` demuxer instead
        // of the dvdread:// protocol — the probe checks demuxer presence.
        caps.Setup(c => c.HasDemuxer("dvdvideo")).Returns(dvdread);
        caps.Setup(c => c.HasFilter(It.IsAny<string>())).Returns(false);
        return caps;
    }

    private static Mock<IFfmpegCapabilities> CapsWithFilter(string filter)
    {
        Mock<IFfmpegCapabilities> caps = new();
        caps.Setup(c => c.AvailableEncoders).Returns(new HashSet<string> { "libx264" });
        caps.Setup(c => c.AvailableDecoders).Returns(new HashSet<string>());
        caps.Setup(c => c.AvailableFilters).Returns(new HashSet<string>());
        caps.Setup(c => c.AvailableProtocols).Returns(new HashSet<string>());
        caps.Setup(c => c.HasProtocol(It.IsAny<string>())).Returns(false);
        // Only the named filter is present; everything else is absent.
        caps.Setup(c => c.HasFilter(filter)).Returns(true);
        caps.Setup(c => c.HasFilter(It.Is<string>(f => f != filter))).Returns(false);
        return caps;
    }

    private static string AllMuxersOutput()
    {
        // Minimal lines for every required muxer.
        return """
            Muxers:
             E hls              Apple HTTP Live Streaming
             E dash             DASH Muxer
             E mp4              MP4 (MPEG-4 Part 14)
             E matroska         Matroska
             E mp3              MP3 (MPEG audio layer 3)
             E flac             raw FLAC
             E ogg              Ogg
            """;
    }

    // -------------------------------------------------------------------------
    // BluRay protocol
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ProbeAsync_returns_BluRayProtocol_true_when_listed()
    {
        Mock<IFfmpegCapabilities> caps = CapsWithProtocol(bluray: true, dvdread: false);
        Mock<IProcessRunner> runner = new();
        Mock<IStorage> storage = new();
        SetupMuxerOutput(runner, AllMuxersOutput());
        SetupFpcalc(runner, 0);
        storage.Setup(s => s.Exists(It.IsAny<string>())).Returns(false);

        FfmpegCapabilityProbe probe = BuildProbe(caps, runner, storage);
        await probe.ProbeAsync();

        probe.GetCachedReport()!.BluRayProtocol.Should().BeTrue();
    }

    [Fact]
    public async Task ProbeAsync_returns_BluRayProtocol_false_when_missing()
    {
        Mock<IFfmpegCapabilities> caps = CapsWithProtocol(bluray: false, dvdread: false);
        Mock<IProcessRunner> runner = new();
        Mock<IStorage> storage = new();
        SetupMuxerOutput(runner, AllMuxersOutput());
        SetupFpcalc(runner, 0);
        storage.Setup(s => s.Exists(It.IsAny<string>())).Returns(false);

        FfmpegCapabilityProbe probe = BuildProbe(caps, runner, storage);
        await probe.ProbeAsync();

        probe.GetCachedReport()!.BluRayProtocol.Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // fpcalc
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ProbeAsync_emits_CapabilityFpcalcMissing_rule_on_nonzero_exit()
    {
        Mock<IFfmpegCapabilities> caps = CapsWithProtocol(false, false);
        Mock<IProcessRunner> runner = new();
        Mock<IStorage> storage = new();
        SetupMuxerOutput(runner, AllMuxersOutput());
        SetupFpcalc(runner, exitCode: 1);
        storage.Setup(s => s.Exists(It.IsAny<string>())).Returns(false);

        FfmpegCapabilityProbe probe = BuildProbe(caps, runner, storage);
        await probe.ProbeAsync();

        CapabilityReport report = probe.GetCachedReport()!;
        report.FpcalcPresent.Should().BeFalse();
        report.Issues.Should().Contain(r => r.Id == EncoderRuleId.CapabilityFpcalcMissing);
    }

    // -------------------------------------------------------------------------
    // Whisper model
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ProbeAsync_emits_CapabilityWhisperMissing_rule_when_model_absent()
    {
        Mock<IFfmpegCapabilities> caps = CapsWithProtocol(false, false);
        Mock<IProcessRunner> runner = new();
        Mock<IStorage> storage = new();
        SetupMuxerOutput(runner, AllMuxersOutput());
        SetupFpcalc(runner, 0);

        EncoderOptions options = new() { WhisperModelPath = "/models/ggml-large.bin" };
        storage.Setup(s => s.Exists("/models/ggml-large.bin")).Returns(false);
        storage
            .Setup(s => s.Exists(It.Is<string>(p => p != "/models/ggml-large.bin")))
            .Returns(false);

        FfmpegCapabilityProbe probe = BuildProbe(caps, runner, storage, options);
        await probe.ProbeAsync();

        CapabilityReport report = probe.GetCachedReport()!;
        report.WhisperModelPresent.Should().BeFalse();
        report.Issues.Should().Contain(r => r.Id == EncoderRuleId.CapabilityWhisperMissing);
    }

    // -------------------------------------------------------------------------
    // Stemsplit model
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ProbeAsync_emits_CapabilityStemsplitMissing_rule_when_model_absent()
    {
        Mock<IFfmpegCapabilities> caps = CapsWithProtocol(false, false);
        Mock<IProcessRunner> runner = new();
        Mock<IStorage> storage = new();
        SetupMuxerOutput(runner, AllMuxersOutput());
        SetupFpcalc(runner, 0);

        EncoderOptions options = new() { StemsplitModelPath = "/models/spleeter-2stems-f16.gguf" };
        storage.Setup(s => s.Exists("/models/spleeter-2stems-f16.gguf")).Returns(false);
        storage
            .Setup(s => s.Exists(It.Is<string>(p => p != "/models/spleeter-2stems-f16.gguf")))
            .Returns(false);

        FfmpegCapabilityProbe probe = BuildProbe(caps, runner, storage, options);
        await probe.ProbeAsync();

        CapabilityReport report = probe.GetCachedReport()!;
        report.StemsplitModelPresent.Should().BeFalse();
        report.Issues.Should().Contain(r => r.Id == EncoderRuleId.CapabilityStemsplitMissing);
    }

    [Fact]
    public async Task ProbeAsync_returns_StemsplitModelPresent_true_when_model_exists()
    {
        Mock<IFfmpegCapabilities> caps = CapsWithProtocol(false, false);
        Mock<IProcessRunner> runner = new();
        Mock<IStorage> storage = new();
        SetupMuxerOutput(runner, AllMuxersOutput());
        SetupFpcalc(runner, 0);

        EncoderOptions options = new() { StemsplitModelPath = "/models/spleeter-2stems-f16.gguf" };
        storage.Setup(s => s.Exists("/models/spleeter-2stems-f16.gguf")).Returns(true);
        storage
            .Setup(s => s.Exists(It.Is<string>(p => p != "/models/spleeter-2stems-f16.gguf")))
            .Returns(false);

        FfmpegCapabilityProbe probe = BuildProbe(caps, runner, storage, options);
        await probe.ProbeAsync();

        CapabilityReport report = probe.GetCachedReport()!;
        report.StemsplitModelPresent.Should().BeTrue();
        report.Issues.Should().NotContain(r => r.Id == EncoderRuleId.CapabilityStemsplitMissing);
    }

    // -------------------------------------------------------------------------
    // Tesseract traineddata
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ProbeAsync_emits_CapabilityTesseractModelMissing_when_eng_traineddata_absent()
    {
        Mock<IFfmpegCapabilities> caps = CapsWithProtocol(false, false);
        Mock<IProcessRunner> runner = new();
        Mock<IStorage> storage = new();
        SetupMuxerOutput(runner, AllMuxersOutput());
        SetupFpcalc(runner, 0);

        EncoderOptions options = new() { TesseractModelsDirectory = "/tessdata" };
        storage.Setup(s => s.Exists("/tessdata/eng.traineddata")).Returns(false);
        storage
            .Setup(s => s.Exists(It.Is<string>(p => p != "/tessdata/eng.traineddata")))
            .Returns(false);

        FfmpegCapabilityProbe probe = BuildProbe(caps, runner, storage, options);
        await probe.ProbeAsync();

        CapabilityReport report = probe.GetCachedReport()!;
        report.TesseractEngTraineddataPresent.Should().BeFalse();
        report.Issues.Should().Contain(r => r.Id == EncoderRuleId.CapabilityTesseractModelMissing);
    }

    // -------------------------------------------------------------------------
    // Missing filters
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ProbeAsync_emits_MissingFilters_for_libplacebo_when_absent()
    {
        // libplacebo absent — all other required filters also absent for simplicity.
        Mock<IFfmpegCapabilities> caps = CapsWithProtocol(false, false);
        // Override: libplacebo specifically absent.
        caps.Setup(c => c.HasFilter("libplacebo")).Returns(false);

        Mock<IProcessRunner> runner = new();
        Mock<IStorage> storage = new();
        SetupMuxerOutput(runner, AllMuxersOutput());
        SetupFpcalc(runner, 0);
        storage.Setup(s => s.Exists(It.IsAny<string>())).Returns(false);

        FfmpegCapabilityProbe probe = BuildProbe(caps, runner, storage);
        await probe.ProbeAsync();

        probe.GetCachedReport()!.MissingFilters.Should().Contain("libplacebo");
    }

    // -------------------------------------------------------------------------
    // Caching
    // -------------------------------------------------------------------------

    [Fact]
    public void GetCachedReport_returns_null_before_first_probe()
    {
        Mock<IFfmpegCapabilities> caps = CapsWithProtocol(false, false);
        Mock<IProcessRunner> runner = new();
        Mock<IStorage> storage = new();

        FfmpegCapabilityProbe probe = BuildProbe(caps, runner, storage);

        probe.GetCachedReport().Should().BeNull();
    }

    [Fact]
    public async Task GetCachedReport_returns_last_report_after_probe_completes()
    {
        Mock<IFfmpegCapabilities> caps = CapsWithProtocol(bluray: true, dvdread: true);
        Mock<IProcessRunner> runner = new();
        Mock<IStorage> storage = new();
        SetupMuxerOutput(runner, AllMuxersOutput());
        SetupFpcalc(runner, 0);
        storage.Setup(s => s.Exists(It.IsAny<string>())).Returns(false);

        FfmpegCapabilityProbe probe = BuildProbe(caps, runner, storage);

        probe.GetCachedReport().Should().BeNull("probe has not run yet");

        await probe.ProbeAsync();

        CapabilityReport? report = probe.GetCachedReport();
        report.Should().NotBeNull();
        report!.BluRayProtocol.Should().BeTrue();
        report.DvdReadProtocol.Should().BeTrue();
    }
}
