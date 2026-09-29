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
using NoMercy.NmSystem.Auth;
using NoMercy.PluginSdk.Telemetry;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// One slow SaaS call must never take the host down. An HttpClient timeout
/// surfaces as a <see cref="TaskCanceledException"/>, which IS an
/// <see cref="OperationCanceledException"/> — a catch that excludes every
/// <c>OperationCanceledException</c> lets a timeout escape uncaught into the
/// BackgroundService that owns this sink, which stops the host.
/// </summary>
public class PluginHttpTelemetrySinkTests
{
    private sealed class StubTokens : IAuthTokenStore
    {
        public string? AccessToken { get; private set; } = "token";

        public void SetAccessToken(string? token) => AccessToken = token;

#pragma warning disable CS0067
        public event EventHandler<string?>? AccessTokenChanged;
#pragma warning restore CS0067
    }

    private sealed class TimingOutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            // What HttpClient.Timeout produces: the request's own linked token
            // fires, not the caller's — the caller's token is untouched.
            throw new TaskCanceledException("The request timed out.", new TimeoutException());
    }

    [Fact]
    public async Task SendAsync_WhenHttpClientTimesOut_DoesNotThrow_WhenCallerTokenIsNotCancelled()
    {
        using HttpClient http = new(new TimingOutHandler()) { BaseAddress = new("https://nomercy.tv") };

        PluginHttpTelemetrySink sink = new(
            http,
            new StubTokens(),
            NullLogger<PluginHttpTelemetrySink>.Instance
        );

        PluginTelemetryReport report = new(Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);

        // The caller's token (CancellationToken.None) was never asked to cancel;
        // only the HttpClient timeout fired. That must be swallowed and logged,
        // never rethrown.
        Func<Task> act = () => sink.SendAsync(report, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
