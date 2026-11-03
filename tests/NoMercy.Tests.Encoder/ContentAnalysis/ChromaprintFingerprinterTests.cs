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
using NoMercy.Encoder.ContentAnalysis.Fingerprinting;
using NoMercy.Encoder.Infrastructure;
using NoMercy.NmSystem.Monitoring;
using NoMercy.Tests.Encoder.Storage;

namespace NoMercy.Tests.Encoder.ContentAnalysis;

/// <summary>
/// The chromaprint-backed fingerprinter decodes ffmpeg stdout; these tests
/// pin that decoder against captured real muxer output and pin the
/// command-line shape. ffmpeg itself is not run here.
/// </summary>
public class ChromaprintFingerprinterTests
{
    private readonly EncoderOptions _options = new()
    {
        FfmpegPathOverride = "/usr/bin/ffmpeg",
        FfprobePathOverride = "/usr/bin/ffprobe",
    };

    // Real muxer output, captured once from ffmpeg 7 (chromaprint muxer) for the
    // same 6 s synthetic clip in both formats:
    //   ffmpeg -f lavfi -i "aevalsrc=0.6*sin(2*PI*(220+40*t)*t)+0.3*sin(2*PI*(660-25*t)*t)
    //     +0.2*sin(2*PI*(1500+300*sin(t))*t):s=11025:d=6" -ac 1 -ar 11025
    //     -f chromaprint -fp_format base64 -
    // and again with "-fp_format raw" (108 bytes = 27 little-endian uint32 hashes).
    private const string RealBase64Output =
        "AQAAG-K1JFMU3MKPP9hH1Ekeg8mSnYSq4ynOE1-coM7RLJEw5Ieu5Mi5Fl-c4-2KvRR6orn04Sf2HNpzF3539AUCLaHCE8IBEw5qQYESABlFCCICCSCAAA";

    private static readonly uint[] RealRawHashes =
    [
        3665379362u,
        3660120098u,
        3727360034u,
        3710517282u,
        1412038694u,
        1961507894u,
        816454414u,
        279583308u,
        279579276u,
        27990668u,
        36190860u,
        103298716u,
        103309740u,
        103309736u,
        372793769u,
        910975403u,
        390427050u,
        356744490u,
        97777962u,
        91351342u,
        90237246u,
        1298508046u,
        1307421966u,
        1303224586u,
        3384643864u,
        3650728248u,
        3650855208u,
    ];

