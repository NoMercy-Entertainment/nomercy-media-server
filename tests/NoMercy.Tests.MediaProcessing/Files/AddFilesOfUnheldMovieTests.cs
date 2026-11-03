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
using Moq;
using NoMercy.Database.Models.Libraries;
using NoMercy.MediaProcessing.Files;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.NmSystem.Domain;
using IJobDispatcher = NoMercy.MediaProcessing.Jobs.IJobDispatcher;
using IShouldQueue = NoMercyQueue.Core.Interfaces.IShouldQueue;

namespace NoMercy.Tests.MediaProcessing.Files;

/// <summary>
/// Add content on a movie library, for a movie the server does not hold yet. The encode
/// found no movie row and the file was dropped. The movie is imported first, and its
/// selected file is encoded after.
/// </summary>
public class AddFilesOfUnheldMovieTests
{
    private const int MovieId = 900123;
    private const string InputFile = "/downloads/Example.Movie.2026.1080p.WEB-DL.x264.mkv";

    private static EncodeAfterImportFile File(string id, Ulid folderId) =>
        new()
        {
            Id = id,
            InputFile = InputFile,
            FolderId = folderId,
        };

    private static Library MovieLibrary() =>
        new() { Id = Ulid.NewUlid(), Type = MediaTypes.MovieMediaType };

    [Fact]
    public void An_unheld_movie_queues_one_movie_import_and_no_encode()
    {
        Library library = MovieLibrary();
        Mock<IJobDispatcher> dispatcher = new();
        List<MovieImportJob> imports = [];
        dispatcher
            .Setup(d => d.DispatchJob(It.IsAny<MovieImportJob>()))
            .Callback<MovieImportJob>(imports.Add);
        EncodeAfterImportFile file = File(MovieId.ToString(), Ulid.NewUlid());

        List<EncodeAfterImportFile> remaining = UnheldMovieImports.Dispatch(
            dispatcher.Object,
            library,
            [file],
            new HashSet<int>()
        );

        imports.Should().ContainSingle();
        imports[0].Id.Should().Be(MovieId);
        imports[0].LibraryId.Should().Be(library.Id);
        imports[0].EncodeAfterImport.Should().ContainSingle().Which.Should().Be(file);
        remaining.Should().BeEmpty("no encode is queued for a movie that is not imported yet");
        dispatcher.Verify(
            d => d.Dispatch(It.IsAny<IShouldQueue>(), It.IsAny<string>(), It.IsAny<int>()),
            Times.Never
        );
    }

    [Fact]
    public void A_held_movie_keeps_the_ordinary_path()
    {
        Mock<IJobDispatcher> dispatcher = new();
        EncodeAfterImportFile file = File(MovieId.ToString(), Ulid.NewUlid());

        List<EncodeAfterImportFile> remaining = UnheldMovieImports.Dispatch(
            dispatcher.Object,
            MovieLibrary(),
            [file],
            new HashSet<int> { MovieId }
        );

        remaining.Should().ContainSingle().Which.Should().Be(file);
        dispatcher.Verify(d => d.DispatchJob(It.IsAny<MovieImportJob>()), Times.Never);
    }

    [Fact]
    public void A_non_numeric_id_keeps_the_ordinary_path()
    {
        Mock<IJobDispatcher> dispatcher = new();
        EncodeAfterImportFile file = File("not-a-number", Ulid.NewUlid());

        List<EncodeAfterImportFile> remaining = UnheldMovieImports.Dispatch(
            dispatcher.Object,
            MovieLibrary(),
            [file],
            new HashSet<int>()
        );

        remaining.Should().ContainSingle().Which.Should().Be(file);
        dispatcher.Verify(d => d.DispatchJob(It.IsAny<MovieImportJob>()), Times.Never);
    }

    [Fact]
    public void An_import_with_a_listed_file_queues_the_encode_after_it()
    {
        Ulid libraryId = Ulid.NewUlid();
        MovieImportJob import = new()
        {
            Id = MovieId,
            LibraryId = libraryId,
            EncodeAfterImport = [File(MovieId.ToString(), Ulid.NewUlid())],
        };
        Mock<IJobDispatcher> dispatcher = new();
        List<IShouldQueue> queued = [];
        dispatcher
            .Setup(d => d.Dispatch(It.IsAny<IShouldQueue>(), It.IsAny<string>(), It.IsAny<int>()))
            .Callback<IShouldQueue, string, int>((job, _, _) => queued.Add(job));

        import.DispatchEncodes(dispatcher.Object);

        queued.Should().ContainSingle().Which.Should().BeOfType<VideoEncodeJob>();
        VideoEncodeJob encode = (VideoEncodeJob)queued[0];
        encode.LibraryId.Should().Be(libraryId);
        encode.Id.Should().Be(MovieId.ToString());
    }

    [Fact]
    public void A_payload_queued_before_the_property_existed_still_deserializes()
    {
        MovieImportJob? job = Newtonsoft.Json.JsonConvert.DeserializeObject<MovieImportJob>(
            "{\"Id\":1727734}"
        );

        job.Should().NotBeNull();
        job!.Id.Should().Be(1727734);
        job.EncodeAfterImport.Should().BeEmpty();
    }
}
