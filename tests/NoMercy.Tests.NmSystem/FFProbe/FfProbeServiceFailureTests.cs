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

using System.Text;
using Moq;
using NoMercy.NmSystem.FFProbe;
using NoMercy.Storage;

namespace NoMercy.Tests.NmSystem.FFProbe;

/// <summary>
/// A probe that did not happen must not look like a file with no streams and
/// no duration. A cancelled scan stops; a failed probe says so (#320).
/// </summary>
[Trait("Category", "Unit")]
public class FfProbeServiceFailureTests
{
    private static CancellationToken Cancelled => new(canceled: true);

    [Fact]
    public async Task A_cancelled_local_probe_is_cancelled_not_an_empty_result()
    {
        Func<Task> probe = () => new FfProbeService().CreateAsync("Movie.mkv", Cancelled);

        await probe.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_cancelled_remote_probe_is_cancelled_not_an_empty_result()
    {
        Func<Task> probe = () =>
            new FfProbeService().CreateAsync(RemoteDriver(), "Movie.mkv", Cancelled);

        await probe.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_cancelled_playlist_read_is_cancelled_not_an_empty_result()
    {
        Func<Task> probe = () =>
            new FfProbeService().CreateAsync(RemoteDriver(), "Show/master.m3u8", Cancelled);

        await probe.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_probe_of_a_file_that_is_not_there_says_it_failed()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"nm-missing-{Guid.NewGuid():N}.mkv");

        FfProbeData result = await new FfProbeService().CreateAsync(missing);

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void A_probe_without_errors_has_not_failed()
    {
        new FfProbeData().Failed.Should().BeFalse();
    }

    private static IStorageDriver RemoteDriver()
    {
        Mock<IStorageDriver> driver = new();
        driver
            .Setup(d => d.OpenRead(It.IsAny<string>()))
            .Returns(() => new MemoryStream(Encoding.UTF8.GetBytes("#EXTM3U\n")));

        return driver.Object;
    }
}
