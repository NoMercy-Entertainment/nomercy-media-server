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
using System.Reflection;
using InfiniFrame;
using InfiniFrame.Js.MessageHandlers;
using InfiniFrame.WebServer;
using NoMercy.App.EmbeddedStaticAssets;

namespace NoMercy.App;

internal class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        string windowTitle = "NoMercy TV";
        string iconPath = GetIconPath();

        string browserDataPath = GetBrowserDataPath();
        ClearBrowserDataOnVersionChange(browserDataPath);

        // Set environment variable for URL before creating builder
        if (!Debugger.IsAttached)
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://localhost:7625");
        }

        InfiniFrameWebApplicationBuilder builder = InfiniFrameWebApplication.CreateBuilder(args);

        // InfiniFrame's SetBrowserControlInitParameters is platform-shaped: on Windows
        // (WebView2) it is a raw space-separated Chromium flag string, but on Linux
        // (WebKitGTK) it is parsed as a JSON object of WebKitSettings property overrides
        // via simdjson with no exception handling in the pinned InfiniFrame version — a
        // non-JSON value like a Chromium flag string throws an uncaught simdjson_error and
        // terminates the whole process. Remote-debugging-via-flag is Windows/WebView2-only
        // here, so only pass it there; every other platform gets null (native no-op).
        string? browserControlInitParameters = BrowserControlParameters.Resolve(
            OperatingSystem.IsWindows(),
            Debugger.IsAttached,
            Environment.GetEnvironmentVariable(BrowserControlParameters.RemoteDebugOptInVariable)
        );

        IInfiniFrameWindowBuilder window = builder
            .Window.SetTemporaryFilesPath(browserDataPath)
            .Center()
            .SetTitle(windowTitle)
            .SetMinSize(1280 + 16, 720 + 39)
            .SetSize(1600 + 16, 900 + 39)
            .SetResizable(true)
            .SetIconFile(iconPath)
            .SetUseOsDefaultSize(false)
            .SetMediaAutoplayEnabled(true)
            .SetMediaStreamEnabled(true)
            .SetBrowserControlInitParameters(browserControlInitParameters)
            .RegisterFullScreenWebMessageHandler()
            .RegisterOpenExternalTargetWebMessageHandler()
            .RegisterTitleChangedWebMessageHandler()
            .RegisterWindowManagementWebMessageHandler()
            .RegisterWebMessageReceivedHandler(
                (sender, message) =>
                {
                    if (sender is not IInfiniFrameWindow infiniWindow)
                        return;

                    string response = $"Received message: \"{message}\"";
                    infiniWindow.SendWebMessage(response);
                }
            );

        // Parse --route argument
        string route = "";
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--route" && i + 1 < args.Length)
            {
                route = args[i + 1];
                break;
            }

            if (args[i].StartsWith("--route="))
            {
                route = args[i]["--route=".Length..];
                break;
            }
        }

        // Set start URL with optional route
        if (Debugger.IsAttached)
            window.SetStartUrl($"https://app-dev.nomercy.tv{route}");
        else if (!string.IsNullOrEmpty(route))
            window.SetStartUrl($"http://localhost:7625{route}");

        InfiniFrameWebApplication application = builder.Build();

        application.UseAutoServerClose();

        // Use custom embedded static assets middleware with optimizations
        // (compression, caching, ETags) - replaces MapStaticAssets for embedded resources
        // Also injects the InfiniFrame.js script tag into HTML files at runtime
        application.WebApp.UseEmbeddedStaticAssets(
            options =>
            {
                // Inject InfiniFrame script before </body> - required for InfiniFrame communication
                options.InjectScripts.Add("/_content/InfiniLore.InfiniFrame.Js/InfiniFrame.js");

                // Force media query re-evaluation after WebView2 viewport settles.
                // WebView2 starts with a small initial viewport before InfiniFrame
                // sizes the window, causing Ionic's mobile detection to misfire.
                options.InjectScripts.Add(
                    "<script>window.addEventListener('load',function(){setTimeout(function(){window.dispatchEvent(new Event('resize'))},100)})</script>"
                );
            },
            typeof(Program).Assembly
        );

        application.Run();
    }

    private static string GetIconPath()
    {
        string iconName;
        if (OperatingSystem.IsWindows())
            iconName = "icon.ico";
        else if (OperatingSystem.IsLinux())
            iconName = "icon.png";
        else if (OperatingSystem.IsMacOS())
            iconName = "icon.icns";
        else
            throw new PlatformNotSupportedException("Unsupported OS platform");

        // Extract embedded icon to temp directory (InfiniFrame requires a file path)
        string tempDir = Path.Combine(Path.GetTempPath(), "NoMercyApp");
        Directory.CreateDirectory(tempDir);
        string iconPath = Path.Combine(tempDir, iconName);

        if (!File.Exists(iconPath))
        {
            Assembly assembly = typeof(Program).Assembly;
            string resourceName = $"NoMercy.App.Resources.AppIcon.{iconName}";

            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                throw new FileNotFoundException(
                    $"Embedded icon resource not found: {resourceName}"
                );

            using FileStream fileStream = File.Create(iconPath);
            stream.CopyTo(fileStream);
        }

        return iconPath;
    }

    private static string GetBrowserDataPath()
    {
        // Match the path computed by AppFiles.BrowserPath so the server and app
        // share the same browser-data directory without requiring a project reference
        // to NmSystem (which would bloat the lightweight App executable).
        string appDataPath =
            Environment.OSVersion.Platform == PlatformID.Unix
                ? Path.Combine(
                    Environment.GetEnvironmentVariable("HOME") ?? "/home/current",
                    ".local/share"
                )
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        string baseDir = Path.Combine(appDataPath, "NoMercy", "browser");
        Directory.CreateDirectory(baseDir);
        return baseDir;
    }

    // WebView2 subdirectories inside Default/ that hold login/session state — preserved across updates
    private static readonly HashSet<string> PreservedSubDirectories = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "Session Storage",
        "Local Storage",
        "IndexedDB",
    };

    // WebView2 files inside Default/ that hold login/session state — preserved across updates
    private static readonly string[] PreservedFilePrefixes =
    [
        "Cookies",
        "Login Data",
        "Preferences",
        "Web Data",
    ];

    // WebView2 files in the EBWebView/ root that must survive cache clears
    private static readonly string[] PreservedEbWebViewFiles = ["Local State"];

    private static void ClearBrowserDataOnVersionChange(string browserDataPath)
    {
        string versionFile = Path.Combine(browserDataPath, ".app-version");
        string currentVersion =
            typeof(Program)
                .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
            ?? typeof(Program).Assembly.GetName().Version?.ToString()
            ?? "0.0.0";

        string? previousVersion = null;
        if (File.Exists(versionFile))
            previousVersion = File.ReadAllText(versionFile).Trim();

        if (previousVersion != currentVersion)
        {
            ClearBrowserCache(browserDataPath);
            File.WriteAllText(versionFile, currentVersion);
        }
    }

    private static void ClearBrowserCache(string browserDataPath)
    {
        // WebView2 stores profile data under EBWebView/Default/
        // Delete everything except session/login directories and files
        foreach (string dir in Directory.GetDirectories(browserDataPath))
        {
            string dirName = Path.GetFileName(dir);

            if (string.Equals(dirName, "EBWebView", StringComparison.OrdinalIgnoreCase))
            {
                ClearWebViewProfile(dir);
                continue;
            }

            try
            {
                Directory.Delete(dir, true);
            }
            catch
            { /* locked or inaccessible — skip */
            }
        }

        foreach (string file in Directory.GetFiles(browserDataPath))
        {
            if (Path.GetFileName(file) == ".app-version")
                continue;
            try
            {
                File.Delete(file);
            }
            catch
            { /* skip */
            }
        }
    }

    private static void ClearWebViewProfile(string ebWebViewPath)
    {
        foreach (string profileDir in Directory.GetDirectories(ebWebViewPath))
        {
            string profileName = Path.GetFileName(profileDir);

            if (string.Equals(profileName, "Default", StringComparison.OrdinalIgnoreCase))
            {
                ClearProfileContents(profileDir);
                continue;
            }

            try
            {
                Directory.Delete(profileDir, true);
            }
            catch
            { /* skip */
            }
        }

        foreach (string file in Directory.GetFiles(ebWebViewPath))
        {
            string fileName = Path.GetFileName(file);
            if (
                PreservedEbWebViewFiles.Any(p =>
                    fileName.StartsWith(p, StringComparison.OrdinalIgnoreCase)
                )
            )
                continue;

            try
            {
                File.Delete(file);
            }
            catch
            { /* skip */
            }
        }
    }

    private static void ClearProfileContents(string profilePath)
    {
        foreach (string dir in Directory.GetDirectories(profilePath))
        {
            string dirName = Path.GetFileName(dir);
            if (PreservedSubDirectories.Contains(dirName))
                continue;

            try
            {
                Directory.Delete(dir, true);
            }
            catch
            { /* skip */
            }
        }

        foreach (string file in Directory.GetFiles(profilePath))
        {
            string fileName = Path.GetFileName(file);
            if (
                PreservedFilePrefixes.Any(prefix =>
                    fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                )
            )
                continue;

            try
            {
                File.Delete(file);
            }
            catch
            { /* skip */
            }
        }
    }
}
