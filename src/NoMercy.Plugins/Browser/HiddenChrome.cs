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

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using PuppeteerSharp;

namespace NoMercy.PluginSdk.Browser;

/// <summary>Chrome starts only after its private Windows desktop or X display exists.</summary>
internal sealed class HiddenChrome : IAsyncDisposable
{
    internal static readonly TimeSpan DisplayStartTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan ChromeStartTimeout = TimeSpan.FromSeconds(60);

    // The shell ends when Chrome ends, so a crashed Chrome shows as HasExited. set -m gives Chrome its
    // own process group without setsid, which forks and returns early when the shell leads its group.
    internal const string LinuxLauncher =
        "set -m; executable=$1; log=$2; shift 2; \"$executable\" \"$@\" </dev/null >/dev/null 2>\"$log\" & pid=$!; { IFS= read -r _; /bin/kill -TERM -- \"-$pid\" 2>/dev/null || :; } <&0 & wait \"$pid\"";

    private readonly Process _chrome;
    private readonly Process? _display;
    private readonly WindowsChromeDesktop? _desktop;
    private readonly string _profile;

    private HiddenChrome(
        IBrowser browser,
        Process chrome,
        Process? display,
        WindowsChromeDesktop? desktop,
        string profile
    )
    {
        Browser = browser;
        _chrome = chrome;
        _display = display;
        _desktop = desktop;
        _profile = profile;
    }

    internal IBrowser Browser { get; }

    internal static (string Chrome, string Reason) Availability()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return (
                string.Empty,
                "A hidden browser window is only available on Windows and Linux."
            );

