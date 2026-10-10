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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Events;
using NoMercy.Events.FileWatcher;
using NoMercy.Events.Inbox;
using NoMercy.MediaProcessing.EventHandlers;
using NoMercy.MediaProcessing.Inbox;
using NoMercy.MediaProcessing.Shows;
using NoMercy.NmSystem.Domain;
using NoMercy.Providers.Helpers;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Factory;
using NoMercyQueue;
using NoMercyQueue.Core.Interfaces;
using NoMercyQueue.Core.Models;

namespace NoMercy.Tests.MediaProcessing.EventHandlers;

internal sealed class FileWatcherTestQueueContext : IQueueContext
{
    private int _nextId = 1;

    public List<QueueJobModel> Jobs { get; } = [];

    public void AddJob(QueueJobModel job)
    {
        job.Id = _nextId++;
        Jobs.Add(job);
    }

    public void RemoveJob(QueueJobModel job) => Jobs.RemoveAll(j => j.Id == job.Id);

    public QueueJobModel? GetNextJob(
        string queueName,
        byte maxAttempts,
        long? currentJobId,
        DateTime now
    ) => Jobs.FirstOrDefault(j => string.IsNullOrEmpty(queueName) || j.Queue == queueName);

    public QueueJobModel? FindJob(int id) => Jobs.FirstOrDefault(j => j.Id == id);

    public bool JobExists(string payload) => Jobs.Any(j => j.Payload == payload);

    public QueueJobModel? FindJobByPayloadHash(string payloadHash) => null;

    public FailedJobModel? FindFailedJobByPayloadHash(string payloadHash) => null;

    public void UpdateJob(QueueJobModel job)
    {
        int idx = Jobs.FindIndex(j => j.Id == job.Id);
        if (idx >= 0)
            Jobs[idx] = job;
    }

    public void UpdateJobPayload(int jobId, string newPayload, DateTime availableAt)
    {
        QueueJobModel? job = Jobs.FirstOrDefault(j => j.Id == jobId);
        if (job is null)
            return;
        job.Payload = newPayload;
        job.AvailableAt = availableAt;
        job.ReservedAt = null;
    }

    public void ResetAllReservedJobs()
    {
        foreach (QueueJobModel job in Jobs)
            job.ReservedAt = null;
    }

    public IReadOnlyList<QueueJobModel> GetReservedJobsOlderThan(DateTime cutoffUtc) =>
        [.. Jobs.Where(j => j.ReservedAt < cutoffUtc)];

    public IReadOnlyList<QueueJobModel> GetStrandedJobs(byte maxAttempts, byte maxInterruptions) =>
        [
            .. Jobs.Where(j =>
                j.ReservedAt == null
                && (j.Attempts >= maxAttempts || j.Interruptions >= maxInterruptions)
            ),
        ];

    public bool IsParentFailed(int parentJobId) => false;

    public void AddFailedJob(FailedJobModel failedJob) { }

    public void RemoveFailedJob(FailedJobModel failedJob) { }

    public void AddFailedJobAndRemoveJob(FailedJobModel failedJob, QueueJobModel job) =>
        RemoveJob(job);

    public FailedJobModel? FindFailedJob(int id) => null;

    public IReadOnlyList<FailedJobModel> GetFailedJobs(long? failedJobId = null) => [];

    public IReadOnlyList<CronJobModel> GetEnabledCronJobs() => [];

    public CronJobModel? FindCronJobByName(string name) => null;

    public void AddCronJob(CronJobModel cronJob) { }

    public void UpdateCronJob(CronJobModel cronJob) { }

    public void RemoveCronJob(CronJobModel cronJob) { }

    public void SaveChanges() { }

    public void Dispose() { }
}

internal sealed class FileWatcherTmdbMockFactory : IHttpClientFactory
{
    private const int MovieId = 550;
    private const int ShowId = 1396;

    public HttpClient CreateClient(string name) =>
        new(new Handler()) { BaseAddress = new("https://api.themoviedb.org/3/") };

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;

            string? body = true switch
            {
                _ when path.Contains("/search/movie") => SearchMovieJson(),
                _ when path.Contains("/search/tv") => SearchTvJson(),
                _ when path.Contains($"/movie/{MovieId}") => MovieDetailJson(),
                _ when path.Contains($"/tv/{ShowId}") => TvDetailJson(),
                _ => null,
            };

