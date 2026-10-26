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
/// Every regex in src and tests carries a match timeout, so hostile input cannot hang a worker.
/// The rule mirrors the workspace regex-timeout-check script.
/// </summary>
[Trait("Category", "Unit")]
public partial class RegexTimeoutAuditTests
{
    private const int AttributeLookahead = 20;
    private const int StatementLookahead = 5;

    [Fact]
    public void SourceAndTests_EveryRegexHasAMatchTimeout()
    {
        List<string> violations = [];

        foreach (string rootDir in new[] { RepoPaths.Src, RepoPaths.At("tests") })
        {
            foreach (
                string file in Directory.GetFiles(rootDir, "*.cs", SearchOption.AllDirectories)
            )
            {
                if (BuildOutputPath().IsMatch(file))
                    continue;

                string relative = Path.GetRelativePath(RepoPaths.Root, file);
                violations.AddRange(FindUntimedRegexes(File.ReadAllText(file), relative));
            }
        }

        Assert.Empty(violations);
    }

    // Made-up inputs spell the type as {R} and the attribute as {A}, so the workspace check does not flag this file.
    [Theory]
    [InlineData("{A}(@\"a+\")]")]
    [InlineData("{A}(@\"a+\", RegexOptions.IgnoreCase)]")]
    [InlineData("{A}(\n    @\"a+\",\n    RegexOptions.IgnoreCase\n)]")]
    [InlineData("{R} r = new {R}(\"a+\");")]
    [InlineData("{R} r = new(\"a+\");")]
    [InlineData("bool b = {R}.IsMatch(input, \"a+\");")]
    [InlineData("Match m = {R}.Match(input, \"a+\");")]
    [InlineData("MatchCollection m = {R}.Matches(input, \"a+\");")]
    [InlineData("string s = {R}.Replace(input, \"a+\", \"b\");")]
    [InlineData("string[] s = {R}.Split(input, \"a+\");")]
    [InlineData("int n = {R}.Count(input, \"a+\");")]
    [InlineData("foreach (ValueMatch m in {R}.EnumerateMatches(input, \"a+\")) { }")]
    public void Rule_FlagsARegexWithoutATimeout(string source)
    {
        Assert.Single(FindUntimedRegexes(Expand(source), "made-up.cs"));
    }

    [Theory]
    [InlineData("{A}(@\"a+\", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]")]
    [InlineData(
        "{A}(\n    @\"a+\",\n    RegexOptions.IgnoreCase,\n    matchTimeoutMilliseconds: 1000\n)]"
    )]
    [InlineData("{A}(@\"\\([^)]*\\)\", matchTimeoutMilliseconds: 1000)]")]
    [InlineData("{R} r = new {R}(\"a+\", RegexOptions.None, TimeSpan.FromSeconds(1));")]
    [InlineData("{R} r = new(\"a+\", RegexOptions.None, TimeSpan.FromSeconds(1));")]
    [InlineData("bool b = {R}.IsMatch(input, \"a+\", RegexOptions.None, TimeSpan.FromSeconds(1));")]
    [InlineData("Match m = {R}.Match(input, \"a+\", RegexOptions.None, TimeSpan.FromSeconds(1));")]
    [InlineData(
        "MatchCollection m = {R}.Matches(input, \"a+\", RegexOptions.None, TimeSpan.FromSeconds(1));"
    )]
    [InlineData(
        "string s = {R}.Replace(input, \"a+\", \"b\", RegexOptions.None, TimeSpan.FromSeconds(1));"
    )]
    [InlineData(
        "string[] s = {R}.Split(input, \"a+\", RegexOptions.None, TimeSpan.FromSeconds(1));"
    )]
    [InlineData("int n = {R}.Count(input, \"a+\", RegexOptions.None, TimeSpan.FromSeconds(1));")]
    [InlineData(
        "foreach (ValueMatch m in {R}.EnumerateMatches(input, \"a+\", RegexOptions.None, TimeSpan.FromSeconds(1))) { }"
    )]
    [InlineData("bool b = FooRegex.IsMatch(input);")]
    public void Rule_AcceptsARegexWithATimeout(string source)
    {
        Assert.Empty(FindUntimedRegexes(Expand(source), "made-up.cs"));
    }

    private static string Expand(string source)
    {
        return source.Replace("{A}", "[GeneratedRegex").Replace("{R}", "Regex");
    }

    internal static List<string> FindUntimedRegexes(string text, string file)
    {
        List<string> found = [];
        string[] lines = text.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];

            if (line.TrimStart().StartsWith('[') && line.Contains("GeneratedRegex("))
            {
                string attribute = ReadAttribute(lines, i);
                if (!MatchTimeoutArgument().IsMatch(attribute))
                    found.Add($"{file}:{i + 1}: [GeneratedRegex] without matchTimeoutMilliseconds");
            }
            else if (RuntimeRegexCall().IsMatch(line))
            {
                string statement = ReadStatement(lines, i);
                if (!TimeoutWord().IsMatch(statement))
                    found.Add($"{file}:{i + 1}: runtime Regex call without a TimeSpan timeout");
            }
        }

        return found;
    }

    // The attribute ends on the first line ending in ")]"; a pattern can hold ")]" inside itself.
    private static string ReadAttribute(string[] lines, int start)
    {
        int last = Math.Min(start + AttributeLookahead, lines.Length);
        int end = last;

        for (int j = start; j < last; j++)
        {
            if (AttributeEnd().IsMatch(lines[j]))
            {
                end = j + 1;
                break;
            }
        }

        return string.Join(' ', lines[start..end]);
    }

    private static string ReadStatement(string[] lines, int start)
    {
        int last = Math.Min(start + StatementLookahead, lines.Length);
        string window = string.Join(' ', lines[start..last]);
        int semicolon = window.IndexOf(';');

        return semicolon < 0 ? window : window[..(semicolon + 1)];
    }

    [GeneratedRegex(@"[\\/](bin|obj)[\\/]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BuildOutputPath();

    [GeneratedRegex(
        @"matchTimeoutMilliseconds\s*:",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex MatchTimeoutArgument();

    [GeneratedRegex(@"\)\]\s*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AttributeEnd();

    [GeneratedRegex(
        @"new Regex\(|\bRegex\s+\w+\s*=\s*new\s*\(|\bRegex\.(IsMatch|Match|Matches|Replace|Split|Count|EnumerateMatches)\(",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex RuntimeRegexCall();

    [GeneratedRegex(
        @"TimeSpan|Timeout|matchTimeout",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex TimeoutWord();
}
