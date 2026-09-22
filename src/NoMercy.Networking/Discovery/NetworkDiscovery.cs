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
using System.Text;
using Microsoft.Extensions.Logging;
using Mono.Nat;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.Status;
using NoMercy.Storage;
using HttpClient = System.Net.Http.HttpClient;

namespace NoMercy.Networking.Discovery;

public class NetworkDiscovery : INetworkDiscovery
{
    private readonly IStorageDriver _driver;
    private string? _externalIp;
    private INatDevice? _device;
    private bool _hasFoundDevice;
    private bool _containerIpWarned;
    private static bool _natHandlersSubscribed;
    private TaskCompletionSource<bool>? _deviceFoundSignal;
    private List<Mapping> _createdMappings = [];

    private readonly IAuthTokenStore _authTokenStore;
    private readonly IConnectivityStatus _connectivityStatus;
    private readonly NetworkProbeConfig _networkProbeConfig;

    private readonly ILogger<NetworkDiscovery> _logger;

    public NetworkDiscovery(
        ILogger<NetworkDiscovery> logger,
        IStorageDriver driver,
        IAuthTokenStore authTokenStore,
        IConnectivityStatus connectivityStatus,
        NetworkProbeConfig networkProbeConfig
    )
    {
        _logger = logger;
        _authTokenStore = authTokenStore;
        _connectivityStatus = connectivityStatus;
        _driver = driver;
        _networkProbeConfig = networkProbeConfig;
    }

    /// <summary>
    /// Returns true exactly once for a given flag, flipping it to true. Used to
    /// subscribe the process-wide static Mono.Nat handlers a single time instead
    /// of on every rediscovery.
    /// </summary>
    public static bool ShouldSubscribeOnce(ref bool alreadySubscribed)
    {
        if (alreadySubscribed)
            return false;

        alreadySubscribed = true;
        return true;
    }

    public string InternalIp
    {
        get => field ?? GetInternalIp();
        set
        {
            if (field == value)
                return;
            field = value;
        }
    }

    public string RegistrationInternalIp
    {
        get
        {
            string ip = InternalIp;
            return string.IsNullOrEmpty(ip) || ip == "127.0.0.1" ? "0.0.0.0" : ip;
        }
    }

    public string ExternalIp
    {
        // Empty is what GetExternalIpAsync returns when every lookup failed, and it used to
        // reach the hostname builders verbatim, producing ".<id>.srv.nomercy.tv" — a name
        // with an empty leading label that no resolver will ever answer. Collapse it to the
        // same sentinel a missing value already used, which every caller already guards on.
        get => string.IsNullOrEmpty(_externalIp) ? "0.0.0.0" : _externalIp;
        set
        {
            if (_externalIp == value)
                return;
            _externalIp = value;
        }
    }

    public string? InternalIpV6 => GetInternalIpV6();

