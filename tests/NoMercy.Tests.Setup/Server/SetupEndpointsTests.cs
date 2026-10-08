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

using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using NoMercy.Database;
using NoMercy.Networking.Certificate;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Security;
using NoMercy.Setup.Auth;
using NoMercy.Setup.Boot;
using NoMercy.Setup.Dto;
using NoMercy.Setup.Server;
using NoMercy.Setup.Ui;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Tests.Setup.Infrastructure;
using NoMercyQueue.Workers;

namespace NoMercy.Tests.Setup.Server;

/// <summary>
/// Requirement: the setup HTTP surface (SSO exchange, device-code flow, retry, QR
/// code, status polling) must dispatch each known path to its handler and fall back to
/// 503 for anything else, enforce the correct HTTP verb per handler, and — for the
/// state-mutating handlers — never leave <see cref="SetupState"/> in a worse spot than
/// it started when a downstream call (token exchange, device-code request) fails.
/// </summary>
/// <remarks>
/// <see cref="SetupEndpoints"/> builds its own <c>System.Net.Http.HttpClient</c> for
/// every Keycloak call — a real loopback <see cref="LoopbackHttpServer"/> exercises the
/// actual contract. <see cref="SetupTerminalUi.ForceInteractiveForTests"/> is pinned to
/// false so these tests exercise the (production-common) non-interactive/service-mode
/// branch rather than constructing a real console resize-watcher thread.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class SetupEndpointsTests : IDisposable
{
    private readonly AppDbContext _appContext;
    private readonly AuthManager _authManager;
    private readonly SetupState _setupState;
    private readonly string? _originalAppPath;
    private readonly string _tempAppPath;
    private readonly string? _originalTokenClientId;

    public SetupEndpointsTests()
    {
        SetupTerminalUi.ForceInteractiveForTests = false;

        _originalAppPath = Environment.GetEnvironmentVariable("NOMERCY_APP_PATH");
        _tempAppPath = Path.Combine(Path.GetTempPath(), $"nm-setupep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempAppPath);
        Environment.SetEnvironmentVariable("NOMERCY_APP_PATH", _tempAppPath);
        NoMercy.NmSystem.Information.AppFiles.CreateAppFolders();

        ServiceCollection services = new();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        ServiceProvider provider = services.BuildServiceProvider();
        TokenStore.Initialize(provider);

        DbContextOptionsBuilder<AppDbContext> optionsBuilder = new();
        optionsBuilder.UseSqlite("Data Source=:memory:");
        _appContext = new(optionsBuilder.Options);
        _appContext.Database.OpenConnection();
        _appContext.Database.EnsureCreated();

        _authManager = new(_appContext, new LocalStorageDriver(), new AuthTokenStore());
        _setupState = new();

        using AppDbContext onDisk = new();
        onDisk.Database.EnsureCreated();
        Start.Certificate = new CertificateService(NullLogger<CertificateService>.Instance, null!);

        _originalTokenClientId = ExternalServicesConfig.Current.TokenClientId;
        ExternalServicesConfig.Current.TokenClientId = "nomercy-server";

        CronWorker.SignalDatabaseReady(true);
    }

    public void Dispose()
    {
        SetupTerminalUi.ForceInteractiveForTests = null;
        _appContext.Database.CloseConnection();
        _appContext.Dispose();
        Start.Certificate = null;
        ExternalServicesConfig.Current.TokenClientId = _originalTokenClientId ?? "nomercy-server";
        Environment.SetEnvironmentVariable("NOMERCY_APP_PATH", _originalAppPath);
        try
        {
            if (Directory.Exists(_tempAppPath))
                Directory.Delete(_tempAppPath, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private SetupEndpoints BuildEndpoints(IServerRegistrationService? registrationService = null) =>
        new(
            _setupState,
            _authManager,
            registrationService ?? new FakeRegistrationService(),
            new RealHttpClientFactory(),
            Mock.Of<IHostApplicationLifetime>()
        );

    private sealed class RealHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static DefaultHttpContext BuildContext(
        string method,
        string path,
        string? body = null,
        string? queryString = null,
        string? accept = null,
        string host = "localhost:7626"
    )
    {
        DefaultHttpContext context = new();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Host = HostString.FromUriComponent(host);
        if (queryString is not null)
            context.Request.QueryString = new(queryString);
        if (accept is not null)
            context.Request.Headers.Accept = accept;
        if (body is not null)
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using StreamReader reader = new(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private sealed class FakeRegistrationService : IServerRegistrationService
    {
        public int InitCallCount;

        public Task Init(int maxRetries = 5)
        {
            InitCallCount++;
            return Task.CompletedTask;
        }

        public Task GetTunnelAvailability() => Task.CompletedTask;
    }

    // ── Dispatcher ───────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleRequestAsync_UnknownPath_Returns503()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("GET", "/nonexistent");

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Contains("setup_required", ReadBody(context));
    }

    [Fact]
    public async Task HandleRequestAsync_TrailingSlash_IsNormalized()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("GET", "/setup/");

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleSetupPage_Get_ReturnsHtml()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("GET", "/setup");

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.StartsWith("text/html", context.Response.ContentType);
        Assert.NotEmpty(ReadBody(context));
    }

    [Fact]
    public async Task HandleSetupPage_Post_Returns405()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("POST", "/setup");

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status405MethodNotAllowed, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleEmbeddedResource_SetupCss_Get_ReturnsCss()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("GET", "/setup/setup.css");

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.StartsWith("text/css", context.Response.ContentType);
    }

    [Fact]
    public async Task HandleEmbeddedResource_SetupJs_Get_ReturnsJs()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("GET", "/setup/setup.js");

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.StartsWith("application/javascript", context.Response.ContentType);
    }

    [Fact]
    public async Task HandleEmbeddedResource_WrongMethod_Returns405()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("POST", "/setup/setup.css");

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status405MethodNotAllowed, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleEmbeddedBinary_Favicon_ReturnsIcon()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("GET", "/favicon.ico");

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("image/x-icon", context.Response.ContentType);
    }

    // ── /setup/config ────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleSetupConfig_Get_ReturnsPhaseAndPkceChallenge()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("GET", "/setup/config");

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        string body = ReadBody(context);
        Assert.Contains("code_challenge", body);
        Assert.Contains("pkce_state", body);
        Assert.Contains("Unauthenticated", body);
    }

    [Theory]
    [InlineData("localhost:7626", true)]
    [InlineData("127.0.0.1:7626", true)]
    [InlineData("abc123.nomercy.tv:7626", true)]
    [InlineData("192.0.2.10:7626", false)]
    [InlineData("nas:7626", false)]
    public async Task HandleSetupConfig_TellsThePageWhetherBrowserLoginIsAllowedOnItsHost(
        string host,
        bool expected
    )
    {
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("GET", "/setup/config", host: host);

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains(
            $"\"browser_login_allowed\": {expected.ToString().ToLowerInvariant()}",
            ReadBody(context)
        );
    }

    [Fact]
    public async Task HandleSetupConfig_Post_Returns405()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("POST", "/setup/config");

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status405MethodNotAllowed, context.Response.StatusCode);
    }

    // ── /setup/status ────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleSetupStatus_Json_ReturnsSnapshot()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("GET", "/setup/status");

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        string body = ReadBody(context);
        Assert.Contains("is_setup_required", body);
        Assert.Contains("phase", body);
    }

    [Fact]
    public async Task HandleSetupStatus_CarriesAppUrl_SoCompletionHandsOffToTheClient()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("GET", "/setup/status");

        await endpoints.HandleRequestAsync(context);

        string body = ReadBody(context);

        // The server's own origin serves the API, so a completion handoff to
        // server_url lands the user on Swagger. setup.js redirects to app_url.
        Assert.Contains("app_url", body);
        Assert.Contains(ExternalServicesConfig.Current.AppBaseUrl, body);
    }

    [Fact]
    public async Task HandleSetupStatus_Json_CarriesTheTableLabelAndDetail_ForTheCurrentPhase()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        _setupState.TransitionTo(SetupPhase.Authenticating);
        DefaultHttpContext context = BuildContext("GET", "/setup/status");

        await endpoints.HandleRequestAsync(context);

        string body = ReadBody(context);
        Dictionary<string, object?> fields = JsonConvert.DeserializeObject<
            Dictionary<string, object?>
        >(body)!;

        // The page, the terminal and the tray read these words; none keeps its own copy.
        Assert.Equal("Authenticating", fields["phase"]);
        Assert.Equal(SetupPhaseWords.Label(SetupPhase.Authenticating), fields["label"]);
        Assert.Equal(SetupPhaseWords.Detail(SetupPhase.Authenticating), fields["detail"]);
    }

    [Fact]
    public async Task HandleSetupStatus_Sse_CarriesTheTableLabel()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext(
            "GET",
            "/setup/status",
            accept: "text/event-stream"
        );
        using CancellationTokenSource cts = new();
        context.RequestAborted = cts.Token;

        Task handling = endpoints.HandleRequestAsync(context);

        await Task.Delay(100);
        cts.Cancel();
        await handling.WaitAsync(TimeSpan.FromSeconds(5));

        string body = ReadBody(context);
        string expected = JsonConvert.ToString(SetupPhaseWords.Label(SetupPhase.Unauthenticated));
        Assert.Contains($"\"label\":{expected}", body);
    }

    [Fact]
    public async Task HandleSetupStatus_Post_Returns405()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("POST", "/setup/status");

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status405MethodNotAllowed, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleSetupStatus_Sse_StreamsAtLeastOneEventThenStopsOnCancellation()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext(
            "GET",
            "/setup/status",
            accept: "text/event-stream"
        );
        using CancellationTokenSource cts = new();
        context.RequestAborted = cts.Token;

        Task handling = endpoints.HandleRequestAsync(context);

        await Task.Delay(100);
        cts.Cancel();
        await handling.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("text/event-stream", context.Response.ContentType);
        string body = ReadBody(context);
        Assert.Contains("data: ", body);
    }

    // ── /setup/silent-sso ────────────────────────────────────────────────────

    [Fact]
    public async Task HandleSilentSso_Get_ReturnsPostMessageHtml()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("GET", "/setup/silent-sso");

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("postMessage", ReadBody(context));
    }

    [Fact]
    public async Task HandleSilentSso_Post_Returns405()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("POST", "/setup/silent-sso");

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status405MethodNotAllowed, context.Response.StatusCode);
    }

    // ── POST /setup/exchange ─────────────────────────────────────────────────

    [Fact]
    public async Task HandleExchange_Get_Returns405()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("GET", "/setup/exchange");

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status405MethodNotAllowed, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleExchange_MalformedJsonBody_FromJsonIsForgiving_FallsThroughToMissingCode()
    {
        // string.FromJson<T>() (NoMercy.NmSystem.NewtonSoftConverters.JsonHelper) is
        // deliberately forgiving — malformed JSON returns default(T), not a thrown
        // exception — so this never reaches the outer try/catch's "Invalid request
        // body" branch; it falls through to the null-body "Missing code" check
        // instead. That branch is covered by HandleExchange_MissingCode_Returns400.
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("POST", "/setup/exchange", body: "{not-json");

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("Missing code", ReadBody(context));
    }

    [Fact]
    public async Task HandleExchange_RequestBodyStreamThrows_ReturnsInvalidRequestBody400()
    {
        // Genuinely exercises the outer try/catch (as opposed to FromJson's own
        // forgiving parse failure above) — an already-disposed body stream makes
        // ReadToEndAsync itself throw ObjectDisposedException.
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("POST", "/setup/exchange");
        MemoryStream disposedBody = new();
        await disposedBody.DisposeAsync();
        context.Request.Body = disposedBody;

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("Invalid request body", ReadBody(context));
    }

    [Fact]
    public async Task HandleExchange_MissingCode_Returns400()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("POST", "/setup/exchange", body: "{}");

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("Missing code", ReadBody(context));
    }

    [Fact]
    public async Task HandleExchange_StateMismatch_Returns400()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        string body = JsonConvert.SerializeObject(new { code = "abc", state = "wrong-state" });
        DefaultHttpContext context = BuildContext("POST", "/setup/exchange", body: body);

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("Invalid state parameter", ReadBody(context));
    }

    [Fact]
    public async Task HandleExchange_UntrustedHost_Returns400WithoutCallingKeycloak()
    {
        using LoopbackHttpServer server = new();
        int keycloakCalls = 0;
        server.Handler = _ =>
        {
            keycloakCalls++;
            return new(200, AuthResponseJson(CreateJwt()));
        };
        using ExternalServicesConfigScope scope = new(authBaseUrl: server.BaseUrl);

        SetupEndpoints endpoints = BuildEndpoints();
        string body = JsonConvert.SerializeObject(new { code = "abc" });
        DefaultHttpContext context = BuildContext(
            "POST",
            "/setup/exchange",
            body: body,
            host: "192.0.2.10:7626"
        );

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("device code", ReadBody(context));
        Assert.Equal(0, keycloakCalls);
        Assert.Equal(SetupPhase.Unauthenticated, _setupState.CurrentPhase);
    }

    [Fact]
    public async Task HandleExchange_NoTokenClientId_Returns400()
    {
        ExternalServicesConfig.Current.TokenClientId = string.Empty;
        SetupEndpoints endpoints = BuildEndpoints();
        string body = JsonConvert.SerializeObject(new { code = "abc" });
        DefaultHttpContext context = BuildContext("POST", "/setup/exchange", body: body);

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleExchange_TokenEndpointFails_Returns400()
    {
        using LoopbackHttpServer server = new();
        server.Handler = _ => new(400, "{\"error\":\"invalid_grant\"}");
        using ExternalServicesConfigScope scope = new(authBaseUrl: server.BaseUrl);

        SetupEndpoints endpoints = BuildEndpoints();
        string body = JsonConvert.SerializeObject(new { code = "abc" });
        DefaultHttpContext context = BuildContext("POST", "/setup/exchange", body: body);

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("error", ReadBody(context));
    }

    [Fact]
    public async Task HandleExchange_Success_StoresTokensAndReturnsOk()
    {
        string jwt = CreateJwt();
        using LoopbackHttpServer server = new();
        server.Handler = _ => new(200, AuthResponseJson(jwt));
        using ExternalServicesConfigScope scope = new(authBaseUrl: server.BaseUrl);

        SetupEndpoints endpoints = BuildEndpoints();
        string body = JsonConvert.SerializeObject(new { code = "abc" });
        DefaultHttpContext context = BuildContext("POST", "/setup/exchange", body: body);

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("\"status\": \"ok\"", ReadBody(context));
        Assert.Equal(SetupPhase.Authenticated, _setupState.CurrentPhase);
    }

    // NOTE: HandleExchange's `if (alreadyCompleted) return 409;` branch is not covered
    // by a deterministic test. `_exchangeCompleted` is set true and then reset back to
    // false by RegeneratePkce() — called immediately afterward with no `await` between
    // the two — inside the SAME method invocation (SetupEndpoints.cs, the block right
    // after `await _authManager.StoreTokensAsync(tokens)`). The window where a second,
    // truly concurrent request could observe `true` is a handful of CPU instructions
    // wide; two sequential awaited calls (the only thing reachable through the public
    // HandleRequestAsync API in a test) always see it already reset. Reaching this
    // branch deterministically would need white-box thread coordination (e.g. pausing
    // the SUT mid-method), which the "no mocks of the unit under test" rule rules out.

    // ── POST /setup/device-code ──────────────────────────────────────────────

    [Fact]
    public async Task HandleDeviceCode_Get_Returns405()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("GET", "/setup/device-code");

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status405MethodNotAllowed, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleDeviceCode_NoTokenClientId_Returns503()
    {
        ExternalServicesConfig.Current.TokenClientId = string.Empty;
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("POST", "/setup/device-code");

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleDeviceCode_Success_ReturnsUserCode()
    {
        using LoopbackHttpServer server = new();
        server.Handler = _ =>
            new(
                200,
                JsonConvert.SerializeObject(
                    new DeviceAuthResponse
                    {
                        UserCode = "ABCD-1234",
                        VerificationUri = "https://auth.nomercy.tv/device",
                        VerificationUriComplete = "https://auth.nomercy.tv/device?code=ABCD-1234",
                        DeviceCode = "device-code-xyz",
                        ExpiresIn = 600,
                        Interval = 30,
                    }
                )
            );
        using ExternalServicesConfigScope scope = new(authBaseUrl: server.BaseUrl);

        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("POST", "/setup/device-code");

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("ABCD-1234", ReadBody(context));
    }

    [Fact]
    public async Task HandleDeviceCode_RequestFails_Returns500()
    {
        using LoopbackHttpServer server = new();
        server.Handler = _ => new(400, "{\"error\":\"invalid_client\"}");
        using ExternalServicesConfigScope scope = new(authBaseUrl: server.BaseUrl);

        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("POST", "/setup/device-code");

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
    }

    // ── POST /setup/retry ─────────────────────────────────────────────────────

    [Fact]
    public async Task HandleRetry_Get_Returns405()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("GET", "/setup/retry");

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status405MethodNotAllowed, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleRetry_Unauthenticated_ReturnsUnauthenticatedStatus()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("POST", "/setup/retry");

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("unauthenticated", ReadBody(context));
    }

    [Fact]
    public async Task HandleRetry_Authenticated_ReturnsRetrying()
    {
        _setupState.TransitionTo(SetupPhase.Authenticating);
        _setupState.TransitionTo(SetupPhase.Authenticated);

        FakeRegistrationService registration = new();
        SetupEndpoints endpoints = BuildEndpoints(registration);
        DefaultHttpContext context = BuildContext("POST", "/setup/retry");

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("retrying", ReadBody(context));
    }

    // ── /setup/qr ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleQrCode_Post_Returns405()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("POST", "/setup/qr");

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status405MethodNotAllowed, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleQrCode_MissingDataAndUrl_Returns400()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("GET", "/setup/qr");

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleQrCode_WithDataParam_ReturnsPng()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext(
            "GET",
            "/setup/qr",
            queryString: "?data=https://example.com"
        );

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("image/png", context.Response.ContentType);
        Assert.True(context.Response.ContentLength > 0);
    }

    [Fact]
    public async Task HandleQrCode_WithUrlParamFallback_ReturnsPng()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext(
            "GET",
            "/setup/qr",
            queryString: "?url=https://example.com"
        );

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    // ── /sso-callback ─────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleSsoCallback_Post_Returns405()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("POST", "/sso-callback");

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status405MethodNotAllowed, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleSsoCallback_ErrorParam_ReturnsErrorHtmlAndSetsState()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext(
            "GET",
            "/sso-callback",
            queryString: "?error=access_denied&error_description=User+cancelled"
        );

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("Authorization Failed", ReadBody(context));
        Assert.NotNull(_setupState.ErrorMessage);
    }

    [Fact]
    public async Task HandleSsoCallback_MissingCode_Returns400Json()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("GET", "/sso-callback");

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("Missing authorization code", ReadBody(context));
    }

    [Fact]
    public async Task HandleSsoCallback_StateMismatch_Returns400AndSetsError()
    {
        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext(
            "GET",
            "/sso-callback",
            queryString: "?code=abc&state=totally-wrong"
        );

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.NotNull(_setupState.ErrorMessage);
    }

    [Fact]
    public async Task HandleSsoCallback_TokenExchangeFails_ReturnsHtmlErrorAndSetsUnauthenticated()
    {
        using LoopbackHttpServer server = new();
        server.Handler = _ => new(400, "{\"error\":\"invalid_grant\"}");
        using ExternalServicesConfigScope scope = new(authBaseUrl: server.BaseUrl);

        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("GET", "/sso-callback", queryString: "?code=abc");

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("Authentication Failed", ReadBody(context));
        Assert.Equal(SetupPhase.Unauthenticated, _setupState.CurrentPhase);
    }

    [Fact]
    public async Task HandleSsoCallback_UntrustedHost_RefusesWithoutCallingKeycloak()
    {
        using LoopbackHttpServer server = new();
        int keycloakCalls = 0;
        server.Handler = _ =>
        {
            keycloakCalls++;
            return new(200, AuthResponseJson(CreateJwt()));
        };
        using ExternalServicesConfigScope scope = new(authBaseUrl: server.BaseUrl);

        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext(
            "GET",
            "/sso-callback",
            queryString: "?code=abc",
            host: "nas:7626"
        );

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("device code", ReadBody(context));
        Assert.Equal(0, keycloakCalls);
        Assert.Equal(SetupPhase.Unauthenticated, _setupState.CurrentPhase);
    }

    [Fact]
    public async Task HandleSsoCallback_Success_ReturnsSuccessHtmlAndTransitionsToAuthenticated()
    {
        string jwt = CreateJwt();
        using LoopbackHttpServer server = new();
        server.Handler = _ => new(200, AuthResponseJson(jwt));
        using ExternalServicesConfigScope scope = new(authBaseUrl: server.BaseUrl);

        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildContext("GET", "/sso-callback", queryString: "?code=abc");

        await endpoints.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("Authentication Successful", ReadBody(context));
        // RunPostAuthRegistration fires fire-and-forget (`_ = Task.Run(...)`) the moment
        // the response is written — it may have already advanced the phase past
        // Authenticated by the time this assertion runs, so assert "at least
        // Authenticated" rather than an exact phase to avoid a race with that
        // background task instead of a real assertion of HandleSsoCallback's own effect.
        Assert.True(
            _setupState.CurrentPhase >= SetupPhase.Authenticated,
            $"expected at least Authenticated, was {_setupState.CurrentPhase}"
        );

        // RunPostAuthRegistration fires in the background (Task.Run) — give it a brief
        // window to run against the FakeRegistrationService before disposal tears down
        // NOMERCY_APP_PATH out from under it.
        await Task.Delay(300);
    }

    // ── Static helper methods (pure) ─────────────────────────────────────────

    [Fact]
    public void BuildCallbackHtml_Error_UsesErrorColorAndTitle()
    {
        string html = SetupEndpoints.BuildCallbackHtml("Oops", "Something failed", isError: true);

        Assert.Contains("Oops", html);
        Assert.Contains("Something failed", html);
        Assert.Contains("#f08080", html);
    }

    [Fact]
    public void BuildCallbackHtml_Success_UsesSuccessColor()
    {
        string html = SetupEndpoints.BuildCallbackHtml("Great", "All good", isError: false);

        Assert.Contains("Great", html);
        Assert.Contains("#CBAFFF", html);
    }

    [Fact]
    public void BuildCallbackHtml_EncodesHtmlInTitleAndMessage()
    {
        string html = SetupEndpoints.BuildCallbackHtml(
            "<script>alert(1)</script>",
            "<b>bold</b>",
            isError: false
        );

        // The template's OWN redirect <script> tag is legitimate and expected — only
        // the caller-supplied title/message must never appear as raw, unencoded HTML.
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("&lt;b&gt;bold&lt;/b&gt;", html);
    }

    [Fact]
    public void BuildRedirectUri_UsesRequestHostPort()
    {
        DefaultHttpContext context = new();
        context.Request.Host = new("example.local", 8443);

        string uri = SetupEndpoints.BuildRedirectUri(context.Request);

        Assert.Equal("http://localhost:8443/sso-callback", uri);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string CreateJwt() =>
        new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(
            new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
                issuer: "https://auth.nomercy.tv/realms/NoMercyTV",
                audience: "nomercy-server",
                claims:
                [
                    new(
                        System.Security.Claims.ClaimTypes.NameIdentifier,
                        Guid.NewGuid().ToString()
                    ),
                ],
                notBefore: DateTime.UtcNow.AddMinutes(-5),
                expires: DateTime.UtcNow.AddHours(1)
            )
        );

    private static string AuthResponseJson(string accessToken) =>
        JsonConvert.SerializeObject(
            new AuthResponse
            {
                AccessToken = accessToken,
                RefreshToken = "refresh-1",
                TokenType = "Bearer",
                ExpiresIn = 3600,
            }
        );
}
