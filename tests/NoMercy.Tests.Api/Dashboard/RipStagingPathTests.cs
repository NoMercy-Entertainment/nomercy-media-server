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

using NoMercy.Api.Controllers.V1.Dashboard.Media;
using NoMercy.OpticalMedia.Rip;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

/// <summary>
/// ConfirmDisc used to accept any client-supplied absolute RipOutputPath and then
/// copy+delete it. These assert the confinement guard rejects everything outside
/// the server's own rip staging directory.
/// </summary>
public class RipStagingPathTests
{
    private static readonly string StagingRoot = Path.Combine(
        Path.GetTempPath(),
        "nm-ripstaging-test",
        "ripper"
    );

    [Fact]
    public void FileDirectlyInsideStaging_IsAllowed()
    {
        string candidate = Path.Combine(StagingRoot, "drive0", "movie.mkv");
        Assert.True(RipStagingPath.IsWithinStaging(candidate, StagingRoot));
    }

    [Fact]
    public void TraversalOutOfStaging_IsRejected()
    {
        string candidate = Path.Combine(StagingRoot, "..", "..", "secret.conf");
        Assert.False(RipStagingPath.IsWithinStaging(candidate, StagingRoot));
    }

    [Fact]
    public void SiblingPrefixDirectory_IsRejected()
    {
        // "ripper-evil" must not be treated as inside "ripper".
        string candidate = Path.Combine(StagingRoot + "-evil", "movie.mkv");
        Assert.False(RipStagingPath.IsWithinStaging(candidate, StagingRoot));
    }

    [Fact]
    public void StagingRootItself_IsRejected()
    {
        Assert.False(RipStagingPath.IsWithinStaging(StagingRoot, StagingRoot));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrNullPath_IsRejected(string? candidate)
    {
        Assert.False(RipStagingPath.IsWithinStaging(candidate, StagingRoot));
    }

    [Fact]
    public void AbsolutePathOutsideStaging_IsRejected()
    {
        string candidate = Path.Combine(Path.GetTempPath(), "elsewhere", "movie.mkv");
        Assert.False(RipStagingPath.IsWithinStaging(candidate, StagingRoot));
    }
}
