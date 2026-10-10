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
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using NoMercy.Launcher.Models;
using NoMercy.Launcher.ViewModels;
using NoMercy.Launcher.Views;

namespace NoMercy.Launcher.Services;

public class TrayIconManager
{
    private readonly ServerConnection _serverConnection;
    private readonly ServerProcessLauncher _processLauncher;
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _statusItem;
    private NativeMenuItem? _versionItem;
    private NativeMenuItem? _uptimeItem;
    private NativeMenuItem? _startServerItem;
    private NativeMenuItem? _stopServerItem;
    private ServerState _currentState = ServerState.Disconnected;
    private string? _dashboardUrl;
    private MainWindow? _mainWindow;
    private bool _autoStartAttempted;
    private bool _startupWindowOpened;
    private bool _appAutoLaunched;
    private bool _showOnStartup;
    private readonly bool _isDev;
    private NativeMenuItem? _showOnStartupItem;

    public TrayIconManager(
        ServerConnection serverConnection,
        ServerProcessLauncher processLauncher,
        IClassicDesktopStyleApplicationLifetime lifetime,
        bool showOnStartup = false,
        bool isDev = false
    )
    {
        _serverConnection = serverConnection;
        _processLauncher = processLauncher;
        _lifetime = lifetime;
        _showOnStartup = showOnStartup || LoadShowOnStartup();
        _isDev = isDev;
    }

    public void Initialize()
    {
        NativeMenu menu = new();

        _statusItem = new("Server: Disconnected") { IsEnabled = false };

        _versionItem = new("Version: --") { IsEnabled = false };

        _uptimeItem = new("Uptime: --") { IsEnabled = false };

        NativeMenuItemSeparator separator1 = new();

        NativeMenuItem openAppItem = new("Open App");
        openAppItem.Click += OnOpenApp;

        NativeMenuItem openDashboardItem = new("Open Dashboard");
        openDashboardItem.Click += OnOpenDashboard;

        NativeMenuItem serverControlItem = new("Server Control");
        serverControlItem.Click += OnServerControl;

        NativeMenuItem viewLogsItem = new("View Logs");
        viewLogsItem.Click += OnViewLogs;

        NativeMenuItemSeparator separator2 = new();

        _startServerItem = new("Start Server");
        _startServerItem.Click += OnStartServer;

        _stopServerItem = new("Stop Server") { IsEnabled = false };
        _stopServerItem.Click += OnStopServer;

        NativeMenuItemSeparator separator3 = new();

        _showOnStartupItem = new(_showOnStartup ? "Show on Startup: On" : "Show on Startup: Off");
        _showOnStartupItem.Click += OnToggleShowOnStartup;

        NativeMenuItem quitItem = new("Quit");
        quitItem.Click += OnQuit;

        menu.Items.Add(_statusItem);
        menu.Items.Add(_versionItem);
        menu.Items.Add(_uptimeItem);
        menu.Items.Add(separator1);
        menu.Items.Add(openAppItem);
        menu.Items.Add(openDashboardItem);
        menu.Items.Add(serverControlItem);
        menu.Items.Add(viewLogsItem);
        menu.Items.Add(separator2);
        menu.Items.Add(_startServerItem);
        menu.Items.Add(_stopServerItem);
        menu.Items.Add(separator3);
        menu.Items.Add(_showOnStartupItem);
        menu.Items.Add(quitItem);

        WindowIcon initialIcon = TrayIconFactory.CreateIcon(ServerState.Disconnected);

        _trayIcon = new()
        {
            Icon = initialIcon,
            ToolTipText = "NoMercy MediaServer - Disconnected",
            Menu = menu,
            IsVisible = true,
        };

        _trayIcon.Clicked += OnTrayIconClicked;

        _ = PollServerStatusAsync();
    }

    private async Task PollServerStatusAsync()
    {
        while (_trayIcon?.IsVisible == true)
        {
            await UpdateStatusAsync();

            int delaySeconds = _currentState switch
            {
                ServerState.Starting => 2,
                ServerState.Disconnected => 2,
                _ => 10,
            };

            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
        }
    }

