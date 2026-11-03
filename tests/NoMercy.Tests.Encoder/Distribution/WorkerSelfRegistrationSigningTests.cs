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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Distribution;
using NoMercy.Encoder.Hardware;

namespace NoMercy.Tests.Encoder.Distribution;

/// <summary>
/// The coordinator answers every unsigned /api/v1/distribution/ request with 401,
/// so the client AddNoMercyEncoder wires for self-registration must sign (#228).
/// </summary>
public class WorkerSelfRegistrationSigningTests
{
    private const string Secret = "self-registration-signing-key";

    [Fact]
    public async Task RegisterRequest_FromTheWiredClient_CarriesAValidSignature()
    {
        CapturingHandler capture = new();
        ServiceCollection services = new();
        services.AddNoMercyEncoder(opts =>
        {
            opts.DistributedEncodingSigningKey = Secret;
            opts.CoordinatorUrl = "https://coordinator.example";
            opts.WorkerSelfBaseUrl = "https://worker.example";
            opts.WorkerHeartbeatInterval = TimeSpan.FromMinutes(5);
        });
        services
            .AddHttpClient(WorkerSelfRegistrationService.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => capture);
        ServiceProvider provider = services.BuildServiceProvider();

        WorkerSelfRegistrationService sut = new(
            new HardwareCapabilities([], 4),
            provider.GetRequiredService<EncoderOptions>(),
            provider.GetRequiredService<IHttpClientFactory>(),
            NullLogger<WorkerSelfRegistrationService>.Instance
        );

        using CancellationTokenSource cts = new();
        await sut.StartAsync(cts.Token);
        CapturedRequest register = await capture.FirstRequest.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        await sut.StopAsync(CancellationToken.None);

        register.Path.Should().Be("/api/v1/distribution/workers/register");
        register
            .Timestamp.Should()
            .NotBeNull("the coordinator rejects a request without a timestamp");
        register
            .Signature.Should()
            .NotBeNull("the coordinator rejects a request without a signature");

        HmacSigner signer = new(Secret);
        signer
            .Verify(
                register.Method,
                register.Path,
                long.Parse(register.Timestamp!),
                register.Body,
                register.Signature!,
                TimeSpan.FromMinutes(5)
            )
            .Should()
            .BeTrue("the signature must cover the method, path, timestamp and body that were sent");
    }

    private sealed record CapturedRequest(
        string Method,
        string Path,
        string? Timestamp,
        string? Signature,
        byte[] Body
    );

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<CapturedRequest> _first = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task<CapturedRequest> FirstRequest => _first.Task;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            byte[] body = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);

            _first.TrySetResult(
                new(
                    request.Method.Method,
                    request.RequestUri!.AbsolutePath,
                    Header(request, HmacRequestHeaders.Timestamp),
                    Header(request, HmacRequestHeaders.Signature),
                    body
                )
            );

            return new(HttpStatusCode.OK);
        }

        private static string? Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out IEnumerable<string>? values)
                ? values.FirstOrDefault()
                : null;
    }
}
