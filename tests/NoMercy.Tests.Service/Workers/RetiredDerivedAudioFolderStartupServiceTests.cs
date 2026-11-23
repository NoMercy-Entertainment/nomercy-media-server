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
using NoMercy.Data.Services;
using NoMercy.Service.Workers;

namespace NoMercy.Tests.Service.Workers;

/// <summary>
/// The start-up half of removing the retired derived-audio folder (#689): it
/// asks for the removal once, and a removal that fails must never stop the
/// server from coming up.
/// </summary>
[Trait("Category", "Unit")]
public class RetiredDerivedAudioFolderStartupServiceTests
{
    private static async Task RunAsync(IRetiredDerivedAudioFolder folder)
    {
        using RetiredDerivedAudioFolderStartupService service = new(
            folder,
            NullLogger<RetiredDerivedAudioFolderStartupService>.Instance
        );

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task OnStart_TheFolderIsRemovedOnce()
    {
        Mock<IRetiredDerivedAudioFolder> folder = new();
        folder
            .Setup(retired => retired.RemoveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1024L);

        await RunAsync(folder.Object);

        folder.Verify(retired => retired.RemoveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ARemovalThatFails_DoesNotStopTheServer()
    {
        Mock<IRetiredDerivedAudioFolder> folder = new();
        folder
            .Setup(retired => retired.RemoveAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("the folder is in use"));

        Func<Task> start = () => RunAsync(folder.Object);

        await start.Should().NotThrowAsync();
    }
}