            HttpResponseMessage response = body is null
                ? new(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };

            return Task.FromResult(response);
        }

        private static string SearchMovieJson() =>
            $$"""
                {"page":1,"results":[{"id":{{MovieId}},"title":"Fight Club","original_title":"Fight Club","release_date":"1999-10-15","overview":"","popularity":10.0,"vote_average":8.4,"vote_count":10000,"backdrop_path":null,"poster_path":null,"original_language":"en","adult":false,"video":false}],"total_pages":1,"total_results":1}
                """;

        private static string SearchTvJson() =>
            $$"""
                {"page":1,"results":[{"id":{{ShowId}},"name":"Breaking Bad","original_name":"Breaking Bad","first_air_date":"2008-01-20","overview":"","popularity":10.0,"vote_average":9.5,"vote_count":10000,"backdrop_path":null,"poster_path":null,"original_language":"en","origin_country":["US"],"genre_ids":[],"type":""}],"total_pages":1,"total_results":1}
                """;

        private static string MovieDetailJson() =>
            $$"""{"id":{{MovieId}},"title":"Fight Club","release_date":"1999-10-15","overview":""}""";

        private static string TvDetailJson() =>
            $$"""{"id":{{ShowId}},"name":"Breaking Bad","first_air_date":"2008-01-20","overview":""}""";
    }
}

[Collection("EventBusProvider")]
[Trait("Category", "Integration")]
public class FileWatcherEventHandlerCascadeTests : IDisposable
{
    private static readonly int ExpectedMovieId = 550;
    private static readonly int ExpectedShowId = 1396;

    private readonly FileWatcherTestQueueContext _queueContext;
    private readonly QueueRunner _queueRunner;
    private readonly string _tempRoot;
    private readonly IStorageDriver _localDriver;
    private readonly StorageFactory _storageFactory;

