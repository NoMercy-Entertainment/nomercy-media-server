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
/// DISP-04: Audit test verifying that Process.Start, File.OpenWrite/OpenRead/Create
/// results are properly disposed in cold paths.
/// </summary>
[Trait("Category", "Unit")]
public partial class ColdPathDisposalAuditTests
{
    [Fact]
    public void Source_ProcessStart_HasUsingOrDispose()
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

                if (!ProcessStartStaticPattern().IsMatch(trimmed))
                    continue;

                // Allow: lines with 'using' keyword
                if (trimmed.Contains("using "))
                    continue;

                // Allow: lines that call .Dispose() inline
                if (trimmed.Contains(".Dispose()"))
                    continue;

                // Allow: lines that call ?.Dispose() inline
                if (trimmed.Contains("?.Dispose()"))
                    continue;

                // Allow: instance .Start() calls on managed process objects (not static factory)
                if (InstanceStartPattern().IsMatch(trimmed))
                    continue;

                // Allow: test files
                string relative = Path.GetRelativePath(srcDir, file);
                if (relative.Contains("Test"))
                    continue;

                violations.Add($"{relative}:{i + 1} — {trimmed}");
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Source_FileOpenWrite_HasUsing()
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

                if (!FileOpenPattern().IsMatch(trimmed))
                    continue;

                // Allow: lines with 'using' keyword
                if (trimmed.Contains("using "))
                    continue;

                string relative = Path.GetRelativePath(srcDir, file);
                if (relative.Contains("Test"))
                    continue;

                violations.Add($"{relative}:{i + 1} — {trimmed}");
            }
        }

        Assert.Empty(violations);
    }

    [GeneratedRegex(@"Process\.Start\([""n]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ProcessStartStaticPattern();

    [GeneratedRegex(@"_\w+\.Start\(\)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InstanceStartPattern();

    [GeneratedRegex(
        @"(?<!\w)File\.(OpenWrite|OpenRead|Create)\(",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex FileOpenPattern();
}
