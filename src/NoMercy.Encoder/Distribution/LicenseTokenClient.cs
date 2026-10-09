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
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace NoMercy.Encoder.Distribution;

/// <summary>
/// Short-lived HMAC secret issued by api.nomercy.tv for a paid licensed
/// install. Workers rotate this token before expiry so signing keys stay
/// fresh without operator intervention.
/// </summary>
public sealed record ClusterToken(string Secret, DateTime ExpiresAt, IReadOnlyList<string> Scopes);

/// <summary>
/// Discriminated failure reason returned when a token request does not
/// succeed, so callers can apply the right recovery strategy without
/// parsing error strings.
/// </summary>
public enum LicenseFailureKind
{
    /// <summary>Network error or unexpected HTTP status.</summary>
    NetworkError,

    /// <summary>
    /// The coordinator rejected our credentials (401) — HMAC key may be
    /// wrong or the device cert is not yet trusted.
    /// </summary>
    Unauthenticated,

    /// <summary>
    /// The license has been revoked or the plan does not include distributed
    /// encoding (403). The worker must stop re-registering.
    /// </summary>
    EntitlementRevoked,
}

/// <summary>
/// Result of a token request.  Token is non-null on success;
/// Failure + Message are set on every error path.
/// </summary>
public sealed record ClusterTokenResult(
    ClusterToken? Token,
    LicenseFailureKind? Failure,
    string? Message
);

/// <summary>
/// Result of an introspect call. Active == true means the token is still
/// valid for its stated scopes.
/// </summary>
public sealed record IntrospectResult(bool Active, IReadOnlyList<string> Scopes, string? Message);

public interface ILicenseTokenClient
{
    /// <summary>
    /// Requests a short-lived cluster token from api.nomercy.tv.
    /// Returns a failed result — never throws — so callers can act on the
    /// failure kind without try/catch overhead.
    /// </summary>
    Task<ClusterTokenResult> RequestAsync(CancellationToken ct);

    /// <summary>
    /// Checks whether a token issued to a remote worker is still active.
    /// Result is cached for 30 s to avoid coordinator fan-out on busy
    /// middleware paths.
    /// </summary>
    Task<IntrospectResult> IntrospectAsync(string token, CancellationToken ct);
}

/// <summary>
/// HTTP implementation of <see cref="ILicenseTokenClient"/>.
///
/// Design notes
/// ────────────
/// • Does NOT depend on AppFiles or Globals — those live in NmSystem which
///   the encoder test project deliberately excludes.  Callers (composition
///   root / DI factory) inject the cert/token strings at construction time.
/// • The HttpClient is injected so tests can swap in a fake handler without
///   hitting the network.
/// • Introspect responses are cached in a simple in-memory dictionary keyed
///   on the raw token string with a 30-second TTL.
/// </summary>
public sealed class LicenseTokenClient : ILicenseTokenClient
{
    private const string TokenEndpoint = "cluster/token";
    private const string IntrospectEndpoint = "cluster/token/introspect";
    private static readonly TimeSpan IntrospectCacheTtl = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly Func<string?> _accessTokenProvider;
    private readonly string? _certPem;
    private readonly string? _keyPem;

    // Introspect cache: token → (result, cachedAt)
    private readonly Dictionary<
        string,
        (IntrospectResult Result, DateTime CachedAt)
    > _introspectCache = new(StringComparer.Ordinal);

    private readonly object _cacheLock = new();

    /// <param name="http">
    ///   HttpClient whose BaseAddress is already pointed at api.nomercy.tv.
    /// </param>
    /// <param name="accessTokenProvider">
    ///   Returns the current Keycloak access token (may be null when auth is
    ///   not yet ready — the request will fail gracefully).
    /// </param>
    /// <param name="certPem">PEM-encoded device certificate (optional).</param>
    /// <param name="keyPem">PEM-encoded device private key (optional).</param>
    public LicenseTokenClient(
        HttpClient http,
        Func<string?> accessTokenProvider,
        string? certPem = null,
        string? keyPem = null
    )
    {
        _http = http;
        _accessTokenProvider = accessTokenProvider;
        _certPem = certPem;
        _keyPem = keyPem;
    }

