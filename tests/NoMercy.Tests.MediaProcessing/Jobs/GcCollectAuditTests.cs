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
/// HIGH-20b: Audit tests verifying that all GC.Collect() band-aid calls have been
/// removed from the codebase. GC.Collect() causes stop-the-world pauses that freeze
/// all threads, causing playback stuttering during library scans.
/// </summary>
[Trait("Category", "Unit")]
public partial class GcCollectAuditTests
{
    [Fact]
    public void Source_NoGcCollectCalls()
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

                if (GcCollectPattern().IsMatch(trimmed))
                {
                    string relative = Path.GetRelativePath(srcDir, file);
                    violations.Add($"{relative}:{i + 1} — {trimmed}");
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Source_NoGcWaitForFullGCComplete()
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

                if (GcWaitForFullGcPattern().IsMatch(trimmed))
                {
                    string relative = Path.GetRelativePath(srcDir, file);
                    violations.Add($"{relative}:{i + 1} — {trimmed}");
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Source_NoGcWaitForPendingFinalizers()
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

                if (GcWaitForPendingFinalizersPattern().IsMatch(trimmed))
                {
                    string relative = Path.GetRelativePath(srcDir, file);
                    violations.Add($"{relative}:{i + 1} — {trimmed}");
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Source_NoFinalizersCallingDispose()
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

                if (FinalizerPattern().IsMatch(trimmed))
                {
                    // Check if the next non-empty lines contain Dispose()
                    for (int j = i + 1; j < Math.Min(i + 5, lines.Length); j++)
                    {
                        string next = lines[j].Trim();
                        if (next.Contains("Dispose()"))
                        {
                            string relative = Path.GetRelativePath(srcDir, file);
                            violations.Add($"{relative}:{i + 1} — finalizer calls Dispose()");
                            break;
                        }
                    }
                }
            }
        }

        Assert.Empty(violations);
    }

    [GeneratedRegex(@"GC\.Collect\s*\(", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GcCollectPattern();

    [GeneratedRegex(
        @"GC\.WaitForFullGCComplete\s*\(",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex GcWaitForFullGcPattern();

    [GeneratedRegex(
        @"GC\.WaitForPendingFinalizers\s*\(",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex GcWaitForPendingFinalizersPattern();

    [GeneratedRegex(@"~\w+\s*\(\)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FinalizerPattern();
}
