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
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.NmSystem.Auth;
using NoMercy.PluginSdk.Entitlements;
using NoMercy.PluginSdk.Revocation;
using NoMercy.PluginSdk.Verification;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// One slow nomercy.tv call must never take the host down. An HttpClient
/// timeout surfaces as a <see cref="TaskCanceledException"/>, which IS an
/// <see cref="OperationCanceledException"/> — a catch that excludes every
/// <c>OperationCanceledException</c> lets a timeout escape uncaught out of
/// <c>ExecuteAsync</c>, faulting the BackgroundService and tripping
/// <c>BackgroundServiceExceptionBehavior.StopHost</c>.
/// </summary>
public class PluginTrustRefreshServiceTests
{
    private sealed class TimingOutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            // What HttpClient.Timeout produces: the request's own linked
            // token fires, not the caller's — the caller's token is
            // untouched.
            throw new TaskCanceledException("timeout", new TimeoutException());
    }

    private sealed class NoTokens : IAuthTokenStore
    {
        // Null so RefreshOnceAsync never reaches the entitlement client —
        // keys.RefreshAsync is first in line and is the one under test.
        public string? AccessToken => null;

        public void SetAccessToken(string? token) { }

#pragma warning disable CS0067
        public event EventHandler<string?>? AccessTokenChanged;
#pragma warning restore CS0067
    }

    private static PluginTrustRefreshService MakeService()
    {
        // Drives the real code path: the fake handler makes the real
        // PluginMarketplaceKeyClient's HttpClient.GetStringAsync throw a
        // timeout, exactly as production HttpClient.Timeout does. Not
        // `using` — it must outlive this method, since the clients built
        // from it are used after MakeService returns.
        HttpClient timingOutHttp = new(new TimingOutHandler()) { BaseAddress = new("https://nomercy.tv") };

        PluginMarketplaceKeyClient keys = new(
            timingOutHttp,
            new PluginMarketplaceKeys(new Dictionary<string, string>()),
            NullLogger<PluginMarketplaceKeyClient>.Instance
        );

        PluginRevocationClient revocations = new(
            timingOutHttp,
            Mock.Of<IPluginRevocationStore>(),
            Mock.Of<IPluginTrustedKeys>(),
            NullLogger<PluginRevocationClient>.Instance
        );

        PluginEntitlementClient entitlements = new(
            timingOutHttp,
            Mock.Of<IPluginEntitlementStore>(),
            Mock.Of<IPluginTrustedKeys>(),
            NullLogger<PluginEntitlementClient>.Instance
        );

        PluginTrustAddresses addresses = new(
            new("https://nomercy.tv/keys"),
            new("https://nomercy.tv/revocations"),
            () => new("https://nomercy.tv/entitlements")
        );

        return new(
            keys,
            revocations,
            entitlements,
            new NoTokens(),
            addresses,
            NullLogger<PluginTrustRefreshService>.Instance
        );
    }

    [Fact]
    public async Task RefreshOnceAsync_WhenTheKeyFetchTimesOut_DoesNotThrow_WhenCallerTokenIsNotCancelled()
    {
        PluginTrustRefreshService sut = MakeService();

        // The caller's token (CancellationToken.None) was never asked to
        // cancel; only the HttpClient timeout fired. That must be swallowed
        // and logged, never rethrown into the BackgroundService loop.
        Func<Task> act = () => sut.RefreshOnceAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
