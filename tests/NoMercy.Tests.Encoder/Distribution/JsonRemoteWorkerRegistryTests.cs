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
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Encoder.Distribution;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.Jobs;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.Encoder.Distribution;

/// <summary>
/// Persistence tests for <see cref="JsonRemoteWorkerRegistry"/>.
/// Each test gets its own temp file so they run safely in parallel.
/// </summary>
public class JsonRemoteWorkerRegistryTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private readonly byte[] _signingKey = Encoding.UTF8.GetBytes(
        "test-signing-key-32-bytes-padded"
    );
    private readonly TaskSerializer _serializer = new();

    public JsonRemoteWorkerRegistryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nm-registry-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "workers.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IStorage MakeStorage() =>
        new LocalStorage(
            new LocalStorageDriver(),
            new([], new LocalStorageDriver())
        );

    private JsonRemoteWorkerRegistry BuildRegistry() =>
        new(
            inner: new(),
            filePath: _path,
            httpClientFactory: MakeHttpClientFactory(),
            serializer: _serializer,
            signingKey: _signingKey,
            logger: NullLogger<JsonRemoteWorkerRegistry>.Instance,
            storage: MakeStorage()
        );

    private static IHttpClientFactory MakeHttpClientFactory()
    {
        // Returns an HttpClient with a stub handler so rehydration doesn't
        // attempt real network calls.
        Mock<IHttpClientFactory> factory = new();
        factory
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() =>
                new(new NoOpHandler()) { BaseAddress = new("http://worker.test/") }
            );
        return factory.Object;
    }

    private HttpRemoteWorker MakeWorker(string id, string baseUrl = "http://worker.test/") =>
        new(
            workerId: id,
            http: new(new NoOpHandler()) { BaseAddress = new(baseUrl) },
            serializer: _serializer,
            signingKey: _signingKey,
            initialCapabilities: new HardwareCapabilities([], 4),
            initialBudget: new(AvailableGpuSlots: 0, AvailableCpuThreads: 4, GpuUtilization: 0),
            logger: NullLogger<HttpRemoteWorker>.Instance
        );

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public void Register_HttpRemoteWorker_WritesJsonFile()
    {
        JsonRemoteWorkerRegistry sut = BuildRegistry();

        sut.Register(MakeWorker("beast", "http://beast.local/"));

        File.Exists(_path).Should().BeTrue("registration must persist workers.json");
    }

    [Fact]
    public void RegisterAndReload_RestoresWorkers()
    {
        // First instance: register a worker, then dispose (go away).
        JsonRemoteWorkerRegistry first = BuildRegistry();
        first.Register(MakeWorker("phoenix", "http://phoenix.local/"));

        // Second instance: fresh object from the same path.
        JsonRemoteWorkerRegistry second = BuildRegistry();

        IReadOnlyList<IRemoteWorker> active = second.GetActiveWorkers();

        active.Should().HaveCount(1, "persisted worker must rehydrate on construction");
        active[0].WorkerId.Should().Be("phoenix");
    }

    [Fact]
    public void RegisterMultiple_AllPersisted_AllRehydrated()
    {
        JsonRemoteWorkerRegistry first = BuildRegistry();
        first.Register(MakeWorker("w1", "http://w1.local/"));
        first.Register(MakeWorker("w2", "http://w2.local/"));
        first.Register(MakeWorker("w3", "http://w3.local/"));

        JsonRemoteWorkerRegistry second = BuildRegistry();

        second.GetActiveWorkers().Should().HaveCount(3, "all three workers must survive a restart");
    }

    [Fact]
    public void Unregister_RemovesFromFile_NotRehydratedAfterRestart()
    {
        JsonRemoteWorkerRegistry first = BuildRegistry();
        first.Register(MakeWorker("removable", "http://removable.local/"));
        first.Unregister("removable");

        JsonRemoteWorkerRegistry second = BuildRegistry();

        second.GetActiveWorkers().Should().BeEmpty("unregistered worker must not rehydrate");
    }

    [Fact]
    public void ReRegister_SameId_OverwritesEntry()
    {
        JsonRemoteWorkerRegistry first = BuildRegistry();
        first.Register(MakeWorker("stable", "http://stable.local/"));
        first.Register(MakeWorker("stable", "http://stable.local/")); // re-register

        JsonRemoteWorkerRegistry second = BuildRegistry();

        second
            .GetActiveWorkers()
            .Should()
            .HaveCount(1, "re-register must not create duplicate persistence entry");
    }

    [Fact]
    public void MissingFile_StartsEmpty_NoException()
    {
        // Path points at a file that doesn't exist.
        JsonRemoteWorkerRegistry sut = new(
            inner: new(),
            filePath: Path.Combine(_dir, "nonexistent.json"),
            httpClientFactory: MakeHttpClientFactory(),
            serializer: _serializer,
            signingKey: _signingKey,
            logger: NullLogger<JsonRemoteWorkerRegistry>.Instance,
            storage: MakeStorage()
        );

        sut.GetActiveWorkers().Should().BeEmpty();
    }

    [Fact]
    public void CorruptFile_StartsEmpty_NoException()
    {
        File.WriteAllText(_path, "{ this is not valid json !!!}");

        JsonRemoteWorkerRegistry sut = BuildRegistry();

        sut.GetActiveWorkers().Should().BeEmpty("corrupt file must be handled gracefully");
    }

    [Fact]
    public void Heartbeat_DelegatesInner_DoesNotAffectPersistence()
    {
        JsonRemoteWorkerRegistry sut = BuildRegistry();
        sut.Register(MakeWorker("hb", "http://hb.local/"));

        // Heartbeat should return true (worker is registered in inner).
        sut.Heartbeat("hb").Should().BeTrue();
    }

    [Fact]
    public void RecordTaskOutcome_DelegatesInner()
    {
        JsonRemoteWorkerRegistry sut = BuildRegistry();
        sut.Register(MakeWorker("tracked", "http://tracked.local/"));

        // Should not throw — delegates to inner InMemoryRemoteWorkerRegistry.
        Action act = () =>
        {
            sut.RecordTaskOutcome("tracked", success: false);
            sut.RecordTaskOutcome("tracked", success: false);
            sut.RecordTaskOutcome("tracked", success: false);
        };
        act.Should().NotThrow();

        // After 3 failures the inner registry puts the worker in cooldown.
        sut.GetActiveWorkers()
            .Should()
            .BeEmpty("3 failures must trigger cooldown via inner registry");
    }

    [Fact]
    public void GetAllWorkersWithHealth_DelegatesInner()
    {
        JsonRemoteWorkerRegistry sut = BuildRegistry();
        sut.Register(MakeWorker("healthy", "http://healthy.local/"));
        sut.Register(MakeWorker("cooling", "http://cooling.local/"));
        sut.RecordTaskOutcome("cooling", false);
        sut.RecordTaskOutcome("cooling", false);
        sut.RecordTaskOutcome("cooling", false);

        IReadOnlyList<WorkerHealthSnapshot> snapshots = sut.GetAllWorkersWithHealth();

        snapshots.Should().HaveCount(2, "health snapshot must include cooled-down workers");
    }

    // ── Stub HTTP handler ─────────────────────────────────────────────────

    private sealed class NoOpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
