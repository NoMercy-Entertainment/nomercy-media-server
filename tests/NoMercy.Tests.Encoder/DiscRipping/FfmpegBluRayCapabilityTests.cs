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
using NoMercy.Encoder.Infrastructure;
using NoMercy.OpticalMedia.Capabilities;
using NoMercy.OpticalMedia.Drives;
using NoMercy.OpticalMedia.Rip;
using NoMercy.OpticalMedia.Sources;
using NoMercy.OpticalMedia.Sources.Bluray;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.Encoder.DiscRipping;

/// <summary>
/// Offline tests for AACS/BD+ capability probe and error classification.
/// All tests stub <see cref="IProcessRunner"/> — no real ffmpeg required.
/// </summary>
public class FfmpegBluRayCapabilityTests
{
    // ── FfmpegBluRayCapability.ProbeAsync ──────────────────────────────────

    [Fact]
    public async Task ProbeAsync_WhenBluRayInOutput_SetsBluRayProtocolPresent()
    {
        EncoderOptions options = new()
        {
            FfmpegPathOverride = "/usr/bin/ffmpeg",
            FfprobePathOverride = "/usr/bin/ffprobe",
        };

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
            .ReturnsAsync(
                new ProcessResult(0, "bluray\nhls\nrtmp", "", TimeSpan.FromMilliseconds(10))
            );

        FfmpegBluRayCapability cap = new(
            options,
            runner.Object,
            NullLogger<FfmpegBluRayCapability>.Instance
        );

        await cap.ProbeAsync();

        cap.BluRayProtocolPresent.Should().BeTrue();
    }

    [Fact]
    public async Task ProbeAsync_WhenBluRayAbsent_BluRayProtocolPresentIsFalse()
    {
        EncoderOptions options = new()
        {
            FfmpegPathOverride = "/usr/bin/ffmpeg",
            FfprobePathOverride = "/usr/bin/ffprobe",
        };

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
            .ReturnsAsync(
                new ProcessResult(0, "hls\nrtmp\nfile", "", TimeSpan.FromMilliseconds(10))
            );

        FfmpegBluRayCapability cap = new(
            options,
            runner.Object,
            NullLogger<FfmpegBluRayCapability>.Instance
        );

        await cap.ProbeAsync();

        cap.BluRayProtocolPresent.Should().BeFalse();
    }

    [Fact]
    public async Task ProbeAsync_WithKeyDbOverride_ActiveKeyDbPathReportsOverride()
    {
        EncoderOptions options = new()
        {
            FfmpegPathOverride = "/usr/bin/ffmpeg",
            FfprobePathOverride = "/usr/bin/ffprobe",
            BluRay = new() { KeyDbOverridePath = "/home/user/KEYDB.cfg" },
        };

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
            .ReturnsAsync(new ProcessResult(0, "bluray", "", TimeSpan.FromMilliseconds(10)));

        FfmpegBluRayCapability cap = new(
            options,
            runner.Object,
            NullLogger<FfmpegBluRayCapability>.Instance
        );

        await cap.ProbeAsync();

        cap.ActiveKeyDbPath.Should().Contain("/home/user/KEYDB.cfg");
    }

    [Fact]
    public async Task ProbeAsync_WithoutOverride_ActiveKeyDbPathReportsBundled()
    {
        EncoderOptions options = new()
        {
            FfmpegPathOverride = "/usr/bin/ffmpeg",
            FfprobePathOverride = "/usr/bin/ffprobe",
        };

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
            .ReturnsAsync(new ProcessResult(0, "bluray", "", TimeSpan.FromMilliseconds(10)));

        FfmpegBluRayCapability cap = new(
            options,
            runner.Object,
            NullLogger<FfmpegBluRayCapability>.Instance
        );

        await cap.ProbeAsync();

        cap.ActiveKeyDbPath.Should().Contain("bundled");
    }

    // ── DiscScanner.ClassifyBluRayStderr — each stderr pattern ────────────

