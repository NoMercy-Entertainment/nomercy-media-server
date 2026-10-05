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
using NoMercy.Api.Security;
using NoMercy.Service.Jobs;
using Xunit;

namespace NoMercy.Tests.Api.Security;

public class IpBanExpiryCronJobTests
{
    [Fact]
    public async Task ExecuteAsync_DeletesRowsPastRetentionAndRefreshesTheCache()
    {
        DateTime now = DateTime.UtcNow;
        InMemoryIpBanRepository repository = new();
        repository.Rows.Add(
            new()
            {
                Address = "203.0.113.90",
                Reason = "KnownProbe",
                BannedAt = now.AddDays(-60),
                ExpiresAt = now.AddDays(-59),
            }
        );
        repository.Rows.Add(
            new()
            {
                Address = "203.0.113.91",
                Reason = "KnownProbe",
                BannedAt = now,
                ExpiresAt = now.AddHours(1),
            }
        );
        Mock<IAbuseGuard> guard = new();
        IpBanExpiryCronJob job = new(
            repository,
            guard.Object,
            Mock.Of<ILogger<IpBanExpiryCronJob>>()
        );

        await job.ExecuteAsync(string.Empty, CancellationToken.None);

        repository.Rows.Should().ContainSingle().Which.Address.Should().Be("203.0.113.91");
        guard.Verify(x => x.RefreshAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_LeavesAnExpiredButRecentBanInPlaceForTheDashboard()
    {
        DateTime now = DateTime.UtcNow;
        InMemoryIpBanRepository repository = new();
        repository.Rows.Add(
            new()
            {
                Address = "203.0.113.92",
                Reason = "KnownProbe",
                BannedAt = now.AddDays(-2),
                ExpiresAt = now.AddDays(-1),
            }
        );
        IpBanExpiryCronJob job = new(
            repository,
            Mock.Of<IAbuseGuard>(),
            Mock.Of<ILogger<IpBanExpiryCronJob>>()
        );

        await job.ExecuteAsync(string.Empty, CancellationToken.None);

        repository.Rows.Should().ContainSingle();
    }

    [Fact]
    public void CronExpression_RunsHourly()
    {
        IpBanExpiryCronJob job = new(
            new InMemoryIpBanRepository(),
            Mock.Of<IAbuseGuard>(),
            Mock.Of<ILogger<IpBanExpiryCronJob>>()
        );

        job.CronExpression.Should().Be("0 * * * *");
    }
}
