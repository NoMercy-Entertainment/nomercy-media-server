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

using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Timers;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NoMercy.Events;
using NoMercy.Events.Cast;
using NoMercy.Networking.Discovery;
using NoMercy.NmSystem.Extensions;
using NoMercy.Storage.Common;
using Sharpcaster;
using Sharpcaster.Models;
using Sharpcaster.Models.ChromecastStatus;
using Sharpcaster.Models.Media;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace NoMercy.Networking.Cast;

public class ChromeCastService : IChromeCastService
{
    private readonly INetworkDiscovery _networkDiscovery;

    private readonly ILogger<ChromeCastService> _logger;

    public ChromeCastService(ILogger<ChromeCastService> logger, INetworkDiscovery networkDiscovery)
    {
        _logger = logger;
        _networkDiscovery = networkDiscovery;
    }

    private readonly ChromecastLocator Locator = new();
    private IEnumerable<ChromecastReceiver> _chromecastReceivers = new List<ChromecastReceiver>();

    // Synthesized entries survive mDNS rescans (which replace _chromecastReceivers
    // wholesale). Without this, two TVs alternate wiping each other every ping.
    private readonly ConcurrentDictionary<string, ChromecastReceiver> _synthesizedReceivers = new(
        StringComparer.OrdinalIgnoreCase
    );

    private readonly SemaphoreSlim _rediscoveryGate = new(1, 1);
    private DateTime _lastRediscoveryUtc = DateTime.MinValue;
    private readonly TimeSpan _rediscoveryCooldown = TimeSpan.FromSeconds(30);

    // Per-receiver client pool keyed by receiver name. Thread-safe.
    private readonly ConcurrentDictionary<string, ChromecastClient> ClientPool = new(
        StringComparer.OrdinalIgnoreCase
    );

    // Per-name connect gate so concurrent callers (VideoHub, MusicHub × 2
    // observed in practice) don't each open a wasted TCP connection to the
    // same TV. First caller wins, others wait and read from ClientPool.
    //
    // The shared keyed lock, which also removes a receiver's key once nobody
    // is connecting to it any more. Its keys are ordinal, so the name is
    // lower-cased on the way in: the gate has to be at least as coarse as the
    // pool's comparer, or two spellings of one TV would each open their own
    // connection to it.
    private readonly KeyedAsyncLock _connectGates = new();

    // Tracks which receiver was most recently selected (for compat callers that
    // call SelectChromecast then Launch/CastPlaylist without passing a name).
    private volatile string? _lastSelectedName;

    public async Task Init()
    {
        // Serialize with the on-demand rediscovery path (FindReceiverNameByIpAsync)
        // so a boot-time scan and a cache-miss rescan cannot race on _chromecastReceivers.
        await _rediscoveryGate.WaitAsync();
        try
        {
            _chromecastReceivers = [.. await Locator.FindReceiversAsync()];
            _lastRediscoveryUtc = DateTime.UtcNow;

            foreach (ChromecastReceiver chromecast in _chromecastReceivers)
                _logger.LogInformation("Found chromecast: {Name}", chromecast.Name);
        }
        finally
        {
            _rediscoveryGate.Release();
        }
    }

    public string[] GetChromeCasts()
    {
        return [.. _chromecastReceivers.Select(x => x.Name)];
    }

