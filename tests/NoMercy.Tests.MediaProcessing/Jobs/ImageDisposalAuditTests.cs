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
/// DISP-01: Audit tests verifying that Image&lt;Rgba32&gt; objects are properly disposed.
/// Each Image&lt;Rgba32&gt; holds 5-50MB of unmanaged memory. During library scans with
/// thousands of images, undisposed images cause severe memory growth.
/// </summary>
[Trait("Category", "Unit")]
public partial class ImageDisposalAuditTests
{
    [Fact]
    public void Source_ImageLoadInLocalScope_HasUsing()
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

                if (!ImageLoadPattern().IsMatch(trimmed))
                    continue;

                // Allow: return statements (ownership transferred to caller)
                if (trimmed.Contains("return ") || trimmed.Contains("return\t"))
                    continue;

                // Allow: lines with 'using' keyword on this line or the preceding line
                // (handles multi-line 'using Image<T> img =\n    await Image.LoadAsync(...)')
                if (trimmed.Contains("using "))
                    continue;

                if (i > 0 && lines[i - 1].Trim().Contains("using "))
                    continue;

                string relative = Path.GetRelativePath(srcDir, file);
                violations.Add($"{relative}:{i + 1} — {trimmed}");
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Source_DownloadCallers_DisposeReturnedImage()
    {
        string srcDir = RepoPaths.Src;
        string[] csFiles = Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories);

        List<string> violations = [];

        foreach (string file in csFiles)
        {
            // Skip the Download method definitions themselves
            string fileName = Path.GetFileName(file);
            if (
                fileName
                is "TmdbImageClient.cs"
                    or "FanArtImageClient.cs"
                    or "CoverArtCoverArtClient.cs"
                    or "NoMercyImageClient.cs"
            )
                continue;

            string content = File.ReadAllText(file);
            string[] lines = content.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.StartsWith("//") || trimmed.StartsWith("*"))
                    continue;

                if (!DownloadCallPattern().IsMatch(trimmed))
                    continue;

                // Check that the result is disposed: either via 'using' on this or previous line
                bool hasUsing = trimmed.Contains("using ");
                if (i > 0)
                {
                    string prevLine = lines[i - 1].Trim();
                    if (prevLine.Contains("using "))
                        hasUsing = true;
                }

                if (!hasUsing)
                {
                    string relative = Path.GetRelativePath(srcDir, file);
                    violations.Add($"{relative}:{i + 1} — {trimmed}");
                }
            }
        }

        Assert.Empty(violations);
    }

    [GeneratedRegex(
        @"Image\.Load(?:Async)?[<\(]",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex ImageLoadPattern();

    [GeneratedRegex(
        @"(?:TmdbImageClient|FanArtImageClient|CoverArtCoverArtClient|NoMercyImageClient)\.Download\s*\(",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex DownloadCallPattern();
}
