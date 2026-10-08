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
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.NewtonSoftConverters;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Setup.Auth;
using NoMercy.Setup.Boot;
using NoMercy.Setup.Dto;
using NoMercy.Setup.Ui;
using NoMercyQueue.Workers;
using QRCoder;
using Serilog.Events;
using SystemHttpClient = System.Net.Http.HttpClient;

namespace NoMercy.Setup.Server;

/// <summary>
/// Request handlers for the setup flow. Designed to be called by middleware —
/// no standalone WebApplication. PKCE state is managed per-flow and regenerated
/// after each exchange. SetupState and AuthManager come from DI; never replaced
/// with <c>new()</c>.
/// </summary>
public class SetupEndpoints
{
    private const string BrowserLoginNotAllowedMessage =
        "Browser login is not available on this address. Sign in with the device code shown on the setup page.";

    private readonly SetupState _state;
    private readonly AuthManager _authManager;
    private readonly SetupTerminalUi? _terminalUi;

    private readonly object _pkceLock = new();
    private string _codeVerifier;
    private string _codeChallenge;
    private string _pkceState;
    private bool _exchangeCompleted;

    private readonly IServerRegistrationService _serverRegistrationService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CancellationToken _appStopping;

    public SetupEndpoints(
        SetupState state,
        AuthManager authManager,
        IServerRegistrationService serverRegistrationService,
        IHttpClientFactory httpClientFactory,
        IHostApplicationLifetime appLifetime
    )
    {
        _state = state;
        _authManager = authManager;
        _serverRegistrationService = serverRegistrationService;
        _httpClientFactory = httpClientFactory;
        _appStopping = appLifetime.ApplicationStopping;

        _terminalUi = SetupTerminalUi.IsInteractiveTerminal ? new SetupTerminalUi() : null;

        _codeVerifier = AuthManager.GenerateCodeVerifier();
        _codeChallenge = AuthManager.GenerateCodeChallenge(_codeVerifier);
        _pkceState = Guid.NewGuid().ToString("N");
        _exchangeCompleted = false;
    }

    // ── Dispatcher ──────────────────────────────────────────────────────────

    public async Task HandleRequestAsync(HttpContext context)
    {
        string path = (context.Request.Path.Value?.TrimEnd('/') ?? string.Empty).ToLowerInvariant();

        switch (path)
        {
            case "/setup":
                await HandleSetupPage(context);
                break;

            case "/setup/setup.css":
                await HandleEmbeddedResource(context, "setup.css", "text/css");
                break;

            case "/setup/setup.js":
                await HandleEmbeddedResource(context, "setup.js", "application/javascript");
                break;

            case "/favicon.ico":
                await HandleEmbeddedBinary(context, "favicon.ico", "image/x-icon");
                break;

            case "/setup/config":
                await HandleSetupConfig(context);
                break;

            case "/setup/status":
                await HandleSetupStatus(context);
                break;

            case "/setup/silent-sso":
                await HandleSilentSso(context);
                break;

            case "/setup/exchange":
                await HandleExchange(context);
                break;

            case "/setup/device-code":
                await HandleDeviceCode(context);
                break;

            case "/setup/retry":
                await HandleRetry(context);
                break;

            case "/setup/qr":
                await HandleQrCode(context);
                break;

            case "/sso-callback":
                await HandleSsoCallback(context);
                break;

            default:
                await HandleServiceUnavailable(context);
                break;
        }
    }

    // ── Static file handlers ────────────────────────────────────────────────

    private static async Task HandleSetupPage(HttpContext context)
    {
        if (context.Request.Method != HttpMethods.Get)
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        string html = await LoadEmbeddedResource("setup.html");

        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.WriteAsync(html, Encoding.UTF8);
    }

    private static async Task HandleEmbeddedResource(
        HttpContext context,
        string filename,
        string contentType
    )
    {
        if (context.Request.Method != HttpMethods.Get)
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        string content = await LoadEmbeddedResource(filename);

        context.Response.ContentType = $"{contentType}; charset=utf-8";
        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.WriteAsync(content, Encoding.UTF8);
    }

    // ── /setup/config ───────────────────────────────────────────────────────

