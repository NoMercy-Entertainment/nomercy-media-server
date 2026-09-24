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

using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NoMercy.Tests.Common;
using Xunit;

namespace NoMercy.Tests.Launcher;

/// <summary>
/// The server builds from this repository alone.
///
/// <para>
/// NoMercy.Storage.csproj used to read libnfs from a sibling folder of the
/// developer's workspace first, and from the vendored copy only when that folder
/// was missing. A build on the developer's machine and a release build could
/// then ship different binaries, and every copy item was guarded by Exists, so a
/// missing binary produced a release with no NFS library at all instead of a
/// failed build ("Unable to load DLL 'nfs'", July 2026).
/// </para>
/// </summary>
public class BuildInputsStayInRepoTests
{
    private static readonly string[] NfsBinaries =
    [
        "win-x64/libnfs.dll",
        "linux-x64/libnfs.so",
        "linux-arm64/libnfs.so",
        "osx-x64/libnfs.dylib",
        "osx-arm64/libnfs.dylib",
    ];

    [Fact]
    public void NoBuildFileReadsOutsideTheRepository()
    {
        string root = Path.GetFullPath(RepoPaths.Root);
        List<string> escapes = [];

        foreach (string file in BuildFiles(root))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            string directory = Path.GetDirectoryName(file)!;

            foreach (XElement element in XDocument.Load(file).Descendants())
            {
                foreach (string value in Values(element))
                {
                    Match match = Regex.Match(value, @"\$\(MSBuildThisFileDirectory\)((?:\.\.[/\\])+[^;""]*)");
                    if (!match.Success)
                        continue;

                    string target = Path.GetFullPath(Path.Combine(directory, match.Groups[1].Value));
                    if (target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        continue;

                    escapes.Add($"{relative}:{element.Name.LocalName} -> {match.Groups[1].Value}");
                }
            }
        }

        escapes.Should().BeEmpty("a build that reads a sibling folder works only in the developer's workspace");
    }

    [Fact]
    public void EveryVendoredNfsBinaryIsPresentAndMatchesItsChecksum()
    {
        string nfs = RepoPaths.At("libs", "nfs");
        Dictionary<string, string> expected = File.ReadAllLines(Path.Combine(nfs, "CHECKSUMS.txt"))
            .Where(line => line.Trim().Length > 0)
            .Select(line => line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries))
            .ToDictionary(parts => parts[1].Trim().TrimStart('.', '/'), parts => parts[0]);

        foreach (string binary in NfsBinaries)
        {
            string path = Path.Combine(nfs, binary);
            File.Exists(path).Should().BeTrue($"{binary} ships in every release");
            expected.Should().ContainKey(binary);
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
                .Should().Be(expected[binary], $"{binary} must be the published libnfs build");
        }
    }

    [Fact]
    public void AMissingNfsBinaryFailsTheBuildInsteadOfShippingWithoutIt()
    {
        XDocument storage = XDocument.Load(RepoPaths.At("src", "NoMercy.Storage", "NoMercy.Storage.csproj"));

        List<string> guarded = storage.Descendants()
            .Where(element => (string?)element.Attribute("Include") is { } include && include.Contains("$(LibNfsDir)"))
            .Where(element => element.Attribute("Condition") is not null)
            .Select(element => (string)element.Attribute("Include")!)
            .ToList();

        guarded.Should().BeEmpty("an Exists guard turns a missing libnfs into a release without NFS");
    }

    [Fact]
    public void SmbLibraryComesFromThePublishedPackage()
    {
        foreach (string project in new[]
                 {
                     RepoPaths.At("src", "NoMercy.Storage", "NoMercy.Storage.csproj"),
                     RepoPaths.At("tests", "NoMercy.Tests.Storage", "NoMercy.Tests.Storage.csproj"),
                 })
        {
            XDocument document = XDocument.Load(project);

            document.Descendants("PackageReference")
                .Select(element => (string?)element.Attribute("Include"))
                .Should().Contain("NoMercy.SMBLibrary", $"{Path.GetFileName(project)} takes the fork from nuget.org");
            document.Descendants("HintPath")
                .Select(element => element.Value)
                .Should().NotContain(value => value.Contains("SMBLibrary"), "a copied DLL is not the published package");
        }

        Directory.Exists(RepoPaths.At("libs", "smb")).Should().BeFalse("the vendored SMBLibrary copy is replaced by the package");
    }

    private static IEnumerable<string> BuildFiles(string root)
    {
        return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                           || file.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                           || file.EndsWith(".targets", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "bin" or "obj" or "node_modules" or ".git"));
    }

    private static IEnumerable<string> Values(XElement element)
    {
        if (!element.HasElements)
            yield return element.Value;

        foreach (XAttribute attribute in element.Attributes())
            yield return attribute.Value;
    }
}
