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

using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Database;
using NoMercy.Networking.Discovery;
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

    // The mDNS scanners join the multicast group on UDP 5353 at 0.0.0.0, which
    // is a wildcard bind the source scan above cannot see: it happens inside
    // the Makaretu.Dns package. This fact builds and starts both scanners the
    // way the scanner tests do and asserts no new 5353 listener appeared.
    [Fact]
    public void ConstructingAndStartingTheMdnsScannersInATestOpensNoUdp5353()
    {
        int before = CountUdp5353Listeners();
        using CancellationTokenSource cts = new();

        using MdnsDeviceScanner mdnsScanner = new(
            new ThrowingDbContextFactory(),
            NullLogger<MdnsDeviceScanner>.Instance,
            multicast: new NonBindingMulticastTransport()
        );
        using GoogleCastDeviceScanner castScanner = new(
            NullLogger<GoogleCastDeviceScanner>.Instance,
            new NonBindingMulticastTransport()
        );
        mdnsScanner.Start(cts.Token);
        castScanner.Start(cts.Token);

        int after = CountUdp5353Listeners();
        cts.Cancel();

        Assert.True(
            after == before,
            $"A test must never bind UDP 5353: listeners went from {before} to {after}. "
                + "Pass a non-binding multicast transport to the scanner."
        );
    }

    // A scanner built without the fake transport joins the 5353 group the
    // moment Start() runs. This reads every test source that builds one and
    // requires the fake in the same file.
    [Fact]
    public void EveryTestThatBuildsAnMdnsScannerPassesTheNonBindingTransport()
    {
        string testsRoot = RepoPaths.At("tests");
        Regex buildsScanner = new(
            @"\b(MdnsDeviceScanner|GoogleCastDeviceScanner)\s+\w+\s*=\s*new\b"
        );
        List<string> offenders = [];

        foreach (
            string file in Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
        )
        {
            string relative = Path.GetRelativePath(testsRoot, file).Replace('\\', '/');

            if (relative.Contains("/bin/") || relative.Contains("/obj/"))
                continue;

            string text = File.ReadAllText(file);

            if (buildsScanner.IsMatch(text) && !text.Contains(nameof(NonBindingMulticastTransport)))
                offenders.Add(relative);
        }

        Assert.True(
            offenders.Count == 0,
            "A test that builds an mDNS scanner must pass a NonBindingMulticastTransport, "
                + "or the test run binds UDP 5353 and asks the Windows firewall:\n"
                + string.Join('\n', offenders)
        );
    }

    private static int CountUdp5353Listeners() =>
        IPGlobalProperties
            .GetIPGlobalProperties()
            .GetActiveUdpListeners()
            .Count(endpoint => endpoint.Port == 5353);

    private sealed class ThrowingDbContextFactory : IDbContextFactory<MediaContext>
    {
        public MediaContext CreateDbContext() => throw new NotSupportedException();
    }
}