    public FileWatcherEventHandlerCascadeTests()
    {
        HttpClientProvider.Initialize(new FileWatcherTmdbMockFactory());

        _queueContext = new();
        QueueConfiguration config = new() { WorkerCounts = new() { ["import"] = 0 } };
        _queueRunner = new(_queueContext, config, NullLoggerFactory.Instance);

        _tempRoot = Path.Combine(Path.GetTempPath(), "nm-fweh-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);

        _localDriver = new LocalStorageDriver();
        _storageFactory = new(_localDriver, NullLogger<StorageFactory>.Instance);
    }

    public void Dispose()
    {
        HttpClientProvider.Initialize(new TmdbMockHttpClientFactory());
        _queueRunner.StopAll();

        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch { }
    }

    private FileWatcherEventHandler MakeHandler() =>
        new(
            NullLogger<FileWatcherEventHandler>.Instance,
            new InMemoryEventBus(),
            _localDriver,
            _storageFactory
        );

    private string CreateMovieFolder(string name = "Fight Club (1999)")
    {
        string folder = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "Fight.Club.1999.mkv"), [0x00, 0x00]);
        return folder;
    }

    private string CreateTvFolder(string name = "Breaking Bad (2008)")
    {
        string folder = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "Season 01"));
        File.WriteAllBytes(
            Path.Combine(folder, "Season 01", "Breaking.Bad.S01E01.mkv"),
            [0x00, 0x00]
        );
        return folder;
    }

    [Fact]
    public async Task MovieLibrary_FileCreated_DispatchesMovieImportJob_WithCorrectTmdbId()
    {
        string movieFolder = CreateMovieFolder();

        FileWatcherEventHandler handler = MakeHandler();

        FileCreatedEvent @event = new()
        {
            FolderPath = movieFolder,
            LibraryId = Ulid.NewUlid(),
            LibraryType = MediaTypes.MovieMediaType,
        };

        await handler.OnFileCreated(@event, CancellationToken.None);

        _queueContext.Jobs.Should().ContainSingle("one movie folder → one MovieImportJob");

        QueueJobModel queued = _queueContext.Jobs[0];
        queued.Queue.Should().Be("import");

        JObject payload = JObject.Parse(queued.Payload);
        string? typeName = payload["$type"]?.Value<string>();
        typeName.Should().Contain("MovieImportJob");

        int dispatchedId = payload["id"]?.Value<int>() ?? 0;
        dispatchedId.Should().Be(ExpectedMovieId, "TMDB search returned movie id 550 (Fight Club)");

        string? libraryId = payload["libraryId"]?.Value<string>();
        libraryId.Should().Be(@event.LibraryId.ToString());
    }

    [Fact]
    public async Task TvLibrary_FileCreated_DispatchesShowImportJob_WithCorrectTmdbId()
    {
        string tvFolder = CreateTvFolder();

        FileWatcherEventHandler handler = MakeHandler();

        FileCreatedEvent @event = new()
        {
            FolderPath = tvFolder,
            LibraryId = Ulid.NewUlid(),
            LibraryType = MediaTypes.TvMediaType,
        };

        await handler.OnFileCreated(@event, CancellationToken.None);

        _queueContext.Jobs.Should().ContainSingle("one TV folder → one ShowImportJob");

        QueueJobModel queued = _queueContext.Jobs[0];
        queued.Queue.Should().Be("import");

        JObject payload = JObject.Parse(queued.Payload);
        string? typeName = payload["$type"]?.Value<string>();
        typeName.Should().Contain("ShowImportJob");

        int dispatchedId = payload["id"]?.Value<int>() ?? 0;
        dispatchedId
            .Should()
            .Be(ExpectedShowId, "TMDB search returned TV show id 1396 (Breaking Bad)");

        string? libraryId = payload["libraryId"]?.Value<string>();
        libraryId.Should().Be(@event.LibraryId.ToString());
    }

    [Fact]
    public async Task AnimeLibrary_FileCreated_DispatchesShowImportJob()
    {
        string tvFolder = CreateTvFolder("Breaking Bad Anime (2008)");

        FileWatcherEventHandler handler = MakeHandler();

        FileCreatedEvent @event = new()
        {
            FolderPath = tvFolder,
            LibraryId = Ulid.NewUlid(),
            LibraryType = MediaTypes.AnimeMediaType,
        };

        await handler.OnFileCreated(@event, CancellationToken.None);

        _queueContext.Jobs.Should().ContainSingle("anime library uses TV path → ShowImportJob");

        JObject payload = JObject.Parse(_queueContext.Jobs[0].Payload);
        string? typeName = payload["$type"]?.Value<string>();
        typeName.Should().Contain("ShowImportJob");
    }

    [Fact]
    public async Task InboxLibrary_FileCreated_DoesNotDispatchAnyJob()
    {
        string folder = Path.Combine(_tempRoot, "inbox");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "somefile.mkv"), [0x00]);

        FileWatcherEventHandler handler = MakeHandler();

        FileCreatedEvent @event = new()
        {
            FolderPath = folder,
            LibraryId = Ulid.NewUlid(),
            LibraryType = MediaTypes.InboxMediaType,
        };

        await handler.OnFileCreated(@event, CancellationToken.None);

        _queueContext
            .Jobs.Should()
            .BeEmpty(
                "inbox type exits immediately — FileWatcherEventHandler must not dispatch import jobs for inbox folders"
            );
    }

    [Fact]
    public async Task MovieLibrary_TmdbNoMatch_NoJobDispatched()
    {
        string folder = Path.Combine(_tempRoot, "XYZ Completely Unknown Movie ZZZZZ");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "movie.mkv"), [0x00]);

        HttpClientProvider.Initialize(new NoResultsTmdbFactory());

        FileWatcherEventHandler handler = MakeHandler();

        FileCreatedEvent @event = new()
        {
            FolderPath = folder,
            LibraryId = Ulid.NewUlid(),
            LibraryType = MediaTypes.MovieMediaType,
        };

        await handler.OnFileCreated(@event, CancellationToken.None);

        _queueContext
            .Jobs.Should()
            .BeEmpty("when TMDB returns no results the handler must not dispatch");
    }

    private sealed class NoResultsTmdbFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new Handler()) { BaseAddress = new("https://api.themoviedb.org/3/") };

        private sealed class Handler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken
            ) =>
                Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """{"page":1,"results":[],"total_pages":0,"total_results":0}""",
                            Encoding.UTF8,
                            "application/json"
                        ),
                    }
                );
        }
    }
}

