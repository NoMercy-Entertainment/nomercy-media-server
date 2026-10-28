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
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Browser;
using NoMercy.PluginSdk.Runtime;
using PuppeteerSharp;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginBrowserHostTests
{
    [Fact]
    public async Task AReadInterruptedBySelfNavigationRetriesThenReportsNotReady()
    {
        int attempts = 0;
        bool readable = await PluginBrowserPage.DuringNavigationAsync(
            () =>
            {
                attempts++;
                return Task.FromException<bool>(
                    new PuppeteerException("Execution context was destroyed")
                );
            },
            false,
            CancellationToken.None
        );

        readable.Should().BeFalse();
        attempts.Should().Be(2);
    }

    [Fact]
    public async Task NavigationAndPageFetchCannotLeaveTheGrantedHosts()
    {
        PluginResourceLedger ledger = new();
        Ulid pluginId = Ulid.NewUlid();
        await using PluginBrowser browser = new(
            pluginId,
            new FakeCapabilities(null),
            new PluginCapabilities
            {
                Hooks = [PluginCapabilityNames.BrowserHeadless],
                Network = new PluginNetworkCapability { Hosts = ["allowed.example"] },
            },
            () => [],
            ledger
        );

        Func<Task> open = async () =>
            await browser.OpenAsync(new Uri("https://denied.example/"), new PluginBrowserOptions());
        await open.Should().ThrowAsync<PluginRefusedException>();
        ledger.Held(pluginId).Should().Be(0);
    }

    [Fact]
    public async Task AGrantedBrowserHostStillRequiresTheNetworkFetchCapability()
    {
        ScopeRecordingCapabilities broker = new();
        await using PluginBrowser browser = new(
            Ulid.NewUlid(),
            broker,
            new PluginCapabilities
            {
                Network = new PluginNetworkCapability { Hosts = ["allowed.example"] },
            },
            () => [],
            new PluginResourceLedger()
        );

        browser.Allows(new Uri("https://allowed.example/")).Should().BeTrue();
        broker.Capability.Should().Be(PluginCapabilityNames.NetworkFetch);
        broker.Scope.Should().Be("allowed.example");
    }

    [Fact]
    public async Task StoppingAnIdlePluginReplacesItsBrowserFacade()
    {
        PluginBrowserRegistry registry = new();
        PluginResourceLedger ledger = new();
        Ulid pluginId = Ulid.NewUlid();
        IPluginBrowser first = registry.For(
            pluginId,
            new FakeCapabilities(null),
            null,
            () => [],
            ledger
        );

        await registry.ReleaseAsync(pluginId);

        IPluginBrowser second = registry.For(
            pluginId,
            new FakeCapabilities(null),
            null,
            () => [],
            ledger
        );
        second.Should().NotBeSameAs(first);
        registry.Dispose();
    }

    [Fact]
    public void TheVirtualDisplayHasRoomToStartOnASlowOrBusyHost()
    {
        HiddenChrome.DisplayStartTimeout.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void ALinuxHostRunningAsRootIsRefusedBecauseChromeNeedsItsSandbox()
    {
        HiddenChrome.SandboxReason(isLinux: true, userName: "root").Should().Contain("root");
        HiddenChrome.SandboxReason(isLinux: true, userName: "nomercy").Should().BeEmpty();
        HiddenChrome.SandboxReason(isLinux: false, userName: "root").Should().BeEmpty();
    }

    [Fact]
    public void ChromeHasRoomToOpenItsControlPortOnASlowOrBusyHost()
    {
        HiddenChrome.ChromeStartTimeout.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void ChromesStderrTailKeepsOnlyTheLastTwentyLines()
    {
        string path = Path.Combine(Path.GetTempPath(), "nomercy-stderr-" + Ulid.NewUlid());
        File.WriteAllLines(path, Enumerable.Range(1, 30).Select(n => "line " + n));
        try
        {
            string[] tail = HiddenChrome.ReadStderrTail(path).Split(Environment.NewLine);

            tail.Should().HaveCount(20);
            tail[0].Should().Be("line 11");
            tail[^1].Should().Be("line 30");
            HiddenChrome.ReadStderrTail(path + ".missing").Should().BeEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SkippableFact]
    [SupportedOSPlatform("linux")]
    public async Task AChromeThatDiesOnStartupReportsItsOwnWords()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "The virtual display path is Linux only.");
        string folder = Path.Combine(Path.GetTempPath(), "nomercy-fake-chrome-" + Ulid.NewUlid());
        Directory.CreateDirectory(folder);
        try
        {
            string xvfb = WriteScript(folder, "Xvfb", "echo 99\nexec sleep 300");
            string chrome = WriteScript(
                folder,
                "chrome",
                "echo 'first line' >&2\necho 'fake chrome cannot create its sandbox' >&2\nexit 7"
            );

            Func<Task> start = async () => await HiddenChrome.StartAsync(chrome, xvfb, default);

            (await start.Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should()
                .Contain("fake chrome cannot create its sandbox");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [SkippableFact]
    [SupportedOSPlatform("linux")]
    public async Task TheLauncherOutlivesChromeEvenWhenItIsAProcessGroupLeader()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "The virtual display path is Linux only.");
        string folder = Path.Combine(Path.GetTempPath(), "nomercy-fake-chrome-" + Ulid.NewUlid());
        Directory.CreateDirectory(folder);
        try
        {
            string chrome = WriteScript(
                folder,
                "chrome",
                "sleep 0.3\necho 'fake chrome cannot create its sandbox' >&2\nexit 7"
            );
            string log = Path.Combine(folder, "stderr.log");
            // setsid makes the launcher a group leader, which is how a CI runner can start it.
            ProcessStartInfo start = new("setsid")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
            };
            foreach (string argument in new[] { "sh", "-c", HiddenChrome.LinuxLauncher })
                start.ArgumentList.Add(argument);
            foreach (string argument in new[] { "nomercy-chrome", chrome, log })
                start.ArgumentList.Add(argument);
            using Process launcher = Process.Start(start)!;

            await launcher.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            HiddenChrome
                .ReadStderrTail(log)
                .Should()
                .Contain("fake chrome cannot create its sandbox");
            launcher.StandardInput.Close();
        }
        finally
        {
            await Task.Delay(500);
            Directory.Delete(folder, recursive: true);
        }
    }

    [SupportedOSPlatform("linux")]
    private static string WriteScript(string folder, string name, string body)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return path;
    }

    [SkippableFact]
    public async Task ARealWindowedChromeSupportsTheSolverMembersAndClosesWithItsLastPage()
    {
        using LocalPageServer server = new();
        PluginResourceLedger ledger = new();
        Ulid pluginId = Ulid.NewUlid();
        await using PluginBrowser browser = new(
            pluginId,
            new FakeCapabilities(null),
            new PluginCapabilities
            {
                Hooks = [PluginCapabilityNames.BrowserHeadless],
                Network = new PluginNetworkCapability { Hosts = ["127.0.0.1"] },
            },
            () => [],
            ledger
        );
        Skip.IfNot(browser.IsAvailable, browser.UnavailableReason);

        await using (
            IPluginBrowserPage page = await browser.OpenAsync(
                server.Page,
                new PluginBrowserOptions()
            )
        )
        {
            (await page.IsReadableAsync()).Should().BeTrue();
            (await page.ContentTypeAsync()).Should().StartWith("text/html");
            (await page.ContentAsync()).Should().Contain("solver-ready");
            (await page.UserAgentAsync()).Should().Contain("Chrome");
            (await page.FetchTextAsync(server.Data)).Should().Be("{\"ok\":true}");
            (await page.CookiesAsync()).Should().Contain(cookie => cookie.Name == "cf_clearance");
            await page.ReloadAsync();
            (await page.IsReadableAsync()).Should().BeTrue();
            ledger.Held(pluginId).Should().Be(1);
        }

        ledger.Held(pluginId).Should().Be(0);
    }

    private sealed class LocalPageServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serve;

        public LocalPageServer()
        {
            _listener.Start();
            _serve = ServeAsync();
        }

        public Uri Page => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");

        public Uri Data => new(Page, "/data");

        private async Task ServeAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await using NetworkStream stream = client.GetStream();
                    using StreamReader reader = new(stream, Encoding.ASCII, leaveOpen: true);
                    string? first = await reader.ReadLineAsync(_stop.Token);
                    string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_stop.Token)))
                    { }

                    bool data = first?.Contains(" /data ", StringComparison.Ordinal) == true;
                    string body = data
                        ? "{\"ok\":true}"
                        : "<html><body><main>solver-ready</main></body></html>";
                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    string header =
                        $"HTTP/1.1 200 OK\r\nContent-Type: {(data ? "application/json" : "text/html")}; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nSet-Cookie: cf_clearance=test; Path=/\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _stop.Token);
                    await stream.WriteAsync(bytes, _stop.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _serve.GetAwaiter().GetResult();
            _stop.Dispose();
        }
    }
}
