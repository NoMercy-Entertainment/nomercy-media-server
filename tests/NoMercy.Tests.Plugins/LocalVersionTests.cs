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

using System.Diagnostics;
using FluentAssertions;
using NoMercy.Tests.Common;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A local or dev build used to report the fallback in Directory.Build.props
/// (0.1.404) however new the code was. CI passes the release tag as the
/// version, so only those builds misreported. <c>build/LocalVersion.targets</c>
/// takes the newest release tag in the repo instead.
/// </summary>
public class LocalVersionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "nm-localversion-" + Guid.NewGuid().ToString("N")
    );

    public LocalVersionTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
    }

    [Fact]
    public void A_build_without_a_version_takes_the_newest_release_tag()
    {
        InitRepo();
        Git("tag", "v0.1.9");
        Git("tag", "v0.1.10");
        Git("tag", "wave1-backup");

        Print().Should().Contain("version=0.1.10 assembly=0.1.10.0");
    }

    [Fact]
    public void A_version_passed_by_ci_is_left_alone()
    {
        InitRepo();
        Git("tag", "v0.1.10");

        Print("-p:Version=7.7.7").Should().Contain("version=7.7.7 assembly=7.7.7.0");
    }

    [Fact]
    public void A_folder_without_git_keeps_the_fallback()
    {
        WriteProject();

        Print().Should().Contain("version=0.1.404 assembly=0.1.404.0");
    }

    [Fact]
    public void A_repo_without_a_release_tag_keeps_the_fallback()
    {
        InitRepo();
        Git("tag", "wave1-backup");

        Print().Should().Contain("version=0.1.404 assembly=0.1.404.0");
    }

    private void WriteProject()
    {
        Directory.CreateDirectory(Path.Combine(_root, "build"));
        File.Copy(
            RepoPaths.At(Path.Combine("build", "LocalVersion.targets")),
            Path.Combine(_root, "build", "LocalVersion.targets")
        );
        File.WriteAllText(
            Path.Combine(_root, "Probe.proj"),
            """
            <Project>
              <PropertyGroup>
                <NmVersionGiven>$(Version)</NmVersionGiven>
                <Version>0.1.404</Version>
                <AssemblyVersion>$(Version).0</AssemblyVersion>
                <FileVersion>$(Version).0</FileVersion>
              </PropertyGroup>
              <Import Project="build/LocalVersion.targets" />
              <Target Name="Print">
                <Message Importance="high" Text="version=$(Version) assembly=$(AssemblyVersion)" />
              </Target>
            </Project>
            """
        );
    }

    private void InitRepo()
    {
        WriteProject();
        Git("init", "-q");
        Git("add", "-A");
        Git(
            "-c",
            "user.name=t",
            "-c",
            "user.email=t@example.com",
            "-c",
            "commit.gpgsign=false",
            "commit",
            "-q",
            "-m",
            "x"
        );
    }

    private string Print(params string[] extra) =>
        Run("dotnet", ["msbuild", "Probe.proj", "-t:Print", "-nologo", .. extra]);

    private void Git(params string[] args) =>
        Run("git", ["-c", "tag.gpgsign=false", "-c", "tag.forceAnnotate=false", .. args]);

    private string Run(string file, string[] args)
    {
        ProcessStartInfo info = new(file)
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in args)
            info.ArgumentList.Add(arg);
        using Process process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, output);
        return output;
    }
}