        string? chrome = Environment.GetEnvironmentVariable("NOMERCY_CHROME_PATH");
        if (string.IsNullOrWhiteSpace(chrome))
        {
            string[] candidates = OperatingSystem.IsWindows()
                ?
                [
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        "Google",
                        "Chrome",
                        "Application",
                        "chrome.exe"
                    ),
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                        "Google",
                        "Chrome",
                        "Application",
                        "chrome.exe"
                    ),
                ]
                : ["/usr/bin/google-chrome", "/usr/bin/chromium", "/usr/bin/chromium-browser"];
            chrome = candidates.FirstOrDefault(File.Exists);
        }

        if (chrome is null || !File.Exists(chrome))
            return (string.Empty, "Chrome is not installed on this host.");

        if (OperatingSystem.IsLinux() && FindOnPath("Xvfb") is null)
            return (string.Empty, "Xvfb is not installed on this host.");

        return (chrome, string.Empty);
    }

    internal static async Task<HiddenChrome> StartAsync(CancellationToken ct)
    {
        (string executable, string reason) = Availability();
        if (reason.Length != 0)
            throw new InvalidOperationException(reason);

        return await StartAsync(executable, FindOnPath("Xvfb"), ct);
    }

    internal static async Task<HiddenChrome> StartAsync(
        string executable,
        string? xvfbPath,
        CancellationToken ct
    )
    {
        string profile = Path.Combine(
            Path.GetTempPath(),
            "nomercy-plugin-browser-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(profile);
        string stderrLog = Path.Combine(profile, "chrome-stderr.log");
        Process? display = null;
        Process? chrome = null;
        WindowsChromeDesktop? desktop = null;
        try
        {
            string? displayName = null;
            if (OperatingSystem.IsLinux())
            {
                ProcessStartInfo xvfb = new("/bin/sh")
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                };
                xvfb.ArgumentList.Add("-c");
                xvfb.ArgumentList.Add(
                    "\"$1\" -displayfd 1 -screen 0 1280x1024x24 -nolisten tcp & pid=$!; IFS= read -r _; kill -TERM \"$pid\" 2>/dev/null || :; wait \"$pid\""
                );
                xvfb.ArgumentList.Add("nomercy-xvfb");
                xvfb.ArgumentList.Add(xvfbPath!);
                display =
                    Process.Start(xvfb)
                    ?? throw new InvalidOperationException("Xvfb did not start.");
                string? number = await display
                    .StandardOutput.ReadLineAsync(ct)
                    .AsTask()
                    .WaitAsync(DisplayStartTimeout, ct);
                if (!int.TryParse(number, CultureInfo.InvariantCulture, out int displayNumber))
                    throw new InvalidOperationException("Xvfb did not create a display.");
                displayName = ":" + displayNumber.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                desktop = WindowsChromeDesktop.Create();
            }

            string[] arguments =
            [
                "--remote-debugging-port=0",
                "--remote-debugging-address=127.0.0.1",
                "--user-data-dir=" + profile,
                "--no-first-run",
                "--no-default-browser-check",
                "--disable-background-mode",
                "--new-window",
                "about:blank",
            ];
            if (desktop is not null)
                chrome = desktop.Start(executable, arguments);
            else
            {
                ProcessStartInfo start = new("/bin/sh")
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                };
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add(LinuxLauncher);
                start.ArgumentList.Add("nomercy-chrome");
                start.ArgumentList.Add(executable);
                start.ArgumentList.Add(stderrLog);
                foreach (string argument in arguments)
                    start.ArgumentList.Add(argument);
                start.Environment["DISPLAY"] = displayName;
                chrome =
                    Process.Start(start)
                    ?? throw new InvalidOperationException("Chrome did not start.");
            }

            string portFile = Path.Combine(profile, "DevToolsActivePort");
            string[] portLines = [];
            using CancellationTokenSource ready = CancellationTokenSource.CreateLinkedTokenSource(
                ct
            );
            ready.CancelAfter(ChromeStartTimeout);
            while (portLines.Length < 2)
            {
                ready.Token.ThrowIfCancellationRequested();
                if (chrome.HasExited)
                    throw new InvalidOperationException(
                        "Chrome exited before opening its control channel."
                    );
                if (File.Exists(portFile))
                {
                    try
                    {
                        portLines = await File.ReadAllLinesAsync(portFile, ready.Token);
                    }
                    catch (IOException)
                    {
                        // Chrome may still be writing the second line.
                    }
                }
                if (portLines.Length < 2)
                    await Task.Delay(50, ready.Token);
            }

            if (!int.TryParse(portLines[0], CultureInfo.InvariantCulture, out int port))
                throw new InvalidOperationException("Chrome reported an invalid control port.");

            IBrowser browser = await Puppeteer
                .ConnectAsync(
                    new ConnectOptions
                    {
                        BrowserURL =
                            "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture),
                    }
                )
                .WaitAsync(ct);
            return new HiddenChrome(browser, chrome, display, desktop, profile);
        }
        catch (Exception error)
        {
            string said = ReadStderrTail(stderrLog);
            StopProcess(chrome);
            StopProcess(display);
            desktop?.Dispose();
            await DeleteProfileAsync(profile);
            if (said.Length == 0 || ct.IsCancellationRequested)
                throw;
            throw new InvalidOperationException(
                error.Message + " Chrome said:" + Environment.NewLine + said,
                error
            );
        }
    }

    internal static string ReadStderrTail(string path, int lines = 20)
    {
        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
            );
            using StreamReader reader = new(stream);
            Queue<string> tail = new();
            while (reader.ReadLine() is { } line)
            {
                tail.Enqueue(line);
                if (tail.Count > lines)
                    tail.Dequeue();
            }
            return string.Join(Environment.NewLine, tail);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);

    private static void StopProcess(Process? process)
    {
        if (process is null)
            return;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task DeleteProfileAsync(string profile)
    {
        for (int attempt = 0; attempt < 25; attempt++)
        {
            if (!Directory.Exists(profile))
                return;

            try
            {
                Directory.Delete(profile, recursive: true);
                return;
            }
            catch (Exception error)
                when (attempt < 24 && error is IOException or UnauthorizedAccessException)
            {
                // Chrome's subprocesses can release their metrics files after its main process exits.
                await Task.Delay(200);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Browser.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // A closed control channel cannot be asked to close Chrome; kill the process below.
        }
        finally
        {
            StopProcess(_chrome);
            StopProcess(_display);
            _desktop?.Dispose();
            await DeleteProfileAsync(_profile);
        }
    }
}
