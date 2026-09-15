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

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NoMercy.Networking.Connectivity;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.Status;

namespace NoMercy.Networking.Discovery;

public class NetworkChangeMonitor : IHostedService, IDisposable
{
    private readonly INetworkDiscovery _networkDiscovery;
    private readonly IConnectivityManager _connectivityManager;
    private readonly IConnectivityStatus _connectivityStatus;
    private readonly SemaphoreSlim _reevaluationLock = new(1, 1);
    private CancellationTokenSource? _debounceCts;
    private static readonly TimeSpan ReevaluationDebounceWindow = TimeSpan.FromMilliseconds(100);

    private readonly IAuthTokenStore _authTokenStore;

    private readonly ILogger<NetworkChangeMonitor> _logger;

    public NetworkChangeMonitor(
        ILogger<NetworkChangeMonitor> logger,
        IAuthTokenStore authTokenStore,
        INetworkDiscovery networkDiscovery,
        IConnectivityManager connectivityManager,
        IConnectivityStatus connectivityStatus
    )
    {
        _logger = logger;
        _authTokenStore = authTokenStore;
        _networkDiscovery = networkDiscovery;
        _connectivityManager = connectivityManager;
        _connectivityStatus = connectivityStatus;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        _logger.LogDebug("Network change monitor started");
        return Task.CompletedTask;
    }

    // Internal (not private) so the OS NetworkChange event handlers — which
    // this class can only otherwise reach by actually triggering a real NIC
    // address/availability change on the test machine — are directly
    // unit-testable against fake INetworkDiscovery/IConnectivityManager
    // collaborators.
    internal async void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        string oldIp = _networkDiscovery.InternalIp;
        // Force re-discovery by reading from interfaces
        string newIp = GetCurrentInternalIp();

        if (newIp == oldIp)
            return;

        // A NIC flap fires this burst multiple times in quick succession. Coalesce the
        // burst into the one that survives the window rather than dropping every event
        // after the first — the old WaitAsync(0) single-flight gate discarded a concurrent
        // change outright, which could leave a stale address never re-pinged if the change
        // that got dropped was the real, final one.
        if (!await DebounceAsync())
            return;

        if (!await _reevaluationLock.WaitAsync(0))
            return;

