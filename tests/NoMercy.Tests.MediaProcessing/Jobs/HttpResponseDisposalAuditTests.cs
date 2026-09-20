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
/// DISP-02: Audit test verifying that HttpResponseMessage objects are properly disposed.
/// HttpResponseMessage implements IDisposable and holds network buffers.
/// Every API call that doesn't dispose the response leaks memory.
/// </summary>
[Trait("Category", "Unit")]
public partial class HttpResponseDisposalAuditTests
{
    [Fact]
    public void Source_HttpResponseMessage_HasUsing()
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

                if (!HttpResponseDeclarationPattern().IsMatch(trimmed))
                    continue;

                // Allow: lines with 'using' keyword
                if (trimmed.Contains("using "))
                    continue;

                // Allow: the declaration is the first argument of a multi-line
                // `using (\n    HttpResponseMessage x = ...\n)` statement — the
                // formatter puts the `using (` opener on its own line above,
                // so this line alone doesn't contain the keyword.
                bool multiLineUsingOpener = false;
                for (int look = i - 1; look >= Math.Max(0, i - 3); look--)
                {
                    if (lines[look].Trim() == "using (")
                    {
                        multiLineUsingOpener = true;
                        break;
                    }
                }
                if (multiLineUsingOpener)
                    continue;

                if (OwnershipAccountedFor(lines, i))
                    continue;

                violations.Add($"{Path.GetRelativePath(srcDir, file)}:{i + 1} — {trimmed}");
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// The ways this codebase says who disposes a response it does not dispose
    /// itself. A wrapper that closes it with its stream, a registration that
    /// closes it with the request, or a sentence naming the owner when the
    /// answer travels further than either.
    /// </summary>
    private static readonly string[] Ownership =
    [
        "new HttpResponseStream(",
        "RegisterForDispose(",
        "Owned by",
    ];

    private static bool OwnershipAccountedFor(string[] lines, int declaration)
    {
        int from = Math.Max(0, declaration - 4);
        int to = Math.Min(declaration + 6, lines.Length);

        for (int look = from; look < to; look++)
        {
            if (look == declaration)
                continue;

            foreach (string marker in Ownership)
            {
                if (lines[look].Contains(marker, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"HttpResponseMessage\s+\w+\s*=")]
    private static partial Regex HttpResponseDeclarationPattern();
}
