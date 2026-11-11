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
using Microsoft.Extensions.Time.Testing;
using NoMercy.Setup.Server;

namespace NoMercy.Tests.Setup.Server;

/// <summary>
/// The lyricsalign model is fetched the first time a plugin's graph names it.
/// A plugin sweeping a library asks once per track, so what matters here is
/// when a download is started: one at a time, and not again straight after
/// one ended. The download itself is proven on <see cref="Binaries" />.
/// </summary>
[Trait("Category", "Unit")]
public sealed class LyricsAlignModelInstallerTests
{
    private readonly FakeTimeProvider _clock = new();
    private int _downloads;

    [Fact]
    public async Task ARequest_StartsOneDownload_AndFurtherRequestsWhileItRunsStartNone()
    {
        TaskCompletionSource download = new();
        LyricsAlignModelInstaller installer = new(
            () =>
            {
                Interlocked.Increment(ref _downloads);
                return download.Task;
            },
            _clock
        );

        installer.RequestInstall();
        installer.RequestInstall();
        installer.RequestInstall();
        await WaitForAsync(() => Volatile.Read(ref _downloads) > 0);

        _downloads.Should().Be(1);

        download.SetResult();
        await installer.Running!;
        _downloads.Should().Be(1);
    }

    /// <summary>
    /// A release without the asset, or a provider that is down, ends the
    /// download at once. Without a pause it would be asked again for every
    /// track the plugin tries next.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AfterADownloadEnded_TheNextOneWaitsOutThePause_WhetherItFailedOrNot(
        bool fails
    )
    {
        LyricsAlignModelInstaller installer = new(
            () =>
            {
                Interlocked.Increment(ref _downloads);
                return fails
                    ? Task.FromException(new HttpRequestException("github is down"))
                    : Task.CompletedTask;
            },
            _clock
        );

        installer.RequestInstall();
        await installer.Running!;

        installer.RequestInstall();
        _clock.Advance(LyricsAlignModelInstaller.PauseBetweenAttempts - TimeSpan.FromSeconds(1));
        installer.RequestInstall();
        await installer.Running!;
        _downloads.Should().Be(1, "inside the pause nothing is started");

        _clock.Advance(TimeSpan.FromSeconds(2));
        installer.RequestInstall();
        await installer.Running!;
        _downloads.Should().Be(2);
    }

    [Fact]
    public async Task AFailingDownload_NeverReachesTheCaller()
    {
        LyricsAlignModelInstaller installer = new(
            () => throw new InvalidDataException("SHA-256 mismatch"),
            _clock
        );

        Action request = installer.RequestInstall;

        request.Should().NotThrow();
        Func<Task> running = () => installer.Running!;
        await running.Should().NotThrowAsync("the failure is logged, not handed on");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("timed out waiting for the download to start");

            await Task.Delay(5);
        }
    }
}