    [Fact]
    public void ClassifyBluRayStderr_AacsCertMissing_ThrowsDiscAacsCertMissing()
    {
        const string stderr =
            "aacs: no matching certificate for volume AABBCCDD11223344AABBCCDD11223344\n";

        EncoderRuntimeException ex = Assert.Throws<EncoderRuntimeException>(() =>
            DiscScanner.ClassifyBluRayStderr("bluray:/dev/sr0", stderr)
        );

        ex.Shape.Id.Should().Be(EncoderRuleId.DiscAacsCertMissing);
        ex.Shape.Message.Should().Contain("AABBCCDD11223344AABBCCDD11223344");
        ex.Shape.Suggestion.Should().Contain("KEYDB.cfg");
    }

    [Fact]
    public void ClassifyBluRayStderr_BdplusConverterMissing_ThrowsDiscBdplusConverterMissing()
    {
        const string stderr = "bdplus: no matching converter for this disc\n";

        EncoderRuntimeException ex = Assert.Throws<EncoderRuntimeException>(() =>
            DiscScanner.ClassifyBluRayStderr("bluray:/dev/sr0", stderr)
        );

        ex.Shape.Id.Should().Be(EncoderRuleId.DiscBdplusConverterMissing);
    }

    [Fact]
    public void ClassifyBluRayStderr_ProtocolNotFound_ThrowsDiscReadError()
    {
        const string stderr = "Protocol not found\n";

        EncoderRuntimeException ex = Assert.Throws<EncoderRuntimeException>(() =>
            DiscScanner.ClassifyBluRayStderr("bluray:/dev/sr0", stderr)
        );

        ex.Shape.Id.Should().Be(EncoderRuleId.DiscReadError);
    }

    [Fact]
    public void ClassifyBluRayStderr_EmptyStderr_DoesNotThrow()
    {
        // Empty stderr is a no-op — probe timed out or disc responded fine.
        Action act = () => DiscScanner.ClassifyBluRayStderr("bluray:/dev/sr0", "");

        act.Should().NotThrow();
    }

    [Fact]
    public void ClassifyBluRayStderr_UnknownStderr_DoesNotThrow()
    {
        // Unknown stderr is not classified — the caller falls through to generic handling.
        Action act = () =>
            DiscScanner.ClassifyBluRayStderr("bluray:/dev/sr0", "some random ffprobe output\n");

        act.Should().NotThrow();
    }

    // ── Env var forwarding ─────────────────────────────────────────────────

