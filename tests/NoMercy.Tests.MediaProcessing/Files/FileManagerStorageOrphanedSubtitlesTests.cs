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

using System.Reflection;
using NoMercy.Database.Models.Libraries;
using NoMercy.MediaProcessing.Files;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;

namespace NoMercy.Tests.MediaProcessing.Files;

// ---------------------------------------------------------------------------
// SelectOrphanedBitmapSubtitles / DispatchOrphanedBitmapOcrBackfill queue an
// OCR job for a bitmap subtitle (.sup/.vob/.idx/.mks) that has no text
// sibling carrying the same {lang}.{variant} — the operator-visible signal
// that OCR failed or never ran on a title encoded before the pipeline
// existed. SelectOrphanedBitmapSubtitles is pure and internal (testable
// directly); Dispatch's "job queue configured" branch needs a live
// QueueRunner singleton this test layer does not stand up — see the
// residue note in the coverage report.
// ---------------------------------------------------------------------------
[Trait("Category", "Unit")]
public sealed class FileManagerStorageOrphanedSubtitlesTests : IDisposable
{
    private readonly string _tempRoot;

    public FileManagerStorageOrphanedSubtitlesTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"nm-orphan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public void SelectOrphanedBitmapSubtitles_BitmapWithoutTextSibling_IsReturned()
    {
        IReadOnlyList<OrphanedBitmapSubtitle> orphans = FileManager.SelectOrphanedBitmapSubtitles([
            "Movie.jpn.full.sup",
        ]);

        orphans.Should().ContainSingle();
        orphans[0].Language.Should().Be("jpn");
        orphans[0].Variant.Should().Be("full");
        orphans[0].MediaTitle.Should().Be("Movie");
    }

    [Fact]
    public void SelectOrphanedBitmapSubtitles_BitmapWithTextSibling_IsExcluded()
    {
        IReadOnlyList<OrphanedBitmapSubtitle> orphans = FileManager.SelectOrphanedBitmapSubtitles([
            "Movie.jpn.full.sup",
            "Movie.jpn.full.vtt",
        ]);

        orphans.Should().BeEmpty();
    }

    [Fact]
    public void SelectOrphanedBitmapSubtitles_UnrecognizedFileName_IsIgnored()
    {
        IReadOnlyList<OrphanedBitmapSubtitle> orphans = FileManager.SelectOrphanedBitmapSubtitles([
            "README.txt",
        ]);

        orphans.Should().BeEmpty();
    }

    [Fact]
    public void SelectOrphanedBitmapSubtitles_MultipleOrphans_MixedWithTextTracks()
    {
        IReadOnlyList<OrphanedBitmapSubtitle> orphans = FileManager.SelectOrphanedBitmapSubtitles([
            "Show.jpn.full.sup", // orphan
            "Show.eng.full.sup", // has sibling
            "Show.eng.full.srt", // sibling
            "Show.fre.full.vob", // orphan
        ]);

        orphans.Should().HaveCount(2);
        orphans.Should().Contain(o => o.Language == "jpn");
        orphans.Should().Contain(o => o.Language == "fre");
        orphans.Should().NotContain(o => o.Language == "eng");
    }

    // -----------------------------------------------------------------------
    // DispatchOrphanedBitmapOcrBackfill — the "no subtitles folder" and "no
    // configured job queue" early-return branches. QueueRunner.Current is
    // process-wide static state this assembly never configures, so it is
    // null here — the exact state a headless one-shot scan (outside the
    // full service host) runs in.
    // -----------------------------------------------------------------------

    private static void InvokeDispatch(IStorage storage, Folder folder, string hostFolder)
    {
        MethodInfo method =
            typeof(FileManager).GetMethod(
                "DispatchOrphanedBitmapOcrBackfill",
                BindingFlags.NonPublic | BindingFlags.Static
            ) ?? throw new InvalidOperationException("DispatchOrphanedBitmapOcrBackfill not found");
        method.Invoke(null, [storage, folder, hostFolder]);
    }

    private static IStorage BuildLocalStorage()
    {
        LocalStorageDriver driver = new();
        return new LocalStorage(driver, new StoragePathGuard([], driver));
    }

    [Fact]
    public void DispatchOrphanedBitmapOcrBackfill_NoSubtitlesFolder_DoesNothing()
    {
        string hostDir = Path.Combine(_tempRoot, "Movie.NoSubsFolder");
        Directory.CreateDirectory(hostDir);
        Folder folder = new()
        {
            Id = Ulid.NewUlid(),
            Path = hostDir,
            DriverId = Ulid.NewUlid(),
        };

        Action act = () => InvokeDispatch(BuildLocalStorage(), folder, hostDir);

        act.Should().NotThrow();
    }

    [Fact]
    public void DispatchOrphanedBitmapOcrBackfill_SubtitlesFolderExists_NoQueueRunnerConfigured_DoesNothing()
    {
        string hostDir = Path.Combine(_tempRoot, "Movie.OrphanNoQueue");
        string subtitleDir = Path.Combine(hostDir, "subtitles");
        Directory.CreateDirectory(subtitleDir);
        File.WriteAllBytes(Path.Combine(subtitleDir, "Movie.jpn.full.sup"), [0x00]);
        Folder folder = new()
        {
            Id = Ulid.NewUlid(),
            Path = hostDir,
            DriverId = Ulid.NewUlid(),
        };

        Action act = () => InvokeDispatch(BuildLocalStorage(), folder, hostDir);

        act.Should()
            .NotThrow(
                "with no QueueRunner configured (the state outside the full service host) this must be a no-op, not a crash"
            );
    }
}