    [Fact]
    public async Task FingerprintAsync_RealMuxerOutput_YieldsTheHashesTheMuxerComputed()
    {
        string[]? captured = null;
        Mock<IProcessRunner> runner = new();
        runner
            .Setup(r =>
                r.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<string, string[], string?, CancellationToken>(
                (_, args, _, _) => captured = args
            )
            .ReturnsAsync(
                new ProcessResult(0, RealBase64Output, "", TimeSpan.FromMilliseconds(500))
            );

        ChromaprintFingerprinter fp = new(
            _options,
            runner.Object,
            TestStorageFactory.CreateLocal(),
            NullLogger<ChromaprintFingerprinter>.Instance,
            new MediaActivityMonitor()
        );

        AudioFingerprint result = await fp.FingerprintAsync(
            "/media/ep1.mkv",
            null,
            CancellationToken.None
        );

        captured
            .Should()
            .ContainInConsecutiveOrder(
                ["-fp_format", "base64"],
                "stdout is read as text, so the muxer must be asked for a text format"
            );
        result
            .Hashes.Should()
            .Equal(
                RealRawHashes,
                "the hashes must be the ones the muxer computed, not digit runs scraped from its output"
            );
    }

    [Fact]
    public void DecodeBase64_RealMuxerOutput_ReturnsTheRawHashes()
    {
        ChromaprintFingerprintDecoder.DecodeBase64(RealBase64Output).Should().Equal(RealRawHashes);
    }

    [Fact]
    public void DecodeBase64_TrailingNewline_IsIgnored()
    {
        ChromaprintFingerprintDecoder
            .DecodeBase64(RealBase64Output + "\n")
            .Should()
            .Equal(RealRawHashes);
    }

    [Fact]
    public void DecodeBase64_HeaderWithZeroHashes_ReturnsEmptyArray()
    {
        // Algorithm 1, count 0 — what the muxer writes for audio too short to hash.
        ChromaprintFingerprintDecoder.DecodeBase64("AQAAAA").Should().BeEmpty();
    }

    [Fact]
    public void DecodeBase64_Truncated_ReturnsEmptyArray()
    {
        ChromaprintFingerprintDecoder.DecodeBase64(RealBase64Output[..40]).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t")]
    [InlineData("1,2,3")]
    [InlineData("not base64 at all!")]
    public void DecodeBase64_NotAFingerprint_ReturnsEmptyArray(string stdout)
    {
        ChromaprintFingerprintDecoder.DecodeBase64(stdout).Should().BeEmpty();
    }

    [Fact]
    public async Task FingerprintAsync_AppliesWindowArgs()
    {
        string[]? captured = null;
        Mock<IProcessRunner> runner = new();
        runner
            .Setup(r =>
                r.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<string, string[], string?, CancellationToken>(
                (_, args, _, _) => captured = args
            )
            .ReturnsAsync(
                new ProcessResult(0, RealBase64Output, "", TimeSpan.FromMilliseconds(500))
            );

        ChromaprintFingerprinter fp = new(
            _options,
            runner.Object,
            TestStorageFactory.CreateLocal(),
            NullLogger<ChromaprintFingerprinter>.Instance,
            new MediaActivityMonitor()
        );

        FingerprintWindow window = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(300));
        AudioFingerprint result = await fp.FingerprintAsync(
            "/media/ep1.mkv",
            window,
            CancellationToken.None
        );

        captured.Should().NotBeNull();
        captured!.Should().Contain("-ss");
        captured.Should().Contain("10.000");
        captured.Should().Contain("-t");
        captured.Should().Contain("300.000");
        captured.Should().Contain("-f");
        captured.Should().Contain("chromaprint");
        result.Hashes.Should().Equal(RealRawHashes);
        result.StartTime.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task FingerprintAsync_NegativeWindowStart_UsesSseofNotSs()
    {
        // A negative Start (e.g. the outro window, -4min) means "relative to
        // the end of file" — -ss only accepts a non-negative offset from the
        // start, so ffmpeg either ignores it or errors. The end-relative case
        // must use -sseof, which accepts the same negative value.
        string[]? captured = null;
        Mock<IProcessRunner> runner = new();
        runner
            .Setup(r =>
                r.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<string, string[], string?, CancellationToken>(
                (_, args, _, _) => captured = args
            )
            .ReturnsAsync(
                new ProcessResult(0, RealBase64Output, "", TimeSpan.FromMilliseconds(500))
            );

        ChromaprintFingerprinter fp = new(
            _options,
            runner.Object,
            TestStorageFactory.CreateLocal(),
            NullLogger<ChromaprintFingerprinter>.Instance,
            new MediaActivityMonitor()
        );

        FingerprintWindow window = new(TimeSpan.FromMinutes(-4), TimeSpan.FromMinutes(4));
        await fp.FingerprintAsync("/media/ep1.mkv", window, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Should().NotContain("-ss");
        captured.Should().ContainInConsecutiveOrder(["-sseof", "-240.000"]);
        captured.Should().ContainInConsecutiveOrder(["-t", "240.000"]);
    }

    [Fact]
    public async Task FingerprintAsync_FailedExec_ReturnsEmptyPrint()
    {
        Mock<IProcessRunner> runner = new();
        runner
            .Setup(r =>
                r.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new ProcessResult(1, "", "ffmpeg barfed", TimeSpan.Zero));

        ChromaprintFingerprinter fp = new(
            _options,
            runner.Object,
            TestStorageFactory.CreateLocal(),
            NullLogger<ChromaprintFingerprinter>.Instance,
            new MediaActivityMonitor()
        );

        AudioFingerprint result = await fp.FingerprintAsync(
            "/media/ep1.mkv",
            null,
            CancellationToken.None
        );

        result.Hashes.Should().BeEmpty();
        result.StartTime.Should().Be(TimeSpan.Zero);
    }
}
