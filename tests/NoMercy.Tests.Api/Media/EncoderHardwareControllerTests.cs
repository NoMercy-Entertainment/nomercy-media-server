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

using Microsoft.AspNetCore.Mvc;
using Moq;
using NoMercy.Api.Controllers.V1.Encoder;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Errors;
using NoMercy.Encoder.Execution;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.Startup;
using NoMercy.Resources;
using Xunit;

namespace NoMercy.Tests.Api.Media;

/// <summary>
/// Coverage for EncoderHardwareController: benchmark start/status/list, the
/// live utilization proxy, and the cached-capabilities probe endpoint. All
/// five collaborators are interfaces with no DB/network dependency, so the
/// controller is constructed directly with Moq doubles rather than through
/// NoMercyApiFactory.
/// </summary>
[Trait("Category", "Unit")]
public class EncoderHardwareControllerTests
{
    private readonly Mock<IBenchmarkJobTracker> _tracker = new();
    private readonly Mock<IResourceMonitor> _monitor = new();
    private readonly Mock<IEncoderProcessRegistry> _registry = new();
    private readonly Mock<IHardwareCapabilities> _hardware = new();
    private readonly Mock<IFfmpegCapabilityProbe> _probe = new();

    private EncoderHardwareController CreateController() =>
        new(_tracker.Object, _monitor.Object, _registry.Object, _hardware.Object, _probe.Object);

    private static BenchmarkJobStatus MakeJob(string jobId, string status = "queued") =>
        new(jobId, status, DateTime.UtcNow, null, 0, [], [], null);

    // =========================================================================
    // StartBenchmark
    // =========================================================================

    [Fact]
    public void StartBenchmark_NullRequest_StartsWithEmptyCodecsAndResolutions()
    {
        BenchmarkJobStatus job = MakeJob("job-1");
        _tracker
            .Setup(t =>
                t.Start(It.Is<List<VideoCodecType>>(c => c.Count == 0), It.IsAny<List<int>>())
            )
            .Returns(job);

        IActionResult result = CreateController().StartBenchmark(null);

        AcceptedResult accepted = Assert.IsType<AcceptedResult>(result);
        _tracker.Verify(
            t =>
                t.Start(
                    It.Is<List<VideoCodecType>>(c => c.Count == 0),
                    It.Is<List<int>>(r => r.Count == 0)
                ),
            Times.Once
        );
        accepted.Value.Should().NotBeNull();
    }

    [Fact]
    public void StartBenchmark_EmptyCodecsArray_StartsWithEmptyCodecs()
    {
        BenchmarkJobStatus job = MakeJob("job-2");
        _tracker
            .Setup(t => t.Start(It.IsAny<List<VideoCodecType>>(), It.IsAny<List<int>>()))
            .Returns(job);

        IActionResult result = CreateController()
            .StartBenchmark(new(Codecs: [], Resolutions: null));

        Assert.IsType<AcceptedResult>(result);
        _tracker.Verify(
            t => t.Start(It.Is<List<VideoCodecType>>(c => c.Count == 0), It.IsAny<List<int>>()),
            Times.Once
        );
    }

    [Fact]
    public void StartBenchmark_ValidCodecNames_ParsesAndStartsWithThoseCodecs()
    {
        BenchmarkJobStatus job = MakeJob("job-3");
        List<VideoCodecType>? captured = null;
        _tracker
            .Setup(t => t.Start(It.IsAny<List<VideoCodecType>>(), It.IsAny<List<int>>()))
            .Callback<IReadOnlyList<VideoCodecType>, IReadOnlyList<int>>(
                (codecs, _) => captured = codecs.ToList()
            )
            .Returns(job);

        IActionResult result = CreateController()
            .StartBenchmark(new(Codecs: ["H264", "Av1"], Resolutions: null));

        Assert.IsType<AcceptedResult>(result);
        captured.Should().Equal(VideoCodecType.H264, VideoCodecType.Av1);
    }

    [Fact]
    public void StartBenchmark_CaseInsensitiveCodecNames_StillParse()
    {
        BenchmarkJobStatus job = MakeJob("job-4");
        List<VideoCodecType>? captured = null;
        _tracker
            .Setup(t => t.Start(It.IsAny<List<VideoCodecType>>(), It.IsAny<List<int>>()))
            .Callback<IReadOnlyList<VideoCodecType>, IReadOnlyList<int>>(
                (codecs, _) => captured = codecs.ToList()
            )
            .Returns(job);

        IActionResult result = CreateController()
            .StartBenchmark(new(Codecs: ["h265"], Resolutions: null));

        Assert.IsType<AcceptedResult>(result);
        captured.Should().Equal(VideoCodecType.H265);
    }

    [Fact]
    public void StartBenchmark_WithResolutions_PassesResolutionsToTracker()
    {
        BenchmarkJobStatus job = MakeJob("job-5");
        List<int>? captured = null;
        _tracker
            .Setup(t => t.Start(It.IsAny<List<VideoCodecType>>(), It.IsAny<List<int>>()))
            .Callback<IReadOnlyList<VideoCodecType>, IReadOnlyList<int>>(
                (_, resolutions) => captured = resolutions.ToList()
            )
            .Returns(job);

        IActionResult result = CreateController()
            .StartBenchmark(new(Codecs: null, Resolutions: [1080, 2160]));

        Assert.IsType<AcceptedResult>(result);
        captured.Should().Equal(1080, 2160);
    }