    private async Task HandleSetupConfig(HttpContext context)
    {
        if (context.Request.Method != HttpMethods.Get)
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        string codeChallenge;
        string pkceState;
        lock (_pkceLock)
        {
            codeChallenge = _codeChallenge;
            pkceState = _pkceState;
        }

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = StatusCodes.Status200OK;

        object response = new
        {
            status = "setup_required",
            phase = _state.CurrentPhase.ToString(),
            error = _state.ErrorMessage,
            server_port = RuntimeServerSettings.Current.InternalServerPort,
            auth_base_url = ExternalServicesConfig.Current.AuthBaseUrl.OrEmpty(),
            client_id = ExternalServicesConfig.Current.TokenClientId.OrEmpty(),
            code_challenge = codeChallenge,
            pkce_state = pkceState,
            is_first_boot = !Start.Certificate!.HasValidCertificate(),
            browser_login_allowed = TrustedSetupHost.IsTrusted(context.Request.Host),
        };

        await WriteJsonResponse(context.Response, response);
    }

    // ── /setup/status ───────────────────────────────────────────────────────

    private async Task HandleSetupStatus(HttpContext context)
    {
        if (context.Request.Method != HttpMethods.Get)
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        string accept = context.Request.Headers.Accept.ToString();

        if (accept.Contains("text/event-stream"))
        {
            await HandleSetupStatusSse(context);
            return;
        }

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = StatusCodes.Status200OK;
        await WriteJsonResponse(context.Response, BuildStatusSnapshot());
    }

    private async Task HandleSetupStatusSse(HttpContext context)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.StatusCode = StatusCodes.Status200OK;

        CancellationToken cancellationToken = context.RequestAborted;

        string json = JsonConvert.SerializeObject(BuildStatusSnapshot());
        await WriteSseEvent(context.Response, json, cancellationToken);

        while (!cancellationToken.IsCancellationRequested && _state.IsSetupRequired)
        {
            try
            {
                await _state.WaitForChangeAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            json = JsonConvert.SerializeObject(BuildStatusSnapshot());
            await WriteSseEvent(context.Response, json, cancellationToken);
        }
    }

    private object BuildStatusSnapshot()
    {
        return new
        {
            phase = _state.CurrentPhase.ToString(),
            is_setup_required = _state.IsSetupRequired,
            is_authenticated = _state.IsAuthenticated,
            error = _state.ErrorMessage,
            detail = _state.PhaseDetail,
            server_url = _state.ServerUrl,
            // The server's own origin serves the API (Swagger at /), not the client,
            // so finishing setup has to hand off to the app instead of to ServerUrl.
            app_url = ExternalServicesConfig.Current.AppBaseUrl,
        };
    }

    private static async Task WriteSseEvent(
        HttpResponse response,
        string data,
        CancellationToken cancellationToken
    )
    {
        await response.WriteAsync($"data: {data}\n\n", Encoding.UTF8, cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    // ── /setup/silent-sso ───────────────────────────────────────────────────

    private static async Task HandleSilentSso(HttpContext context)
    {
        if (context.Request.Method != HttpMethods.Get)
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.WriteAsync(
            "<html><body><script>parent.postMessage(location.href, location.origin)</script></body></html>",
            Encoding.UTF8
        );
    }

    // ── POST /setup/exchange ─────────────────────────────────────────────────

    private async Task HandleExchange(HttpContext context)
    {
        if (context.Request.Method != HttpMethods.Post)
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        context.Response.ContentType = "application/json";

        ExchangeRequest? body;
        try
        {
            string rawBody = await new StreamReader(context.Request.Body).ReadToEndAsync();
            body = rawBody.FromJson<ExchangeRequest>();
        }
        catch
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteJsonResponse(
                context.Response,
                new { status = "error", message = "Invalid request body" }
            );
            return;
        }

        if (body is null || string.IsNullOrEmpty(body.Code))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteJsonResponse(
                context.Response,
                new { status = "error", message = "Missing code" }
            );
            return;
        }

        string currentPkceState;
        string currentCodeVerifier;
        bool alreadyCompleted;

        lock (_pkceLock)
        {
            currentPkceState = _pkceState;
            currentCodeVerifier = _codeVerifier;
            alreadyCompleted = _exchangeCompleted;
        }

        if (!string.IsNullOrEmpty(body.State) && body.State != currentPkceState)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteJsonResponse(
                context.Response,
                new { status = "error", message = "Invalid state parameter" }
            );
            return;
        }

