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

using Microsoft.Extensions.Logging;
using Moq;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Hardware;
using NoMercy.Resources;

namespace NoMercy.Tests.Encoder.Hardware;

public class ResourceBudgetTests
{
    private static readonly GpuDevice TestGpu = new(
        GpuVendor.Nvidia,
        "RTX 4090",
        24576,
        3,
        [VideoCodecType.H264, VideoCodecType.H265, VideoCodecType.Av1]
    );

    [Fact]
    public void InitialState_AllSlotsAvailable()
    {
        ResourceBudget budget = new([TestGpu], cpuCores: 8);
        budget.AvailableGpuEncoderSlots(TestGpu.Name).Should().Be(3);
        budget.AvailableCpuThreads().Should().Be(8);
    }

    [Fact]
    public void Acquire_GpuSlot_DecreasesAvailable()
    {
        ResourceBudget budget = new([TestGpu], cpuCores: 8);
        ResourceRequirement requirement = new(
            GpuDeviceKey: TestGpu.Name,
            GpuSlots: 1,
            CpuThreads: 0
        );
        ResourceLease lease = budget.Acquire(requirement);
        budget.AvailableGpuEncoderSlots(TestGpu.Name).Should().Be(2);
        lease.Should().NotBeNull();
    }

    [Fact]
    public void Release_RestoresSlots()
    {
        ResourceBudget budget = new([TestGpu], cpuCores: 8);
        ResourceRequirement requirement = new(
            GpuDeviceKey: TestGpu.Name,
            GpuSlots: 1,
            CpuThreads: 0
        );
        ResourceLease lease = budget.Acquire(requirement);
        budget.AvailableGpuEncoderSlots(TestGpu.Name).Should().Be(2);
        budget.Release(lease);
        budget.AvailableGpuEncoderSlots(TestGpu.Name).Should().Be(3);
    }

    [Fact]
    public void Acquire_CpuThreads_DecreasesAvailable()
    {
        ResourceBudget budget = new([], cpuCores: 8);
        ResourceRequirement requirement = new(GpuDeviceKey: null, GpuSlots: 0, CpuThreads: 4);
        ResourceLease lease = budget.Acquire(requirement);
        budget.AvailableCpuThreads().Should().Be(4);
        budget.Release(lease);
        budget.AvailableCpuThreads().Should().Be(8);
    }

    [Fact]
    public void TryAcquire_WhenExhausted_ReturnsNull()
    {
        ResourceBudget budget = new([TestGpu], cpuCores: 8);
        ResourceRequirement requirement = new(
            GpuDeviceKey: TestGpu.Name,
            GpuSlots: 1,
            CpuThreads: 0
        );
        ResourceLease lease1 = budget.Acquire(requirement);
        ResourceLease lease2 = budget.Acquire(requirement);
        ResourceLease lease3 = budget.Acquire(requirement);
        ResourceLease? lease4 = budget.TryAcquire(requirement, TimeSpan.FromMilliseconds(50));
        lease4.Should().BeNull();
        budget.Release(lease1);
        budget.Release(lease2);
        budget.Release(lease3);
    }

    // ── IsGpuDeviceRegistered — Fillz's field bug: an AMF-pinned job on an
    // Nvidia-only host must be recognizable as "absent", not "busy". ────────

    [Fact]
    public void IsGpuDeviceRegistered_TrueForRegisteredDevice()
    {
        ResourceBudget budget = new([TestGpu], cpuCores: 8);

        budget.IsGpuDeviceRegistered(TestGpu.Name).Should().BeTrue();
    }

    [Fact]
    public void IsGpuDeviceRegistered_TrueForVendorAliasOfRegisteredDevice()
    {
        // Nvidia() only device — "nvenc" and "h264_nvenc" are vendor/encoder
        // aliases of the same semaphore, not separate devices.
        ResourceBudget budget = new([TestGpu], cpuCores: 8);

        budget.IsGpuDeviceRegistered("nvenc").Should().BeTrue();
        budget.IsGpuDeviceRegistered("h264_nvenc").Should().BeTrue();
    }

