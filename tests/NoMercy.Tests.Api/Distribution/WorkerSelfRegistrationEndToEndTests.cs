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

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Distribution;
using NoMercy.Encoder.Hardware;
using NoMercy.NmSystem.Information;
using NoMercy.Service;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Distribution;

/// <summary>
/// A remote encoder worker registers with, heartbeats to and unregisters from a
/// coordinator (#228). The coordinator here is the real server pipeline
/// (HmacValidationMiddleware, authorization, AccessLogMiddleware and
/// WorkersController) with no bearer token, which is what a headless worker sends.
/// The worker side is the real <see cref="WorkerSelfRegistrationService"/> talking
/// through the named client AddNoMercyEncoder wires, signing handler included;
/// only its transport is pointed at the in-memory coordinator.
/// </summary>
[Trait("Category", "Integration")]
public class WorkerSelfRegistrationEndToEndTests(AnonymousNoMercyApiFactory factory)
    : IClassFixture<AnonymousNoMercyApiFactory>
{
    private const string SigningKey = "end-to-end-distribution-signing-key";

    [Fact]
    public async Task Worker_RegistersHeartbeatsAndUnregisters_WithTheCoordinator()
    {
        using WebApplicationFactory<Startup> coordinator = StartCoordinator(SigningKey);
        InMemoryRemoteWorkerRegistry registry =
            coordinator.Services.GetRequiredService<InMemoryRemoteWorkerRegistry>();
        ResponseRecorder recorder = new();
        await using ServiceProvider worker = BuildWorker(
            coordinator,
            SigningKey,
            "e2e-worker",
            recorder
        );
        WorkerSelfRegistrationService service = CreateService(worker);

        using CancellationTokenSource stopping = new();
        await service.StartAsync(stopping.Token);
        Task<RecordedResponse> heartbeat = recorder.WaitForAsync("/heartbeat");
        await Task.WhenAny(heartbeat, Task.Delay(TimeSpan.FromSeconds(10)));

        RecordedResponse register = recorder.First("/register");
        register.Status.Should().Be(HttpStatusCode.OK, "register answered {0}", register.Body);
        heartbeat.IsCompleted.Should().BeTrue("the worker must reach its first heartbeat");
        RecordedResponse firstHeartbeat = await heartbeat;
        firstHeartbeat
            .Status.Should()
            .Be(HttpStatusCode.OK, "heartbeat answered {0}", firstHeartbeat.Body);
        registry.GetActiveWorkers().Should().Contain(w => w.WorkerId == "e2e-worker");

        await stopping.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        RecordedResponse unregister = recorder.First("/e2e-worker", HttpMethod.Delete);
        unregister
            .Status.Should()
            .Be(HttpStatusCode.NoContent, "unregister answered {0}", unregister.Body);
        registry.GetActiveWorkers().Should().NotContain(w => w.WorkerId == "e2e-worker");
    }

    [Fact]
    public async Task Worker_WithADifferentKey_IsRefusedAndNotRegistered()
    {
        using WebApplicationFactory<Startup> coordinator = StartCoordinator(SigningKey);
        InMemoryRemoteWorkerRegistry registry =
            coordinator.Services.GetRequiredService<InMemoryRemoteWorkerRegistry>();
        ResponseRecorder recorder = new();
        await using ServiceProvider worker = BuildWorker(
            coordinator,
            "not-the-coordinators-key",
            "wrong-key-worker",
            recorder
        );
        WorkerSelfRegistrationService service = CreateService(worker);

        using CancellationTokenSource stopping = new();
        await service.StartAsync(stopping.Token);
        RecordedResponse register = await recorder
            .WaitForAsync("/register")
            .WaitAsync(TimeSpan.FromSeconds(10));
        await stopping.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        register.Status.Should().Be(HttpStatusCode.Unauthorized);
        ReasonOf(register.Body).Should().Be("signature_invalid");
        registry.GetActiveWorkers().Should().NotContain(w => w.WorkerId == "wrong-key-worker");
    }

    [Fact]
    public async Task Register_SignedLongAgo_IsRefused()
    {
        using WebApplicationFactory<Startup> coordinator = StartCoordinator(SigningKey);
        using HttpClient client = coordinator.CreateClient();
        long sixMinutesAgo = DateTimeOffset.UtcNow.AddMinutes(-6).ToUnixTimeSeconds();

        using HttpRequestMessage request = SignedRegister(
            "/api/v1/distribution/workers/register",
            "stale-worker",
            SigningKey,
            sixMinutesAgo
        );
        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        ReasonOf(await response.Content.ReadAsStringAsync()).Should().Be("signature_invalid");
        RegisteredIds(coordinator).Should().NotContain("stale-worker");
    }

    [Fact]
    public async Task Register_WithoutASignature_IsRefused()
    {
        using WebApplicationFactory<Startup> coordinator = StartCoordinator(SigningKey);
        using HttpClient client = coordinator.CreateClient();

        using HttpResponseMessage response = await client.PostAsync(
            "/api/v1/distribution/workers/register",
            RegisterBody("unsigned-worker")
        );

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        ReasonOf(await response.Content.ReadAsStringAsync()).Should().Be("missing_timestamp");
        RegisteredIds(coordinator).Should().NotContain("unsigned-worker");
    }

    [Fact]
    public async Task Register_OnACoordinatorWithoutAKey_IsRefused()
    {
        using WebApplicationFactory<Startup> coordinator = StartCoordinator(signingKey: null);
        using HttpClient client = coordinator.CreateClient();

        using HttpRequestMessage request = SignedRegister(
            "/api/v1/distribution/workers/register",
            "keyless-worker",
            SigningKey,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        );
        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        RegisteredIds(coordinator).Should().NotContain("keyless-worker");
    }

    // The legacy /dashboard/workers alias sits outside the HMAC middleware's
    // prefixes, so a signature proves nothing there: it must stay owner-only.
    [Theory]
    [InlineData("POST", "/api/v1/dashboard/workers/register")]
    [InlineData("POST", "/api/v1/dashboard/workers/alias-worker/heartbeat")]
    [InlineData("DELETE", "/api/v1/dashboard/workers/alias-worker")]
    public async Task LegacyAlias_WithASignatureButNoOwner_IsRefused(string method, string path)
    {
        using WebApplicationFactory<Startup> coordinator = StartCoordinator(SigningKey);
        using HttpClient client = coordinator.CreateClient();

        using HttpRequestMessage request = Signed(
            new(method),
            path,
            Encoding.UTF8.GetBytes(RegisterJson("alias-worker")),
            SigningKey,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        );
        using HttpResponseMessage response = await client.SendAsync(request);

        response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.Unauthorized,
                "body was {0}",
                await response.Content.ReadAsStringAsync()
            );
        RegisteredIds(coordinator).Should().NotContain("alias-worker");
    }

    private WebApplicationFactory<Startup> StartCoordinator(string? signingKey) =>
        factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<EncoderOptions>();
                services.AddSingleton(
                    new EncoderOptions
                    {
                        DistributedEncodingSigningKey = signingKey,
                        WorkerRegistryPath = Path.Combine(
                            AppFiles.DataPath,
                            "distribution",
                            "workers.json"
                        ),
                    }
                );
            })
        );

    private static ServiceProvider BuildWorker(
        WebApplicationFactory<Startup> coordinator,
        string signingKey,
        string workerId,
        ResponseRecorder recorder
    )
    {
        TestServer server = coordinator.Server;
        ServiceCollection services = new();
        services.AddNoMercyEncoder(opts =>
        {
            opts.DistributedEncodingSigningKey = signingKey;
            opts.CoordinatorUrl = server.BaseAddress.ToString();
            opts.WorkerSelfBaseUrl = "https://worker.example";
            opts.WorkerId = workerId;
            opts.WorkerHeartbeatInterval = TimeSpan.FromMilliseconds(100);
        });
        services
            .AddHttpClient(WorkerSelfRegistrationService.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() =>
                new RecordingHandler(recorder) { InnerHandler = server.CreateHandler() }
            );
        return services.BuildServiceProvider();
    }

    private static WorkerSelfRegistrationService CreateService(ServiceProvider worker) =>
        new(
            new HardwareCapabilities([], 4),
            worker.GetRequiredService<EncoderOptions>(),
            worker.GetRequiredService<IHttpClientFactory>(),
            NullLogger<WorkerSelfRegistrationService>.Instance
        );

    private static List<string> RegisteredIds(WebApplicationFactory<Startup> coordinator) =>
        coordinator
            .Services.GetRequiredService<InMemoryRemoteWorkerRegistry>()
            .GetActiveWorkers()
            .Select(w => w.WorkerId)
            .ToList();

    private static HttpRequestMessage SignedRegister(
        string path,
        string workerId,
        string key,
        long timestamp
    ) =>
        Signed(
            HttpMethod.Post,
            path,
            Encoding.UTF8.GetBytes(RegisterJson(workerId)),
            key,
            timestamp
        );

    private static HttpRequestMessage Signed(
        HttpMethod method,
        string path,
        byte[] body,
        string key,
        long timestamp
    )
    {
        HttpRequestMessage request = new(method, path)
        {
            Content = new ByteArrayContent(body)
            {
                Headers = { { "Content-Type", "application/json" } },
            },
        };
        request.Headers.Add(
            HmacRequestHeaders.Timestamp,
            timestamp.ToString(CultureInfo.InvariantCulture)
        );
        request.Headers.Add(
            HmacRequestHeaders.Signature,
            new HmacSigner(key).Sign(method.Method, path, timestamp, body)
        );
        return request;
    }

    private static StringContent RegisterBody(string workerId) =>
        new(RegisterJson(workerId), Encoding.UTF8, "application/json");

    private static string RegisterJson(string workerId) =>
        $$"""{"worker_id":"{{workerId}}","base_url":"https://worker.example","cpu_cores":4}""";

    private static string? ReasonOf(string body)
    {
        using JsonDocument json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("reason", out JsonElement reason)
            ? reason.GetString()
            : null;
    }

    private sealed record RecordedResponse(
        HttpMethod Method,
        string Path,
        HttpStatusCode Status,
        string Body
    );

    /// <summary>Keeps every coordinator answer the worker received, in order.</summary>
    private sealed class ResponseRecorder
    {
        private readonly List<RecordedResponse> _responses = [];
        private readonly List<(
            string Suffix,
            TaskCompletionSource<RecordedResponse> Signal
        )> _waiters = [];

        public void Add(RecordedResponse response)
        {
            lock (_responses)
            {
                _responses.Add(response);
                foreach ((string suffix, TaskCompletionSource<RecordedResponse> signal) in _waiters)
                    if (response.Path.EndsWith(suffix, StringComparison.Ordinal))
                        signal.TrySetResult(response);
            }
        }

        public Task<RecordedResponse> WaitForAsync(string pathSuffix)
        {
            lock (_responses)
            {
                RecordedResponse? seen = _responses.FirstOrDefault(r =>
                    r.Path.EndsWith(pathSuffix, StringComparison.Ordinal)
                );
                if (seen is not null)
                    return Task.FromResult(seen);

                TaskCompletionSource<RecordedResponse> signal = new(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                _waiters.Add((pathSuffix, signal));
                return signal.Task;
            }
        }

        public RecordedResponse First(string pathSuffix, HttpMethod? method = null)
        {
            lock (_responses)
            {
                RecordedResponse? match = _responses.FirstOrDefault(r =>
                    r.Path.EndsWith(pathSuffix, StringComparison.Ordinal)
                    && (method is null || r.Method == method)
                );
                match
                    .Should()
                    .NotBeNull(
                        "the worker should have sent {0}; it sent: {1}",
                        pathSuffix,
                        string.Join(
                            ", ",
                            _responses.Select(r => $"{r.Method} {r.Path} {(int)r.Status}")
                        )
                    );
                return match!;
            }
        }
    }

    /// <summary>Sits between the signing handler and the in-memory coordinator.</summary>
    private sealed class RecordingHandler(ResponseRecorder recorder) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            recorder.Add(
                new(request.Method, request.RequestUri!.AbsolutePath, response.StatusCode, body)
            );
            return response;
        }
    }
}
