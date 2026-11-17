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

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NoMercy.NmSystem.Status;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Service;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

/// <summary>
/// GET dashboard/server/update/check answers every signed-in user, as it always did.
/// Only a moderator's call may make the server ask GitHub again (through the throttled
/// <see cref="IUpdateChecker.RefreshAsync"/>); anyone else reads the last known status
/// and changes nothing.
/// </summary>
[Trait("Category", "Authorization")]
public sealed class UpdateCheckAccessTests : IClassFixture<NoMercyApiFactory>, IDisposable
{
    private const string Url = "/api/v1/dashboard/server/update/check";

    private sealed class CountingUpdateChecker : IUpdateChecker
    {
        private int _checks;
        private int _refreshes;

        public int Checks => _checks;

        public int Refreshes => _refreshes;

        public Task<bool> IsUpdateAvailableAsync()
        {
            Interlocked.Increment(ref _checks);
            return Task.FromResult(false);
        }

        public Task<bool> RefreshAsync()
        {
            Interlocked.Increment(ref _refreshes);
            return Task.FromResult(false);
        }
    }

    private readonly CountingUpdateChecker _checker = new();
    private readonly WebApplicationFactory<Startup> _host;

    public UpdateCheckAccessTests(NoMercyApiFactory factory)
    {
        _host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUpdateChecker>();
                services.AddSingleton<IUpdateChecker>(_checker);
            })
        );
    }

    public void Dispose() => _host.Dispose();

    private static async Task<bool> UpdateAvailableAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("updateAvailable").GetBoolean();
    }

    [Fact]
    public async Task NonModerator_ReadsTheLastKnownStatus_WithoutAnyCheck()
    {
        IUpdateStatus status = _host.Services.GetRequiredService<IUpdateStatus>();
        bool before = status.UpdateAvailable;
        string? latestBefore = status.LatestVersion;
        status.UpdateAvailable = true;
        try
        {
            HttpResponseMessage response = await _host
                .CreateClient()
                .AsSecondaryUser()
                .GetAsync(Url);

            (await UpdateAvailableAsync(response)).Should().BeTrue();
            _checker.Refreshes.Should().Be(0);
            _checker.Checks.Should().Be(0);
            status.LatestVersion.Should().Be(latestBefore);
        }
        finally
        {
            status.UpdateAvailable = before;
        }
    }

    [Fact]
    public async Task Owner_AsksForAThrottledRefresh()
    {
        HttpResponseMessage response = await _host.CreateClient().AsAuthenticated().GetAsync(Url);

        (await UpdateAvailableAsync(response)).Should().BeFalse();
        _checker.Refreshes.Should().Be(1);
        _checker.Checks.Should().Be(0);
    }
}