    /// <summary>
    /// Looks up a discovered Chromecast receiver by its LAN IP address. Useful
    /// when the caller knows the device IP (from the Devices table) but not the
    /// receiver's mDNS broadcast name (which the user sets independently in
    /// Android TV settings, e.g. "Tv in woonkamer" vs the NoMercy custom name
    /// "Woonkamer TV"). Re-discovers via mDNS once when the cache misses
    /// because Init() runs at server start and stale caches are common — the
    /// TV may have come online after that boot phase.
    /// </summary>
    public async Task<string?> FindReceiverNameByIpAsync(string ip)
    {
        if (string.IsNullOrEmpty(ip))
            return null;

        // A Cast device answers on port 8009 of the network this server sits on, so an
        // address off that network can never be one. Device rows store where the client
        // connected FROM, and a phone or TV reaching the server through the tunnel
        // registers its public address — this method then rescanned mDNS for it, failed,
        // synthesized a receiver named after the WAN IP, and every device listing tried a
        // TCP connect to the owner's own router. Refusing the address here is the one
        // choke point all three callers share.
        if (!CastAddress.IsOnThisNetwork(ip))
        {
            _logger.LogDebug("Not a castable address, skipping Chromecast lookup: {Ip}", ip);
            return null;
        }

        string? hit = LookupNameByIp(ip);
        if (hit is not null)
            return hit;

        // Cooldown gate: rescanning mDNS on every ping for every TV burned 2-3s
        // per call and spammed the log. Skip the rescan if we just tried.
        bool shouldRescan;
        await _rediscoveryGate.WaitAsync();
        try
        {
            shouldRescan = DateTime.UtcNow - _lastRediscoveryUtc >= _rediscoveryCooldown;
            if (shouldRescan)
            {
                _lastRediscoveryUtc = DateTime.UtcNow;
                _logger.LogInformation("Chromecast cache miss for {Ip} — refreshing mDNS", ip);
                try
                {
                    _chromecastReceivers = [.. await Locator.FindReceiversAsync()];
                    foreach (ChromecastReceiver chromecast in _chromecastReceivers)
                        _logger.LogInformation(
                            "Discovered chromecast: {Name} @ {Host}",
                            chromecast.Name,
                            chromecast.DeviceUri.Host
                        );
                }
                catch (Exception ex)
                {
                    _logger.LogInformation("Chromecast re-discovery failed: {Message}", ex.Message);
                }
            }
        }
        finally
        {
            _rediscoveryGate.Release();
        }

        if (shouldRescan)
        {
            string? cached = LookupNameByIp(ip);
            if (cached is not null)
                return cached;
        }

        // mDNS still empty — Windows firewall / Zeroconf flakiness commonly
        // blocks the multicast scan even when the device is reachable on the
        // same /24. Synthesize a receiver record from the IP and the default
        // Cast control port so SelectChromecast can still try a direct TCP
        // connect. The phone-to-TV cast already proved IP reachability; only
        // the discovery layer is broken.
        // ConcurrentDictionary.GetOrAdd may invoke the factory multiple times
        // under concurrent miss; we want the "Synthesized..." log emitted at
        // most once per IP per process. Build candidate first, then TryAdd.
        if (!_synthesizedReceivers.TryGetValue(ip, out ChromecastReceiver? synthetic))
        {
            ChromecastReceiver candidate = new()
            {
                Name = ip,
                DeviceUri = new($"https://{ip}"),
                Port = 8009,
                Model = "Chromecast",
                Version = "0",
                Status = string.Empty,
                ExtraInformation = new Dictionary<string, string>(),
            };

            if (_synthesizedReceivers.TryAdd(ip, candidate))
            {
                _logger.LogInformation(
                    "Synthesized Chromecast receiver for {Ip}:8009 — mDNS unavailable, will attempt direct connect",
                    ip
                );
                synthetic = candidate;
            }
            else
            {
                synthetic = _synthesizedReceivers[ip];
            }
        }

        return synthetic.Name;
    }

    private string? LookupNameByIp(string ip)
    {
        foreach (ChromecastReceiver receiver in _chromecastReceivers)
        {
            if (
                receiver.DeviceUri is not null
                && string.Equals(receiver.DeviceUri.Host, ip, StringComparison.OrdinalIgnoreCase)
            )
                return receiver.Name;
        }

        return _synthesizedReceivers.TryGetValue(ip, out ChromecastReceiver? synthetic)
            ? synthetic.Name
            : null;
    }

    // --- Pool helpers ---

