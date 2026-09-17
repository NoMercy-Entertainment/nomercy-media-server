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
/// Pins the shape an older importer run wrote into <c>HostFolder</c>: the album
/// folder twice, forward slashes first, then a separator, then the same folder
/// with backslashes. Every consumer combines <c>HostFolder</c> with
/// <c>Filename</c>, so such a row addresses nothing and the track is unplayable
/// and unanalysable. The importer refuses to write one and a startup sweep
/// repairs the rows already stored — both decide with these two functions, so a
/// regression here either lets the bad shape back in or throws away a folder
/// that was fine.
/// </summary>
public class HostFolderPathTests
{
    // The production shape, with invented names: the same album folder twice,
    // the second half spelled with the other separator.
    private const string DoubledDriveFolder =
        @"Q:/Music/Nine Vaults/Paper Lanterns\Q:\Music\Nine Vaults\Paper Lanterns";

    private const string SingleDriveFolder = "Q:/Music/Nine Vaults/Paper Lanterns";

    private const string DoubledShareFolder =
        @"//vault-01/music/Nine Vaults/Paper Lanterns\\\vault-01\music\Nine Vaults\Paper Lanterns";

    private const string SingleShareFolder = "//vault-01/music/Nine Vaults/Paper Lanterns";

    [Fact]
    public void ContainsSecondRoot_FindsTheDoubledProductionShape()
    {
        HostFolderPath.ContainsSecondRoot(DoubledDriveFolder).Should().BeTrue();
    }

    [Fact]
    public void RepairDoubled_ReturnsTheFirstHalfOfTheDoubledProductionShape()
    {
        HostFolderPath.RepairDoubled(DoubledDriveFolder).Should().Be(SingleDriveFolder);
    }

    [Fact]
    public void ContainsSecondRoot_LeavesAPlainDriveFolderAlone()
    {
        HostFolderPath.ContainsSecondRoot(SingleDriveFolder).Should().BeFalse();
        HostFolderPath.RepairDoubled(SingleDriveFolder).Should().BeNull();
    }

    /// <summary>
    /// A share root is two separators, so the folder's own leading <c>//</c>
    /// must not read as a second root — otherwise every UNC library would be
    /// refused on import.
    /// </summary>
    [Fact]
    public void ContainsSecondRoot_LeavesAUncFolderAlone()
    {
        HostFolderPath.ContainsSecondRoot(SingleShareFolder).Should().BeFalse();
        HostFolderPath.ContainsSecondRoot(@"\\vault-01\music\Nine Vaults").Should().BeFalse();
    }

    [Fact]
    public void RepairDoubled_ReturnsTheFirstHalfOfADoubledUncFolder()
    {
        HostFolderPath.RepairDoubled(DoubledShareFolder).Should().Be(SingleShareFolder);
    }

    /// <summary>
    /// A colon-free, root-relative folder — every remote driver key looks like
    /// this — carries no root at all and must pass untouched.
    /// </summary>
    [Theory]
    [InlineData("music/Nine Vaults/Paper Lanterns")]
    [InlineData("/mnt/vault/music/Nine Vaults/Paper Lanterns")]
    [InlineData(@"music\Nine Vaults\Paper Lanterns")]
    [InlineData("")]
    [InlineData(null)]
    public void ContainsSecondRoot_LeavesAColonFreePathAlone(string? hostFolder)
    {
        HostFolderPath.ContainsSecondRoot(hostFolder).Should().BeFalse();
        HostFolderPath.RepairDoubled(hostFolder).Should().BeNull();
    }

    /// <summary>
    /// Two roots that name different folders are a value only a human can
    /// judge: the sweep must leave the row alone rather than pick a half.
    /// </summary>
    [Fact]
    public void RepairDoubled_RefusesASecondRootThatIsNotACopyOfTheFirst()
    {
        const string mismatched = @"Q:/Music/Nine Vaults/Paper Lanterns\R:\Archive\Nine Vaults";

        HostFolderPath.ContainsSecondRoot(mismatched).Should().BeTrue();
        HostFolderPath.RepairDoubled(mismatched).Should().BeNull();
    }

    /// <summary>
    /// A drive letter that is not preceded by a separator is part of a name,
    /// not a root — the only colon that counts is one that starts a path.
    /// </summary>
    [Fact]
    public void ContainsSecondRoot_IgnoresAColonThatStartsNoPath()
    {
        HostFolderPath.ContainsSecondRoot("Q:/Music/Live A:B Sessions").Should().BeFalse();
    }
}
