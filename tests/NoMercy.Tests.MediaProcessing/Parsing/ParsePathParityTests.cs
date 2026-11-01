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

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MovieFileLibrary;
using NoMercy.MediaProcessing.Files;
using NoMercy.MediaProcessing.Files.Parsing;
using NoMercy.MediaProcessing.Inbox;
using NoMercy.MediaProcessing.Shows;
using NoMercy.NmSystem.Dto;
using NoMercy.Providers.TMDB.Models.Shared;
using NoMercy.Storage;

namespace NoMercy.Tests.MediaProcessing.Parsing;

/// <summary>
/// The file list, the inbox, the library scan and the unheld-show lookup each
/// read a name before they search for it. For the same name they must read the
/// same title, season and episode as the resolver does.
/// </summary>
public class ParsePathParityTests
{
    private const string FileList = "FileListService";
    private const string Inbox = "InboxClassifier";
    private const string LibraryScan = "MediaScan";
    private const string UnheldShow = "UnheldShowLocator";

    public static TheoryData<string, CorpusMode> PathAndMode()
    {
        TheoryData<string, CorpusMode> data = new();
        foreach (string path in new[] { FileList, Inbox, LibraryScan, UnheldShow })
        {
            foreach (CorpusMode mode in Enum.GetValues<CorpusMode>())
                data.Add(path, mode);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PathAndMode))]
    public void A_parse_path_reads_a_name_the_way_the_resolver_does(string path, CorpusMode mode)
    {
        Func<CorpusInput, Answer> parsePath = ParsePathNamed(path);
        FilenameResolver resolver = new(ProductionFilenameParser.Pipeline());

        List<string> differences = [];
        foreach (SceneCorpusEntry entry in SceneCorpus.Entries)
        {
            CorpusInput input = CorpusInput.For(entry, mode);
            string libraryType =
                path == Inbox
                    ? InboxClassifier.StructuralType(input.Path) == "tv"
                        ? "tv"
                        : "movie"
                    : input.LibraryType;
            Answer expected = Answer.Of(
                resolver.Resolve(input.FileName, input.Directory, input.Path, libraryType).Parsed
            );
            Answer actual = parsePath(input);

            // The file list drops a file with no title (FileListService.cs:263).
            if (path == FileList && expected.Title.Length == 0)
                expected = Answer.None;

            if (actual != expected)
                differences.Add($"{input.Path} | resolver {expected} | {path} {actual}");
        }

        DumpIfAsked(path, mode, differences);
        differences
            .Should()
            .BeEmpty(
                $"{path} {mode}: {differences.Count} of {SceneCorpus.Entries.Count} names differ. "
                    + string.Join(" || ", differences.Take(8))
            );
    }

    private static Func<CorpusInput, Answer> ParsePathNamed(string path)
    {
        switch (path)
        {
            case FileList:
                return FileListAnswer();
            case Inbox:
                InboxClassifier classifier = new(
                    new Mock<IInboxMetadataProbe>().Object,
                    new Mock<IInboxAudioTagReader>().Object,
                    new Mock<IMediaTypeClassifier>().Object,
                    ProductionFilenameParser.Pipeline()
                );
                return input =>
                    Answer.Of(
                        classifier.ParseName(input.Path, InboxClassifier.StructuralType(input.Path))
                    );
            case LibraryScan:
                MediaScan scan = new(new Mock<IStorageDriver>().Object);
                return input => Answer.Of(scan.ParseFileName(input.Path, input.LibraryType));
            case UnheldShow:
                IFilenameParserPipeline pipeline = ProductionFilenameParser.Pipeline();
                return input =>
                    Answer.Of(
                        UnheldShowLocator
                            .ResolveName(pipeline, input.Path, input.LibraryType)
                            .Parsed
                    );
            default:
                throw new ArgumentOutOfRangeException(nameof(path), path, null);
        }
    }

    private static Func<CorpusInput, Answer> FileListAnswer()
    {
        List<StorageEntry> current = [];
        Mock<IStorage> storage = new();
        storage.SetupGet(s => s.Driver).Returns(new Mock<IStorageDriver>().Object);
        storage
            .Setup(s => s.List(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .Returns(() => current);

        Mock<IMediaIdentificationService> identification = new();
        identification
            .Setup(i =>
                i.IdentifyAsync(
                    It.IsAny<MovieFile>(),
                    It.IsAny<string>(),
                    It.IsAny<TimeSpan?>(),
                    It.IsAny<int?>(),
                    It.IsAny<bool>(),
                    It.IsAny<DateOnly?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(((MovieOrEpisode match, string? imdbId)?)null);

        FileListService service = new(
            new Mock<IStorageDriver>().Object,
            identification.Object,
            ProductionFilenameParser.Pipeline(),
            NullLogger<FileListService>.Instance
        );

        return input =>
        {
            current = [new(input.Path, false, 1000, DateTimeOffset.UtcNow)];
            List<FileItem> items = service
                .GetFilesInDirectory("root", input.LibraryType, storage.Object)
                .GetAwaiter()
                .GetResult();

            return items.Count == 0 ? Answer.None : Answer.Of(items[0].Parsed);
        };
    }

    private static void DumpIfAsked(string path, CorpusMode mode, List<string> differences)
    {
        string? folder = Environment.GetEnvironmentVariable("NM_PARITY_DUMP");
        if (!string.IsNullOrEmpty(folder))
            File.WriteAllLines(Path.Combine(folder, $"{path}.{mode}.txt"), differences);
    }

    private sealed record Answer(string Title, int? Season, int? Episode)
    {
        public static Answer None { get; } = new(string.Empty, null, null);

        public static Answer Of(MovieFile? parsed) =>
            parsed is null
                ? None
                : new(CorpusNameNormalizer.Title(parsed.Title), parsed.Season, parsed.Episode);

        public override string ToString() => $"'{Title}' S{Season} E{Episode}";
    }
}
