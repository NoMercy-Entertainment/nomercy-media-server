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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Encoder.BuildingBlocks.Drm;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Infrastructure;
using NoMercy.NmSystem.Information;
using NoMercy.Storage;
using NoMercy.Tests.Encoder.Integration;

namespace NoMercy.Tests.Encoder.BuildingBlocks.Drm;

public class CencDrmProcessorTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static byte[] FakeKeyId() =>
        Enumerable.Range(0, 16).Select(i => (byte)(0x10 + i)).ToArray();

    private static byte[] FakeKey() =>
        Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray();

    private static CencKeyEntry SdEntry() => new("SD", FakeKeyId(), FakeKey());

    private static DrmConfig CencConfig(IReadOnlyList<CencKeyEntry>? keys = null) =>
        new(
            DrmMethod.Cenc,
            KeyUri: "https://license.example/widevine",
            CencKeys: keys ?? [SdEntry()]
        );

    private static IStorage MakeStorage() => Mock.Of<IStorage>();

    private static CencDrmProcessor BuildSut(
        string? packagerPath = "/fake/packager",
        IProcessRunner? runner = null
    )
    {
        EncoderOptions opts = new()
        {
            FfmpegPathOverride = "/fake/ffmpeg",
            ShakaPackagerPathOverride = packagerPath,
        };
        IProcessRunner processRunner =
            runner
            ?? Mock.Of<IProcessRunner>(r =>
                r.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                ) == Task.FromResult(new ProcessResult(0, "", "", TimeSpan.Zero))
            );
        return new(opts, processRunner, NullLogger<CencDrmProcessor>.Instance, MakeStorage());
    }

    // ── BuildPackagerArguments (pure, no I/O) ─────────────────────────────

    [Fact]
    public void BuildPackagerArguments_SingleKey_ContainsRawKeyFlag()
    {
        CencKeyEntry entry = SdEntry();
        CencStreamDescriptor descriptor = new(
            "/out/video.mp4",
            "video",
            "/out/video_enc.mp4",
            "SD"
        );

        List<string> args = CencDrmProcessor.BuildPackagerArguments(
            [descriptor],
            [entry],
            "/out/manifest.mpd"
        );

        args.Should().Contain("--enable_raw_key_encryption");
        args.Should().Contain("--mpd_output");
        args.Should().Contain("/out/manifest.mpd");
    }

    [Fact]
    public void BuildPackagerArguments_KeysFlag_HasCorrectHexFormat()
    {
        CencKeyEntry entry = new("HD", new byte[16], new byte[16]);
        CencStreamDescriptor descriptor = new("/in.mp4", "video", "/out.mp4", "HD");

        List<string> args = CencDrmProcessor.BuildPackagerArguments(
            [descriptor],
            [entry],
            "/manifest.mpd"
        );

        // --keys must be followed by the label:key_id:key triple
        int keysIndex = args.IndexOf("--keys");
        keysIndex.Should().BeGreaterThanOrEqualTo(0, "--keys flag must be present");

        string keySpec = args[keysIndex + 1];
        keySpec.Should().StartWith("label=HD:");
        keySpec.Should().Contain(":key_id=");
        keySpec.Should().Contain(":key=");
        // 16 zero bytes → 32 zero hex chars
        keySpec.Should().Contain("key_id=00000000000000000000000000000000");
        keySpec.Should().Contain(":key=00000000000000000000000000000000");
    }

    [Fact]
    public void BuildPackagerArguments_StreamSpec_ForwardSlashes()
    {
        CencStreamDescriptor descriptor = new(
            @"C:\out\video.mp4",
            "video",
            @"C:\out\video_enc.mp4",
            "SD"
        );

        List<string> args = CencDrmProcessor.BuildPackagerArguments(
            [descriptor],
            [SdEntry()],
            "/manifest.mpd"
        );

        string spec = args[0];
        spec.Should().NotContain("\\", "shaka-packager requires forward slashes");
        spec.Should().StartWith("in=C:/out/video.mp4");
    }

    [Fact]
    public void BuildPackagerArguments_MultipleKeys_EmitsOneKeysEntryEach()
    {
        List<CencKeyEntry> keys =
        [
            new("SD", FakeKeyId(), FakeKey()),
            new("HD", FakeKeyId(), FakeKey()),
        ];
        CencStreamDescriptor descriptor = new("/v.mp4", "video", "/v_enc.mp4", "SD");

        List<string> args = CencDrmProcessor.BuildPackagerArguments(
            [descriptor],
            keys,
            "/manifest.mpd"
        );

        int count = args.Count(a => a == "--keys");
        count.Should().Be(2, "one --keys flag per key entry");
    }

    [Fact]
    public async Task PackageAsync_LogsPackagerArgumentsWithoutTheContentKey()
    {
        string keyHex = Convert.ToHexString(FakeKey()).ToLowerInvariant();
        string keyIdHex = Convert.ToHexString(FakeKeyId()).ToLowerInvariant();
        CapturingLogger logger = new();
        EncoderOptions opts = new()
        {
            FfmpegPathOverride = "/fake/ffmpeg",
            ShakaPackagerPathOverride = "/fake/packager",
        };
        IProcessRunner runner = Mock.Of<IProcessRunner>(r =>
            r.RunAsync(
                It.IsAny<string>(),
                It.IsAny<string[]>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()
            ) == Task.FromResult(new ProcessResult(0, "", "", TimeSpan.Zero))
        );
        IStorage storage = Mock.Of<IStorage>(s => s.Exists(It.IsAny<string>()) == true);
        CencDrmProcessor sut = new(opts, runner, logger, storage);

        await sut.PackageAsync(
            "/out",
            [new("/out/v.mp4", "video", "/out/v_enc.mp4", "SD")],
            CencConfig(),
            "/out/manifest.mpd",
            CancellationToken.None
        );

        logger.Messages.Should().NotBeEmpty();
        logger.Messages.Should().AllSatisfy(message => message.Should().NotContain(keyHex));
        logger.Messages.Should().Contain(message => message.Contains($"key_id={keyIdHex}"));
    }

    private sealed class CapturingLogger : ILogger<CencDrmProcessor>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Messages.Add(formatter(state, exception));
    }

    // ── PrepareAsync — must be a no-op for CENC ───────────────────────────

    [Fact]
    public async Task PrepareAsync_Cenc_ReturnsEmptySentinel()
    {
        CencDrmProcessor sut = BuildSut();
        DrmConfig config = CencConfig();

        DrmArtifact artifact = await sut.PrepareAsync(
            "/tmp/drm-test",
            config,
            CancellationToken.None
        );

        artifact.KeyInfoFilePath.Should().BeEmpty("CENC pre-encode prep is a no-op");
        artifact.KeyFilePath.Should().BeEmpty();
        artifact.Key.Should().BeEmpty();
        artifact.Iv.Should().BeEmpty();
        artifact.KeyUri.Should().Be(config.KeyUri);
    }

    [Fact]
    public async Task PrepareAsync_WrongMethod_ThrowsArgumentException()
    {
        CencDrmProcessor sut = BuildSut();
        DrmConfig wrongConfig = new(DrmMethod.Aes128, KeyUri: "http://k");

        Func<Task> act = () =>
            sut.PrepareAsync("/tmp/drm-test", wrongConfig, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*CENC only*");
    }

    // ── PackageAsync error paths ───────────────────────────────────────────

    [Fact]
    public async Task PackageAsync_WithMissingPackager_ThrowsClearError()
    {
        // ShakaPackagerPathOverride points to a non-existent file;
        // FfmpegPathOverride sibling also won't exist → GetShakaPackagerPath() throws.
        EncoderOptions opts = new()
        {
            FfmpegPathOverride = "/nonexistent/ffmpeg",
            ShakaPackagerPathOverride = null,
        };
        CencDrmProcessor sut = new(
            opts,
            Mock.Of<IProcessRunner>(),
            NullLogger<CencDrmProcessor>.Instance,
            MakeStorage()
        );

        Func<Task> act = () =>
            sut.PackageAsync(
                "/tmp",
                [new("/v.mp4", "video", "/v_enc.mp4", "SD")],
                CencConfig(),
                "/manifest.mpd",
                CancellationToken.None
            );

        // GetShakaPackagerPath throws InvalidOperationException when nothing is found.
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*shaka-packager*");
    }

    [Fact]
    public async Task PackageAsync_NonZeroExitCode_ThrowsWithDetails()
    {
        Mock<IProcessRunner> runner = new();
        runner
            .Setup(r =>
                r.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new ProcessResult(1, "", "Fatal: bad key", TimeSpan.Zero));

        CencDrmProcessor sut = BuildSut(runner: runner.Object);

        Func<Task> act = () =>
            sut.PackageAsync(
                "/tmp",
                [new("/v.mp4", "video", "/v_enc.mp4", "SD")],
                CencConfig(),
                "/tmp/manifest.mpd",
                CancellationToken.None
            );

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*shaka-packager exited with code 1*");
    }

    [Fact]
    public async Task PackageAsync_NoCencKeys_ThrowsArgumentException()
    {
        CencDrmProcessor sut = BuildSut();
        DrmConfig noKeys = new(DrmMethod.Cenc, KeyUri: "https://lic.example", CencKeys: []);

        Func<Task> act = () =>
            sut.PackageAsync(
                "/tmp",
                [new("/v.mp4", "video", "/v_enc.mp4", "SD")],
                noKeys,
                "/tmp/manifest.mpd",
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*CencKeys*");
    }

    // ── CencStreamDescriptor ─────────────────────────────────────────────

    [Fact]
    public void CencStreamDescriptor_ToPackagerSpec_ReturnsCorrectFormat()
    {
        CencStreamDescriptor descriptor = new(
            "/out/video.mp4",
            "video",
            "/out/video_enc.mp4",
            "HD"
        );

        string spec = descriptor.ToPackagerSpec();

        spec.Should().Be("in=/out/video.mp4,stream=video,output=/out/video_enc.mp4,drm_label=HD");
    }

    // ── Integration: real shaka-packager end-to-end ─────────────────────────
    // Runs when the packager binary is resolvable — via SHAKA_PACKAGER_PATH or
    // the standard binaries location (AppFiles.ShakaPackagerPath, downloaded by
    // the server's binaries step alongside ffmpeg). Skips with a clear reason
    // only when neither the packager nor the fork ffmpeg is present.

    [SkippableFact]
    public async Task PackageAsync_WithRealPackager_ProducesMpd()
    {
        string? packagerPath =
            NoMercyFfmpegProbe.ResolveShakaPackagerPath()
            ?? (File.Exists(AppFiles.ShakaPackagerPath) ? AppFiles.ShakaPackagerPath : null);
        Skip.If(
            packagerPath is null,
            "shaka-packager binary not present — set SHAKA_PACKAGER_PATH or run the "
                + "server binaries step (downloads it alongside ffmpeg)."
        );

        string? ffmpegPath = ResolveFfmpegForFixture();
        Skip.If(ffmpegPath is null, "ffmpeg not resolvable for fixture generation.");

        string tmpDir = Path.Combine(Path.GetTempPath(), $"cenc-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            // 1) Generate a tiny fragmented mp4 the packager can ingest.
            string inputMp4 = Path.Combine(tmpDir, "input.mp4");
            await RunProcessAsync(
                ffmpegPath!,
                [
                    "-y",
                    "-f",
                    "lavfi",
                    "-i",
                    "testsrc2=size=320x180:rate=25:duration=2",
                    "-c:v",
                    "libx264",
                    "-preset",
                    "ultrafast",
                    "-pix_fmt",
                    "yuv420p",
                    inputMp4,
                ]
            );
            File.Exists(inputMp4).Should().BeTrue("ffmpeg must produce the input clip");

            // 2) Package it with real CENC raw-key encryption via the processor.
            //    The packager writes real files, so storage.Exists must reflect the
            //    real filesystem (not the always-false default mock).
            EncoderOptions opts = new() { ShakaPackagerPathOverride = packagerPath };
            IStorage storage = Mock.Of<IStorage>(s => s.Exists(It.IsAny<string>()) == false);
            Mock.Get(storage).Setup(s => s.Exists(It.IsAny<string>())).Returns<string>(File.Exists);
            CencDrmProcessor processor = new(
                opts,
                new ProcessRunner(NullLogger<ProcessRunner>.Instance),
                NullLogger<CencDrmProcessor>.Instance,
                storage
            );

            string manifestPath = Path.Combine(tmpDir, "manifest.mpd");
            CencStreamDescriptor descriptor = new(
                inputMp4,
                "video",
                Path.Combine(tmpDir, "video_enc.mp4"),
                "SD"
            );

            string resultManifest = await processor.PackageAsync(
                tmpDir,
                [descriptor],
                CencConfig(),
                manifestPath,
                CancellationToken.None
            );

            // 3) A real MPD with ContentProtection must have been produced.
            File.Exists(resultManifest).Should().BeTrue("packager must produce the MPD");
            string mpd = await File.ReadAllTextAsync(resultManifest);
            mpd.Should().Contain("<MPD", "output must be a DASH manifest");
            mpd.Should().Contain("ContentProtection", "CENC encryption must be signalled");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    private static string? ResolveFfmpegForFixture()
    {
        string candidate = AppFiles.FfmpegPath;
        if (File.Exists(candidate))
            return candidate;
        return NoMercyFfmpegProbe.ResolveFfmpegPath();
    }

    private static async Task RunProcessAsync(string fileName, string[] args)
    {
        ProcessStartInfo psi = new()
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        using Process process = Process.Start(psi)!;
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} failed: {stderr}");
    }
}
