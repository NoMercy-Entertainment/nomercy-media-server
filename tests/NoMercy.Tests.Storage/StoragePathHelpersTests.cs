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

namespace NoMercy.Tests.Storage;

/// <summary>
/// Pure-function tests for StoragePathHelpers. These helpers are the
/// no-IStorage-in-scope fallback used across the codebase wherever
/// generator or analyzer code needs to manipulate forward-slash paths
/// without taking a dependency. Every consumer trusts these to split
/// on '/' (Rule 2 of the IStorage path contract), never on the OS
/// separator. A regression here would silently mis-parse Windows paths.
/// </summary>
public class StoragePathHelpersTests
{
    // ── GetName ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(["a/b/c.mkv", "c.mkv"])]
    [InlineData(["just-a-name.mkv", "just-a-name.mkv"])]
    [InlineData(["a/b/", "b"])]
    [InlineData(["a/", "a"])]
    [InlineData(["/leading/file", "file"])]
    public void GetName_ReturnsLastSegment(string input, string expected)
    {
        StoragePathHelpers.GetName(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void GetName_EmptyOrNull_ReturnsEmpty(string? input)
    {
        StoragePathHelpers.GetName(input!).Should().Be(string.Empty);
    }

    [Fact]
    public void GetName_BackslashOnlyPath_RequiresCallerToNormalizeFirst()
    {
        // Rule 2: GetName splits on '/' only, by design. A raw UNC/Windows
        // path handed in without normalizing separators first returns the
        // whole string unchanged instead of the trailing segment — this is
        // exactly what let Track.Filename get stored as
        // "/\\192.168.2.120\mnt\vault\Media\...\track.mp3" instead of
        // "/track.mp3" (RecordingManager.Store, MusicLogic.StoreTrack) before
        // those call sites normalized backslashes to '/' first.
        string uncPath = "\\\\192.168.2.120\\mnt\\vault\\Media\\track.mp3";
        StoragePathHelpers.GetName(uncPath).Should().Be(uncPath);
        StoragePathHelpers.GetName(uncPath.Replace('\\', '/')).Should().Be("track.mp3");
    }

    // ── GetParent ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(["a/b/c.mkv", "a/b"])]
    [InlineData(["a/b", "a"])]
    [InlineData(["a/b/c/", "a/b"])]
    public void GetParent_ReturnsParentSegment(string input, string expected)
    {
        StoragePathHelpers.GetParent(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("solo.mkv")]
    [InlineData("solo")]
    [InlineData("")]
    public void GetParent_NoParent_ReturnsNull(string input)
    {
        StoragePathHelpers.GetParent(input).Should().BeNull();
    }

    [Fact]
    public void GetParent_TrailingSlashIsTrimmedBeforeSplit()
    {
        // "a/" trims to "a" — no parent.
        StoragePathHelpers.GetParent("a/").Should().BeNull();
    }

    // ── GetNameWithoutExtension ────────────────────────────────────────────

    [Theory]
    [InlineData(["a/b/c.mkv", "c"])]
    [InlineData(["c.mkv", "c"])]
    [InlineData(["multi.dot.name.mkv", "multi.dot.name"])]
    [InlineData(["noext", "noext"])]
    [InlineData(["a/b/noext", "noext"])]
    public void GetNameWithoutExtension_StripsLastDotSegment(string input, string expected)
    {
        StoragePathHelpers.GetNameWithoutExtension(input).Should().Be(expected);
    }

    [Fact]
    public void GetNameWithoutExtension_LeadingDotFile_TreatedAsExtension()
    {
        // ".bashrc" - the LastIndexOf('.') returns 0, so the slice is
        // empty. Documents the current behaviour: dotfile-style names
        // map to empty. Consumers should not rely on this for hidden-file
        // semantics — IStorage paths are forward-slash relative, not
        // POSIX-flavored.
        StoragePathHelpers.GetNameWithoutExtension(".bashrc").Should().BeEmpty();
    }

    // ── Combine ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(["a", "b", "a/b"])]
    [InlineData(["a/", "b", "a/b"])]
    [InlineData(["a", "/b", "a/b"])]
    [InlineData(["a/", "/b", "a/b"])]
    [InlineData(["a/b", "c/d", "a/b/c/d"])]
    public void Combine_JoinsWithSingleSlash(string parent, string child, string expected)
    {
        StoragePathHelpers.Combine(parent, child).Should().Be(expected);
    }

    [Theory]
    [InlineData(["", "child", "child"])]
    [InlineData(["parent", "", "parent"])]
    [InlineData(["", "", ""])]
    public void Combine_EmptyOperand_ReturnsOtherUnchanged(
        string parent,
        string child,
        string expected
    )
    {
        StoragePathHelpers.Combine(parent, child).Should().Be(expected);
    }

    [Fact]
    public void Combine_StripsBackslashesFromBothEnds()
    {
        // Windows-style backslash on the parent or leading-slash on the
        // child must not survive the join. The helper is the safety net
        // when something accidentally hands a Windows-flavored fragment
        // into a storage-relative path.
        StoragePathHelpers.Combine("a\\", "b").Should().Be("a/b");
        StoragePathHelpers.Combine("a", "\\b").Should().Be("a/b");
        StoragePathHelpers.Combine("a\\", "\\b").Should().Be("a/b");
    }

    // ── TryGetLibraryRelativeFolder ────────────────────────────────────────

    [Fact]
    public void TryGetLibraryRelativeFolder_EmptyRoot_TreatsTheDirectoryAsAlreadyRelative()
    {
        // A Folders.Path of "" means the folder IS the driver's scope root —
        // not "no root to resolve against". Before this, an empty root was
        // rejected outright, which made StoreVideoItem skip every file under
        // a root-scoped folder and, combined with FindFiles' unconditional
        // delete, permanently dropped that show's registrations (issue #55).
        bool ok = StoragePathHelpers.TryGetLibraryRelativeFolder(
            "Show.(2013)/Show.S09E01",
            "",
            out string folder
        );

        ok.Should().BeTrue();
        folder.Should().Be("/Show.(2013)/Show.S09E01");
    }

    [Fact]
    public void TryGetLibraryRelativeFolder_EmptyRootAndEmptyDirectory_ReturnsFalse()
    {
        // An empty root is a valid scope, but an empty directory still has
        // nothing to resolve — that half of the guard is unchanged.
        bool ok = StoragePathHelpers.TryGetLibraryRelativeFolder(
            string.Empty,
            string.Empty,
            out string folder
        );

        ok.Should().BeFalse();
        folder.Should().BeEmpty();
    }
}