    private async Task UpdateStatusAsync()
    {
        ServerStatusResponse? status = await _serverConnection.GetAsync<ServerStatusResponse>(
            "/manage/status"
        );

        if (status is null)
        {
            bool wasConnected = await _serverConnection.ConnectAsync();

            if (!wasConnected)
            {
                if (!_autoStartAttempted && !_isDev)
                {
                    _autoStartAttempted = true;
                    string extraArgs = LauncherSettings.Load().StartupArguments;
                    await _processLauncher.StartServerAsync(extraArgs);
                }

                SetState(ServerState.Disconnected, "Disconnected", null, null, null);
                return;
            }

            status = await _serverConnection.GetAsync<ServerStatusResponse>("/manage/status");
        }

        if (status is null)
        {
            SetState(ServerState.Disconnected, "Disconnected", null, null, null);
            return;
        }

        ServerState state = status.Status switch
        {
            "running" => ServerState.Running,
            "starting" => ServerState.Starting,
            _ => ServerState.Running,
        };

        // Auto-open log window when first detecting startup (if show on startup is enabled)
        if (_showOnStartup && !_startupWindowOpened)
        {
            _startupWindowOpened = true;
            OpenMainWindow(state == ServerState.Starting ? 1 : 0);
        }

        // Auto-launch the App when setup is in progress so the user sees the setup UI
        if (
            !_appAutoLaunched
            && !string.IsNullOrEmpty(status.SetupPhase)
            && status.SetupPhase != "Complete"
        )
        {
            _appAutoLaunched = await TryLaunchAppForSetup();
        }

        _dashboardUrl = status.InternalAddress;

        string uptimeText = FormatUptime(status.UptimeSeconds);
        string versionText = string.IsNullOrEmpty(status.Version) ? null! : status.Version;

        SetState(
            state,
            status.Status,
            versionText,
            uptimeText,
            status.SetupPhase,
            status.SetupLabel
        );
    }

    /// <summary>
    /// The tray keeps no table of setup phase words: the server sends them in
    /// /manage/status as setup_label (issue #438). An older server sends no
    /// label, so the raw phase name is shown rather than nothing.
    /// </summary>
    internal static string ResolveSetupPhaseLabel(string? setupPhase, string? setupLabel)
    {
        if (!string.IsNullOrEmpty(setupLabel))
            return setupLabel;

        return setupPhase ?? "";
    }

    private void SetState(
        ServerState state,
        string statusText,
        string? version,
        string? uptime,
        string? setupPhase,
        string? setupLabel = null
    )
    {
        if (_trayIcon is null)
            return;

        if (state != _currentState)
        {
            _currentState = state;
            _trayIcon.Icon = TrayIconFactory.CreateIcon(state);
        }

        string stateLabel = state switch
        {
            ServerState.Running => "Running",
            ServerState.Starting => "Starting",
            ServerState.Disconnected => "Disconnected",
            _ => "Unknown",
        };

        string phaseDetail = "";
        if (
            state == ServerState.Starting
            && !string.IsNullOrEmpty(setupPhase)
            && setupPhase != "Complete"
        )
        {
            phaseDetail = ResolveSetupPhaseLabel(setupPhase, setupLabel);
        }

        string tooltipText = string.IsNullOrEmpty(phaseDetail)
            ? $"NoMercy MediaServer - {stateLabel}"
            : $"NoMercy MediaServer - {stateLabel} — {phaseDetail}";

        _trayIcon.ToolTipText = tooltipText;

        if (_statusItem is not null)
        {
            _statusItem.Header = string.IsNullOrEmpty(phaseDetail)
                ? $"Server: {stateLabel}"
                : $"Server: {stateLabel} — {phaseDetail}";
        }

        if (_versionItem is not null)
            _versionItem.Header = version is not null ? $"Version: {version}" : "Version: --";

        if (_uptimeItem is not null)
            _uptimeItem.Header = uptime is not null ? $"Uptime: {uptime}" : "Uptime: --";

        if (_startServerItem is not null)
            _startServerItem.IsEnabled = state == ServerState.Disconnected;

        if (_stopServerItem is not null)
            _stopServerItem.IsEnabled = state != ServerState.Disconnected;
    }