        if (alreadyCompleted)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await WriteJsonResponse(
                context.Response,
                new { status = "error", message = "Exchange already completed" }
            );
            return;
        }

        if (!TrustedSetupHost.IsTrusted(context.Request.Host))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteJsonResponse(
                context.Response,
                new { status = "error", message = BrowserLoginNotAllowedMessage }
            );
            return;
        }

        string redirectUri =
            $"{context.Request.Scheme}://{context.Request.Host.Value}/setup/silent-sso";

        try
        {
            if (string.IsNullOrEmpty(ExternalServicesConfig.Current.TokenClientId))
                throw new InvalidOperationException("Auth configuration not available");

            List<KeyValuePair<string, string>> tokenBody = AuthManager.BuildAuthorizationCodeBody(
                ExternalServicesConfig.Current.TokenClientId,
                body.Code,
                redirectUri,
                currentCodeVerifier
            );

            string tokenEndpoint =
                $"{ExternalServicesConfig.Current.AuthBaseUrl}protocol/openid-connect/token";

            SystemHttpClient httpClient = _httpClientFactory.CreateClient(
                ExternalServicesConfig.KeycloakHttpClientName
            );

            using HttpResponseMessage tokenResponse = await httpClient.PostAsync(
                tokenEndpoint,
                new FormUrlEncodedContent(tokenBody)
            );

            string responseContent = await tokenResponse.Content.ReadAsStringAsync();

            if (!tokenResponse.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Token exchange failed ({(int)tokenResponse.StatusCode}): {responseContent}"
                );

            AuthResponse tokens =
                responseContent.FromJson<AuthResponse>()
                ?? throw new InvalidOperationException("Failed to deserialize token response");

            await _authManager.StoreTokensAsync(tokens);
            _state.TransitionTo(SetupPhase.Authenticating);
            _state.TransitionTo(SetupPhase.Authenticated);

            lock (_pkceLock)
            {
                _exchangeCompleted = true;
            }

            RegeneratePkce();

            context.Response.StatusCode = StatusCodes.Status200OK;
            await WriteJsonResponse(context.Response, new { status = "ok" });
        }
        catch (Exception ex)
        {
            Logger.Setup(
                $"Silent SSO exchange failed: {ex.GetType().Name} — {ex.DescribeConnectionFailure()}",
                LogEventLevel.Warning
            );
            // Return a non-2xx so the client's fetch treats this as a failure — a 200
            // with an error body was silently swallowed and read as success.
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteJsonResponse(
                context.Response,
                new { status = "error", message = ex.DescribeConnectionFailure() }
            );
        }
    }

    // ── /sso-callback ───────────────────────────────────────────────────────

    private async Task HandleSsoCallback(HttpContext context)
    {
        if (context.Request.Method != HttpMethods.Get)
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        string error = context.Request.Query["error"].ToString();
        if (!string.IsNullOrEmpty(error))
        {
            string errorDescription = context.Request.Query["error_description"].ToString();

            string displayMessage = !string.IsNullOrEmpty(errorDescription)
                ? $"Authorization failed: {errorDescription}"
                : $"Authorization failed: {error}";

            Logger.Setup(
                $"SSO callback error: {error} — {errorDescription}",
                LogEventLevel.Warning
            );

            _state.SetError(displayMessage);

            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.WriteAsync(
                BuildCallbackHtml("Authorization Failed", displayMessage, isError: true),
                Encoding.UTF8
            );
            return;
        }

        string code = context.Request.Query["code"].ToString();
        if (string.IsNullOrEmpty(code))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json";
            await WriteJsonResponse(
                context.Response,
                new { status = "error", message = "Missing authorization code" }
            );
            return;
        }

        string stateParam = context.Request.Query["state"].ToString();
        string currentPkceState;
        string currentCodeVerifier;

        lock (_pkceLock)
        {
            currentPkceState = _pkceState;
            currentCodeVerifier = _codeVerifier;
        }

        if (!string.IsNullOrEmpty(stateParam) && stateParam != currentPkceState)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json";
            await WriteJsonResponse(
                context.Response,
                new { status = "error", message = "Invalid state parameter" }
            );
            _state.SetError("Invalid state parameter during PKCE callback.");
            return;
        }

        if (!TrustedSetupHost.IsTrusted(context.Request.Host))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json";
            await WriteJsonResponse(
                context.Response,
                new { status = "error", message = BrowserLoginNotAllowedMessage }
            );
            return;
        }

        _state.TransitionTo(SetupPhase.Authenticating);

        // Must byte-for-byte match the redirect_uri the browser sent to Keycloak.
        // A hardcoded localhost fails with invalid_grant when the user reaches setup
        // from a LAN IP or NAS hostname — derive it from the actual request host, the
        // same way HandleExchange does.
        string redirectUri =
            $"{context.Request.Scheme}://{context.Request.Host.Value}/sso-callback";
        string responseTitle;
        string responseMessage;
        bool responseIsError;

        try
        {
            if (string.IsNullOrEmpty(ExternalServicesConfig.Current.TokenClientId))
                throw new InvalidOperationException("Auth configuration not available");

            List<KeyValuePair<string, string>> tokenBody = AuthManager.BuildAuthorizationCodeBody(
                ExternalServicesConfig.Current.TokenClientId,
                code,
                redirectUri,
                currentCodeVerifier
            );

            string tokenEndpoint =
                $"{ExternalServicesConfig.Current.AuthBaseUrl}protocol/openid-connect/token";

            SystemHttpClient httpClient = _httpClientFactory.CreateClient(
                ExternalServicesConfig.KeycloakHttpClientName
            );

            using HttpResponseMessage tokenResponse = await httpClient.PostAsync(
                tokenEndpoint,
                new FormUrlEncodedContent(tokenBody)
            );

            string responseContent = await tokenResponse.Content.ReadAsStringAsync();

            if (!tokenResponse.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Token exchange failed ({(int)tokenResponse.StatusCode}): {responseContent}"
                );

            AuthResponse tokens =
                responseContent.FromJson<AuthResponse>()
                ?? throw new InvalidOperationException("Failed to deserialize token response");

            await _authManager.StoreTokensAsync(tokens);
            _state.TransitionTo(SetupPhase.Authenticating);
            _state.TransitionTo(SetupPhase.Authenticated);

            _terminalUi?.ShowProgress("Authenticated", "Signed in via browser");
            Logger.Setup("OAuth token exchange completed successfully");

            responseTitle = "Authentication Successful";
            responseMessage = "Tokens saved. Completing server registration...";
            responseIsError = false;
        }
        catch (Exception ex)
        {
            Logger.Setup(
                $"Token exchange failed: {ex.GetType().Name} — {ex.DescribeConnectionFailure()}",
                LogEventLevel.Error
            );
            // Transition first: TransitionTo clears ErrorMessage, so setting the error
            // before it wiped the message the /setup/status poll surfaces to the user.
            _state.TransitionTo(SetupPhase.Unauthenticated);
            _state.SetError($"Sign in failed: {ex.DescribeConnectionFailure()}");

            responseTitle = "Authentication Failed";
            responseMessage = $"Sign in failed: {ex.DescribeConnectionFailure()}";
            responseIsError = true;

            RegeneratePkce();
        }

        if (!responseIsError)
        {
            RegeneratePkce();

            context.Response.Headers.Location = "/setup";
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.WriteAsync(
                BuildCallbackHtml(responseTitle, responseMessage, responseIsError),
                Encoding.UTF8
            );
            await context.Response.CompleteAsync();

            _ = Task.Run(async () =>
            {
                try
                {
                    await RunPostAuthRegistration();
                }
                catch (Exception ex)
                {
                    Logger.Setup(
                        $"Post-auth registration failed: {ex.GetType().Name} — {ex.DescribeConnectionFailure()}",
                        LogEventLevel.Error
                    );
                    _state.SetError(
                        $"Could not connect your server: {ex.DescribeConnectionFailure()}"
                    );
                }
            });
        }
        else
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.WriteAsync(
                BuildCallbackHtml(responseTitle, responseMessage, responseIsError),
                Encoding.UTF8
            );
        }
    }

    // ── POST /setup/device-code ─────────────────────────────────────────────

    private async Task HandleDeviceCode(HttpContext context)
    {
        if (context.Request.Method != HttpMethods.Post)
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        context.Response.ContentType = "application/json";

        if (string.IsNullOrEmpty(ExternalServicesConfig.Current.TokenClientId))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await WriteJsonResponse(
                context.Response,
                new { error = true, message = "Auth configuration not available" }
            );
            return;
        }

        try
        {
            List<KeyValuePair<string, string>> deviceCodeBody =
                AuthManager.BuildDeviceCodeRequestBody(
                    ExternalServicesConfig.Current.TokenClientId
                );

            string deviceCodeEndpoint =
                $"{ExternalServicesConfig.Current.AuthBaseUrl}protocol/openid-connect/auth/device";

            SystemHttpClient httpClient = _httpClientFactory.CreateClient(
                ExternalServicesConfig.KeycloakHttpClientName
            );

            using HttpResponseMessage deviceResponse = await httpClient.PostAsync(
                deviceCodeEndpoint,
                new FormUrlEncodedContent(deviceCodeBody)
            );

            string deviceCodeResponse = await deviceResponse.Content.ReadAsStringAsync();

            // A Keycloak 4xx returns an error body that still deserializes into a
            // DeviceAuthResponse with empty fields — check the status first so a failed
            // request isn't handed to the user as an empty-but-valid device code.
            if (!deviceResponse.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Device code request failed ({(int)deviceResponse.StatusCode}): {deviceCodeResponse}"
                );

            DeviceAuthResponse deviceData =
                deviceCodeResponse.FromJson<DeviceAuthResponse>()
                ?? throw new InvalidOperationException("Failed to get device code");

            context.Response.StatusCode = StatusCodes.Status200OK;
            await WriteJsonResponse(
                context.Response,
                new
                {
                    user_code = deviceData.UserCode,
                    verification_uri = deviceData.VerificationUri,
                    verification_uri_complete = deviceData.VerificationUriComplete,
                    expires_in = deviceData.ExpiresIn,
                    interval = deviceData.Interval,
                }
            );

            if (SetupTerminalUi.IsInteractiveTerminal)
            {
                SetupTerminalUi terminalUi = _terminalUi ?? new SetupTerminalUi();
                terminalUi.Show(
                    deviceData.VerificationUriComplete,
                    deviceData.VerificationUri,
                    deviceData.UserCode,
                    SetupAddress.Current()
                );
            }

            _ = Task.Run(async () =>
            {
                await PollDeviceGrant(deviceData);
            });
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await WriteJsonResponse(
                context.Response,
                new
                {
                    error = true,
                    message = $"Failed to initiate device login: {ex.DescribeConnectionFailure()}",
                }
            );
        }
    }

    // ── POST /setup/retry ───────────────────────────────────────────────────

    private async Task HandleRetry(HttpContext context)
    {
        if (context.Request.Method != HttpMethods.Post)
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        context.Response.ContentType = "application/json";

        SetupPhase phase = _state.CurrentPhase;

        // Post-registration phases must be retryable too — Registered (cert never
        // arrived) and Failed (registration rejected, or cert poll exhausted) used
        // to fall through to "unauthenticated" and bounce an already-signed-in
        // user back to login instead of re-running registration/cert acquisition.
        if (
            phase == SetupPhase.Authenticated
            || phase == SetupPhase.Registering
            || phase == SetupPhase.Registered
            || phase == SetupPhase.Failed
        )
        {
            _state.ClearError();

            context.Response.StatusCode = StatusCodes.Status200OK;
            await WriteJsonResponse(context.Response, new { status = "retrying" });

            _ = Task.Run(async () =>
            {
                try
                {
                    _state.TransitionTo(SetupPhase.Authenticated);
                    await RunPostAuthRegistration();
                }
                catch (Exception ex)
                {
                    Logger.Setup(
                        $"Registration retry failed: {ex.GetType().Name} — {ex.DescribeConnectionFailure()}",
                        LogEventLevel.Error
                    );
                    _state.SetError(
                        $"Could not connect your server: {ex.DescribeConnectionFailure()}"
                    );
                }
            });

            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        await WriteJsonResponse(context.Response, new { status = "unauthenticated" });
    }

    // ── /setup/qr ───────────────────────────────────────────────────────────

    private static async Task HandleQrCode(HttpContext context)
    {
        if (context.Request.Method != HttpMethods.Get)
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        string url = context.Request.Query["data"].ToString();

        if (string.IsNullOrEmpty(url))
            url = context.Request.Query["url"].ToString();

        if (string.IsNullOrEmpty(url))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        byte[] pngBytes = GenerateQrPng(url);

        context.Response.ContentType = "image/png";
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentLength = pngBytes.Length;
        await context.Response.Body.WriteAsync(pngBytes);
    }

    // ── 503 catch-all ───────────────────────────────────────────────────────

    private static async Task HandleServiceUnavailable(HttpContext context)
    {
        context.Response.ContentType = "application/json";
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;

        await WriteJsonResponse(
            context.Response,
            new
            {
                status = "setup_required",
                message = "Server is in setup mode",
                setup_url = "/setup",
            }
        );
    }

    // ── Post-auth registration ───────────────────────────────────────────────

    private async Task RunPostAuthRegistration()
    {
        if (_state.CurrentPhase != SetupPhase.Authenticated)
            return;

        try
        {
            using CancellationTokenSource dbTimeoutCts = new(TimeSpan.FromSeconds(30));
            bool dbReady = await CronWorker.GetDatabaseReadyTask().WaitAsync(dbTimeoutCts.Token);
            if (!dbReady)
                Logger.Setup(
                    "Database schema init reported failure — continuing registration anyway",
                    LogEventLevel.Warning
                );

            _state.TransitionTo(SetupPhase.Registering);
            // Set BEFORE Init() runs, not after: Init() is register + assign +
            // certificate in one call, so a detail set only once it returns
            // describes work that is already done — the user watched "Connecting
            // to NoMercy" for the whole multi-minute poll.
            _state.SetPhaseDetail(
                "Registering server and securing your connection... (this can take a couple of minutes)"
            );
            _terminalUi?.ShowProgress("Registering", "Connecting your server to NoMercy...");

            if (Start.NetworkDiscovery is not null)
                await Start.NetworkDiscovery.DiscoverExternalIpAsync();
            // Init() is register + assign + certificate. Register and assign each retry five
            // times on a 2/5/15/30/60s backoff, and the certificate poll runs up to 30 attempts
            // 10s apart because issuance genuinely takes minutes. Capping the whole thing at
            // two minutes cancelled healthy setups partway through and reported a timeout, so
            // the ceiling now sits above the budget the steps themselves use.
            using CancellationTokenSource registrationTimeoutCts = new(TimeSpan.FromMinutes(10));
            await _serverRegistrationService.Init().WaitAsync(registrationTimeoutCts.Token);

            _state.TransitionTo(SetupPhase.Registered);
            _terminalUi?.ShowProgress("Registered", "Setting up your server address...");

            if (Start.Certificate!.HasValidCertificate())
            {
                // Built by NetworkDiscovery rather than assembled here: this hand-rolled form
                // appended the port forward's port to an apex hostname, which a tunnelled
                // server never serves, and it ignored the srv scheme entirely. Setup's closing
                // screen was handing people a URL that could not load.
                string serverUrl =
                    Start.NetworkDiscovery?.ExternalAddress
                    ?? $"https://{Info.DeviceId}.nomercy.tv:{RuntimeServerSettings.Current.ExternalServerPort}";
                _state.SetServerUrl(serverUrl);
                _state.TransitionTo(SetupPhase.CertificateAcquired);
                _state.TransitionTo(SetupPhase.Complete);
                _terminalUi?.ShowComplete(serverUrl);
                Logger.Setup("Setup complete — server will restart with HTTPS");
            }
            else
            {
                // Distinct, retryable failure — not the Registered+error dead end
                // that HandleRetry could not recover from and the setup page
                // rendered with no error, no retry, no server URL.
                _state.TransitionTo(SetupPhase.Failed);
                _state.SetError("Registration completed but certificate was not acquired");
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Setup(
                "Post-auth registration timed out (registration_timeout)",
                LogEventLevel.Error
            );
            _state.TransitionTo(SetupPhase.Failed);
            _state.SetError("Registration timed out. Please check your connection and try again.");
        }
        catch (Exception ex)
        {
            // The cooldown InvalidOperationException is internal backpressure, not
            // a registration failure — showing its raw message told the operator
            // their registration failed when the server was simply about to retry.
            bool isCooldown = ex is InvalidOperationException && ex.Message.Contains("cooldown");
            string displayMessage = isCooldown
                ? "Registration is briefly paused after a recent attempt. Retrying shortly — you can also retry now."
                : $"Could not connect your server: {ex.DescribeConnectionFailure()}";

            Logger.Setup(
                $"Post-auth registration failed: {ex.GetType().Name} — {ex.DescribeConnectionFailure()}",
                LogEventLevel.Error
            );
            _state.TransitionTo(SetupPhase.Failed);
            _state.SetError(displayMessage);
        }
    }

    // ── Device grant polling ────────────────────────────────────────────────

    private async Task PollDeviceGrant(DeviceAuthResponse deviceData)
    {
        if (string.IsNullOrEmpty(ExternalServicesConfig.Current.TokenClientId))
            return;

        // Don't transition to Authenticating here — that hides the login UI.
        // Transition only happens when the user completes auth (success path below).

        // If auth already completed (e.g. via browser login while device poll runs), stop.
        if (_state.IsAuthenticated || _state.CurrentPhase == SetupPhase.Complete)
            return;

        List<KeyValuePair<string, string>> tokenBody = AuthManager.BuildDeviceTokenBody(
            ExternalServicesConfig.Current.TokenClientId,
            deviceData.DeviceCode
        );

        string tokenEndpoint =
            $"{ExternalServicesConfig.Current.AuthBaseUrl}protocol/openid-connect/token";
        DateTime expiresAt = DateTime.UtcNow.AddSeconds(deviceData.ExpiresIn);

        SystemHttpClient httpClient = _httpClientFactory.CreateClient(
            ExternalServicesConfig.KeycloakHttpClientName
        );

        // Clamp the server-supplied interval so a hostile or buggy IDP can't
        // tight-loop us (interval=0) or stall the setup phase (huge value).
        int intervalSec = Math.Clamp(deviceData.Interval, 1, 30);

        // Consecutive transient failures (network blip, DNS hiccup, timeout) —
        // a single one used to end the whole device login outright, which is the
        // only Docker/NAS login path there is. Capped so a persistently dead IdP
        // still gives up eventually rather than spinning past the code's own
        // RFC 8628 expiry silently.
        int consecutiveTransientFailures = 0;
        const int maxConsecutiveTransientFailures = 5;

        while (DateTime.UtcNow < expiresAt && !_appStopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(intervalSec * 1000, _appStopping);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Stop polling if auth completed via another path (browser login, silent SSO)
            if (_state.IsAuthenticated || _state.CurrentPhase == SetupPhase.Complete)
                return;

            try
            {
                using HttpResponseMessage response = await httpClient.PostAsync(
                    tokenEndpoint,
                    new FormUrlEncodedContent(tokenBody),
                    _appStopping
                );

                if (response.IsSuccessStatusCode)
                {
                    string content = await response.Content.ReadAsStringAsync();
                    AuthResponse data =
                        content.FromJson<AuthResponse>()
                        ?? throw new InvalidOperationException(
                            "Failed to deserialize token response"
                        );

                    await _authManager.StoreTokensAsync(data);
                    _state.TransitionTo(SetupPhase.Authenticating);
                    _state.TransitionTo(SetupPhase.Authenticated);

                    _terminalUi?.ShowProgress("Authenticated", "Signed in successfully!");

                    await RunPostAuthRegistration();
                    return;
                }

                string errorContent = await response.Content.ReadAsStringAsync();
                dynamic? error = JsonConvert.DeserializeObject<dynamic>(errorContent);
                string? errorCode = error?.error?.ToString();

                // Reset only once the response has been read AND parsed into a
                // legitimate RFC 8628 outcome -- not merely once PostAsync or
                // ReadAsStringAsync returned without throwing. A connection that
                // fails between headers and body, or that delivers a body
                // DeserializeObject can't parse, used to still zero the counter
                // here (in two successive, equally wrong spots) before the
                // failure surfaced, so it re-incremented to 1 in the same
                // iteration and could never pass 1 -- a persistently dead IdP
                // polled silently forever instead of ever giving up.
                consecutiveTransientFailures = 0;

                // RFC 8628 §3.5: slow_down means keep polling but back off — it is NOT
                // fatal. Treating it as fatal aborted an otherwise-recoverable device login.
                if (errorCode == "slow_down")
                {
                    intervalSec = Math.Clamp(intervalSec + 5, 1, 30);
                    continue;
                }

                // An expired code is the normal fate of a QR nobody scanned in
                // time — the setup page re-requests a fresh code shortly before
                // expiry, so ending this poll quietly is the whole handling.
                // Surfacing it as a setup ERROR told a brand-new user their
                // onboarding failed when nothing was wrong yet.
                if (errorCode == "expired_token")
                    return;

                if (errorCode != "authorization_pending")
                {
                    // Transition first: TransitionTo clears ErrorMessage.
                    _state.TransitionTo(SetupPhase.Unauthenticated);
                    _state.SetError($"Device login failed: {error?.error_description}");
                    return;
                }
            }
            catch (OperationCanceledException) when (_appStopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                consecutiveTransientFailures++;

                // A blip (timeout, DNS hiccup, connection reset) must not end the only
                // Docker/NAS login path there is — keep polling like authorization_pending,
                // same as RFC 8628's own retry story, until it either recovers or the
                // failure streak/code expiry gives up on its own.
                if (consecutiveTransientFailures >= maxConsecutiveTransientFailures)
                {
                    _state.TransitionTo(SetupPhase.Unauthenticated);
                    _state.SetError($"Device login error: {ex.DescribeConnectionFailure()}");
                    return;
                }

                Logger.Setup(
                    $"Device poll transient error ({consecutiveTransientFailures}/{maxConsecutiveTransientFailures}): {ex.DescribeConnectionFailure()} — retrying",
                    LogEventLevel.Warning
                );
            }
        }

        // Natural expiry without a grant: same story as expired_token above —
        // the page mints a fresh code; this is not an error state.
    }

    // ── Private helpers ─────────────────────────────────────────────────────

    private void RegeneratePkce()
    {
        lock (_pkceLock)
        {
            _codeVerifier = AuthManager.GenerateCodeVerifier();
            _codeChallenge = AuthManager.GenerateCodeChallenge(_codeVerifier);
            _pkceState = Guid.NewGuid().ToString("N");
            _exchangeCompleted = false;
        }
    }

    private static async Task<string> LoadEmbeddedResource(string filename)
    {
        Assembly assembly = typeof(SetupEndpoints).Assembly;
        string resourceName = $"NoMercy.Setup.Resources.{filename}";
        await using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            throw new FileNotFoundException($"Embedded resource not found: {resourceName}");
        using StreamReader reader = new(stream);
        return await reader.ReadToEndAsync();
    }

    internal static string BuildCallbackHtml(string title, string message, bool isError = false)
    {
        string color = isError ? "#f08080" : "#CBAFFF";

        if (isError)
        {
            return "<!DOCTYPE html><html><head>"
                + "<meta charset=\"UTF-8\">"
                + "<style>"
                + "body{background:#0a0a0f;color:#e0e0e0;font-family:-apple-system,"
                + "BlinkMacSystemFont,\"Segoe UI\",Roboto,sans-serif;"
                + "display:flex;align-items:center;justify-content:center;"
                + "min-height:100vh;margin:0;}"
                + ".card{background:#16161e;border:1px solid #2a2a3a;"
                + "border-radius:12px;padding:32px 24px;text-align:center;"
                + "max-width:440px;width:100%;}"
                + $"h2{{color:{color};margin-bottom:12px;}}"
                + "p{color:#999;font-size:14px;}"
                + "</style></head><body>"
                + "<div class=\"card\">"
                + $"<h2>{WebUtility.HtmlEncode(title)}</h2>"
                + $"<p>{WebUtility.HtmlEncode(message)}</p>"
                + "<p style=\"margin-top:16px;color:#666;\">Redirecting to setup...</p>"
                + "</div>"
                + "<script>setTimeout(function(){window.location.href='/setup';}, 1500);</script>"
                + "</body></html>";
        }

        // Success case: redirect back to setup page to show progress
        return "<!DOCTYPE html><html><head>"
            + "<meta charset=\"UTF-8\">"
            + "<style>"
            + "body{background:#0a0a0f;color:#e0e0e0;font-family:-apple-system,"
            + "BlinkMacSystemFont,\"Segoe UI\",Roboto,sans-serif;"
            + "display:flex;align-items:center;justify-content:center;"
            + "min-height:100vh;margin:0;}"
            + ".card{background:#16161e;border:1px solid #2a2a3a;"
            + "border-radius:12px;padding:32px 24px;text-align:center;"
            + "max-width:440px;width:100%;}"
            + $"h2{{color:{color};margin-bottom:12px;}}"
            + "p{color:#999;font-size:14px;}"
            + "</style></head><body>"
            + "<div class=\"card\">"
            + $"<h2>{WebUtility.HtmlEncode(title)}</h2>"
            + $"<p>{WebUtility.HtmlEncode(message)}</p>"
            + "<p style=\"margin-top:16px;color:#666;\">Redirecting to setup...</p>"
            + "</div>"
            + "<script>setTimeout(function(){window.location.href='/setup';}, 1500);</script>"
            + "</body></html>";
    }

    internal static string BuildRedirectUri(HttpRequest request)
    {
        int port = request.Host.Port ?? RuntimeServerSettings.Current.InternalServerPort;
        return $"http://localhost:{port}/sso-callback";
    }

    private static async Task HandleEmbeddedBinary(
        HttpContext context,
        string filename,
        string contentType
    )
    {
        Assembly assembly = typeof(SetupEndpoints).Assembly;
        string resourceName = $"NoMercy.Setup.Resources.{filename}";
        await using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = contentType;
        context.Response.StatusCode = StatusCodes.Status200OK;
        await stream.CopyToAsync(context.Response.Body);
    }

    private static byte[] GenerateQrPng(string url)
    {
        using QRCodeGenerator generator = new();
        using QRCodeData qrData = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.L);
        using PngByteQRCode qrCode = new(qrData);
        return qrCode.GetGraphic(8);
    }

    private static async Task WriteJsonResponse(HttpResponse response, object data)
    {
        string json = JsonConvert.SerializeObject(data, Formatting.Indented);
        await response.WriteAsync(json, Encoding.UTF8);
    }

    // ── Inner types ──────────────────────────────────────────────────────────

    private sealed class ExchangeRequest
    {
        [JsonProperty("code")]
        public string? Code { get; set; }

        [JsonProperty("state")]
        public string? State { get; set; }
    }
}
