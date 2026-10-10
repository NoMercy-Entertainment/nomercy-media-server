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

using System.Text.RegularExpressions;
using NoMercy.Tests.Common;

namespace NoMercy.Tests.MediaProcessing.Jobs;

[Trait("Category", "Unit")]
public sealed class ExtrasJobTimeoutAuditTests
{
    [Theory]
    [InlineData("MovieExtrasJob.cs", 9)]
    [InlineData("CollectionExtrasJob.cs", 1)]
    [InlineData("PersonExtrasJob.cs", 2)]
    public void EveryStoreCallUsesPerCallTimeout(string jobFile, int expectedStoreCalls)
    {
        string path = Path.Combine(
            RepoPaths.Src,
            "NoMercy.MediaProcessing",
            "Jobs",
            "MediaJobs",
            jobFile
        );
        string source = File.ReadAllText(path);
        MatchCollection calls = Regex.Matches(
            source,
            @"await\s+\w+Manager\s*\.\s*Store\w*\s*\([^;]*?\);",
            RegexOptions.Singleline
        );

        calls.Count.Should().Be(expectedStoreCalls);
        foreach (Match call in calls)
            call.Value.Should()
                .Contain(".WithTimeout(", $"{jobFile} has an unbounded store call: {call.Value}");
    }
}