    // Internal (not private) so the event-wiring can be exercised directly
    // against a real, unconnected Sharpcaster ChromecastClient — no live
    // Chromecast device is needed to construct one or register handlers on it.
    internal ChromecastClient BuildClient(string receiverName)
    {
        ChromecastClient client = new();

        client.MediaChannel.StatusChanged += (sender, args) =>
        {
            if (EventBusProvider.IsConfigured)
                _ = EventBusProvider.Current.PublishAsync(
                    new CastDeviceStatusChangedEvent
                    {
                        EventType = "StatusChanged",
                        StatusData = new()
                        {
                            { "sender", sender },
                            { "args", args },
                            { "receiverName", receiverName },
                        },
                    }
                );
        };

        client.ReceiverChannel.ReceiverStatusChanged += (sender, args) =>
        {
            if (EventBusProvider.IsConfigured)
                _ = EventBusProvider.Current.PublishAsync(
                    new CastDeviceStatusChangedEvent
                    {
                        EventType = "ReceiverStatusChanged",
                        StatusData = new()
                        {
                            { "sender", sender },
                            { "args", args },
                            { "receiverName", receiverName },
                        },
                    }
                );
        };

        client.ReceiverChannel.LaunchStatusChanged += (sender, args) =>
        {
            if (EventBusProvider.IsConfigured)
                _ = EventBusProvider.Current.PublishAsync(
                    new CastDeviceStatusChangedEvent
                    {
                        EventType = "LaunchStatusChanged",
                        StatusData = new()
                        {
                            { "sender", sender },
                            { "args", args },
                            { "receiverName", receiverName },
                        },
                    }
                );
        };

        return client;
    }

    // Sharpcaster heartbeat is async-void on a System.Timers.Timer. When the
    // cast device disconnects mid-flight, the SSL write inside SendAsync
    // throws a SocketException that escapes via the AsyncStateMachine and
    // becomes UnhandledException — which kills the entire process in
    // .NET 5+. Stop+Dispose alone races with in-flight Elapsed callbacks
    // and Sharpcaster's reconnect logic, so we also rewire the Timer's
    // private _onIntervalElapsed multicast delegate to a no-op wrapper.
    // If the Timer ever fires after this call, our wrapper runs in place
    // of Sharpcaster's TimerElapsed and swallows synchronously — no
    // async-void chain ever starts. Real fix is to vendor-fork Sharpcaster
    // and try/catch its TimerElapsed body.
    private readonly FieldInfo? _timerOnIntervalElapsedField = typeof(System.Timers.Timer).GetField(
        "_onIntervalElapsed",
        BindingFlags.NonPublic | BindingFlags.Instance
    );