        try
        {
            _logger.LogInformation("Network address changed: {OldIp} → {NewIp}", [oldIp, newIp]);
            _networkDiscovery.InternalIp = newIp;

            // Re-discover external IP (force past the one-shot completion gate)
            await _networkDiscovery.ForceRediscoveryAsync();

            // Re-evaluate connectivity strategies
            await _connectivityManager.EvaluateAsync(CancellationToken.None);

            // Send update to NoMercy API
            await SendUpdate();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Network change handling failed: {Message}", ex.Message);
        }
        finally
        {
            _reevaluationLock.Release();
        }
    }

    internal async void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable)
            return;

        // Shares the debounce window with OnNetworkAddressChanged: a NIC flap raises both
        // events, and coalescing them into one reevaluation avoids racing two evaluations
        // against each other on the ConnectivityManager's active strategy.
        if (!await DebounceAsync())
            return;

        // Share the single-flight lock with OnNetworkAddressChanged: two concurrent
        // EvaluateAsync calls would race on the ConnectivityManager's active strategy
        // (tear down / double-establish the tunnel or port-forward against each other).
        if (!await _reevaluationLock.WaitAsync(0))
            return;

        try
        {
            await _networkDiscovery.ForceRediscoveryAsync();
            await _connectivityManager.EvaluateAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Network availability change handling failed: {Message}",
                ex.Message
            );
        }
        finally
        {
            _reevaluationLock.Release();
        }
    }

    /// <summary>
    /// Waits out the debounce window, restarting it (and cancelling any wait already in
    /// progress) every time this is called. Returns false when superseded by a later call
    /// within the window — that later call carries the reevaluation forward, so the earlier
    /// one exits without ever reaching the single-flight lock.
    /// </summary>
    private async Task<bool> DebounceAsync()
    {
        CancellationTokenSource cts = new();
        CancellationTokenSource? previous = Interlocked.Exchange(ref _debounceCts, cts);
        previous?.Cancel();
        previous?.Dispose();

        try
        {
            await Task.Delay(ReevaluationDebounceWindow, cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // Internal so the real socket-trick + NIC-enumeration resolution can be
    // asserted against this machine's actual network stack directly.
    internal static string GetCurrentInternalIp()
    {
        // UDP socket trick: OS picks the outbound source address — always the real LAN IP.
        try
        {
            using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            IPAddress? address = (socket.LocalEndPoint as IPEndPoint)?.Address;
            if (
                address is not null
                && !IPAddress.IsLoopback(address)
                && !address.Equals(IPAddress.Any)
                && address.AddressFamily == AddressFamily.InterNetwork
            )
            {
                string socketIp = address.ToString();
                if (socketIp != "0.0.0.0")
                    return socketIp;
            }
        }
        catch
        {
            // Fall through to NIC enumeration
        }

        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (
                    nic.NetworkInterfaceType
                    is NetworkInterfaceType.Loopback
                        or NetworkInterfaceType.Tunnel
                )
                    continue;

                foreach (UnicastIPAddressInformation addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    if (IPAddress.IsLoopback(addr.Address))
                        continue;
                    if (addr.Address.Equals(IPAddress.Any))
                        continue;

                    string nicIp = addr.Address.ToString();
                    if (nicIp == "0.0.0.0")
                        continue;

                    return nicIp;
                }
            }
        }
        catch
        {
            // Fall through
        }

        return "127.0.0.1";
    }

    // Internal so the no-auth-token early-out (the only branch reachable
    // without a live POST to the NoMercy API) is directly unit-testable.
    public Task SendUpdate() => SendUpdate("network change");

    public async Task SendUpdate(string reason)
    {
        try
        {
            Dictionary<string, string> serverData = new()
            {
                { "id", Info.DeviceId.ToString() },
                { "name", Info.DeviceName },
                { "internal_ip", _networkDiscovery.RegistrationInternalIp },
                { "internal_ipv6", _networkDiscovery.InternalIpV6.OrEmpty() },
                { "external_ipv6", _networkDiscovery.ExternalIpV6.OrEmpty() },
                { "internal_port", RuntimeServerSettings.Current.InternalServerPort.ToString() },
                { "external_port", RuntimeServerSettings.Current.ExternalServerPort.ToString() },
                { "version", Software.Version!.ToString() },
                { "platform", Info.Platform },
                { "stun_public_ip", _connectivityStatus.StunPublicIp.OrEmpty() },
                { "stun_public_port", (_connectivityStatus.StunPublicPort?.ToString()).OrEmpty() },
                { "stun_nat_type", _connectivityStatus.NatStatus.ToString() },
                { "transport", _connectivityStatus.Transport },
                { "public_url", _connectivityStatus.PublicUrl ?? string.Empty },
            };

            _logger.LogInformation("Updating server information ({Reason})...", reason);

            string? token = _authTokenStore.AccessToken;
            if (string.IsNullOrEmpty(token))
            {
                _logger.LogTrace("Skipping network change ping — no auth token");
                return;
            }

            GenericHttpClient authClient = new(ExternalServicesConfig.Current.ApiServerBaseUrl);
            authClient.SetDefaultHeaders(ExternalServicesConfig.Current.UserAgent, token);
            string response = await authClient.SendAndReadAsync(
                HttpMethod.Post,
                "ping",
                new FormUrlEncodedContent(serverData)
            );

            object? data = JsonConvert.DeserializeObject(response);

            if (data == null)
                throw new("Failed to update server information");

            _logger.LogInformation("Server information updated successfully");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to send IP update: {Message}", ex.Message);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _reevaluationLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
