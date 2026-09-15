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

using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using NoMercy.Database;
using NoMercy.Database.Models.Common;
using NoMercy.Database.Models.Users;
using NoMercy.Networking.Certificate;
using NoMercy.Networking.Discovery;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.NewtonSoftConverters;
using NoMercy.NmSystem.Status;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Setup.Dto;
using Serilog.Events;

namespace NoMercy.Setup.Auth;

public class ServerRegistrationService : IServerRegistrationService
{
    private readonly IDbContextFactory<AppDbContext> _appDbContextFactory;
    private readonly IUserProvisioningService _userProvisioningService;
    private readonly IConnectivityStatus _connectivityStatus;
    private readonly INetworkDiscovery? _networkDiscovery;

    private static readonly int[] BackoffSeconds = [2, 5, 15, 30, 60];
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private Task? _inFlightInit;
    private DateTime _lastFailureUtc = DateTime.MinValue;
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(60);

    private readonly IAuthTokenStore _authTokenStore;
    private readonly ICertificateService _certificateService;

    public ServerRegistrationService(
        IAuthTokenStore authTokenStore,
        IDbContextFactory<AppDbContext> appDbContextFactory,
        IUserProvisioningService userProvisioningService,
        IConnectivityStatus connectivityStatus,
        ICertificateService certificateService,
        INetworkDiscovery? networkDiscovery = null
    )
    {
        _authTokenStore = authTokenStore;
        _certificateService = certificateService;
        _appDbContextFactory = appDbContextFactory;
        _userProvisioningService = userProvisioningService;
        _connectivityStatus = connectivityStatus;
        _networkDiscovery = networkDiscovery;
    }

    /// <summary>
    /// Registers + assigns the server and renews its certificate. Concurrent
    /// callers share the SAME in-flight attempt instead of a "loser" returning
    /// early on a non-blocking lock: the loser used to advance to Registered
    /// (and check for a certificate) before the winner's attempt — including
    /// the certificate renewal — had actually finished, producing a spurious
    /// "certificate not acquired" downstream. A caller that joins an
    /// already-running attempt does not apply its own <paramref name="maxRetries"/>
    /// — only the value the FIRST caller passed seeds the shared attempt.
    /// </summary>
    public Task Init(int maxRetries = 5)
    {
        TimeSpan sinceLastFailure = DateTime.UtcNow - _lastFailureUtc;
        if (sinceLastFailure < FailureCooldown)
        {
            Logger.Register(
                $"Registration failed recently, cooling down for {(FailureCooldown - sinceLastFailure).TotalSeconds:F0}s"
            );
            throw new InvalidOperationException("Registration on cooldown after recent failure");
        }

        _initLock.Wait();
        try
        {
            if (_inFlightInit is null || _inFlightInit.IsCompleted)
            {
                _inFlightInit = RunRegistrationAsync(maxRetries);
            }

            return _inFlightInit;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task RunRegistrationAsync(int maxRetries)
    {
        try
        {
            await RegisterServer(maxRetries);
            await AssignServerWithRetry(maxRetries);
            await _certificateService.RenewSslCertificate(_authTokenStore.AccessToken);
        }
        catch
        {
            _lastFailureUtc = DateTime.UtcNow;
            throw;
        }
    }

    private async Task RegisterServer(int maxRetries)
    {
        Logger.Register("Registering Server, this takes a moment...");

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                Dictionary<string, string> serverData = await GetServerInfo();
                GenericHttpClient authClient = new(ExternalServicesConfig.Current.ApiServerBaseUrl);
                authClient.SetDefaultHeaders(
                    ExternalServicesConfig.Current.UserAgent,
                    _authTokenStore.AccessToken
                );
                string response = await authClient.SendAndReadAsync(
                    HttpMethod.Post,
                    "register",
                    new FormUrlEncodedContent(serverData)
                );

                object? data = JsonConvert.DeserializeObject(response);
                if (data == null)
                    throw new InvalidOperationException(
                        "Failed to register Server — empty response"
                    );

                Logger.Register("Server registered successfully");
                return;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                // 401 means the auth token is rejected — waiting cannot heal it, and
                // silently continuing used to let boot march on to certificate fetch
                // with the same dead token (a 30-attempt 401 retry storm downstream).
                // Fail the registration loudly so the caller re-enters auth instead.
                if (ex.Message.Contains("401") || ex.Message.Contains("Unauthorized"))
                    throw new InvalidOperationException(
                        "Server registration rejected with 401 Unauthorized — the auth token is invalid or expired. Re-authenticate and retry.",
                        ex
                    );

                int delay = BackoffSeconds[Math.Min(attempt - 1, BackoffSeconds.Length - 1)];
                Logger.Register(
                    $"Registration failed: {ex.Unwrap()}, retrying in {delay}s (attempt {attempt}/{maxRetries})",
                    LogEventLevel.Warning
                );

                await Task.Delay(TimeSpan.FromSeconds(delay));
            }
        }
    }