    // Internal so the reflection-based neutralization can be verified directly
    // against a bare (unconnected) ChromecastClient in tests.
    internal void DisableSharpcasterHeartbeat(ChromecastClient client)
    {
        try
        {
            object? channels = client
                .GetType()
                .GetProperty("Channels", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(client);

            if (channels is System.Collections.IList channelList)
            {
                List<object> heartbeats = [];

                foreach (object? channel in channelList)
                {
                    if (
                        channel is null
                        || !channel.GetType().Name.Contains("Heartbeat", StringComparison.Ordinal)
                    )
                        continue;

                    heartbeats.Add(channel);
                    NeutralizeTimersIn(channel);
                }

                foreach (object heartbeat in heartbeats)
                    channelList.Remove(heartbeat);
            }

            // Also reach the HeartbeatChannel via its public property — Sharpcaster
            // exposes it directly, and the timer there can be constructed lazily
            // after Channels is built. Hitting both paths catches every variant.
            object? hbProp = client
                .GetType()
                .GetProperty("HeartbeatChannel", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(client);
            if (hbProp is not null)
                NeutralizeTimersIn(hbProp);
        }
        catch (Exception ex)
        {
            _logger.LogInformation("DisableSharpcasterHeartbeat failed: {Message}", ex.Message);
        }
    }

    // Sharpcaster reconstructs HeartbeatChannel._timer on internal reconnects, so a
    // one-shot neutralize at Connect leaves a window where the new timer fires and
    // an async-void TimerElapsed crashes the process. The warden re-applies the
    // neutralize every WardenIntervalMs across every client in the pool. Catching
    // here is mandatory — the warden's own Elapsed must never re-throw.
    private const double WardenIntervalMs = 2000;
    private readonly System.Timers.Timer _heartbeatWarden = new(WardenIntervalMs)
    {
        AutoReset = true,
    };
    private int _wardenStarted;

    private void EnsureHeartbeatWardenStarted()
    {
        if (Interlocked.CompareExchange(ref _wardenStarted, 1, 0) != 0)
            return;

        _heartbeatWarden.Elapsed += (_, _) =>
        {
            try
            {
                foreach (ChromecastClient pooled in ClientPool.Values)
                {
                    try
                    {
                        DisableSharpcasterHeartbeat(pooled);
                    }
                    catch
                    {
                        // best-effort per client
                    }
                }
            }
            catch
            {
                // best-effort — warden survives anything
            }
        };
        _heartbeatWarden.Start();
    }

    // Wire disconnect cleanup once per client: when Sharpcaster reports the SSL/TCP
    // session dropped, remove from the pool so the next caller gets a fresh client
    // instead of reusing one whose heartbeat is about to PING a dead socket.
    private void WireDisconnectCleanup(string name, ChromecastClient client)
    {
        client.Disconnected += (_, _) =>
        {
            _logger.LogInformation("Chromecast disconnected: {Name} — removing from pool", name);
            if (ClientPool.TryRemove(name, out ChromecastClient? removed))
            {
                try
                {
                    DisableSharpcasterHeartbeat(removed);
                }
                catch
                {
                    // best-effort
                }
            }
        };
    }

    // Internal so the pure reflection-walk can be tested against a plain
    // object graph with a private System.Timers.Timer field — no Chromecast
    // client or network is involved.
    internal void NeutralizeTimersIn(object owner)
    {
        foreach (
            FieldInfo field in owner
                .GetType()
                .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
        )
        {
            object? value;
            try
            {
                value = field.GetValue(owner);
            }
            catch
            {
                continue;
            }

            if (value is System.Timers.Timer timer)
                NeutralizeTimer(timer);
        }
    }

    // Internal so the timer-neutering behavior (Stop, AutoReset=false, private
    // _onIntervalElapsed swap) can be verified against a real System.Timers.Timer.
    internal void NeutralizeTimer(System.Timers.Timer timer)
    {
        try
        {
            timer.Stop();
            timer.AutoReset = false;
        }
        catch
        {
            // best-effort
        }

        if (_timerOnIntervalElapsedField is not null)
        {
            try
            {
                ElapsedEventHandler safe = (_, _) => {
                    // Replaces Sharpcaster's TimerElapsed entirely. If the
                    // timer fires before Stop takes effect or Sharpcaster
                    // restarts it via reconnect, we no-op instead of
                    // entering the SocketException-prone async-void path.
                };
                _timerOnIntervalElapsedField.SetValue(timer, safe);
            }
            catch
            {
                // best-effort
            }
        }

        // Do NOT Dispose the Timer. Sharpcaster's HeartbeatChannel.OnMessageReceived
        // toggles `_timer.Enabled = true` whenever a PONG arrives — set_Enabled on a
        // disposed timer throws ObjectDisposedException from an async-void path and
        // kills the process. Keeping the timer alive with an empty handler is safe:
        // ticks still fire but our no-op runs, no SendAsync, no SocketException.
    }

    private async Task<ChromecastClient?> GetOrCreateClientAsync(string name)
    {
        ChromecastReceiver? receiver = _chromecastReceivers.FirstOrDefault(x =>
            string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)
        );

        // Fall back to synthesized receivers — when mDNS is blocked (Windows
        // firewall / multicast scoping issues), FindReceiverNameByIpAsync
        // produces a synthetic record keyed by IP. Without this lookup the
        // Cast launch path always returned "Chromecast not found" even
        // though a synthetic was available for direct connect.
        if (
            receiver == null
            && _synthesizedReceivers.TryGetValue(name, out ChromecastReceiver? synthetic)
        )
            receiver = synthetic;

        if (receiver == null)
        {
            _logger.LogInformation("Chromecast not found: {Name}", name);
            return null;
        }

        // Lazy-create and connect only when a new entry is added to the pool.
        // If an existing client is already in the pool we reuse it as-is; the
        // caller may reconnect explicitly via SelectChromecast if needed.
        if (ClientPool.TryGetValue(name, out ChromecastClient? existing))
            return existing;

        // Serialize per-name so 3 concurrent callers don't each open a TCP
        // connection. First caller does the work; the rest wait, then read
        // from ClientPool (which the winner populated below). Lower-cased
        // because the gate has to be at least as coarse as the pool's own
        // case-insensitive comparer.
        using IDisposable gate = await _connectGates.AcquireAsync(name.ToLowerInvariant());

        if (ClientPool.TryGetValue(name, out ChromecastClient? raced))
            return raced;

        ChromecastClient newClient = BuildClient(name);
        _logger.LogInformation("Connecting to chromecast: {Name}", name);
        await newClient.ConnectChromecast(receiver);

        // Disable Sharpcaster's internal heartbeat after Connect — the
        // timer is only constructed once Connect spins up the channels.
        DisableSharpcasterHeartbeat(newClient);
        WireDisconnectCleanup(name, newClient);
        EnsureHeartbeatWardenStarted();

        ClientPool[name] = newClient;
        return newClient;
    }

    // The Sharpcaster heartbeat is disabled process-wide (re-enabling it crashes
    // .NET via an async-void SocketException on disconnect — see
    // DisableSharpcasterHeartbeat). The trade-off: cast_shell drops idle sockets
    // and, with no heartbeat, the client's Disconnected event never fires to evict
    // the dead pool entry — so a cast that reuses such an entry writes to a closed
    // socket (WSAECONNABORTED). When a caller detects that (a failed status probe),
    // evict the stale client and build a fresh connection.
    private async Task<ChromecastClient?> ForceReconnectAsync(string name)
    {
        if (ClientPool.TryRemove(name, out ChromecastClient? stale))
        {
            try
            {
                DisableSharpcasterHeartbeat(stale);
            }
            catch
            {
                // best-effort
            }

            try
            {
                await stale.DisconnectAsync();
            }
            catch
            {
                // best-effort — the socket is already gone
            }
        }

        return await GetOrCreateClientAsync(name);
    }

    // --- Public API (name-explicit overloads) ---

    /// <summary>
    /// Connects to (or reuses an existing connection to) the named receiver and
    /// makes it the "current" receiver for subsequent parameterless calls on
    /// this thread.
    /// </summary>
    public async Task SelectChromecast(string name)
    {
        ChromecastClient? client = await GetOrCreateClientAsync(name);
        if (client is not null)
            _lastSelectedName = name;
    }

    public async Task SelectChromecast(ChromecastReceiver? receiver)
    {
        if (receiver == null)
        {
            _logger.LogInformation("Chromecast not found");
            return;
        }

        await SelectChromecast(receiver.Name);
    }

    /// <summary>
    /// Launches the NoMercy cast application on the named receiver. If
    /// <paramref name="name"/> is null the last receiver selected on this
    /// thread is used.
    /// </summary>
    public async Task Launch(string? name = null)
    {
        string? target = name ?? _lastSelectedName;
        if (target == null)
            return;

        if (!ClientPool.TryGetValue(target, out ChromecastClient? client))
            return;

        _logger.LogInformation("Launching chromecast: {Target}", target);
        _ = await client.LaunchApplicationAsync("925B4C3C");
    }

    private int _androidLaunchRequestId = new Random().Next();

    /// <summary>
    /// Launches the NoMercy cast application as an Android TV receiver. Sends
    /// a LAUNCH protocol message with androidReceiverCompatible=true so cast_shell
    /// foregrounds the native APK (tv.nomercy.app) instead of falling back to
    /// the Web Receiver placeholder. Sharpcaster's built-in LaunchApplicationAsync
    /// doesn't expose launchOptions, so we craft and send the JSON ourselves
    /// against the public ChromecastClient.SendAsync surface.
    ///
    /// When <paramref name="customData"/> is supplied (cast-receiver-leanback
    /// flow), the object is serialized into the LAUNCH payload's customData
    /// field. Both the APK (via Cast Connect intent extras) and the Web
    /// Receiver (via cast.framework.system.LaunchRequestEvent.data) receive
    /// it. The APK ignores auth fields (it has its own persistent Keycloak
    /// session); the Web Receiver consumes them to bootstrap volatile auth.
    /// </summary>
    public async Task LaunchAndroidReceiver(
        string? name = null,
        object? customData = null,
        bool useAndroidReceiver = true
    )
    {
        string? target = name ?? _lastSelectedName;
        if (target == null)
            return;

        if (!ClientPool.TryGetValue(target, out ChromecastClient? client))
            return;

        // Force a round-trip GET_STATUS before LAUNCH. Sharpcaster's
        // ConnectChromecast fires CONNECT and returns; the actual processing
        // on cast_shell side races with our subsequent LAUNCH. If LAUNCH
        // lands before cast_shell finished wiring the session, cast_shell
        // accepts it but skips the CEC OneTouchPlay step (gated on full
        // session handshake). GetChromecastStatusAsync awaits a
        // RECEIVER_STATUS reply, which can only arrive after CONNECT is
        // processed end-to-end.
        try
        {
            await client.ReceiverChannel.GetChromecastStatusAsync();
        }
        catch (Exception ex)
        {
            // A write failure here means the pooled connection is dead: cast_shell
            // dropped the idle socket and — with the heartbeat disabled — no
            // Disconnected event fired to evict the stale pool entry, so we just
            // pulled a client writing to a closed socket. Rebuild the connection
            // once and re-probe before attempting LAUNCH.
            _logger.LogInformation(
                "LaunchAndroidReceiver pre-LAUNCH GET_STATUS failed for {Target}: {Message} — reconnecting stale client",
                target,
                ex.Message
            );

            ChromecastClient? reconnected = await ForceReconnectAsync(target);
            if (reconnected is null)
            {
                _logger.LogInformation(
                    "LaunchAndroidReceiver: reconnect to {Target} failed — aborting launch",
                    target
                );
                return;
            }

            client = reconnected;

            try
            {
                await client.ReceiverChannel.GetChromecastStatusAsync();
            }
            catch (Exception retryEx)
            {
                _logger.LogInformation(
                    "LaunchAndroidReceiver: GET_STATUS still failing for {Target} after reconnect: {Message}",
                    target,
                    retryEx.Message
                );
            }
        }

        int requestId = Interlocked.Increment(ref _androidLaunchRequestId);
        string json = BuildLaunchJson(requestId, customData, useAndroidReceiver);

        string flavor = useAndroidReceiver ? "androidReceiverCompatible" : "webReceiverOnly";
        _logger.LogInformation(
            "Launching cast-tv ({Flavor}{CustomData}) on {Target}",
            flavor,
            (customData is null ? "" : ", customData"),
            target
        );

        // If the receiver is already running our app (panel already on,
        // e.g. mid-playback or idle on our splash) cast_shell treats a
        // repeat LAUNCH as a no-op from its point of view and never emits a
        // NEW RECEIVER_STATUS broadcast — nothing about the top-level
        // Application object changed. The event-based watcher below would
        // then wait out the full timeout and "retry" for a device that was
        // never actually broken. The pre-LAUNCH GET_STATUS above already
        // refreshed client.ChromecastStatus, so use that snapshot to treat
        // an already-active app as an immediate ack. LAUNCH is still sent
        // (below) so the running app receives the fresh customData/session
        // info — only the wait-for-broadcast confirmation is skipped.
        bool alreadyRunning = string.Equals(
            client.ChromecastStatus?.Application?.AppId,
            "925B4C3C",
            StringComparison.OrdinalIgnoreCase
        );

        // Watch for cast_shell's reply on this request id. RECEIVER_STATUS
        // arrives back on the receiver channel when cast_shell accepts the
        // LAUNCH — if we don't see ChromecastStatus.Application.AppId match
        // ours within 1.5s, retry once. Single retry is enough; the failure
        // mode is a timing race during the first cold connect, not a hard
        // protocol break.
        bool launchAccepted = alreadyRunning;
        EventHandler<ChromecastStatus>? watcher = null;
        watcher = (sender, status) =>
        {
            if (
                status.Application is not null
                && string.Equals(
                    status.Application.AppId,
                    "925B4C3C",
                    StringComparison.OrdinalIgnoreCase
                )
            )
                launchAccepted = true;
        };
        client.ReceiverChannel.ReceiverStatusChanged += watcher;

        try
        {
            await SendLaunchAsync(client, requestId, json);

            // Wait for the RECEIVER_STATUS broadcast confirming the app was
            // launched. 5s covers cast_shell's handshake + resource allocation
            // window after mDNS fallback to direct-connect on port 8009.
            int waited = 0;
            while (!launchAccepted && waited < 5000)
            {
                await Task.Delay(100);
                waited += 100;
            }

            if (!launchAccepted)
            {
                _logger.LogInformation(
                    "LaunchAndroidReceiver: no LAUNCH ack from {Target} within 5s — retrying once",
                    target
                );
                int retryRequestId = Interlocked.Increment(ref _androidLaunchRequestId);
                string retryJson = BuildLaunchJson(retryRequestId, customData, useAndroidReceiver);
                await SendLaunchAsync(client, retryRequestId, retryJson);
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation(
                "LaunchAndroidReceiver failed for {Target}: {Message}",
                target,
                ex.Message
            );
        }
        finally
        {
            client.ReceiverChannel.ReceiverStatusChanged -= watcher;
        }
    }

    // Internal so all four LAUNCH-payload permutations (customData present/absent
    // x androidReceiver/webReceiver) are directly unit-testable — reaching this
    // through LaunchAndroidReceiver's public surface requires a live Chromecast
    // connection already present in ClientPool.
    internal string BuildLaunchJson(int requestId, object? customData, bool useAndroidReceiver)
    {
        // Newtonsoft.Json honours [JsonProperty("snake_case")] on
        // LaunchCustomData so the receiver sees access_token / refresh_token /
        // user_id. System.Text.Json ignores those attributes and silently
        // drops the receiver's auth hydration.
        //
        // useAndroidReceiver controls whether cast_shell tries to dispatch to
        // the registered Cast Connect APK first. When true, supportedAppTypes
        // and launchOptions.androidReceiverCompatible nudge cast_shell toward
        // the APK path; if no APK is installed cast_shell falls back to the
        // Web Receiver — and the fallback drops customData on the floor. For
        // Web-Receiver-only TVs (no APK), call with false so cast_shell goes
        // straight to the Web Receiver and customData survives.
        if (customData is null)
        {
            object payload = useAndroidReceiver
                ? new
                {
                    type = "LAUNCH",
                    requestId,
                    appId = "925B4C3C",
                    language = "en-US",
                    supportedAppTypes = new[] { "ANDROID_TV" },
                    launchOptions = new { androidReceiverCompatible = true },
                }
                : new
                {
                    type = "LAUNCH",
                    requestId,
                    appId = "925B4C3C",
                    language = "en-US",
                    supportedAppTypes = new[] { "WEB" },
                };
            return JsonConvert.SerializeObject(payload);
        }

        object payloadWithCustomData = useAndroidReceiver
            ? new
            {
                type = "LAUNCH",
                requestId,
                appId = "925B4C3C",
                language = "en-US",
                supportedAppTypes = new[] { "ANDROID_TV" },
                launchOptions = new { androidReceiverCompatible = true },
                customData,
            }
            : new
            {
                type = "LAUNCH",
                requestId,
                appId = "925B4C3C",
                language = "en-US",
                supportedAppTypes = new[] { "WEB" },
                customData,
            };
        return JsonConvert.SerializeObject(payloadWithCustomData);
    }

    private async Task SendLaunchAsync(ChromecastClient client, int requestId, string json)
    {
        await client.SendAsync(
            logger: null,
            ns: "urn:x-cast:com.google.cast.receiver",
            messageRequestId: requestId,
            messagePayload: json,
            destinationId: "receiver-0"
        );
    }

    /// <summary>
    /// Casts a playlist to the named receiver. If <paramref name="name"/> is
    /// null the last receiver selected on this thread is used.
    /// </summary>
    public async Task CastPlaylist(string value, string? name = null, string? accessToken = null)
    {
        string? target = name ?? _lastSelectedName;
        if (target == null)
            return;

        if (!ClientPool.TryGetValue(target, out ChromecastClient? client))
            return;

        _logger.LogInformation("Casting playlist to {Target}: {Value}", target, value);

        string externalAddress = (_networkDiscovery?.ExternalAddress).OrEmpty();
        string? token = accessToken;

        CastCustomData customData = new()
        {
            AccessToken = token,
            BasePath = externalAddress,
            Playlist = $"{externalAddress}/api/v1/{value}/watch",
            DeepLink = $"tv.nomercy.app://{value}/watch",
        };

        string jsonElement = JsonSerializer.Serialize(customData);
        Media media = new() { CustomData = jsonElement };

        await client.MediaChannel.LoadAsync(media).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the status for the named receiver, or the last selected one if
    /// <paramref name="name"/> is null.
    /// </summary>
    public ChromecastStatus? GetChromecastStatus(string? name = null)
    {
        string? target = name ?? _lastSelectedName;
        if (target == null)
            return null;

        return ClientPool.TryGetValue(target, out ChromecastClient? client)
            ? client.ChromecastStatus
            : null;
    }

    /// <summary>
    /// Returns the media status for the named receiver, or the last selected
    /// one if <paramref name="name"/> is null.
    /// </summary>
    public MediaStatus? GetMediaStatus(string? name = null)
    {
        string? target = name ?? _lastSelectedName;
        if (target == null)
            return null;

        return ClientPool.TryGetValue(target, out ChromecastClient? client)
            ? client.MediaChannel.MediaStatus
            : null;
    }

    /// <summary>
    /// Stops media on the named receiver, or the last selected one if
    /// <paramref name="name"/> is null.
    /// </summary>
    public async Task Stop(string? name = null)
    {
        string? target = name ?? _lastSelectedName;
        if (target == null)
            return;

        if (!ClientPool.TryGetValue(target, out ChromecastClient? client))
            return;

        await client.MediaChannel.StopAsync();
    }

    /// <summary>
    /// Disconnects and removes the client for the named receiver from the
    /// pool. If <paramref name="name"/> is null the last selected receiver is
    /// used. Pass "*" to disconnect all receivers.
    /// </summary>
    public async Task Disconnect(string? name = null)
    {
        if (name == "*")
        {
            await DisconnectAllAsync();
            return;
        }

        string? target = name ?? _lastSelectedName;
        if (target == null)
            return;

        if (!ClientPool.TryRemove(target, out ChromecastClient? client))
            return;

        try
        {
            await client.DisconnectAsync();
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>
    /// Disconnects and disposes every client in the pool. Called on
    /// application shutdown.
    /// </summary>
    public async Task DisconnectAllAsync()
    {
        string[] keys = [.. ClientPool.Keys];
        foreach (string key in keys)
        {
            if (!ClientPool.TryRemove(key, out ChromecastClient? client))
                continue;

            try
            {
                await client.DisconnectAsync();
            }
            catch
            {
                // best-effort
            }
        }
    }

    public class CastCustomData
    {
        [JsonPropertyName("accessToken")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("basePath")]
        public string? BasePath { get; set; }

        [JsonPropertyName("playlist")]
        public string? Playlist { get; set; }

        [JsonPropertyName("deepLink")]
        public string? DeepLink { get; set; }
    }
}
