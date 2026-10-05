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
using NoMercy.Encoder.Infrastructure;
using NoMercy.MediaProcessing.Fingerprinting;
using NoMercy.Providers.AcoustId;
using NoMercy.Storage;

namespace NoMercy.Tests.MediaProcessing.Fingerprinting;

/// <summary>
/// The AcoustID lookup needs chromaprint's base64 fingerprint plus a duration.
/// This asked ffmpeg for <c>-fp_format compressed</c> — raw binary with no labels —
/// then regex-matched for <c>FINGERPRINT=</c> and <c>DURATION=</c>, which are
/// fpcalc's output format, not the muxer's. The match could never succeed, so every
/// track logged "produced no FINGERPRINT" followed by a screenful of binary and no
/// untagged album could ever be identified (verified live: 145 mp3s, 0 tracks).
/// </summary>
[Trait("Category", "Unit")]
public sealed class FfmpegChromaprintFingerprinterTests
{
    private const string FfmpegPath = "/bin/ffmpeg";
    private const string FfprobePath = "/bin/ffprobe";

    // A real fingerprint prefix produced by the fork ffmpeg on a library track.
    private const string RealFingerprint =
        "AQADh4nEJdUi4cTX4_IFNB66LDh-HNUDHx9E48QDUTrCRD9-PMnhVl9w4TjpTHjWHneIG0dj7ZgOhJSSw79wHMcXQUd-HDtOPMOPH9ZH9MXl4seZxvCHG8cV";

    private static (FfmpegChromaprintFingerprinter Fingerprinter, List<string[]> Calls) Build(
        Func<string, ProcessResult> respond
    )
    {
        List<string[]> calls = [];

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
            .Returns(
                (string exe, string[] args, string? _, CancellationToken _) =>
                {
                    calls.Add(args);
                    return Task.FromResult(respond(exe));
                }
            );

        Mock<IStorage> storage = new();
        storage
            .Setup(s => s.AcquireLocalPath(It.IsAny<string>()))
            .Returns((string p) => new LocalPathLease(p));

        EncoderOptions options = new()
        {
            FfmpegPathOverride = FfmpegPath,
            FfprobePathOverride = FfprobePath,
        };

        return (
            new(
                options,
                runner.Object,
                storage.Object,
                NullLogger<FfmpegChromaprintFingerprinter>.Instance
            ),
            calls
        );
    }

    private static ProcessResult Ok(string stdout) => new(0, stdout, string.Empty, TimeSpan.Zero);

    [Fact]
    public async Task FingerprintAsync_AsksFfmpegForBase64_NotTheUnparseableCompressedForm()
    {
        (FfmpegChromaprintFingerprinter fingerprinter, List<string[]> calls) = Build(exe =>
            exe == FfmpegPath ? Ok(RealFingerprint) : Ok("114.415782")
        );

        await fingerprinter.FingerprintAsync("/media/track.mp3", CancellationToken.None);

        string[] ffmpegArgs = calls[0];
        int formatIndex = Array.IndexOf(ffmpegArgs, "-fp_format");

        Assert.True(formatIndex >= 0, "the chromaprint muxer must be given an explicit format");
        Assert.Equal("base64", ffmpegArgs[formatIndex + 1]);
    }

    [Fact]
    public async Task FingerprintAsync_ReturnsTheBase64PayloadAndProbedDuration()
    {
        (FfmpegChromaprintFingerprinter fingerprinter, _) = Build(exe =>
            exe == FfmpegPath ? Ok(RealFingerprint + "\n") : Ok("114.415782\n")
        );

        AudioFingerprint? result = await fingerprinter.FingerprintAsync(
            "/media/track.mp3",
            CancellationToken.None
        );

        Assert.NotNull(result);
        Assert.Equal(RealFingerprint, result!.Fingerprint);
        Assert.Equal(114, result.DurationSeconds);
    }

    /// <summary>
    /// The exact live failure: the muxer's compressed form is binary, so it must be
    /// rejected rather than posted to AcoustID as if it were a fingerprint.
    /// </summary>
    [Fact]
    public async Task FingerprintAsync_ReturnsNull_WhenOutputIsBinaryRatherThanBase64()
    {
        (FfmpegChromaprintFingerprinter fingerprinter, _) = Build(exe =>
            exe == FfmpegPath ? Ok("�%�\"�4�,8~") : Ok("114.4")
        );

        Assert.Null(
            await fingerprinter.FingerprintAsync("/media/track.mp3", CancellationToken.None)
        );
    }

    /// <summary>
    /// AcoustID matches on fingerprint AND duration, so a zero duration returns no
    /// results — a failed probe is a failed fingerprint, not a half-usable one.
    /// </summary>
    [Fact]
    public async Task FingerprintAsync_ReturnsNull_WhenDurationCannotBeProbed()
    {
        (FfmpegChromaprintFingerprinter fingerprinter, _) = Build(exe =>
            exe == FfmpegPath ? Ok(RealFingerprint) : new(1, string.Empty, "boom", TimeSpan.Zero)
        );

        Assert.Null(
            await fingerprinter.FingerprintAsync("/media/track.mp3", CancellationToken.None)
        );
    }

    [Fact]
    public async Task FingerprintAsync_ProbesDurationWithFfprobe()
    {
        (FfmpegChromaprintFingerprinter fingerprinter, List<string[]> calls) = Build(exe =>
            exe == FfmpegPath ? Ok(RealFingerprint) : Ok("200.0")
        );

        await fingerprinter.FingerprintAsync("/media/track.mp3", CancellationToken.None);

        Assert.Equal(2, calls.Count);
        Assert.Contains("format=duration", calls[1]);
    }
}