    [Fact]
    public void IsGpuDeviceRegistered_FalseWhenVendorNeverPresent()
    {
        // Only an NVIDIA GPU is registered — an AMD-only key (Fillz's stuck
        // "h264_amf" child jobs) must read as permanently absent, never busy.
        ResourceBudget budget = new([TestGpu], cpuCores: 8);

        budget.IsGpuDeviceRegistered("h264_amf").Should().BeFalse();
        budget.IsGpuDeviceRegistered("amf").Should().BeFalse();
    }

    [Fact]
    public void IsGpuDeviceRegistered_FalseWhenNoGpuAtAll()
    {
        ResourceBudget budget = new([], cpuCores: 8);

        budget.IsGpuDeviceRegistered("h264_nvenc").Should().BeFalse();
    }

    [Fact]
    public void TryAcquire_WhenCpuHeadroomExceeded_ReturnsNull()
    {
        // Monitor reports system CPU at 90 %; headroom threshold is 75 %. The
        // semaphore has slots free but the gate must still deny.
        Mock<IResourceMonitor> monitor = new();
        monitor.Setup(m => m.GetSystemCpuUsagePercent()).Returns(90.0);
        monitor.Setup(m => m.GetGpuEncodeUtilization(It.IsAny<string>())).Returns(0);
        monitor.Setup(m => m.GetAvailableMemoryMb()).Returns(8192);

        ResourceBudgetOptions options = new(
            CpuHeadroomPercent: 75,
            GpuHeadroomPercent: 80,
            MinFreeMemoryMb: 1024
        );
        ResourceBudget budget = new([TestGpu], cpuCores: 8, monitor.Object, options);

        ResourceLease? lease = budget.TryAcquire(
            new(GpuDeviceKey: TestGpu.Name, GpuSlots: 1, CpuThreads: 1),
            TimeSpan.Zero
        );

        lease.Should().BeNull();
        budget.AvailableGpuEncoderSlots(TestGpu.Name).Should().Be(3);
        budget.AvailableCpuThreads().Should().Be(8);
    }