    public string? ExternalIpV6
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
        }
    }

    private const string ApexDnsSuffix = "nomercy.tv";

    private static string DnsSuffix =>
        RuntimeServerSettings.Current.UseSynthesizedDns ? "srv." + ApexDnsSuffix : ApexDnsSuffix;

    /// <summary>
    /// A tunnel terminates at the Cloudflare edge, so the server is reached at its apex
    /// hostname on 443 and the IP-derived host is meaningless. This mirrors what the control
    /// plane publishes for a tunnelled server; without it the API said one address and the
    /// server told SignalR, Chromecast and the management endpoint a different one, sending
    /// clients to a port forward that a tunnelled server does not have.
    /// </summary>
    private bool IsTunneled => _connectivityStatus.NatStatus == NatStatus.Tunneled;

    /// <summary>
    /// A quick tunnel has no name of ours; Cloudflare assigned one, and that is the only
    /// address anything can reach the server on.
    /// </summary>
    private string? QuickTunnelUrl =>
        IsTunneled && !string.IsNullOrEmpty(_connectivityStatus.PublicUrl)
            ? _connectivityStatus.PublicUrl
            : null;

    public string InternalDomain => $"{InternalIp.SafeHost()}.{Info.DeviceId}.{DnsSuffix}";
    public string InternalAddress =>
        $"https://{InternalDomain}:{RuntimeServerSettings.Current.InternalServerPort}";

    public string ExternalDomain =>
        QuickTunnelUrl is not null ? new Uri(QuickTunnelUrl).Host
        : IsTunneled ? $"{Info.DeviceId}.{ApexDnsSuffix}"
        : $"{ExternalIp.SafeHost()}.{Info.DeviceId}.{DnsSuffix}";

    public string ExternalAddress =>
        QuickTunnelUrl
        ?? (
            IsTunneled
                ? $"https://{ExternalDomain}"
                : $"https://{ExternalDomain}:{RuntimeServerSettings.Current.ExternalServerPort}"
        );

    public string DirectExternalAddress =>
        $"https://{ExternalIp.SafeHost()}.{Info.DeviceId}.{DnsSuffix}:{RuntimeServerSettings.Current.ExternalServerPort}";

    public string? ExternalAddressV6 =>
        ExternalIpV6 is not null
            ? $"https://[{ExternalIpV6}]:{RuntimeServerSettings.Current.ExternalServerPort}"
            : null;

    public bool Ipv6Enabled => CheckIpv6();

    private bool _discoveryCompleted;
    private readonly SemaphoreSlim _discoverySemaphore = new(1, 1);
    private DateTime _lastRediscovery = DateTime.MinValue;
    private static readonly TimeSpan MinRediscoveryInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Forces a fresh external-IP / NAT discovery, bypassing the one-shot completion
    /// gate that <see cref="DiscoverExternalIpAsync"/> sets on first run. Throttled to
    /// <see cref="MinRediscoveryInterval"/> so a burst of OS network-change events
    /// (DHCP renew, VPN toggle, adapter flap) cannot trigger a discovery storm.
    /// </summary>
    public async Task ForceRediscoveryAsync()
    {
        await _discoverySemaphore.WaitAsync();
        try
        {
            if (DateTime.UtcNow - _lastRediscovery < MinRediscoveryInterval)
                return;

            _discoveryCompleted = false;
            _lastRediscovery = DateTime.UtcNow;
        }
        finally
        {
            _discoverySemaphore.Release();
        }

        await DiscoverExternalIpAsync();
    }

    public async Task DiscoverExternalIpAsync()
    {
        await _discoverySemaphore.WaitAsync();
        try
        {
            if (_discoveryCompleted)
                return;

            _logger.LogInformation("Discovering Networking");

            if (ShouldSubscribeOnce(ref _natHandlersSubscribed))
            {
                // Static Mono.Nat events. Without a subscribe-once guard each
                // rediscovery re-adds these handlers (the UnknownDeviceFound
                // lambda can never even be removed), so they accumulate on the
                // static NatUtility over a long uptime and every device event
                // then fires N duplicate handlers.
                NatUtility.DeviceFound += DeviceFound;
                NatUtility.UnknownDeviceFound += (_, _) => { };
            }

            _logger.LogInformation("Discovering UPNP devices");

            if (!_hasFoundDevice)
                _deviceFoundSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

            _ = Task.Run(() => NatUtility.StartDiscovery());

            // A container's network namespace almost never has a routable path to the
            // host's IGD, so waiting here just stalls boot for the full window on every
            // Docker install for a discovery that was never going to succeed. Discovery
            // still runs (a container on host networking can occasionally see a device),
            // it just no longer blocks startup on it.
            if (!_hasFoundDevice && !Screen.IsDocker)
            {
                // Event-driven: return the instant DeviceFound fires instead of always
                // sleeping the full window, capped at the same 15s ceiling as before.
                await Task.WhenAny(_deviceFoundSignal!.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            }

            if (!_hasFoundDevice)
            {
                _logger.LogInformation("No UPNP device found");
            }

            if (string.IsNullOrEmpty(_externalIp))
            {
                try
                {
                    ExternalIp = await GetExternalIpAsync();
                }
                catch (Exception e)
                {
                    _logger.LogInformation(
                        "Failed to get external IP from API: {Message}",
                        e.Message
                    );
                }
            }

            // Discover external IPv6 address
            if (Ipv6Enabled)
            {
                try
                {
                    ExternalIpV6 = await GetExternalIpV6Async();
                    if (ExternalIpV6 is null)
                        _logger.LogDebug("No external IPv6 address available");
                }
                catch (Exception e)
                {
                    _logger.LogDebug("Failed to get external IPv6: {Message}", e.Message);
                }
            }

            _discoveryCompleted = true;
        }
        finally
        {
            _discoverySemaphore.Release();
        }
    }

    private void DeviceFound(object? sender, DeviceEventArgs args)
    {
        if (_hasFoundDevice)
            return;

        _logger.LogInformation("UPNP router Found: {DeviceEndpoint}", args.Device.DeviceEndpoint);

        _device = args.Device;
        _hasFoundDevice = true;
        _deviceFoundSignal?.TrySetResult(true);

        ApplyNatStatus();
    }

    private void ApplyNatStatus()
    {
        if (_device == null)
        {
            _connectivityStatus.NatStatus = NatStatus.None;
            return;
        }

        try
        {
            _logger.LogInformation("Trying to add UPNP records");

            Mapping tcpMapping = new(
                Protocol.Tcp,
                RuntimeServerSettings.Current.InternalServerPort,
                RuntimeServerSettings.Current.ExternalServerPort,
                0,
                "NoMercy MediaServer (TCP)"
            );
            Mapping udpMapping = new(
                Protocol.Udp,
                RuntimeServerSettings.Current.InternalServerPort,
                RuntimeServerSettings.Current.ExternalServerPort,
                0,
                "NoMercy MediaServer (UDP)"
            );

            _device.CreatePortMap(tcpMapping);
            _device.CreatePortMap(udpMapping);

            // A router accepting the SOAP call is not proof it actually forwards — Mono.Nat
            // does not confirm this itself, and routers routinely ack a mapping they then
            // drop. Reading the mapping back is the cheapest available proof it registered;
            // this still is not proof a client outside the network can reach it, which is
            // why PortForwardStrategy only ever treats Filtered as a fallback, never Verified.
            Mapping? confirmedTcp = _device.GetSpecificMapping(
                Protocol.Tcp,
                RuntimeServerSettings.Current.ExternalServerPort
            );

            if (confirmedTcp is null)
            {
                _logger.LogInformation(
                    "Router accepted the UPNP mapping request but does not report it back — treating it as not created"
                );
                _hasFoundDevice = false;
                _connectivityStatus.NatStatus = NatStatus.Closed;
                return;
            }

            _createdMappings = [tcpMapping, udpMapping];

            string ip = _device.GetExternalIP().ToString();

            _logger.LogInformation("IP address obtained from UPNP: {Ip}", ip);
            if (!string.IsNullOrEmpty(_externalIp))
                _logger.LogInformation("IP address obtained from API: {_externalIp}", _externalIp);

            if (string.IsNullOrEmpty(_externalIp))
            {
                ExternalIp = ip;
            }
        }
        catch (Exception e)
        {
            _logger.LogInformation("Failed to create UPNP records: {Message}", e.Message);
            _hasFoundDevice = false;
            _connectivityStatus.NatStatus = NatStatus.Closed;
            return;
        }

        _connectivityStatus.NatStatus = NatStatus.Filtered;
    }

    /// <summary>
    /// Removes every mapping this process successfully created and verified. A mapping is
    /// requested with lifetime 0 (permanent), so without this it survives on the router
    /// until it reboots — a stale forward per boot that outlives a graceful stop.
    /// </summary>
    public Task RemovePortMappingsAsync()
    {
        if (_device is null || _createdMappings.Count == 0)
            return Task.CompletedTask;

        foreach (Mapping mapping in _createdMappings)
        {
            try
            {
                _device.DeletePortMap(mapping);
            }
            catch (Exception e)
            {
                _logger.LogDebug("Failed to remove UPNP port mapping: {Message}", e.Message);
            }
        }

        _createdMappings = [];
        return Task.CompletedTask;
    }

    public async Task<bool> IsPortOpenAsync()
    {
        int timeoutMilliseconds = 1500;

        using TcpClient client = new();
        using CancellationTokenSource cts = new(timeoutMilliseconds);

        try
        {
            await client.ConnectAsync(
                ExternalIp,
                RuntimeServerSettings.Current.ExternalServerPort,
                cts.Token
            );
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogTrace(
                "Timeout checking {ExternalIp}:{ExternalServerPort} after {TimeoutMilliseconds}ms.",
                ExternalIp,
                RuntimeServerSettings.Current.ExternalServerPort,
                timeoutMilliseconds
            );
            return false;
        }
        catch (SocketException ex)
        {
            _logger.LogDebug(
                "SocketException checking {ExternalIp}:{ExternalServerPort}: {SocketErrorCode} ({Message})",
                ExternalIp,
                RuntimeServerSettings.Current.ExternalServerPort,
                ex.SocketErrorCode,
                ex.Message
            );
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                "Exception checking {ExternalIp}:{ExternalServerPort}: {Message}",
                ExternalIp,
                RuntimeServerSettings.Current.ExternalServerPort,
                ex.Message
            );
            return false;
        }
    }

    private string GetInternalIp()
    {
        string resolved = ResolveInternalIp();

        // Inside a container the routing interface IS the container interface, so no
        // amount of probing can discover the host's LAN IP — it has to be supplied.
        // Registering the container address anyway publishes a DNS record that no
        // client on the LAN can route to, which presents as "the server won't start".
        // The InternalIp getter re-resolves on every read, so this latches to keep one
        // actionable line in the log instead of a repeating wall of it.
        if (
            !_containerIpWarned
            && Screen.IsDocker
            && IsDockerOrWslAddress(IPAddress.Parse(resolved))
        )
        {
            _containerIpWarned = true;
            _logger.LogError(
                "Internal IP resolved to {Resolved}, a container-internal address that LAN clients "
                    + "cannot route to, so the server will advertise an unreachable address. Set "
                    + "NOMERCY_INTERNAL_IP (or --internal-ip) to this host's LAN IP; with the "
                    + "supplied compose files, export HOST_IP=$(hostname -I | awk '{{print $1}}') "
                    + "before 'docker compose up'.",
                resolved
            );
        }

        return resolved;
    }

    private string ResolveInternalIp()
    {
        // Prefer the UDP socket method — it returns the IP of the interface that would
        // route to the internet, which is always the real LAN adapter, never Docker/WSL.
        try
        {
            using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect(
                _networkProbeConfig.LocalIpDiscoveryIpv4,
                _networkProbeConfig.LocalIpDiscoveryPort
            );
            IPAddress? address = (socket.LocalEndPoint as IPEndPoint)?.Address;
            if (
                address is not null
                && !IPAddress.IsLoopback(address)
                && !address.Equals(IPAddress.Any)
                && address.AddressFamily == AddressFamily.InterNetwork
            )
            {
                string ip = address.ToString();
                if (ip != "0.0.0.0")
                    return ip;
            }
        }
        catch
        {
            // Fall through to interface enumeration
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
                if (IsVirtualNetworkInterface(nic))
                    continue;

                foreach (UnicastIPAddressInformation addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    if (IPAddress.IsLoopback(addr.Address))
                        continue;
                    if (IsDockerOrWslAddress(addr.Address))
                        continue;

                    return addr.Address.ToString();
                }
            }
        }
        catch
        {
            // No suitable interface found
        }

        return "127.0.0.1";
    }

    private static bool IsVirtualNetworkInterface(NetworkInterface nic) =>
        IsVirtualNetworkInterface(nic.Description, nic.Name);

    /// <summary>
    /// Pure keyword match extracted from <see cref="IsVirtualNetworkInterface(NetworkInterface)"/>
    /// so the classification rule is unit-testable without a real
    /// <see cref="NetworkInterface"/>, which the platform only constructs via
    /// <see cref="NetworkInterface.GetAllNetworkInterfaces"/>.
    /// </summary>
    internal static bool IsVirtualNetworkInterface(string description, string name)
    {
        string lowerDescription = description.ToLowerInvariant();
        string lowerName = name.ToLowerInvariant();

        string[] virtualKeywords =
        [
            "hyper-v",
            "virtual",
            "vethernet",
            "docker",
            "wsl",
            "vpn",
            "vmware",
            "virtualbox",
            "vbox",
        ];

        foreach (string keyword in virtualKeywords)
        {
            if (lowerDescription.Contains(keyword) || lowerName.Contains(keyword))
                return true;
        }

        return false;
    }

    internal static bool IsDockerOrWslAddress(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
            return false;

        // Docker default bridge: 172.17.0.0/16, and common Docker networks: 172.18-31.0.0/16
        if (bytes[0] == 172 && bytes[1] >= 17 && bytes[1] <= 31)
            return true;

        // WSL: commonly 172.16.x.x range
        if (bytes[0] == 172 && bytes[1] == 16)
            return true;

        return false;
    }

    private string? GetInternalIpV6()
    {
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
                    if (addr.Address.AddressFamily != AddressFamily.InterNetworkV6)
                        continue;
                    if (addr.Address.IsIPv6LinkLocal)
                        continue;
                    if (addr.Address.IsIPv6SiteLocal)
                        continue;

                    return addr.Address.ToString();
                }
            }
        }
        catch
        {
            // No IPv6 available
        }

        try
        {
            using Socket socket = new(AddressFamily.InterNetworkV6, SocketType.Dgram, 0);
            socket.Connect(
                _networkProbeConfig.LocalIpDiscoveryIpv6,
                _networkProbeConfig.LocalIpDiscoveryPort
            );
            IPEndPoint? endpoint = socket.LocalEndPoint as IPEndPoint;
            if (endpoint?.Address is not null && !endpoint.Address.IsIPv6LinkLocal)
                return endpoint.Address.ToString();
        }
        catch
        {
            // IPv6 not routable
        }

        return null;
    }

    private static string ExternalIpCacheFile =>
        Path.Combine(AppFiles.ConfigPath, "external_ip.cache");

    /// <summary>
    /// API → UPnP → file-cache → empty fallback chain. Internal (rather than
    /// private) so the fully-local branches (no auth token, no UPnP device,
    /// cache hit/miss) are unit-testable without going through the public
    /// <see cref="DiscoverExternalIpAsync"/>, which additionally blocks on a
    /// hardcoded 15s UPnP discovery window that requires real network hardware.
    /// </summary>
    internal async Task<string> GetExternalIpAsync()
    {
        _logger.LogInformation("Getting external IP address");

        // 1. Try API
        string? apiToken = _authTokenStore.AccessToken;
        if (string.IsNullOrEmpty(apiToken))
        {
            _logger.LogTrace("Skipping API external IP lookup — no auth token");
        }
        else
        {
            try
            {
                GenericHttpClient apiClient = new(ExternalServicesConfig.Current.ApiBaseUrl);
                apiClient.SetDefaultHeaders(ExternalServicesConfig.Current.UserAgent, apiToken);
                using HttpResponseMessage response = await apiClient.SendAsync(
                    HttpMethod.Get,
                    "v1/ip"
                );
                if (response.IsSuccessStatusCode)
                {
                    string ip = (await response.Content.ReadAsStringAsync()).Replace("\"", "");
                    if (!string.IsNullOrEmpty(ip))
                    {
                        CacheExternalIp(ip);
                        return ip;
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogWarning("External IP API unavailable: {Message}", e.Message);
            }
        }

        // 2. Try UPnP device
        //
        // Mono.Nat's GetExternalIP() is a synchronous SOAP call with no timeout of its
        // own. A device that answers SSDP discovery but never replies to the WANIPConnection
        // request (seen in the wild from a second "router" a LAN's discovery can pick up
        // that is not actually the gateway) blocks the calling thread forever, and since this
        // runs inside DiscoverExternalIpAsync's semaphore, every other boot step waiting on
        // network discovery piles up behind it. Bound it the same way discovery itself is
        // already bounded elsewhere in this file.
        if (_device is not null)
        {
            try
            {
                Task<string> upnpQuery = Task.Run(() => _device.GetExternalIP().ToString());
                Task completed = await Task.WhenAny(
                    upnpQuery,
                    Task.Delay(TimeSpan.FromSeconds(5))
                );

                if (completed == upnpQuery && !string.IsNullOrEmpty(upnpQuery.Result))
                {
                    CacheExternalIp(upnpQuery.Result);
                    return upnpQuery.Result;
                }

                if (completed != upnpQuery)
                    _logger.LogWarning("UPnP external IP query timed out after 5s");
            }
            catch (Exception e)
            {
                _logger.LogWarning("UPnP external IP unavailable: {Message}", e.Message);
            }
        }

        // 3. Try file cache
        string? cached = LoadCachedExternalIp();
        if (cached is not null)
        {
            _logger.LogInformation("Using cached external IP: {Cached}", cached);
            return cached;
        }

        // 4. No external IP available
        _logger.LogWarning("External IP unavailable — remote access disabled");
        return "";
    }

    private async Task<string?> GetExternalIpV6Async()
    {
        // 1. Try the NoMercy API over IPv6
        try
        {
            using HttpClient httpClient = new(
                new SocketsHttpHandler
                {
                    ConnectCallback = async (context, ct) =>
                    {
                        Socket socket = new(
                            AddressFamily.InterNetworkV6,
                            SocketType.Stream,
                            ProtocolType.Tcp
                        );
                        socket.NoDelay = true;
                        try
                        {
                            await socket.ConnectAsync(context.DnsEndPoint, ct);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch
                        {
                            socket.Dispose();
                            throw;
                        }
                    },
                }
            );
            httpClient.Timeout = TimeSpan.FromSeconds(5);
            httpClient.DefaultRequestHeaders.Add(
                "User-Agent",
                ExternalServicesConfig.Current.UserAgent
            );
            string ip = await httpClient.GetStringAsync(
                $"{ExternalServicesConfig.Current.ApiBaseUrl}v1/ip"
            );
            ip = ip.Replace("\"", "").Trim();
            if (!string.IsNullOrEmpty(ip) && ip.Contains(':'))
                return ip;
        }
        catch (Exception e)
        {
            _logger.LogDebug("External IPv6 API unavailable: {Message}", e.Message);
        }

        // 2. Try well-known IPv6 services
        string[] ipv6Services =
        [
            "https://api64.ipify.org",
            "https://v6.ident.me",
            "https://ipv6.icanhazip.com",
        ];

        foreach (string service in ipv6Services)
        {
            try
            {
                using HttpClient httpClient = new(
                    new SocketsHttpHandler
                    {
                        ConnectCallback = async (context, ct) =>
                        {
                            Socket socket = new(
                                AddressFamily.InterNetworkV6,
                                SocketType.Stream,
                                ProtocolType.Tcp
                            );
                            socket.NoDelay = true;
                            try
                            {
                                await socket.ConnectAsync(context.DnsEndPoint, ct);
                                return new NetworkStream(socket, ownsSocket: true);
                            }
                            catch
                            {
                                socket.Dispose();
                                throw;
                            }
                        },
                    }
                );
                httpClient.Timeout = TimeSpan.FromSeconds(5);
                string ip = (await httpClient.GetStringAsync(service)).Trim();
                if (!string.IsNullOrEmpty(ip) && ip.Contains(':'))
                    return ip;
            }
            catch
            {
                // Try next service
            }
        }

        // 3. No external IPv6 available
        return null;
    }

    // Internal so the cache-write half of the round trip is directly
    // testable — reaching it through GetExternalIpAsync requires a live API
    // response or a real UPnP device, neither available in a unit test.
    internal void CacheExternalIp(string ip)
    {
        try
        {
            using Stream stream = _driver.OpenWrite(ExternalIpCacheFile, overwrite: true);
            using StreamWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(ip);
        }
        catch (Exception e)
        {
            _logger.LogWarning("Failed to cache external IP: {Message}", e.Message);
        }
    }

    private string? LoadCachedExternalIp()
    {
        try
        {
            if (!_driver.FileExists(ExternalIpCacheFile))
                return null;
            using StreamReader reader = new(_driver.OpenRead(ExternalIpCacheFile), Encoding.UTF8);
            string cached = reader.ReadToEnd().Trim();
            return string.IsNullOrEmpty(cached) ? null : cached;
        }
        catch
        {
            return null;
        }
    }

    private static bool CheckIpv6()
    {
        // IPv6 discovery is intentionally disabled: the server advertises IPv4 plus
        // the external domain only. Enabling it requires verified dual-stack STUN/UPnP
        // support and AAAA handling on the API side, neither of which is implemented yet.
        // TODO: re-enable once dual-stack connectivity is supported end-to-end.
        return false;
    }
}