    public async Task<ClusterTokenResult> RequestAsync(CancellationToken ct)
    {
        try
        {
            HttpRequestMessage request = BuildRequest(HttpMethod.Post, TokenEndpoint);
            request.Content = JsonContent.Create(new TokenRequestBody(CertPem: _certPem));

            using HttpResponseMessage response = await _http
                .SendAsync(request, ct)
                .ConfigureAwait(false);

            return response.StatusCode switch
            {
                HttpStatusCode.OK => await ParseSuccessAsync(response, ct).ConfigureAwait(false),
                HttpStatusCode.Unauthorized => new(
                    null,
                    LicenseFailureKind.Unauthenticated,
                    "401 from coordinator"
                ),
                HttpStatusCode.Forbidden => new(
                    null,
                    LicenseFailureKind.EntitlementRevoked,
                    "403 from coordinator"
                ),
                _ => new(
                    null,
                    LicenseFailureKind.NetworkError,
                    $"Unexpected {(int)response.StatusCode} from coordinator"
                ),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(null, LicenseFailureKind.NetworkError, ex.Message);
        }
    }

    public async Task<IntrospectResult> IntrospectAsync(string token, CancellationToken ct)
    {
        // Check cache first.
        lock (_cacheLock)
        {
            if (
                _introspectCache.TryGetValue(
                    token,
                    out (IntrospectResult Result, DateTime CachedAt) entry
                )
                && DateTime.UtcNow - entry.CachedAt < IntrospectCacheTtl
            )
                return entry.Result;
        }

        IntrospectResult result;

        try
        {
            HttpRequestMessage request = BuildRequest(HttpMethod.Post, IntrospectEndpoint);
            request.Content = JsonContent.Create(new IntrospectRequestBody(Token: token));

            using HttpResponseMessage response = await _http
                .SendAsync(request, ct)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                TokenIntrospectResponse? body =
                    await response.Content.ReadFromJsonAsync<TokenIntrospectResponse>(
                        cancellationToken: ct
                    );
                result = new(
                    Active: body?.Active ?? false,
                    Scopes: body?.Scopes ?? [],
                    Message: null
                );
            }
            else
            {
                result = new(
                    Active: false,
                    Scopes: [],
                    Message: $"Introspect returned {(int)response.StatusCode}"
                );
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = new(Active: false, Scopes: [], Message: ex.Message);
        }

        lock (_cacheLock)
        {
            // Sweep entries past their TTL so rotated/expired tokens don't
            // accumulate as permanent keys over the coordinator's lifetime.
            DateTime now = DateTime.UtcNow;
            foreach (
                string expiredKey in ExpiredIntrospectKeys(
                    _introspectCache,
                    now,
                    IntrospectCacheTtl
                )
            )
                _introspectCache.Remove(expiredKey);

            _introspectCache[token] = (result, now);
        }

        return result;
    }

    /// <summary>
    /// Cache keys whose entry is at or past <paramref name="ttl"/> relative to
    /// <paramref name="now"/> — the introspect cache is swept by these on write so
    /// it stays bounded to tokens seen within the TTL window.
    /// </summary>
    public static IEnumerable<string> ExpiredIntrospectKeys(
        IEnumerable<KeyValuePair<string, (IntrospectResult Result, DateTime CachedAt)>> entries,
        DateTime now,
        TimeSpan ttl
    ) =>
        entries
            .Where(entry => now - entry.Value.CachedAt >= ttl)
            .Select(entry => entry.Key)
            .ToList();

    // ── Helpers ──────────────────────────────────────────────────────────────

    private HttpRequestMessage BuildRequest(HttpMethod method, string endpoint)
    {
        HttpRequestMessage request = new(method, endpoint);
        string? accessToken = _accessTokenProvider();
        if (!string.IsNullOrWhiteSpace(accessToken))
            request.Headers.Authorization = new("Bearer", accessToken);
        return request;
    }

    private static async Task<ClusterTokenResult> ParseSuccessAsync(
        HttpResponseMessage response,
        CancellationToken ct
    )
    {
        TokenResponse? body = await response
            .Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct)
            .ConfigureAwait(false);

        if (body is null || string.IsNullOrWhiteSpace(body.Secret))
            return new(
                null,
                LicenseFailureKind.NetworkError,
                "Coordinator returned empty token body"
            );

        ClusterToken token = new(
            Secret: body.Secret,
            ExpiresAt: body.ExpiresAt,
            Scopes: body.Scopes ?? []
        );
        return new(token, null, null);
    }

    // ── Wire-format DTOs (private — not part of the public API) ─────────────

    // Only the public certificate is sent to the coordinator. The private key
    // stays on the device — it must never transit the network in the request
    // body. If mTLS is needed, configure the HttpClient with an X509Certificate2
    // at the transport layer instead.
    // Wire DTOs carry both System.Text.Json and Newtonsoft attributes so the same
    // payload serialises to the same snake_case shape regardless of which serializer
    // a caller picks up — the encoder ships both (BundleManifest etc. use Newtonsoft;
    // PostAsJsonAsync uses System.Text.Json) and the audit flagged the asymmetry.
    private sealed record TokenRequestBody(
        [property: JsonPropertyName("cert_pem")]
        [property: JsonProperty("cert_pem")]
            string? CertPem
    );

    private sealed record IntrospectRequestBody(
        [property: JsonPropertyName("token")] [property: JsonProperty("token")] string Token
    );

    private sealed record TokenResponse(
        [property: JsonPropertyName("secret")] [property: JsonProperty("secret")] string? Secret,
        [property: JsonPropertyName("expires_at")]
        [property: JsonProperty("expires_at")]
            DateTime ExpiresAt,
        [property: JsonPropertyName("scopes")]
        [property: JsonProperty("scopes")]
            List<string>? Scopes
    );

    private sealed record TokenIntrospectResponse(
        [property: JsonPropertyName("active")] [property: JsonProperty("active")] bool Active,
        [property: JsonPropertyName("scopes")]
        [property: JsonProperty("scopes")]
            List<string>? Scopes
    );
}