    private async Task AssignServerWithRetry(int maxRetries)
    {
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await AssignServer();
                return;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                // Same rule as RegisterServer: a 401 is terminal for this token, and
                // swallowing it left the server half-registered (no owner provisioned)
                // while boot continued as if assignment had succeeded.
                if (ex.Message.Contains("401") || ex.Message.Contains("Unauthorized"))
                    throw new InvalidOperationException(
                        "Server assignment rejected with 401 Unauthorized — the auth token is invalid or expired. Re-authenticate and retry.",
                        ex
                    );

                int delay = BackoffSeconds[Math.Min(attempt - 1, BackoffSeconds.Length - 1)];
                Logger.Register(
                    $"Server assignment failed: {ex.Unwrap()}, retrying in {delay}s (attempt {attempt}/{maxRetries})",
                    LogEventLevel.Warning
                );

                await Task.Delay(TimeSpan.FromSeconds(delay));
            }
        }
    }

    private async Task AssignServer()
    {
        Dictionary<string, string> serverData = await GetServerInfo();

        Logger.Register("Assigning Server, this takes a moment...");

        GenericHttpClient authClient = new(ExternalServicesConfig.Current.ApiServerBaseUrl);
        authClient.SetDefaultHeaders(
            ExternalServicesConfig.Current.UserAgent,
            _authTokenStore.AccessToken
        );

        string response = await authClient.SendAndReadAsync(
            HttpMethod.Post,
            "assign",
            new FormUrlEncodedContent(serverData)
        );

        ServerRegisterResponse? data = response.FromJson<ServerRegisterResponse>();

        if (data?.Data is null || data.Data.Status == "error")
            throw new("Failed to assign Server");

        User user = new()
        {
            Id = data.Data.User.Id,
            Name = data.Data.User.Name,
            Email = data.Data.User.Email,
            Owner = true,
            Allowed = true,
            Manage = true,
            CreatedAt = DateTime.UtcNow,
            AudioTranscoding = true,
            NoTranscoding = true,
            VideoTranscoding = true,
        };

        await _userProvisioningService.ProvisionOwner(user);

        Logger.Register("Server assigned successfully");
    }

    public async Task GetTunnelAvailability()
    {
        try
        {
            Dictionary<string, string> serverData = await GetServerInfo();

            GenericHttpClient authClient = new(ExternalServicesConfig.Current.ApiServerBaseUrl);
            authClient.SetDefaultHeaders(
                ExternalServicesConfig.Current.UserAgent,
                _authTokenStore.AccessToken
            );

            string response = await authClient.SendAndReadAsync(
                HttpMethod.Post,
                "tunnel",
                new FormUrlEncodedContent(serverData)
            );

            ServerTunnelAvailabilityResponse? data =
                response.FromJson<ServerTunnelAvailabilityResponse>();

            if (data is null)
            {
                _connectivityStatus.TunnelAvailability = TunnelAvailability.CheckFailed;
                return;
            }

            if (!data.Allowed || data.Token is null)
            {
                // The API answers allowed:false both when no tunnel exists and while one is
                // still being provisioned. Recording which it was is what lets the server say
                // something true instead of guessing.
                _connectivityStatus.TunnelAvailability =
                    data.Token is null && data.Allowed
                        ? TunnelAvailability.Provisioning
                        : TunnelAvailability.NotProvisioned;
                return;
            }

            _connectivityStatus.CloudflareTunnelToken = data.Token;
            _connectivityStatus.TunnelAvailability = TunnelAvailability.Available;

            Logger.Register("Cloudflare tunnel is available", LogEventLevel.Verbose);
        }
        catch (Exception ex)
        {
            // A failed check is not the same as "you do not have a tunnel", and reporting it
            // as one sent people looking at their billing instead of their connectivity.
            _connectivityStatus.TunnelAvailability = TunnelAvailability.CheckFailed;
            Logger.Register(
                $"Tunnel availability check failed: {ex.Unwrap()}",
                LogEventLevel.Warning
            );
        }
    }

    private async Task<Dictionary<string, string>> GetServerInfo()
    {
        return new()
        {
            { "id", Info.DeviceId.ToString() },
            { "name", await GetDeviceName() },
            { "internal_ip", _networkDiscovery?.RegistrationInternalIp ?? "0.0.0.0" },
            { "internal_ipv6", (_networkDiscovery?.InternalIpV6).OrEmpty() },
            { "external_ipv6", (_networkDiscovery?.ExternalIpV6).OrEmpty() },
            { "internal_port", RuntimeServerSettings.Current.InternalServerPort.ToString() },
            { "external_port", RuntimeServerSettings.Current.ExternalServerPort.ToString() },
            { "version", Software.Version!.ToString() },
            { "platform", Info.Platform },
            { "stun_public_ip", _connectivityStatus.StunPublicIp.OrEmpty() },
            { "stun_public_port", (_connectivityStatus.StunPublicPort?.ToString()).OrEmpty() },
            { "stun_nat_type", _connectivityStatus.NatStatus.ToString() },
            { "dns_scheme", RuntimeServerSettings.Current.UseSynthesizedDns ? "srv" : "apex" },
            { "transport", _connectivityStatus.Transport },
            { "public_url", _connectivityStatus.PublicUrl ?? string.Empty },
        };
    }

    private async Task<string> GetDeviceName()
    {
        try
        {
            await using AppDbContext appContext = await _appDbContextFactory.CreateDbContextAsync();
            Configuration? device = await appContext.Configuration.FirstOrDefaultAsync(device =>
                device.Key == "serverName"
            );

            Info.DeviceName = device?.Value ?? Environment.MachineName;
        }
        catch (Exception)
        {
            Info.DeviceName = Environment.MachineName;
        }

        return Info.DeviceName;
    }
}
