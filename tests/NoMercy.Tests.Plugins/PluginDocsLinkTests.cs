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

using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.Tests.Common;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Every docs link a refusal or an SDK readme prints goes to a page that exists.
/// <para>
/// A refusal is read by an author who is already stuck. For months every one of
/// them pointed at a docs path nobody had written, so the one sentence meant to
/// unstick them led nowhere. These tests fail the build the moment a link points
/// at a page that is not there, or a new message spells its link by hand
/// instead of taking it from <see cref="PluginDocs" />.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class PluginDocsLinkTests
{
    /// <summary>
    /// The hand-written plugin pages on the docs site that something here links,
    /// as paths under <see cref="PluginDocs.Site" />. They live in the
    /// nomercy-docs repository (src/content/nomercy-plugins/en). The capability
    /// pages beside them are generated from the plugin contract, one per
    /// capability, and tools/plugin-contract checks they exist, so they are read
    /// from <see cref="PluginCapabilityVocabulary" /> rather than listed here.
    /// Add a path here only after the page is live.
    /// </summary>
    private static readonly string[] HandWrittenSitePages =
    [
        "/nomercy-plugins",
        "/nomercy-plugins/overview",
        "/nomercy-plugins/capabilities",
        "/nomercy-plugins/handbook/runtime-and-isolation",
    ];

    // Every pattern carries a timeout, like all regexes in this repository
    // (RegexTimeoutAuditTests).
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex SiteLink = new(
        @"https://docs\.nomercy\.tv(/nomercy-plugins[a-z0-9/-]*)",
        RegexOptions.Compiled,
        MatchTimeout
    );

    private static readonly Regex RepositoryLink = new(
        Regex.Escape(PluginDocs.Repository) + @"/([a-z0-9-]+\.md)(#[a-z0-9-]+)?",
        RegexOptions.Compiled,
        MatchTimeout
    );

    /// <summary>
    /// The ways a docs link has been, or could be, spelled by hand in source.
    /// Each one belongs in <see cref="PluginDocs" /> instead.
    /// </summary>
    private static readonly (string Why, Regex Pattern)[] HandSpelledDocs =
    [
        // Bare or full, with or without a trailing slash. The lookbehind leaves
        // the nomercy-plugins repository (the plugin index) alone.
        (
            "a docs path",
            new(
                @"(?<!(github|githubusercontent)\.com/NoMercy-Entertainment)/nomercy-plugins\b",
                RegexOptions.Compiled,
                MatchTimeout
            )
        ),
        // A path that starts at /plugins/, the prefix an old refusal used. API
        // routes are left alone: they have a segment such as v1 or a } before it.
        (
            "a /plugins/ docs path",
            new(@"(?<=[""\s(`'])/plugins/", RegexOptions.Compiled, MatchTimeout)
        ),
        ("the docs site", new(@"docs\.nomercy\.tv", RegexOptions.Compiled, MatchTimeout)),
        (
            "a docs page in this repository",
            new(@"blob/dev/docs/plugins", RegexOptions.Compiled, MatchTimeout)
        ),
        (
            "a docs site that does not exist",
            new(@"nomercy\.tv/docs", RegexOptions.Compiled, MatchTimeout)
        ),
        // Docs: has to be followed by a PluginDocs constant, either inside an
        // interpolated string or concatenated, on the same line or the next.
        (
            "a Docs: line without PluginDocs",
            new(
                @"Docs:(?!\s*\{PluginDocs\.|\s*""\s*\+\s*PluginDocs\.)",
                RegexOptions.Compiled,
                MatchTimeout
            )
        ),
    ];

    /// <summary>
    /// Every site constant in <see cref="PluginDocs" /> is one of the known
    /// pages: the hand-written list above or a generated capability page. This
    /// does not reach the live site. It proves the constants and the list
    /// agree, and the list is kept to pages that were live when it was written.
    /// </summary>
    [Fact]
    public void Every_site_constant_is_a_known_site_page()
    {
        HashSet<string> known = KnownSitePages();

        IEnumerable<string> unknown = DocsConstants()
            .Where(url => url.StartsWith(PluginDocs.Site, StringComparison.Ordinal))
            .Select(url => url[PluginDocs.Site.Length..])
            .Where(path => !known.Contains(path));

        string.Join(Environment.NewLine, unknown)
            .Should()
            .BeEmpty("a refusal that names a page the site does not have sends the author nowhere");
    }

    [Fact]
    public void Every_repository_page_the_refusals_link_is_in_docs_plugins()
    {
        List<string> repositoryLinks = DocsConstants()
            .Where(url => url.StartsWith(PluginDocs.Repository, StringComparison.Ordinal))
            .ToList();

        repositoryLinks.Should().NotBeEmpty();

        foreach (string url in repositoryLinks)
            RepositoryPageProblem(url).Should().BeNull(url);
    }

    [Fact]
    public void A_capability_refusal_links_the_page_the_generator_made_for_it()
    {
        PluginRefusal declared = PluginRefusalMessages.CapabilityNotDeclared(
            "Sample 1.0.0",
            "network.dial",
            "The plugin opened a socket."
        );
        PluginRefusal unknown = PluginRefusalMessages.CapabilityNotConsented(
            "Sample 1.0.0",
            "not.a.capability"
        );

        declared.Fix.Should().EndWith(PluginDocs.NetworkDial);
        unknown.Fix.Should().EndWith(PluginDocs.Capabilities);
    }

    [Fact]
    public void No_source_file_spells_a_docs_link_by_hand()
    {
        string root = RepoPaths.Root;

        IEnumerable<string> sources = Files(root, "*.cs", "src", "tools", "templates")
            .Where(file =>
                !file.Contains(
                    $"{Path.DirectorySeparatorChar}Generated{Path.DirectorySeparatorChar}"
                )
            )
            .Where(file => Path.GetFileName(file) != $"{nameof(PluginDocs)}.cs");

        List<string> offenders = [];
        foreach (string file in sources)
            offenders.AddRange(
                HandSpelled(File.ReadAllText(file), Path.GetRelativePath(root, file))
            );

        string.Join(Environment.NewLine, offenders)
            .Should()
            .BeEmpty("a docs link belongs in PluginDocs, where one change moves every refusal");
    }

    [Fact]
    public void Every_docs_link_in_a_readme_or_a_doc_goes_to_a_page_that_exists()
    {
        string root = RepoPaths.Root;
        HashSet<string> known = KnownSitePages();

        IEnumerable<string> documents = Files(root, "README.md", "src")
            .Concat(Files(root, "*.md", "docs", "templates"));

        List<string> problems = [];
        foreach (string file in documents)
        {
            string text = File.ReadAllText(file);
            string where = Path.GetRelativePath(root, file);

            if (text.Contains("nomercy.tv/docs", StringComparison.Ordinal))
                problems.Add($"{where}: nomercy.tv/docs is not the docs site, docs.nomercy.tv is");

            foreach (Match link in SiteLink.Matches(text))
            {
                string path = link.Groups[1].Value.TrimEnd('/');
                if (!known.Contains(path))
                    problems.Add($"{where}: {link.Value}");
            }

            foreach (Match link in RepositoryLink.Matches(text))
            {
                string? problem = RepositoryPageProblem(link.Value);
                if (problem is not null)
                    problems.Add($"{where}: {problem}");
            }
        }

        string.Join(Environment.NewLine, problems)
            .Should()
            .BeEmpty("every link has to reach a page");
    }

    [Fact]
    public void The_sdk_readmes_link_the_docs_site()
    {
        string src = RepoPaths.Src;

        List<string> readmes = Directory.Exists(src)
            ? Directory
                .EnumerateDirectories(src, "NoMercy.PluginSdk.*")
                .Select(folder => Path.Combine(folder, "README.md"))
                .Where(File.Exists)
                .ToList()
            : [];

        readmes.Should().NotBeEmpty();

        foreach (string readme in readmes)
            File.ReadAllText(readme).Should().Contain(PluginDocs.Overview, readme);
    }

    [Theory]
    [InlineData("\"Fix it. Docs: /nomercy-plugins/capabilities/network-dial\"")]
    [InlineData("\"Fix it. See https://docs.nomercy.tv/nomercy-plugins/capabilities\"")]
    [InlineData("const string Root = \"/nomercy-plugins\";")]
    [InlineData("\"Fix it. Docs: /plugins/capabilities/media-proxy\"")]
    [InlineData("\"https://docs.nomercy.tv/nomercy-media-server\"")]
    [InlineData(
        "\"https://github.com/NoMercy-Entertainment/nomercy-media-server/blob/dev/docs/plugins/x.md\""
    )]
    [InlineData("\"https://nomercy.tv/docs\"")]
    [InlineData("\"Fix it. Docs: see the handbook\"")]
    [InlineData("\"Fix it. Docs: \" + SomewhereElse.Page")]
    public void A_hand_spelled_link_is_caught(string line)
    {
        HandSpelled(line, "planted").Should().NotBeEmpty(line);
    }

    [Theory]
    [InlineData("\"Fix it. Docs: \" + PluginDocs.NetworkDial")]
    [InlineData("$\"Fix {it}. Docs: {PluginDocs.Versioning}\"")]
    [InlineData("\"Fix it. Docs: \"\n        + PluginDocs.MediaProxy;")]
    [InlineData(
        "\"https://raw.githubusercontent.com/NoMercy-Entertainment/nomercy-plugins/master/index.json\""
    )]
    [InlineData("[HttpGet(\"api/v{version:apiVersion}/plugins/{id:ulid}/view\")]")]
    [InlineData("$\"/api/v1/plugins/{pluginId}/media/{ticket}\"")]
    public void A_link_through_plugin_docs_or_a_route_is_not_caught(string line)
    {
        HandSpelled(line, "planted").Should().BeEmpty(line);
    }

    private static IEnumerable<string> HandSpelled(string text, string where)
    {
        foreach ((string why, Regex pattern) in HandSpelledDocs)
        foreach (Match match in pattern.Matches(text))
        {
            int line = 1 + text.AsSpan(0, match.Index).Count('\n');
            yield return $"{where}:{line}: {why}";
        }
    }

    private static IEnumerable<string> Files(string root, string pattern, params string[] folders)
    {
        return folders
            .Select(folder => Path.Combine(root, folder))
            .Where(Directory.Exists)
            .SelectMany(folder =>
                Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories)
            )
            .Where(file => !IsBuildOutput(file));
    }

    private static HashSet<string> KnownSitePages()
    {
        return HandWrittenSitePages
            .Concat(PluginCapabilityVocabulary.All.Select(capability => capability.DocsUrl))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<string> DocsConstants()
    {
        return typeof(PluginDocs)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, FieldType.Name: nameof(String) })
            .Where(field =>
                field.Name is not (nameof(PluginDocs.Site) or nameof(PluginDocs.Repository))
            )
            .Select(field => (string)field.GetRawConstantValue()!);
    }

    /// <summary>Null when the page is in docs/plugins and, for a link to a section, the section heading is too.</summary>
    private static string? RepositoryPageProblem(string url)
    {
        Match match = RepositoryLink.Match(url);
        if (!match.Success || match.Value != url)
            return $"{url} is not a page under {PluginDocs.Repository}";

        string page = RepoPaths.At("docs", "plugins", match.Groups[1].Value);
        if (!File.Exists(page))
            return $"{url}: docs/plugins/{match.Groups[1].Value} does not exist";

        if (!match.Groups[2].Success)
            return null;

        string anchor = match.Groups[2].Value[1..];

        return HeadingAnchors(File.ReadAllLines(page)).Contains(anchor)
            ? null
            : $"{url}: no heading in {match.Groups[1].Value} makes the anchor #{anchor}";
    }

    // A # inside a fenced code block is a comment in the code, not a heading.
    private static IEnumerable<string> HeadingAnchors(IEnumerable<string> lines)
    {
        bool inFence = false;
        foreach (string line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (!inFence && line.StartsWith('#'))
                yield return HeadingAnchor(line.TrimStart('#').Trim());
        }
    }

    // The anchor GitHub gives a heading: lower case, punctuation dropped, spaces to hyphens.
    private static string HeadingAnchor(string heading)
    {
        string lower = heading.ToLowerInvariant();
        string kept = new(
            lower
                .Where(character => char.IsLetterOrDigit(character) || character is ' ' or '-')
                .ToArray()
        );
        return kept.Replace(' ', '-');
    }

    private static bool IsBuildOutput(string file)
    {
        char separator = Path.DirectorySeparatorChar;
        return file.Contains($"{separator}bin{separator}")
            || file.Contains($"{separator}obj{separator}")
            || file.Contains($"{separator}node_modules{separator}");
    }
}