    [Fact]
    public void TryAcquire_WhenHeadroomDenied_LogsTheReasonOncePerMinuteAtInformation()
    {
        // The queue worker probes with a zero timeout every 5 s. A denial used
        // to be silent there, so an encoder waiting on CPU looked like a stall.
        Mock<IResourceMonitor> monitor = new();
        monitor.Setup(m => m.GetSystemCpuUsagePercent()).Returns(90.0);
        monitor.Setup(m => m.GetGpuEncodeUtilization(It.IsAny<string>())).Returns(0);
        monitor.Setup(m => m.GetAvailableMemoryMb()).Returns(8192);
        Mock<ILogger<ResourceBudget>> logger = new();

        ResourceBudget budget = new(
            [TestGpu],
            cpuCores: 8,
            monitor.Object,
            new(CpuHeadroomPercent: 75, GpuHeadroomPercent: 80, MinFreeMemoryMb: 1024),
            logger.Object
        );

        for (int probe = 0; probe < 4; probe++)
            budget.TryAcquire(new(GpuDeviceKey: null, GpuSlots: 0, CpuThreads: 1), TimeSpan.Zero);

        logger.Verify(
            l =>
                l.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((state, _) =>
                        state.ToString()!.Contains("Headroom gate denied")
                        && state.ToString()!.Contains("system CPU")
                    ),
                    null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()
                ),
            Times.Once
        );
    }

    [Fact]
    public void TryAcquire_WhenHeadroomBelowThreshold_GrantsLease()
    {
        Mock<IResourceMonitor> monitor = new();
        monitor.Setup(m => m.GetSystemCpuUsagePercent()).Returns(40.0);
        monitor.Setup(m => m.GetGpuEncodeUtilization(It.IsAny<string>())).Returns(0);
        monitor.Setup(m => m.GetAvailableMemoryMb()).Returns(8192);

        ResourceBudgetOptions options = new(
            CpuHeadroomPercent: 75,
            GpuHeadroomPercent: 80,
            MinFreeMemoryMb: 1024
        );
        ResourceBudget budget = new([TestGpu], cpuCores: 8, monitor.Object, options);

        ResourceLease? lease = budget.TryAcquire(
            new(GpuDeviceKey: TestGpu.Name, GpuSlots: 1, CpuThreads: 1),
            TimeSpan.Zero
        );

        lease.Should().NotBeNull();
        budget.Release(lease!);
    }

    [Fact]
    public void TryAcquire_WhenGpuEncodeUtilSaturated_ReturnsNull()
    {
        // Monitor returns GPU encode util in percent (0–100); 95 % exceeds the
        // 80 % threshold.
        Mock<IResourceMonitor> monitor = new();
        monitor.Setup(m => m.GetSystemCpuUsagePercent()).Returns(20.0);
        monitor.Setup(m => m.GetGpuEncodeUtilization(TestGpu.Name)).Returns(95.0);
        monitor.Setup(m => m.GetAvailableMemoryMb()).Returns(8192);

        ResourceBudgetOptions options = new(
            CpuHeadroomPercent: 75,
            GpuHeadroomPercent: 80,
            MinFreeMemoryMb: 1024
        );
        ResourceBudget budget = new([TestGpu], cpuCores: 8, monitor.Object, options);

        ResourceLease? lease = budget.TryAcquire(
            new(GpuDeviceKey: TestGpu.Name, GpuSlots: 1, CpuThreads: 0),
            TimeSpan.Zero
        );

        lease.Should().BeNull();
    }

    [Fact]
    public void TryAcquire_WhenMemoryBelowMinimum_ReturnsNull()
    {
        Mock<IResourceMonitor> monitor = new();
        monitor.Setup(m => m.GetSystemCpuUsagePercent()).Returns(20.0);
        monitor.Setup(m => m.GetGpuEncodeUtilization(It.IsAny<string>())).Returns(0);
        monitor.Setup(m => m.GetAvailableMemoryMb()).Returns(256);

        ResourceBudgetOptions options = new(
            CpuHeadroomPercent: 75,
            GpuHeadroomPercent: 80,
            MinFreeMemoryMb: 1024
        );
        ResourceBudget budget = new([TestGpu], cpuCores: 8, monitor.Object, options);

        ResourceLease? lease = budget.TryAcquire(
            new(GpuDeviceKey: null, GpuSlots: 0, CpuThreads: 1),
            TimeSpan.Zero
        );

        lease.Should().BeNull();
    }

    [Fact]
    public void TryAcquire_WithDisabledOptions_IgnoresMonitor()
    {
        // Saturated host but every threshold is 0 → headroom gate is disabled
        // and only the static semaphores govern.
        Mock<IResourceMonitor> monitor = new();
        monitor.Setup(m => m.GetSystemCpuUsagePercent()).Returns(99.0);
        monitor.Setup(m => m.GetGpuEncodeUtilization(It.IsAny<string>())).Returns(99.0);
        monitor.Setup(m => m.GetAvailableMemoryMb()).Returns(64);

        ResourceBudget budget = new(
            [TestGpu],
            cpuCores: 8,
            monitor.Object,
            ResourceBudgetOptions.Disabled
        );

        ResourceLease? lease = budget.TryAcquire(
            new(GpuDeviceKey: TestGpu.Name, GpuSlots: 1, CpuThreads: 1),
            TimeSpan.Zero
        );

        lease.Should().NotBeNull();
        budget.Release(lease!);
    }

    [Fact]
    public async Task ConcurrentAcquire_IsThreadSafe()
    {
        ResourceBudget budget = new([TestGpu], cpuCores: 8);
        ResourceRequirement requirement = new(
            GpuDeviceKey: TestGpu.Name,
            GpuSlots: 1,
            CpuThreads: 0
        );
        int successCount = 0;
        List<ResourceLease> leases = [];
        object lockObj = new();

        Task[] tasks = Enumerable
            .Range(0, 10)
            .Select(_ =>
                Task.Run(() =>
                {
                    ResourceLease? lease = budget.TryAcquire(
                        requirement,
                        TimeSpan.FromMilliseconds(100)
                    );
                    if (lease is not null)
                    {
                        Interlocked.Increment(ref successCount);
                        lock (lockObj)
                        {
                            leases.Add(lease);
                        }
                    }
                })
            )
            .ToArray();

        await Task.WhenAll(tasks);
        successCount.Should().Be(3);
        foreach (ResourceLease lease in leases)
            budget.Release(lease);
    }
}
