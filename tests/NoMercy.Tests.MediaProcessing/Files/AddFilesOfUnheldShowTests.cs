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
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MovieFileLibrary;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.MediaProcessing.Files;
using NoMercy.MediaProcessing.Files.Parsing;
using NoMercy.MediaProcessing.Files.Parsing.Adapters;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.NmSystem.Domain;
using NoMercy.Providers.Helpers;
using NoMercy.Tests.Common.Providers;
using IJobDispatcher = NoMercy.MediaProcessing.Jobs.IJobDispatcher;
using IShouldQueue = NoMercyQueue.Core.Interfaces.IShouldQueue;

namespace NoMercy.Tests.MediaProcessing.Files;

/// <summary>
/// Add content on a TV library, for a show the server does not hold yet. Ten selected
/// episode rows used to queue ten encodes that each found no episode row and returned
/// without a word. The show is imported once, and its selected files are encoded after.
/// </summary>
[Collection("HttpClientProvider")]
public class AddFilesOfUnheldShowTests : ProviderHttpHarness
{
    private const int ShowId = 771003;
    private const string ReleaseFolder = "The.Ark.S01.1080p.AMZN.WEBRip.DDP5.1.x264-NTb[rartv]";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public AddFilesOfUnheldShowTests()
        : base("TMDB")
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;
        using MediaContext ctx = new(_options);
        ctx.Database.EnsureCreated();
    }

    public override void Dispose()
    {
        _connection.Dispose();
        base.Dispose();
        HttpClientProvider.Initialize(new TmdbMockHttpClientFactory());
        GC.SuppressFinalize(this);
    }

    private static string FileName(int number) =>
        $"The.Ark.S01E{number:D2}.1080p.AMZN.WEB-DL.DDP5.1.H.264-NTb.mkv";

    private static List<EncodeAfterImportFile> TenFiles(Ulid folderId) =>
        [
            .. Enumerable
                .Range(1, 10)
                .Select(number => new EncodeAfterImportFile
                {
                    Id = (9000 + number).ToString(),
                    InputFile = $"/downloads/{ReleaseFolder}/{FileName(number)}",
                    FolderId = folderId,
                }),
        ];

    private static FilenameParserPipeline Pipeline() =>
        new(
            new IFilenameParseAdapter[]
            {
                new EpisodePrefixAdapter(),
                new EpisodeWordAdapter(),
                new CrossFormatAdapter(),
                new SeasonEpisodeAdapter(),
                new MovieDetectorAdapter(),
            }
        );

    private void ScriptTmdb()
    {
        Handler.WhenGet(
            "/search/tv",
            MockResponse.Json(
                HttpStatusCode.OK,
                $$"""
                {"page":1,"total_pages":1,"total_results":1,"results":[
                  {"id":{{ShowId}},"name":"The Ark","first_air_date":"2023-02-01"}
                ]}
                """
            )
        );
    }

    [Fact]
    public async Task Ten_files_of_one_unheld_show_queue_one_show_import_and_no_encode()
    {
        ScriptTmdb();
        Library library = new() { Id = Ulid.NewUlid(), Type = MediaTypes.TvMediaType };
        Mock<IJobDispatcher> dispatcher = new();
        List<ShowImportJob> imports = [];
        dispatcher
            .Setup(d => d.DispatchJob(It.IsAny<ShowImportJob>()))
            .Callback<ShowImportJob>(imports.Add);

        List<EncodeAfterImportFile> remaining = await UnheldShowImports.DispatchAsync(
            dispatcher.Object,
            Pipeline(),
            library,
            TenFiles(Ulid.NewUlid()),
            new HashSet<int>()
        );

        imports.Should().ContainSingle();
        imports[0].Id.Should().Be(ShowId);
        imports[0].LibraryId.Should().Be(library.Id);
        imports[0].AddedBy.Should().Be(LibraryLinkOrigin.Manual);
        imports[0].EncodeAfterImport.Should().HaveCount(10);
        remaining.Should().BeEmpty("no encode is queued for a file whose show is not imported yet");
        dispatcher.Verify(
            d => d.Dispatch(It.IsAny<IShouldQueue>(), It.IsAny<string>(), It.IsAny<int>()),
            Times.Never
        );
    }

    [Fact]
    public async Task Files_of_a_held_episode_keep_the_ordinary_path()
    {
        ScriptTmdb();
        Library library = new() { Id = Ulid.NewUlid(), Type = MediaTypes.TvMediaType };
        Mock<IJobDispatcher> dispatcher = new();
        List<EncodeAfterImportFile> files = TenFiles(Ulid.NewUlid());

        List<EncodeAfterImportFile> remaining = await UnheldShowImports.DispatchAsync(
            dispatcher.Object,
            Pipeline(),
            library,
            files,
            files.Select(file => int.Parse(file.Id)).ToHashSet()
        );

        remaining.Should().HaveCount(10);
        dispatcher.Verify(d => d.DispatchJob(It.IsAny<ShowImportJob>()), Times.Never);
    }

    [Fact]
    public void An_import_with_ten_listed_files_queues_ten_encodes_after_it()
    {
        Ulid libraryId = Ulid.NewUlid();
        ShowImportJob import = new()
        {
            Id = ShowId,
            LibraryId = libraryId,
            EncodeAfterImport = TenFiles(Ulid.NewUlid()),
        };
        Mock<IJobDispatcher> dispatcher = new();
        List<IShouldQueue> queued = [];
        dispatcher
            .Setup(d => d.Dispatch(It.IsAny<IShouldQueue>(), It.IsAny<string>(), It.IsAny<int>()))
            .Callback<IShouldQueue, string, int>((job, _, _) => queued.Add(job));

        import.DispatchEncodes(dispatcher.Object);

        queued.Should().HaveCount(10).And.AllBeOfType<VideoEncodeJob>();
        queued
            .Cast<VideoEncodeJob>()
            .Select(job => job.LibraryId)
            .Should()
            .OnlyContain(id => id == libraryId);
    }

    [Fact]
    public void An_import_with_no_listed_files_queues_no_encode()
    {
        ShowImportJob import = new() { Id = ShowId, LibraryId = Ulid.NewUlid() };
        Mock<IJobDispatcher> dispatcher = new();

        import.DispatchEncodes(dispatcher.Object);

        dispatcher.Verify(
            d => d.Dispatch(It.IsAny<IShouldQueue>(), It.IsAny<string>(), It.IsAny<int>()),
            Times.Never
        );
    }

    [Fact]
    public void A_payload_queued_before_the_property_existed_still_deserializes()
    {
        ShowImportJob? job = Newtonsoft.Json.JsonConvert.DeserializeObject<ShowImportJob>(
            $"{{\"Id\":{ShowId},\"HighPriority\":false}}"
        );

        job.Should().NotBeNull();
        job!.EncodeAfterImport.Should().BeEmpty();
    }

    private sealed class CapturingLogger(List<string> warnings) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel == LogLevel.Warning)
                warnings.Add(formatter(state, exception));
        }
    }

    [Fact]
    public async Task A_missing_episode_logs_a_warning_with_the_path_and_the_id()
    {
        List<string> warnings = [];
        Mock<ILoggerFactory> loggerFactory = new();
        loggerFactory
            .Setup(f => f.CreateLogger(It.IsAny<string>()))
            .Returns(new CapturingLogger(warnings));

        VideoEncodeJob job = new()
        {
            Id = "424242",
            InputFile = "/downloads/The.Ark.S01E01.mkv",
            LoggerFactory = loggerFactory.Object,
        };
        Folder folder = new()
        {
            FolderLibraries =
            [
                new FolderLibrary { Library = new() { Type = MediaTypes.TvMediaType } },
            ],
        };

        await using MediaContext context = new(_options);
        VideoEncodeJob.FileMetadata result = await job.GetFileMetaData(folder, context);

        result.Success.Should().BeFalse();
        warnings.Should().ContainSingle();
        warnings[0].Should().Contain("424242").And.Contain("/downloads/The.Ark.S01E01.mkv");
    }
}
