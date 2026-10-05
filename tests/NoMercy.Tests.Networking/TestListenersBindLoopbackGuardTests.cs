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
using Xunit;

namespace NoMercy.Tests.Networking;

/// <summary>
/// A test that binds a wildcard address (0.0.0.0 or ::) makes Windows ask the
/// firewall on every test run, which halts an unattended run. Every test
/// listener binds loopback instead, and this guard reads the test sources so
/// a new wildcard bind fails here before it reaches a developer's machine.
/// </summary>
public sealed class TestListenersBindLoopbackGuardTests
{
    // A wildcard address inside a socket expression: a constructor call
    // (new(...), new TcpListener(...), new UdpClient(...), new IPEndPoint(...))
    // or a Bind(...) call. An Assert.Equal(IPAddress.IPv6Any, ...) is not a bind.
    private static readonly Regex WildcardBind = new(
        @"(new\s*(TcpListener|UdpClient|IPEndPoint|Socket)?\s*\(|\.Bind\s*\()[^;]*IPAddress\.(Any|IPv6Any)\b",
        RegexOptions.Compiled
    );

    // Test files whose wildcard text is fixture source handed to an analyzer,
    // never a socket this process opens.
    private static readonly string[] FixtureSourceFiles =
    [
        "NoMercy.Tests.Plugins.Analyzers/BannedApiAnalyzerTests.cs",
    ];

    [Fact]
    public void NoTestSourceBindsAWildcardAddress()
    {
        string testsRoot = RepoPaths.At("tests");
        List<string> offenders = [];

        foreach (
            string file in Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
        )
        {
            string relative = Path.GetRelativePath(testsRoot, file).Replace('\\', '/');

            if (relative.Contains("/bin/") || relative.Contains("/obj/"))
                continue;

            if (FixtureSourceFiles.Contains(relative))
                continue;

            string[] lines = File.ReadAllLines(file);

            for (int i = 0; i < lines.Length; i++)
            {
                if (WildcardBind.IsMatch(lines[i]))
                    offenders.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A test listener must bind loopback (127.0.0.1 or ::1), never a wildcard, "
                + "or every test run asks the Windows firewall:\n"
                + string.Join('\n', offenders)
        );
    }
}