    [Fact]
    public void StartBenchmark_UnknownCodecName_Returns422WithSuggestion()
    {
        IActionResult result = CreateController()
            .StartBenchmark(new(Codecs: ["not-a-real-codec"], Resolutions: null));

        UnprocessableEntityObjectResult unprocessable =
            Assert.IsType<UnprocessableEntityObjectResult>(result);
        _tracker.Verify(
            t => t.Start(It.IsAny<List<VideoCodecType>>(), It.IsAny<List<int>>()),
            Times.Never
        );
        string json = Newtonsoft.Json.JsonConvert.SerializeObject(unprocessable.Value);
        json.Should().Contain("not-a-real-codec");
        json.Should().Contain("H264");
    }

    [Fact]
    public void StartBenchmark_MultipleCodecs_FirstInvalidNameWins_NeverReachesSecondEntry()
    {
        // Two invalid entries — only the FIRST must be reported and the loop
        // must stop there rather than continuing to validate the rest.
        IActionResult result = CreateController()
            .StartBenchmark(new(Codecs: ["totally-bogus", "also-bogus"], Resolutions: null));

        UnprocessableEntityObjectResult unprocessable =
            Assert.IsType<UnprocessableEntityObjectResult>(result);
        string json = Newtonsoft.Json.JsonConvert.SerializeObject(unprocessable.Value);
        json.Should().Contain("totally-bogus");
        json.Should().NotContain("also-bogus");
    }

    [Fact]
    public void StartBenchmark_ValidThenInvalidCodec_Returns422_NeverStartsJob()
    {
        IActionResult result = CreateController()
            .StartBenchmark(new(Codecs: ["H264", "not-a-real-codec"], Resolutions: null));

        Assert.IsType<UnprocessableEntityObjectResult>(result);
        _tracker.Verify(
            t => t.Start(It.IsAny<List<VideoCodecType>>(), It.IsAny<List<int>>()),
            Times.Never
        );
    }

    // =========================================================================
    // GetBenchmark
    // =========================================================================

    [Fact]
    public void GetBenchmark_UnknownJobId_Returns404()
    {
        _tracker.Setup(t => t.Get("missing-job")).Returns((BenchmarkJobStatus?)null);

        IActionResult result = CreateController().GetBenchmark("missing-job");

        ObjectResult objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        objectResult.StatusCode.Should().Be(404);
    }

    [Fact]
    public void GetBenchmark_KnownJobId_ReturnsJobStatus()
    {
        BenchmarkJobStatus job = MakeJob("job-known", "completed");
        _tracker.Setup(t => t.Get("job-known")).Returns(job);

        IActionResult result = CreateController().GetBenchmark("job-known");

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        ok.Value.Should().Be(job);
    }

    // =========================================================================
    // ListBenchmarks
    // =========================================================================

    [Fact]
    public void ListBenchmarks_ReturnsAllTrackedJobs()
    {
        List<BenchmarkJobStatus> jobs = [MakeJob("a"), MakeJob("b", "completed")];
        _tracker.Setup(t => t.List()).Returns(jobs);

        IActionResult result = CreateController().ListBenchmarks();

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        ok.Value.Should().NotBeNull();
    }

    // =========================================================================
    // GetUtilization — proxies all four dependencies into one snapshot
    // =========================================================================

    [Fact]
    public async Task GetUtilization_CombinesAllFourCollaboratorsIntoOneSnapshot()
    {
        _monitor.Setup(m => m.GetCpuUsagePercent()).Returns(42.5);
        _monitor.Setup(m => m.GetAvailableMemoryMb()).Returns(8192L);
        List<GpuProcessSample> gpuSamples = [new(1234, 0, 55, 512_000_000)];
        _monitor
            .Setup(m => m.SampleGpuAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(gpuSamples);
        _registry.Setup(r => r.CountConcurrentNvencSessions()).Returns(2);
        List<GpuDevice> gpus =
        [
            new(GpuVendor.Nvidia, "RTX 4070", 12_000, 3, [VideoCodecType.H264]),
        ];
        _hardware.Setup(h => h.Gpus).Returns(gpus);

        IActionResult result = await CreateController().GetUtilization();

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        UtilizationSnapshot snapshot = Assert.IsType<UtilizationSnapshot>(ok.Value);
        snapshot.CpuUsagePercent.Should().Be(42.5);
        snapshot.AvailableMemoryMb.Should().Be(8192L);
        snapshot.GpuSamples.Should().BeEquivalentTo(gpuSamples);
        snapshot.ConcurrentNvencSessions.Should().Be(2);
        snapshot.Gpus.Should().BeEquivalentTo(gpus);
    }

    // =========================================================================
    // GetCapabilities
    // =========================================================================

    [Fact]
    public void GetCapabilities_ProbeNotYetCompleted_ReturnsProbePendingStatus()
    {
        _probe.Setup(p => p.GetCachedReport()).Returns((CapabilityReport?)null);

        IActionResult result = CreateController().GetCapabilities();

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        string json = Newtonsoft.Json.JsonConvert.SerializeObject(ok.Value);
        json.Should().Contain("probe_pending");
    }

    [Fact]
    public void GetCapabilities_CachedReportAvailable_ReturnsTheReport()
    {
        CapabilityReport report = new(
            BluRayProtocol: true,
            DvdReadProtocol: false,
            AvailableEncoders: ["libx264", "h264_nvenc"],
            MissingFilters: [],
            MissingMuxers: [],
            FpcalcPresent: true,
            WhisperModelPresent: false,
            StemsplitModelPresent: false,
            TesseractEngTraineddataPresent: true,
            TesseractModelsDirectory: "/models",
            Issues: []
        );
        _probe.Setup(p => p.GetCachedReport()).Returns(report);

        IActionResult result = CreateController().GetCapabilities();

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        ok.Value.Should().Be(report);
    }
}