[Collection("EventBusProvider")]
[Trait("Category", "Integration")]
public class InboxClassifierCascadeViaEventBusTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaContext _sharedContext;

    public InboxClassifierCascadeViaEventBusTests()
    {
        string dbName = Guid.NewGuid().ToString();
        _connection = new($"DataSource={dbName};Mode=Memory;Cache=Shared");
        _connection.Open();

        DbContextOptions<MediaContext> opts = new DbContextOptionsBuilder<MediaContext>()
            .UseSqlite(_connection)
            .Options;

        _sharedContext = new(opts);
        _sharedContext.Database.EnsureCreated();
        _sharedContext.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF;");
    }

    public void Dispose()
    {
        _sharedContext.Dispose();
        _connection.Dispose();
    }

    private MediaContext CreateContext()
    {
        DbContextOptions<MediaContext> opts = new DbContextOptionsBuilder<MediaContext>()
            .UseSqlite(_connection)
            .Options;
        return new(opts);
    }

    private Ulid SeedInboxLibrary(string folderPath)
    {
        Ulid libraryId = Ulid.NewUlid();
        Ulid folderId = Ulid.NewUlid();
        Ulid driverId = Ulid.NewUlid();

        _sharedContext.Libraries.Add(
            new()
            {
                Id = libraryId,
                Title = "Inbox",
                Type = MediaTypes.InboxMediaType,
            }
        );
        _sharedContext.Folders.Add(
            new()
            {
                Id = folderId,
                Path = folderPath,
                DriverId = driverId,
            }
        );
        _sharedContext.FolderLibrary.Add(new() { LibraryId = libraryId, FolderId = folderId });
        _sharedContext.SaveChanges();

        return libraryId;
    }

    private (
        InMemoryEventBus Bus,
        InboxClassifierEventHandler Handler,
        Mock<IStorageFactory> StorageMock
    ) BuildChain(string folderPath = "/inbox", string childFile = "item.mkv")
    {
        InMemoryEventBus bus = new();

        Mock<IInboxMetadataProbe> probeMock = new();
        probeMock
            .Setup(p =>
                p.SearchMoviesAsync(
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([]);
        probeMock
            .Setup(p =>
                p.SearchTvAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync([]);
        probeMock
            .Setup(p => p.LookupMusicReleaseAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CandidateMatch?)null);

        Mock<IInboxAudioTagReader> tagMock = new();
        tagMock
            .Setup(r =>
                r.ReadAsync(It.IsAny<string>(), It.IsAny<Ulid>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync((InboxAudioTags?)null);

        Mock<IStorage> storageMock = new();
        storageMock
            .Setup(s => s.List("", null, false))
            .Returns([new(childFile, false, 1024, DateTimeOffset.UtcNow)]);
        storageMock
            .Setup(s => s.CombinePath(It.IsAny<string>(), It.IsAny<string[]>()))
            .Returns<string, string[]>(
                (parent, segments) => $"{parent}/{string.Join('/', segments)}"
            );
        storageMock
            .Setup(s => s.OpenRead(It.IsAny<string>()))
            .Returns(new MemoryStream([0x1A, 0x45, 0xDF, 0xA3, 0x00, 0x00, 0x00, 0x00]));

        Mock<IStorageFactory> storageFactoryMock = new();
        storageFactoryMock
            .Setup(f => f.For(It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<string>()))
            .Returns(storageMock.Object);

        Mock<IMediaTypeClassifier> mediaTypeClassifierMock = new();
        mediaTypeClassifierMock
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<int?>()))
            .ReturnsAsync("tv");

        InboxClassifier classifier = new(
            probeMock.Object,
            tagMock.Object,
            mediaTypeClassifierMock.Object
        );
        NoMercy.MediaProcessing.Jobs.JobDispatcher jobDispatcher = new();
        InboxRoutingService routing = new(storageFactoryMock.Object, jobDispatcher);

        InboxClassifierEventHandler handler = new(
            NullLogger<InboxClassifierEventHandler>.Instance,
            bus,
            classifier,
            routing,
            CreateContext,
            storageFactoryMock.Object
        );

        return (bus, handler, storageFactoryMock);
    }

    [Fact]
    public async Task FileCreatedEvent_InboxLibrary_PropagatesTo_InboxItemDetectedEvent_ViaRealBus()
    {
        Ulid libraryId = SeedInboxLibrary("/inbox");
        (InMemoryEventBus bus, InboxClassifierEventHandler _, Mock<IStorageFactory> _) =
            BuildChain();

        List<InboxItemDetectedEvent> detected = [];
        bus.Subscribe<InboxItemDetectedEvent>(
            (evt, _) =>
            {
                detected.Add(evt);
                return Task.CompletedTask;
            }
        );

        await bus.PublishAsync(
            new FileCreatedEvent
            {
                FolderPath = "/inbox/item.mkv",
                LibraryId = libraryId,
                LibraryType = MediaTypes.InboxMediaType,
            }
        );

        detected
            .Should()
            .ContainSingle(
                "FileCreatedEvent for an inbox library must chain to InboxItemDetectedEvent"
            );
        detected[0].DetectedType.Should().NotBeNullOrEmpty();
        detected[0].Status.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task FileCreatedEvent_NonInboxLibrary_DoesNotTriggerInboxClassifier()
    {
        (
            InMemoryEventBus bus,
            InboxClassifierEventHandler _,
            Mock<IStorageFactory> storageFactoryMock
        ) = BuildChain();

        List<InboxItemDetectedEvent> detected = [];
        bus.Subscribe<InboxItemDetectedEvent>(
            (evt, _) =>
            {
                detected.Add(evt);
                return Task.CompletedTask;
            }
        );

        await bus.PublishAsync(
            new FileCreatedEvent
            {
                FolderPath = "/media/movies/Inception (2010)/Inception.mkv",
                LibraryId = Ulid.NewUlid(),
                LibraryType = MediaTypes.MovieMediaType,
            }
        );

        detected
            .Should()
            .BeEmpty(
                "InboxClassifierEventHandler ignores non-inbox library events — no InboxItemDetectedEvent must be published"
            );

        storageFactoryMock.Verify(
            f => f.For(It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<string>()),
            Times.Never,
            "storage was never accessed — the handler exited before any classification attempt"
        );
    }

    [Fact]
    public async Task FileCreatedEvent_InboxLibrary_PublishedEventCarriesCorrectPayload()
    {
        Ulid libraryId = SeedInboxLibrary("/inbox");
        (InMemoryEventBus bus, InboxClassifierEventHandler _, Mock<IStorageFactory> _) =
            BuildChain();

        InboxItemDetectedEvent? captured = null;
        bus.Subscribe<InboxItemDetectedEvent>(
            (evt, _) =>
            {
                captured = evt;
                return Task.CompletedTask;
            }
        );

        await bus.PublishAsync(
            new FileCreatedEvent
            {
                FolderPath = "/inbox/item.mkv",
                LibraryId = libraryId,
                LibraryType = MediaTypes.InboxMediaType,
            }
        );

        captured.Should().NotBeNull();
        captured!.Id.Should().NotBeNullOrEmpty("event Id must be the saved InboxItem Ulid");
        captured
            .DetectedType.Should()
            .NotBeNullOrEmpty("must have a detected type from the classifier");
        captured.Confidence.Should().NotBeNullOrEmpty();
        captured.Status.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task FileCreatedEvent_InboxLibrary_LibraryNotSeeded_NoEventPublished()
    {
        (
            InMemoryEventBus bus,
            InboxClassifierEventHandler _,
            Mock<IStorageFactory> storageFactoryMock
        ) = BuildChain();

        List<InboxItemDetectedEvent> detected = [];
        bus.Subscribe<InboxItemDetectedEvent>(
            (evt, _) =>
            {
                detected.Add(evt);
                return Task.CompletedTask;
            }
        );

        await bus.PublishAsync(
            new FileCreatedEvent
            {
                FolderPath = "/inbox/item.mkv",
                LibraryId = Ulid.NewUlid(),
                LibraryType = MediaTypes.InboxMediaType,
            }
        );

        detected
            .Should()
            .BeEmpty(
                "when the library is not in the DB the handler returns early — no event must propagate"
            );
    }
}
