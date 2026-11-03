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
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Resources;

namespace NoMercy.Tests.Encoder.Hardware;

/// <summary>
/// The GPU headroom gate (EncoderGpuHeadroomPercent) used to compare a constant 0
/// against its threshold: the production monitor never measured the encoder (#254).
/// </summary>
public class GpuEncodeHeadroomTests
{
    private static readonly GpuDevice Nvidia = new(
        GpuVendor.Nvidia,
        "RTX 4090",
        24576,
        8,
        [VideoCodecType.H264, VideoCodecType.H265]
    );

    [Fact]
    public async Task ProductionSampler_ReportingABusyEncoder_HoldsBackTheNextGpuLease()
    {
        NvmlGpuSampler sampler = new(
            NvidiaSmiReportingEncoderAt("97"),
            NullLogger<NvmlGpuSampler>.Instance
        );
        ResourceBudget budget = new(
            [Nvidia],
            cpuCores: 8,
            sampler,
            new(
                CpuHeadroomPercent: 0,
                GpuHeadroomPercent: 95,
                MinFreeMemoryMb: 0,
                GpuEncodePercentPerLease: 0
            )
        );

        double reading = await WaitForEncoderReadingAsync(sampler, "h264_nvenc");
        ResourceLease? lease = budget.TryAcquire(
            new(GpuDeviceKey: "h264_nvenc", GpuSlots: 1, CpuThreads: 0),
            TimeSpan.Zero
        );

        lease
            .Should()
            .BeNull(
                $"nvidia-smi reports the encoder at 97 % (sampler read {reading}), above the 95 % headroom"
            );
    }

    [Fact]
    public void RunningGpuLeases_CountAgainstTheBudget_WhenNothingIsMeasured()
    {
        // NullResourceMonitor stands in for a host without NVIDIA telemetry.
        ResourceBudget budget = new(
            [Nvidia],
            cpuCores: 8,
            new NullResourceMonitor(),
            new(
                CpuHeadroomPercent: 0,
                GpuHeadroomPercent: 95,
                MinFreeMemoryMb: 0,
                GpuEncodePercentPerLease: 50
            )
        );
        ResourceRequirement gpuEncode = new(Nvidia.Name, GpuSlots: 1, CpuThreads: 0);

        ResourceLease? first = budget.TryAcquire(gpuEncode, TimeSpan.Zero);
        ResourceLease? second = budget.TryAcquire(gpuEncode, TimeSpan.Zero);
        ResourceLease? third = budget.TryAcquire(gpuEncode, TimeSpan.Zero);

        first.Should().NotBeNull("no encode runs yet");
        second.Should().NotBeNull("one running encode is estimated at 50 %, under 95 %");
        third
            .Should()
            .BeNull("two running encodes are estimated at 100 %, at or above the 95 % headroom");
    }

    [Fact]
    public void RunningGpuLeases_AreIgnored_WhenTheEstimateIsZero()
    {
        ResourceBudget budget = new(
            [Nvidia],
            cpuCores: 8,
            new NullResourceMonitor(),
            new(
                CpuHeadroomPercent: 0,
                GpuHeadroomPercent: 95,
                MinFreeMemoryMb: 0,
                GpuEncodePercentPerLease: 0
            )
        );
        ResourceRequirement gpuEncode = new(Nvidia.Name, GpuSlots: 1, CpuThreads: 0);

        for (int i = 0; i < Nvidia.MaxEncoderSessions; i++)
            budget.TryAcquire(gpuEncode, TimeSpan.Zero).Should().NotBeNull();
    }

    [Fact]
    public void ReleasedLeases_StopCountingAgainstTheBudget()
    {
        ResourceBudget budget = new(
            [Nvidia],
            cpuCores: 8,
            new NullResourceMonitor(),
            new(
                CpuHeadroomPercent: 0,
                GpuHeadroomPercent: 95,
                MinFreeMemoryMb: 0,
                GpuEncodePercentPerLease: 50
            )
        );
        ResourceRequirement gpuEncode = new(Nvidia.Name, GpuSlots: 1, CpuThreads: 0);
        ResourceLease first = budget.TryAcquire(gpuEncode, TimeSpan.Zero)!;
        ResourceLease second = budget.TryAcquire(gpuEncode, TimeSpan.Zero)!;

        budget.Release(first);

        budget.TryAcquire(gpuEncode, TimeSpan.Zero).Should().NotBeNull();
        budget.Release(second);
    }

    [Theory]
    [InlineData("97\n", 97)]
    [InlineData("12\n88\n", 88)]
    [InlineData("[N/A]\n40\n", 40)]
    [InlineData("[N/A]\n", 0)]
    [InlineData("", 0)]
    public void ParseEncoderUtilization_ReturnsTheBusiestEncoderInPercent(
        string stdOut,
        double expected
    )
    {
        NvmlGpuSampler.ParseEncoderUtilization(stdOut).Should().Be(expected);
    }

    [Theory]
    [InlineData("h264_qsv")]
    [InlineData("hevc_amf")]
    [InlineData("Intel Arc A770")]
    public async Task Sampler_ReportsZero_ForAGpuThatIsNotNvidia(string gpuDeviceKey)
    {
        NvmlGpuSampler sampler = new(
            NvidiaSmiReportingEncoderAt("97"),
            NullLogger<NvmlGpuSampler>.Instance
        );
        await WaitForEncoderReadingAsync(sampler, "h264_nvenc");

        sampler.GetGpuEncodeUtilization(gpuDeviceKey).Should().Be(0);
    }

    [Fact]
    public async Task Sampler_MatchesAnNvidiaGpuByItsDeviceName()
    {
        NvmlGpuSampler sampler = new(
            NvidiaSmiReportingEncoderAt("61"),
            NullLogger<NvmlGpuSampler>.Instance,
            new HardwareCapabilities([Nvidia], CpuCores: 8)
        );

        double reading = await WaitForEncoderReadingAsync(sampler, Nvidia.Name);

        reading.Should().Be(61);
    }

    private static IProcessRunner NvidiaSmiReportingEncoderAt(string percent)
    {
        Mock<IProcessRunner> runner = new();
        runner
            .Setup(r =>
                r.RunAsync(
                    "nvidia-smi",
                    It.IsAny<string[]>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                (string _, string[] args, string? _, CancellationToken _) =>
                    args.Any(a => a.Contains("utilization.encoder"))
                        ? new(0, percent + "\n", "", TimeSpan.Zero)
                        : new ProcessResult(0, "", "", TimeSpan.Zero)
            );
        return runner.Object;
    }

    // The sampler refreshes in the background so TryAcquire never waits on nvidia-smi.
    private static async Task<double> WaitForEncoderReadingAsync(
        IResourceMonitor monitor,
        string gpuDeviceKey
    )
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        double reading = monitor.GetGpuEncodeUtilization(gpuDeviceKey);
        while (reading <= 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
            reading = monitor.GetGpuEncodeUtilization(gpuDeviceKey);
        }

        return reading;
    }
}
