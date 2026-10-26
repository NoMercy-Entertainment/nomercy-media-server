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
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NoMercy.Setup.Server;
using NoMercy.Tests.Common;
using RegexMatch = System.Text.RegularExpressions.Match;

namespace NoMercy.Tests.Providers.Setup;

[Trait("Category", "Characterization")]
public partial class BinariesCloudflaredArchTests
{
    [GeneratedRegex(
        @"OSPlatform\.OSX\).*?Architecture\.Arm64\s*\).*?cloudflared-darwin-(\w+)\.tgz",
        RegexOptions.Singleline,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex MacOsArm64Branch();

    [GeneratedRegex(
        @"OSPlatform\.OSX\).*?Architecture\.X64\s*\).*?cloudflared-darwin-(\w+)\.tgz",
        RegexOptions.Singleline,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex MacOsX64Branch();

    [GeneratedRegex(
        @"OSPlatform\.Linux\).*?Architecture\.Arm64\s*\).*?cloudflared-linux-(\w+)""",
        RegexOptions.Singleline,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex LinuxArm64Branch();

    [GeneratedRegex(
        @"OSPlatform\.Linux\).*?Architecture\.X64\s*\).*?cloudflared-linux-(\w+)""",
        RegexOptions.Singleline,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex LinuxX64Branch();

    private static readonly string SourcePath = FindSourceFile();

    private static string FindSourceFile()
    {
        return RepoPaths.At("src", "NoMercy.Setup", "Server", "Binaries.cs");
    }

    private static string GetSourceCode() => File.ReadAllText(SourcePath);

    private static string ExtractDownloadCloudflaredMethod(string source)
    {
        int start = source.IndexOf(
            "internal async Task DownloadCloudflared()",
            StringComparison.Ordinal
        );
        Assert.True(start >= 0, "Could not find DownloadCloudflared method in source");

        int braceStart = source.IndexOf('{', start);
        int depth = 0;
        int i = braceStart;
        while (i < source.Length)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}')
                depth--;
            if (depth == 0)
                break;
            i++;
        }
        return source[start..(i + 1)];
    }

    [Fact]
    public void DownloadCloudflared_IsAsyncMethod()
    {
        MethodInfo? method = typeof(Binaries).GetMethod(
            "DownloadCloudflared",
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        Assert.NotNull(method);
        Assert.NotNull(method.GetCustomAttribute<AsyncStateMachineAttribute>());
    }

    [Fact]
    public void DownloadCloudflared_MacOS_Arm64_Downloads_Arm64_Binary()
    {
        string method = ExtractDownloadCloudflaredMethod(GetSourceCode());

        // Match: OSPlatform.OSX) && ...Architecture.Arm64) ... cloudflared-darwin-XXX.tgz
        // \s* before \) handles formatter putting the closing paren on its own line
        RegexMatch match = MacOsArm64Branch().Match(method);
        Assert.True(match.Success, "Could not find macOS Arm64 branch with darwin asset");
        Assert.Equal("arm64", match.Groups[1].Value);
    }

    [Fact]
    public void DownloadCloudflared_MacOS_X64_Downloads_Amd64_Binary()
    {
        string method = ExtractDownloadCloudflaredMethod(GetSourceCode());

        // Match: OSPlatform.OSX) && ...Architecture.X64) ... cloudflared-darwin-XXX.tgz
        // \s* before \) handles formatter putting the closing paren on its own line
        RegexMatch match = MacOsX64Branch().Match(method);
        Assert.True(match.Success, "Could not find macOS X64 branch with darwin asset");
        Assert.Equal("amd64", match.Groups[1].Value);
    }

    [Fact]
    public void DownloadCloudflared_MacOS_Architectures_Not_Swapped()
    {
        string method = ExtractDownloadCloudflaredMethod(GetSourceCode());

        // Extract both macOS branches and verify each downloads the correct architecture
        // \s* before \) handles formatter putting the closing paren on its own line
        RegexMatch arm64Match = MacOsArm64Branch().Match(method);
        RegexMatch x64Match = MacOsX64Branch().Match(method);

        Assert.True(arm64Match.Success, "macOS Arm64 branch not found");
        Assert.True(x64Match.Success, "macOS X64 branch not found");

        // Arm64 host must download arm64 binary (not amd64)
        Assert.Equal("arm64", arm64Match.Groups[1].Value);
        // X64 host must download amd64 binary (not arm64)
        Assert.Equal("amd64", x64Match.Groups[1].Value);
    }

    [Fact]
    public void DownloadCloudflared_Windows_Downloads_Amd64()
    {
        string method = ExtractDownloadCloudflaredMethod(GetSourceCode());
        Assert.Contains("cloudflared-windows-amd64.exe", method);
    }

    [Fact]
    public void DownloadCloudflared_Linux_Arm64_Downloads_Arm64()
    {
        string method = ExtractDownloadCloudflaredMethod(GetSourceCode());

        // \s* before \) handles formatter putting the closing paren on its own line
        RegexMatch match = LinuxArm64Branch().Match(method);
        Assert.True(match.Success, "Could not find Linux Arm64 branch");

        // cloudflared-linux-arm is the 32-bit build. This assertion previously pinned the
        // arm64 branch to it, which is how the mismatch survived — and on a Raspberry Pi or
        // similar the tunnel is often the only remote transport available.
        Assert.Equal("arm64", match.Groups[1].Value);
    }

    [Fact]
    public void DownloadCloudflared_Linux_X64_Downloads_Amd64()
    {
        string method = ExtractDownloadCloudflaredMethod(GetSourceCode());

        // \s* before \) handles formatter putting the closing paren on its own line
        RegexMatch match = LinuxX64Branch().Match(method);
        Assert.True(match.Success, "Could not find Linux X64 branch");
        Assert.Equal("amd64", match.Groups[1].Value);
    }

    [Fact]
    public void DownloadCloudflared_All_Platform_Assets_Present()
    {
        string method = ExtractDownloadCloudflaredMethod(GetSourceCode());

        Assert.Contains("cloudflared-windows-amd64.exe", method);
        Assert.Contains("cloudflared-linux-arm", method);
        Assert.Contains("cloudflared-linux-amd64", method);
        Assert.Contains("cloudflared-darwin-arm64.tgz", method);
        Assert.Contains("cloudflared-darwin-amd64.tgz", method);
    }
}
