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
using System.Text;
using Microsoft.Extensions.Time.Testing;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Status;
using NoMercy.NmSystem.SystemCalls;

namespace NoMercy.Tests.Setup;

/// <summary>
/// A refresh someone asks for reaches GitHub at most once per
/// <see cref="UpdateChecker.MinimumRefreshInterval"/>, counting the scheduled check too;
/// in between it answers the last known status.
/// </summary>
[Trait("Category", "Unit")]
public class UpdateCheckerRefreshTests
{
    private sealed class CountingGithub : HttpMessageHandler
    {
        private int _requests;

        public int Requests => _requests;

        public string TagName { get; set; } = "v9999.0.0";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Interlocked.Increment(ref _requests);
            await Task.Delay(20, cancellationToken);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{ "tag_name": "{{TagName}}", "draft": false, "prerelease": false, "body": "" }""",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        }
    }

    private static (UpdateChecker Checker, UpdateStatus Status) Build(
        CountingGithub github,
        FakeTimeProvider clock
    )
    {
        UpdateStatus status = new();
        RuntimeServerSettings settings = new() { UpdateChannel = ReleaseChannel.Stable };
        return (new(status, settings, new HttpClient(github), clock), status);
    }

    [Fact]
    public async Task ASecondRefreshWithinTheInterval_DoesNotAskGithub_AndReturnsTheLastStatus()
    {
        CountingGithub github = new();
        FakeTimeProvider clock = new();
        (UpdateChecker checker, UpdateStatus status) = Build(github, clock);

        bool first = await checker.RefreshAsync();
        clock.Advance(UpdateChecker.MinimumRefreshInterval - TimeSpan.FromSeconds(1));
        bool second = await checker.RefreshAsync();

        github.Requests.Should().Be(1);
        first.Should().BeTrue();
        second.Should().Be(status.UpdateAvailable).And.BeTrue();
    }

    [Fact]
    public async Task ARefreshAfterTheInterval_AsksGithubAgain()
    {
        CountingGithub github = new();
        FakeTimeProvider clock = new();
        (UpdateChecker checker, _) = Build(github, clock);

        await checker.RefreshAsync();
        clock.Advance(UpdateChecker.MinimumRefreshInterval);
        await checker.RefreshAsync();

        github.Requests.Should().Be(2);
    }

    [Fact]
    public async Task TheScheduledCheck_CountsAsTheLastCheck()
    {
        CountingGithub github = new();
        FakeTimeProvider clock = new();
        (UpdateChecker checker, _) = Build(github, clock);

        await checker.IsUpdateAvailableAsync();
        await checker.RefreshAsync();

        github.Requests.Should().Be(1);
    }

    [Fact]
    public async Task TheScheduledCheck_IsNeverHeldBackByARefresh()
    {
        CountingGithub github = new();
        FakeTimeProvider clock = new();
        (UpdateChecker checker, _) = Build(github, clock);

        await checker.RefreshAsync();
        await checker.IsUpdateAvailableAsync();

        github.Requests.Should().Be(2);
    }

    [Fact]
    public async Task ManyRefreshesAtOnce_AskGithubOnce()
    {
        CountingGithub github = new();
        FakeTimeProvider clock = new();
        (UpdateChecker checker, _) = Build(github, clock);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => checker.RefreshAsync()));

        github.Requests.Should().Be(1);
    }
}
