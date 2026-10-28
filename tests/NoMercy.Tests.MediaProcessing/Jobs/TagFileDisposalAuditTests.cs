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

/// <summary>
/// DISP-03: Audit test verifying that TagLib.File and TagFile.Create() results are properly disposed.
/// TagLib.File implements IDisposable and holds file handles. Leaking these inside
/// Parallel.ForEach loops means scanning 1000 songs leaks 1000 file handles.
/// </summary>
[Trait("Category", "Unit")]
public partial class TagFileDisposalAuditTests
{
    [Fact]
    public void Source_TagLibFileCreate_HasUsing()
    {
        string srcDir = RepoPaths.Src;
        string[] csFiles = Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories);

        List<string> violations = [];

        foreach (string file in csFiles)
        {
            string content = File.ReadAllText(file);
            string[] lines = content.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.StartsWith("//") || trimmed.StartsWith("*"))
                    continue;

                if (!TagLibFileCreatePattern().IsMatch(trimmed))
                    continue;

                // Allow: lines with 'using' keyword
                if (trimmed.Contains("using "))
                    continue;

                string relative = Path.GetRelativePath(srcDir, file);
                violations.Add($"{relative}:{i + 1} — {trimmed}");
            }
        }

        Assert.Empty(violations);
    }

    // Matches: TagLib.File xxx = TagLib.File.Create(...)  or  FileTag? xxx = FileTag.Create(...)
    [GeneratedRegex(
        @"(TagLib\.File|FileTag\??)\s+\w+\s*=\s*(TagLib\.File|FileTag)\.Create",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex TagLibFileCreatePattern();
}