    internal static string FormatUptime(long totalSeconds)
    {
        TimeSpan span = TimeSpan.FromSeconds(totalSeconds);

        if (span.TotalDays >= 1)
            return $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m";

        if (span.TotalHours >= 1)
            return $"{span.Hours}h {span.Minutes}m";

        return $"{span.Minutes}m {span.Seconds}s";
    }

    private void OpenMainWindow(int selectedTab)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_mainWindow is { IsVisible: true })
            {
                _mainWindow.SelectTab(selectedTab);
                _mainWindow.Activate();
                return;
            }

            MainViewModel viewModel = new(_serverConnection, _processLauncher)
            {
                SelectedTabIndex = selectedTab,
            };
            _mainWindow = new(viewModel);
            _mainWindow.Closed += (_, _) => _mainWindow = null;
            _mainWindow.Show();
        });
    }

    private void OnTrayIconClicked(object? sender, EventArgs e)
    {
        OpenMainWindow(0);
    }

    private async Task<bool> TryLaunchAppForSetup()
    {
        if (_serverConnection.IsConnected)
        {
            bool posted = await _serverConnection.PostAsync("/manage/app/start");
            if (posted)
                return true;
        }

        return await _processLauncher.LaunchAppAsync();
    }

    private async void OnOpenApp(object? sender, EventArgs e)
    {
        try
        {
            if (_serverConnection.IsConnected)
                await _serverConnection.PostAsync("/manage/app/start");
            else
                await _processLauncher.LaunchAppAsync();
        }
        catch (Exception ex)
        {
            LauncherLog.Error($"Tray OnOpenApp failed: {ex.Message}", ex);
        }
    }

    private void OnOpenDashboard(object? sender, EventArgs e)
    {
        string url = _dashboardUrl is not null
            ? $"{_dashboardUrl}/dashboard/system"
            : "https://app.nomercy.tv";
        OpenUrl(url);
    }

    private void OnViewLogs(object? sender, EventArgs e)
    {
        OpenMainWindow(1);
    }

    private void OnServerControl(object? sender, EventArgs e)
    {
        OpenMainWindow(0);
    }

    private async void OnStartServer(object? sender, EventArgs e)
    {
        try
        {
            string extraArgs = LauncherSettings.Load().StartupArguments;
            await _processLauncher.StartServerAsync(extraArgs);
        }
        catch (Exception ex)
        {
            LauncherLog.Error($"Tray OnStartServer failed: {ex.Message}", ex);
        }
    }

    private async void OnStopServer(object? sender, EventArgs e)
    {
        try
        {
            await _serverConnection.PostAsync("/manage/stop");
        }
        catch (Exception ex)
        {
            LauncherLog.Error($"Tray OnStopServer failed: {ex.Message}", ex);
        }
    }

    private void OnToggleShowOnStartup(object? sender, EventArgs e)
    {
        _showOnStartup = !_showOnStartup;
        SaveShowOnStartup(_showOnStartup);

        if (_showOnStartupItem is not null)
            _showOnStartupItem.Header = _showOnStartup
                ? "Show on Startup: On"
                : "Show on Startup: Off";
    }

    private static bool LoadShowOnStartup()
    {
        return LauncherSettings.Load().ShowOnStartup;
    }

    private static void SaveShowOnStartup(bool value)
    {
        TraySettings settings = LauncherSettings.Load();
        settings.ShowOnStartup = value;
        LauncherSettings.Save(settings);
    }

    private void OnQuit(object? sender, EventArgs e)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Clicked -= OnTrayIconClicked;
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _serverConnection.Dispose();
        _lifetime.Shutdown();
    }

    private static void OpenUrl(string url)
    {
        using Process? proc = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
