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

using Newtonsoft.Json;
using NoMercy.Networking.Certificate;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.Security;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Setup.Auth;
using NoMercy.Setup.Dto;
using NoMercy.Setup.Server;
using NoMercy.Setup.Ui;
using Serilog.Events;

namespace NoMercy.Setup.Boot;

public class BootOrchestrator
{
    private readonly SetupState _setupState;
    private readonly AuthManager _authManager;
    private readonly IApiKeyLoader _apiKeyLoader;
    private readonly IDegradedModeRecovery _degradedModeRecovery;
    private readonly IServerRegistrationService _serverRegistrationService;

    private readonly IAuthTokenStore _authTokenStore;
    private readonly ICertificateService _certificateService;
    private readonly IHttpClientFactory _httpClientFactory;

    public BootOrchestrator(
        SetupState setupState,
        AuthManager authManager,
        IApiKeyLoader apiKeyLoader,
        IDegradedModeRecovery degradedModeRecovery,
        IServerRegistrationService serverRegistrationService,
        IAuthTokenStore authTokenStore,
        ICertificateService certificateService,
        IHttpClientFactory httpClientFactory
    )
    {
        _authTokenStore = authTokenStore;
        _certificateService = certificateService;
        _setupState = setupState;
        _authManager = authManager;
        _apiKeyLoader = apiKeyLoader;
        _degradedModeRecovery = degradedModeRecovery;
        _serverRegistrationService = serverRegistrationService;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Runs Phase 1 (essential, no network) and Phase 2 (auth).
    /// Returns true if setup mode is needed (interactive auth required).
    /// </summary>
    public async Task<bool> RunAsync(IServiceProvider services, CancellationToken ct)
    {
        // Phase 1: Essential tasks (blocking, no network)
        // Uses Start.cs as shim until Task 17 inlines task definitions
        Logger.Setup("Phase 1: Running essential tasks...");
        await Start.InitEssential();

        // Initialize TokenStore before any DB access that touches SecureValue
        TokenStore.Initialize(services);

        // Load SSL certificate into memory cache (from DB or legacy PEM files)
        try
        {
            _certificateService.LoadFromDb();
        }
        catch (Exception ex)
        {
            Logger.Setup($"Certificate pre-load skipped: {ex.Message}", LogEventLevel.Verbose);
        }

        // Phase 2: Authentication
        Logger.Setup("Phase 2: Authentication...");
        await CheckKeycloakReachabilityAsync();
        bool authSucceeded = await _authManager.InitializeAsync();

        // Must run after InitializeAsync: IAuthTokenStore.AccessToken is only set
        // inside AuthManager, so a call here before auth completed sent every
        // first-boot GET /v1/info with no bearer — the request that never fails
        // outright (the API still answers) but silently returns no keys.
        await _apiKeyLoader.LoadKeys(ct);

        if (authSucceeded)
        {
            NmSystem.Lifecycle.ServerPhaseTracker.Current?.MarkComplete(
                NmSystem.Lifecycle.BootStage.Auth
            );

            bool isRegistered = _certificateService.HasValidCertificate();
            _setupState.DetermineInitialPhase(hasValidToken: true, isRegistered: isRegistered);

            if (isRegistered)
            {
                NmSystem.Lifecycle.ServerPhaseTracker.Current?.MarkComplete(
                    NmSystem.Lifecycle.BootStage.Registered
                );

                // Registration also carries the address clients are told to reach this
                // server on, and it was only ever sent on the very first boot. A LAN
                // address is not stable — a DHCP lease moves it, a container gets a new
                // bridge IP — and the record kept the old one forever, so the heartbeat
                // went on reporting the server online while every client resolved an
                // address that answers nothing. Re-announcing is off the boot path
                // because a registered server must not wait on the API to start serving.
                _ = RefreshRegistrationAsync(ct);
            }
            else
            {
                // Phase 3: Registration (blocking on first boot)
                await RunRegistrationAsync(ct);
            }

            // Phase 4: Background tasks (non-blocking)
            _authManager.ScheduleBackgroundRefresh(ct);
            _ = RunBackgroundTasksAsync(ct);

            return false; // No setup mode needed
        }

        // Auth failed — enter setup mode
        Logger.Setup("Interactive authentication required — entering setup mode");
        return true;
    }

    /// <summary>
    /// Called after interactive auth completes (setup flow).
    /// Waits for Authenticated state, runs registration, starts background tasks.
    /// Returns true if HTTPS restart is needed (cert was acquired).
    /// </summary>
    public async Task<bool> RunPostAuthAsync(CancellationToken ct)
    {
        Logger.Setup("Waiting for authentication to complete...");

        while (!_setupState.IsAuthenticated && !ct.IsCancellationRequested)
        {
            await _setupState.WaitForChangeAsync(ct);
        }

        if (ct.IsCancellationRequested)
            return false;

        Logger.Setup("Authentication complete — running registration...");
        NmSystem.Lifecycle.ServerPhaseTracker.Current?.MarkComplete(
            NmSystem.Lifecycle.BootStage.Auth
        );

        // Phase 3: Registration + Certificate
        bool certAcquired = await RunRegistrationAsync(ct);

        // Phase 4: Background tasks
        _authManager.ScheduleBackgroundRefresh(ct);
        _ = RunBackgroundTasksAsync(ct);

        return certAcquired;
    }

    /// <summary>
    /// For headless/Docker environments: starts device code flow server-side.
    /// Does not block — runs in background. Completes when user authenticates
    /// via the device code, or when setup mode is exited by another path.
    /// </summary>
    public async Task StartHeadlessDeviceCodeFlowAsync(CancellationToken ct)
    {
        if (AuthManager.IsDesktopEnvironment())
            return;

        Logger.Setup("Headless environment detected — starting device code flow");

        while (!ct.IsCancellationRequested && !_setupState.IsAuthenticated)
        {
            DeviceCodeOutcome outcome = await RunOneDeviceCodeAttemptAsync(ct);

            if (outcome is DeviceCodeOutcome.Granted || ct.IsCancellationRequested)
                return;

            if (outcome is DeviceCodeOutcome.Unreachable)
            {
                // Nothing was shown to the user, so this is a retry rather than a
                // new code. Backing off keeps a down auth server from spinning.
                await Task.Delay(TimeSpan.FromSeconds(DeviceCodeRetryDelaySeconds), ct);
                continue;
            }

            Logger.Setup("Setup code expired — issuing a new one");
        }
    }

    private const int DeviceCodeRetryDelaySeconds = 10;

    private enum DeviceCodeOutcome
    {
        /// <summary>The user approved the code and tokens are stored.</summary>
        Granted,

        /// <summary>The code was shown but ran out, or the user declined it.</summary>
        Ended,

        /// <summary>No code was ever shown — the request itself failed.</summary>
        Unreachable,
    }

    private async Task<DeviceCodeOutcome> RunOneDeviceCodeAttemptAsync(CancellationToken ct)
    {
        try
        {
            string deviceEndpoint =
                $"{ExternalServicesConfig.Current.AuthBaseUrl}protocol/openid-connect/auth/device";

            HttpClient client = _httpClientFactory.CreateClient(
                ExternalServicesConfig.KeycloakHttpClientName
            );
            List<KeyValuePair<string, string>> body = AuthManager.BuildDeviceCodeRequestBody(
                ExternalServicesConfig.Current.TokenClientId
            );

            using HttpResponseMessage response = await client.PostAsync(
                deviceEndpoint,
                new FormUrlEncodedContent(body)
            );

            if (!response.IsSuccessStatusCode)
            {
                Logger.Setup("Device code request failed", LogEventLevel.Warning);
                return DeviceCodeOutcome.Unreachable;
            }

            string json = await response.Content.ReadAsStringAsync();
            DeviceAuthResponse? deviceResponse = JsonConvert.DeserializeObject<DeviceAuthResponse>(
                json
            );

            if (deviceResponse is null)
            {
                Logger.Setup("Device code response could not be parsed", LogEventLevel.Warning);
                return DeviceCodeOutcome.Unreachable;
            }

            string verificationUri = deviceResponse.VerificationUriComplete;
            string userCode = deviceResponse.UserCode;
            string deviceCode = deviceResponse.DeviceCode;
            int interval = deviceResponse.Interval > 0 ? deviceResponse.Interval : 5;

            if (!string.IsNullOrEmpty(verificationUri))
            {
                SetupTerminalUi ui = new();
                ui.Show(
                    verificationUri,
                    deviceResponse.VerificationUri,
                    userCode,
                    SetupAddress.Current()
                );
            }

            if (string.IsNullOrEmpty(deviceCode))
                return DeviceCodeOutcome.Unreachable;

            return await PollDeviceGrant(deviceCode, interval, ct);
        }
        catch (OperationCanceledException)
        {
            return DeviceCodeOutcome.Ended;
        }
        catch (Exception ex)
        {
            Logger.Setup($"Device code flow error: {ex.Unwrap()}", LogEventLevel.Warning);
            return DeviceCodeOutcome.Unreachable;
        }
    }

    /// <summary>
    /// Probes the Keycloak well-known config endpoint and logs an explicit error when it is
    /// unreachable AND no cached JWKS key is available. Failures are never swallowed silently.
    /// </summary>
    private async Task CheckKeycloakReachabilityAsync()
    {
        string wellKnown =
            $"{ExternalServicesConfig.Current.AuthBaseUrl}.well-known/openid-configuration";

        try
        {
            HttpClient client = _httpClientFactory.CreateClient(
                ExternalServicesConfig.KeycloakHttpClientName
            );

            using HttpResponseMessage response = await client.GetAsync(wellKnown);

            if (response.IsSuccessStatusCode)
            {
                Logger.Setup($"Keycloak reachable at {ExternalServicesConfig.Current.AuthBaseUrl}");
            }
            else
            {
                Logger.Setup(
                    $"Keycloak returned {(int)response.StatusCode} from {wellKnown} — auth may degrade",
                    LogEventLevel.Warning
                );
            }
        }
        catch (Exception ex)
        {
            bool hasCachedKey = OfflineJwksCache.CachedSigningKey is not null;

            if (hasCachedKey)
            {
                Logger.Setup(
                    $"Keycloak unreachable ({ex.DescribeConnectionFailure()}) — offline JWKS cache is present; JWT validation will use cached keys",
                    LogEventLevel.Warning
                );
            }
            else
            {
                Logger.Setup(
                    $"BOOT FAILURE: Keycloak unreachable at {ExternalServicesConfig.Current.AuthBaseUrl} and no cached JWKS key found. "
                        + $"Cause: {ex.DescribeConnectionFailure()}. "
                        + $"The server cannot validate JWTs. Complete setup at /setup or ensure Keycloak is reachable before restarting.",
                    LogEventLevel.Error
                );
            }
        }
    }

    /// <summary>
    /// Re-sends this server's details for an already-registered boot, so a changed
    /// address reaches the API. Failure is not fatal: the server is registered and
    /// serving, and the next boot tries again.
    /// </summary>
    private async Task RefreshRegistrationAsync(CancellationToken ct)
    {
        try
        {
            await _serverRegistrationService.Init();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Logger.Setup(
                $"Could not refresh this server's registered address: {ex.Unwrap()}",
                LogEventLevel.Warning
            );
        }
    }

    public async Task<bool> RunRegistrationAsync(CancellationToken ct)
    {
        try
        {
            // TransitionTo sets the Registering detail from SetupPhaseWords BEFORE
            // the work starts: Init() below performs registration AND certificate
            // acquisition in one call, so a detail set only after it returns
            // describes a step that already finished.
            _setupState.TransitionTo(SetupPhase.Registering);

            await _serverRegistrationService.Init();

            _setupState.TransitionTo(SetupPhase.Registered);

            bool hasCert = _certificateService.HasValidCertificate();

            if (!hasCert)
            {
                // Cert poll exhausted — a distinct, retryable failure, never the
                // misleading "Complete" a degraded boot used to reach.
                _setupState.TransitionTo(SetupPhase.Failed);
                _setupState.SetError(
                    "Registered, but the SSL certificate could not be acquired. You can retry."
                );

                NmSystem.Lifecycle.ServerPhaseTracker.Current?.MarkComplete(
                    NmSystem.Lifecycle.BootStage.Registered
                );

                return false;
            }

            _setupState.TransitionTo(SetupPhase.CertificateAcquired);
            _setupState.SetServerUrl(BuildServerUrl());
            _setupState.TransitionTo(SetupPhase.Complete);
            Logger.Setup("Registration and certificate setup complete");

            NmSystem.Lifecycle.ServerPhaseTracker.Current?.MarkComplete(
                NmSystem.Lifecycle.BootStage.Registered
            );

            return true;
        }
        catch (Exception ex)
        {
            // The cooldown InvalidOperationException is internal backpressure, not
            // a registration failure — showing its raw message told the operator
            // their registration failed when the server was simply about to retry.
            bool isCooldown = ex is InvalidOperationException && ex.Message.Contains("cooldown");
            string displayMessage = isCooldown
                ? "Registration is briefly paused after a recent attempt. Retrying shortly — you can also retry now."
                : $"Registration failed: {ex.DescribeConnectionFailure()}";

            Logger.Setup(displayMessage, LogEventLevel.Error);

            // Don't block — DegradedModeRecovery will retry. Mark Registered as
            // complete so workers don't block forever on a known-degraded boot —
            // partial functionality beats no functionality, and the recovery loop
            // will quietly retry registration in the background.
            NmSystem.Lifecycle.ServerPhaseTracker.Current?.MarkComplete(
                NmSystem.Lifecycle.BootStage.Registered
            );

            // Transition BEFORE recording the error: TransitionTo clears
            // _errorMessage as stale-progress cleanup, so the old order wiped the
            // failure it had just recorded. Failed (not Complete) so /setup/status
            // reports a real failure with a retry, never a false "Setup complete!".
            _setupState.TransitionTo(SetupPhase.Failed);
            _setupState.SetError(displayMessage);
            return false;
        }
    }

    /// <summary>
    /// Built by NetworkDiscovery rather than assembled here where possible: a
    /// hand-rolled apex-hostname form ignores a tunnelled server's srv scheme and
    /// never resolves. Mirrors SetupEndpoints.RunPostAuthRegistration so every
    /// completion path — interactive setup or this non-interactive boot path —
    /// ends with a server URL the setup page can show as the "open" link.
    /// </summary>
    private static string BuildServerUrl()
    {
        return Start.NetworkDiscovery?.ExternalAddress
            ?? $"https://{Info.DeviceId}.nomercy.tv:{RuntimeServerSettings.Current.ExternalServerPort}";
    }

    private async Task RunBackgroundTasksAsync(CancellationToken ct)
    {
        try
        {
            Logger.Setup("Phase 4: Starting background tasks...");
            await Start.InitRemaining(_degradedModeRecovery, _authTokenStore.AccessToken, ct);
        }
        catch (Exception ex)
        {
            Logger.Setup($"Background tasks error: {ex.Message}", LogEventLevel.Warning);
        }
    }

    /// <summary>
    /// Polls until the user approves the code or the code dies. The caller mints a
    /// replacement when it dies: a first-time user has to register, accept the terms
    /// and verify an email address inside the code's ten-minute life, which routinely
    /// runs over, and before this returned an outcome the whole flow simply stopped —
    /// leaving a server sitting in setup mode with no code and no way to reach one
    /// short of a restart. The browser setup page has always minted a fresh code; the
    /// console path is the one a Docker user has, and it did not.
    /// </summary>
    private async Task<DeviceCodeOutcome> PollDeviceGrant(
        string deviceCode,
        int interval,
        CancellationToken ct
    )
    {
        string tokenEndpoint =
            $"{ExternalServicesConfig.Current.AuthBaseUrl}protocol/openid-connect/token";

        while (!ct.IsCancellationRequested && !_setupState.IsAuthenticated)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);

            try
            {
                HttpClient client = _httpClientFactory.CreateClient(
                    ExternalServicesConfig.KeycloakHttpClientName
                );
                List<KeyValuePair<string, string>> body = AuthManager.BuildDeviceTokenBody(
                    ExternalServicesConfig.Current.TokenClientId,
                    deviceCode
                );

                using HttpResponseMessage response = await client.PostAsync(
                    tokenEndpoint,
                    new FormUrlEncodedContent(body)
                );

                string json = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    AuthResponse? tokens = JsonConvert.DeserializeObject<AuthResponse>(json);
                    if (tokens?.AccessToken != null)
                    {
                        await _authManager.StoreTokensAsync(tokens);
                        _setupState.TransitionTo(SetupPhase.Authenticating);
                        _setupState.TransitionTo(SetupPhase.Authenticated);
                        Logger.Setup("Device code authentication successful");
                        return DeviceCodeOutcome.Granted;
                    }
                }

                dynamic? error = JsonConvert.DeserializeObject(json);
                string? errorCode = error?.error?.ToString();
                if (errorCode is "expired_token" or "access_denied")
                {
                    Logger.Setup($"Device code flow ended: {errorCode}");
                    return DeviceCodeOutcome.Ended;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Setup($"Device poll error: {ex.Unwrap()}", LogEventLevel.Warning);
            }
        }

        return _setupState.IsAuthenticated ? DeviceCodeOutcome.Granted : DeviceCodeOutcome.Ended;
    }
}