    [Fact]
    public async Task DiscRipper_WithKeyDbOverride_ForwardsLibaacsKeyDbEnvVar()
    {
        EncoderOptions options = new()
        {
            FfmpegPathOverride = "/usr/bin/ffmpeg",
            FfprobePathOverride = "/usr/bin/ffprobe",
            BluRay = new() { KeyDbOverridePath = "/mnt/keys/KEYDB.cfg" },
        };

        IReadOnlyDictionary<string, string>? capturedEnv = null;
        Mock<IProcessRunner> runner = new();

        // Capture calls via the extraEnv overload.
        runner
            .Setup(r =>
                r.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<IReadOnlyDictionary<string, string>?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<
                string,
                string[],
                IReadOnlyDictionary<string, string>?,
                string?,
                CancellationToken
            >((_, _, env, _, _) => capturedEnv = env)
            .ReturnsAsync(new ProcessResult(0, "", "", TimeSpan.FromSeconds(1)));

        // Also stub the standard overload (used when no env overrides).
        runner
            .Setup(r =>
                r.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new ProcessResult(0, "", "", TimeSpan.FromSeconds(1)));

        string outputDir = Path.Combine(Path.GetTempPath(), $"Rip_{Guid.NewGuid():N}");
        try
        {
            LocalStorageDriver driver = new();
            LocalStorage storage = new(driver, new([], driver));
            DriveLockRegistry lockRegistry = new();
            DiscRipper ripper = new(
                options,
                runner.Object,
                storage,
                lockRegistry,
                NullLogger<DiscRipper>.Instance
            );

            RipRequest request = new(
                DrivePath: "bluray:/dev/sr0",
                SelectedTitleIndices: [0],
                MetadataId: null,
                Custom: null,
                LibraryId: Ulid.NewUlid(),
                FolderId: Ulid.NewUlid(),
                EncodingProfileId: null,
                AudioTracks: [new(0, true)],
                Subtitles: []
            );

            await ripper.RipAsync(request, outputDir, CancellationToken.None);

            capturedEnv.Should().NotBeNull();
            capturedEnv!.Should().ContainKey("LIBAACS_KEY_DB");
            capturedEnv["LIBAACS_KEY_DB"].Should().Be("/mnt/keys/KEYDB.cfg");
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task DiscRipper_WithBdPlusOverride_ForwardsLibbdplusDatabaseEnvVar()
    {
        EncoderOptions options = new()
        {
            FfmpegPathOverride = "/usr/bin/ffmpeg",
            FfprobePathOverride = "/usr/bin/ffprobe",
            BluRay = new() { AacsKeysOverridePath = "/mnt/keys/bdplus/" },
        };

        IReadOnlyDictionary<string, string>? capturedEnv = null;
        Mock<IProcessRunner> runner = new();

        runner
            .Setup(r =>
                r.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<IReadOnlyDictionary<string, string>?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<
                string,
                string[],
                IReadOnlyDictionary<string, string>?,
                string?,
                CancellationToken
            >((_, _, env, _, _) => capturedEnv = env)
            .ReturnsAsync(new ProcessResult(0, "", "", TimeSpan.FromSeconds(1)));

        runner
            .Setup(r =>
                r.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new ProcessResult(0, "", "", TimeSpan.FromSeconds(1)));

        string outputDir = Path.Combine(Path.GetTempPath(), $"Rip_{Guid.NewGuid():N}");
        try
        {
            LocalStorageDriver driver = new();
            LocalStorage storage = new(driver, new([], driver));
            DriveLockRegistry lockRegistry = new();
            DiscRipper ripper = new(
                options,
                runner.Object,
                storage,
                lockRegistry,
                NullLogger<DiscRipper>.Instance
            );

            RipRequest request = new(
                DrivePath: "bluray:/dev/sr0",
                SelectedTitleIndices: [0],
                MetadataId: null,
                Custom: null,
                LibraryId: Ulid.NewUlid(),
                FolderId: Ulid.NewUlid(),
                EncodingProfileId: null,
                AudioTracks: [new(0, true)],
                Subtitles: []
            );

            await ripper.RipAsync(request, outputDir, CancellationToken.None);

            capturedEnv.Should().NotBeNull();
            capturedEnv!.Should().ContainKey("LIBBDPLUS_DATABASE");
            capturedEnv["LIBBDPLUS_DATABASE"].Should().Be("/mnt/keys/bdplus/");
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task DiscRipper_WithoutBluRayOptions_UsesStandardRunAsync()
    {
        EncoderOptions options = new()
        {
            FfmpegPathOverride = "/usr/bin/ffmpeg",
            FfprobePathOverride = "/usr/bin/ffprobe",
            // BluRay is null — no overrides
        };

        bool standardOverloadCalled = false;
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
                (_, _, _, _) => standardOverloadCalled = true
            )
            .ReturnsAsync(new ProcessResult(0, "", "", TimeSpan.FromSeconds(1)));

        string outputDir = Path.Combine(Path.GetTempPath(), $"Rip_{Guid.NewGuid():N}");
        try
        {
            LocalStorageDriver driver = new();
            LocalStorage storage = new(driver, new([], driver));
            DriveLockRegistry lockRegistry = new();
            DiscRipper ripper = new(
                options,
                runner.Object,
                storage,
                lockRegistry,
                NullLogger<DiscRipper>.Instance
            );

            RipRequest request = new(
                DrivePath: "bluray:/dev/sr0",
                SelectedTitleIndices: [0],
                MetadataId: null,
                Custom: null,
                LibraryId: Ulid.NewUlid(),
                FolderId: Ulid.NewUlid(),
                EncodingProfileId: null,
                AudioTracks: [new(0, true)],
                Subtitles: []
            );

            await ripper.RipAsync(request, outputDir, CancellationToken.None);

            standardOverloadCalled.Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }
}
