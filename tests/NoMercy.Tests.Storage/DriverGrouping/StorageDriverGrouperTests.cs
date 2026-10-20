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

using NoMercy.Storage.DriverGrouping;

namespace NoMercy.Tests.Storage.DriverGrouping;

[Trait("Category", "Unit")]
public class StorageDriverGrouperTests
{
    // -----------------------------------------------------------------------
    // DetectEndpoint
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData([@"\\192.168.1.1\Media\Anime", @"\\192.168.1.1\Media", StorageEndpointKind.Smb])]
    [InlineData([@"\\192.168.1.1\Media\Movies", @"\\192.168.1.1\Media", StorageEndpointKind.Smb])]
    [InlineData([@"\\NAS\Share\Sub\Folder", @"\\NAS\Share", StorageEndpointKind.Smb])]
    [InlineData([@"\\server\share", @"\\server\share", StorageEndpointKind.Smb])]
    public void DetectEndpoint_UncPath_ReturnsSmbEndpoint(
        string path,
        string expectedKey,
        StorageEndpointKind expectedKind
    )
    {
        StorageEndpoint endpoint = StorageDriverGrouper.DetectEndpoint(path);

        endpoint.Key.Should().Be(expectedKey);
        endpoint.Kind.Should().Be(expectedKind);
    }

    [Theory]
    [InlineData([@"C:\Media\Movies", "C:", StorageEndpointKind.Local])]
    [InlineData([@"D:\NAS\Anime", "D:", StorageEndpointKind.Local])]
    [InlineData([@"E:\", "E:", StorageEndpointKind.Local])]
    public void DetectEndpoint_WindowsDrivePath_ReturnsLocalEndpoint(
        string path,
        string expectedKey,
        StorageEndpointKind expectedKind
    )
    {
        StorageEndpoint endpoint = StorageDriverGrouper.DetectEndpoint(path);

        endpoint.Key.Should().Be(expectedKey);
        endpoint.Kind.Should().Be(expectedKind);
    }

    [Fact]
    public void DetectEndpoint_PosixPath_ReturnsLocalEndpointWithSlashKey()
    {
        StorageEndpoint endpoint = StorageDriverGrouper.DetectEndpoint("/mnt/nas/media");

        endpoint.Key.Should().Be("/");
        endpoint.Kind.Should().Be(StorageEndpointKind.Local);
    }

    // -----------------------------------------------------------------------
    // ComputeCommonAncestor
    // -----------------------------------------------------------------------

    [Fact]
    public void ComputeCommonAncestor_SinglePath_ReturnsThatPath()
    {
        string result = StorageDriverGrouper.ComputeCommonAncestor(
            [@"C:\Media\Movies"],
            StorageEndpointKind.Local
        );

        result.Should().Be(@"C:\Media\Movies");
    }

    [Fact]
    public void ComputeCommonAncestor_TwoSiblingPaths_ReturnsSharedParent()
    {
        string result = StorageDriverGrouper.ComputeCommonAncestor(
            [@"C:\Media\Movies", @"C:\Media\TV"],
            StorageEndpointKind.Local
        );

        result.Should().Be(@"C:\Media");
    }

    [Fact]
    public void ComputeCommonAncestor_FourUncPaths_ReturnsUncShare()
    {
        List<string> paths =
        [
            @"\\192.168.1.1\Media\Anime",
            @"\\192.168.1.1\Media\Movies",
            @"\\192.168.1.1\Media\TV",
            @"\\192.168.1.1\Media\Music",
        ];

        string result = StorageDriverGrouper.ComputeCommonAncestor(paths, StorageEndpointKind.Smb);

        result.Should().Be(@"\\192.168.1.1\Media");
    }

    [Fact]
    public void ComputeCommonAncestor_NestedPaths_ReturnsDeepestCommonAncestor()
    {
        List<string> paths =
        [
            @"C:\Media\TV\Anime",
            @"C:\Media\TV\Western",
            @"C:\Media\TV\Anime\2024",
        ];

        string result = StorageDriverGrouper.ComputeCommonAncestor(
            paths,
            StorageEndpointKind.Local
        );

        result.Should().Be(@"C:\Media\TV");
    }

    // -----------------------------------------------------------------------
    // ComputeSubPath
    // -----------------------------------------------------------------------

    [Fact]
    public void ComputeSubPath_FolderEqualsRoot_ReturnsEmpty()
    {
        string result = StorageDriverGrouper.ComputeSubPath(
            @"C:\Media\Anime",
            @"C:\Media\Anime",
            StorageEndpointKind.Local
        );

        result.Should().BeEmpty();
    }

    [Fact]
    public void ComputeSubPath_FolderUnderRoot_ReturnsRelativeSegment()
    {
        string result = StorageDriverGrouper.ComputeSubPath(
            @"C:\Media",
            @"C:\Media\Anime",
            StorageEndpointKind.Local
        );

        result.Should().Be("Anime");
    }

    [Fact]
    public void ComputeSubPath_UncFolderUnderShare_ReturnsRelativeName()
    {
        string result = StorageDriverGrouper.ComputeSubPath(
            @"\\192.168.1.1\Media",
            @"\\192.168.1.1\Media\Movies",
            StorageEndpointKind.Smb
        );

        result.Should().Be("Movies");
    }

    [Fact]
    public void ComputeSubPath_ForwardSlashFolderUnderBackslashRoot_ReturnsRelativePathInRootSeparator()
    {
        // The common ancestor is always joined with the drive separator, but a
        // stored folder path may still use '/'; the sub path must not depend on
        // which style the row happens to hold.
        string result = StorageDriverGrouper.ComputeSubPath(
            @"C:\Media",
            "C:/Media/TV/Anime",
            StorageEndpointKind.Local
        );

        result.Should().Be(@"TV\Anime");
    }

    // -----------------------------------------------------------------------
    // Group — primary scenarios
    // -----------------------------------------------------------------------

    [Fact]
    public void Group_EmptyInput_ReturnsEmpty()
    {
        IReadOnlyList<DriverGroup> result = StorageDriverGrouper.Group([]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Group_SingleFolder_ProducesOneDriveRootedAtThatFolder()
    {
        Ulid folderId = Ulid.NewUlid();
        FolderRootInput[] inputs = [new(folderId, @"C:\Media\Movies")];

        IReadOnlyList<DriverGroup> groups = StorageDriverGrouper.Group(inputs);

        groups.Should().HaveCount(1);
        DriverGroup group = groups[0];
        group.DriverRoot.Should().Be(@"C:\Media\Movies");
        group.DriverType.Should().Be("local");
        group.Folders.Should().ContainSingle(a => a.FolderId == folderId && a.SubPath == "");
    }

    [Fact]
    public void Group_FourUncFoldersUnderOneShare_ProducesOneLocalDriverWithFourSubPaths()
    {
        Ulid animeId = Ulid.NewUlid();
        Ulid moviesId = Ulid.NewUlid();
        Ulid tvId = Ulid.NewUlid();
        Ulid musicId = Ulid.NewUlid();

        FolderRootInput[] inputs =
        [
            new(animeId, @"\\192.168.1.1\Media\Anime"),
            new(moviesId, @"\\192.168.1.1\Media\Movies"),
            new(tvId, @"\\192.168.1.1\Media\TV"),
            new(musicId, @"\\192.168.1.1\Media\Music"),
        ];

        IReadOnlyList<DriverGroup> groups = StorageDriverGrouper.Group(inputs);

        groups.Should().HaveCount(1);
        DriverGroup group = groups[0];
        group.DriverType.Should().Be("local");
        group.DriverRoot.Should().Be(@"\\192.168.1.1\Media");
        group.Folders.Should().HaveCount(4);
        group.Folders.Should().Contain(a => a.FolderId == animeId && a.SubPath == "Anime");
        group.Folders.Should().Contain(a => a.FolderId == moviesId && a.SubPath == "Movies");
        group.Folders.Should().Contain(a => a.FolderId == tvId && a.SubPath == "TV");
        group.Folders.Should().Contain(a => a.FolderId == musicId && a.SubPath == "Music");
    }

    [Fact]
    public void Group_TwoDifferentDrives_ProducesTwoLocalDrivers()
    {
        Ulid movieId = Ulid.NewUlid();
        Ulid tvId = Ulid.NewUlid();

        FolderRootInput[] inputs = [new(movieId, @"C:\Media\Movies"), new(tvId, @"D:\Media\TV")];

        IReadOnlyList<DriverGroup> groups = StorageDriverGrouper.Group(inputs);

        groups.Should().HaveCount(2);
        groups.Select(g => g.DriverType).Should().AllBe("local");
        groups.Should().Contain(g => g.Folders.Any(a => a.FolderId == movieId));
        groups.Should().Contain(g => g.Folders.Any(a => a.FolderId == tvId));
    }

    [Fact]
    public void Group_TwoDifferentUncShares_ProducesTwoDriversNotMerged()
    {
        Ulid movies1Id = Ulid.NewUlid();
        Ulid movies2Id = Ulid.NewUlid();

        FolderRootInput[] inputs =
        [
            new(movies1Id, @"\\nas1\Media\Movies"),
            new(movies2Id, @"\\nas2\Media\Movies"),
        ];

        IReadOnlyList<DriverGroup> groups = StorageDriverGrouper.Group(inputs);

        groups.Should().HaveCount(2);
        groups.Select(g => g.DriverType).Should().AllBe("local");
        groups.Select(g => g.DriverRoot).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Group_MixedLocalSubTreesUnderSameDrive_ProducesOneDriverWithLca()
    {
        Ulid animesId = Ulid.NewUlid();
        Ulid moviesId = Ulid.NewUlid();
        Ulid musicId = Ulid.NewUlid();

        FolderRootInput[] inputs =
        [
            new(animesId, @"C:\Data\Media\Anime"),
            new(moviesId, @"C:\Data\Media\Movies"),
            new(musicId, @"C:\Data\Media\Music"),
        ];

        IReadOnlyList<DriverGroup> groups = StorageDriverGrouper.Group(inputs);

        groups.Should().HaveCount(1);
        DriverGroup group = groups[0];
        group.DriverRoot.Should().Be(@"C:\Data\Media");
        group.DriverType.Should().Be("local");
        group.Folders.Should().HaveCount(3);
        group.Folders.Should().Contain(a => a.FolderId == animesId && a.SubPath == "Anime");
        group.Folders.Should().Contain(a => a.FolderId == moviesId && a.SubPath == "Movies");
        group.Folders.Should().Contain(a => a.FolderId == musicId && a.SubPath == "Music");
    }

    [Fact]
    public void Group_WindowsFoldersStoredWithForwardSlashes_ProducesSubPathsRelativeToTheSharedRoot()
    {
        Ulid moviesId = Ulid.NewUlid();
        Ulid tvId = Ulid.NewUlid();

        FolderRootInput[] inputs = [new(moviesId, "C:/Media/Movies"), new(tvId, "C:/Media/TV")];

        IReadOnlyList<DriverGroup> groups = StorageDriverGrouper.Group(inputs);

        groups.Should().HaveCount(1);
        DriverGroup group = groups[0];
        group.DriverRoot.Should().Be(@"C:\Media");
        group.Folders.Should().Contain(a => a.FolderId == moviesId && a.SubPath == "Movies");
        group.Folders.Should().Contain(a => a.FolderId == tvId && a.SubPath == "TV");
    }

    [Fact]
    public void Group_LcaCorrectness_TwoPathsDifferingAtFirstSegment_RootsAtDrive()
    {
        Ulid aId = Ulid.NewUlid();
        Ulid bId = Ulid.NewUlid();

        FolderRootInput[] inputs = [new(aId, @"C:\Alpha\Movies"), new(bId, @"C:\Beta\TV")];

        IReadOnlyList<DriverGroup> groups = StorageDriverGrouper.Group(inputs);

        groups.Should().HaveCount(1);
        DriverGroup group = groups[0];
        group.DriverRoot.Should().Be(@"C:\");
        group.Folders.Should().HaveCount(2);
    }

    [Fact]
    public void Group_UncAndLocalFolders_NeverMergedAcrossEndpoints()
    {
        Ulid localId = Ulid.NewUlid();
        Ulid uncId = Ulid.NewUlid();

        FolderRootInput[] inputs =
        [
            new(localId, @"C:\Media\Movies"),
            new(uncId, @"\\nas1\Media\Movies"),
        ];

        IReadOnlyList<DriverGroup> groups = StorageDriverGrouper.Group(inputs);

        groups.Should().HaveCount(2);
        groups.Select(g => g.DriverType).Should().AllBe("local");
        groups.Should().Contain(g => g.DriverRoot.StartsWith(@"C:"));
        groups.Should().Contain(g => g.DriverRoot.StartsWith(@"\\nas1\Media"));
    }
}
