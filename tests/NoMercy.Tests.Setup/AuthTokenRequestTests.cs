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

using NoMercy.Setup.Auth;

namespace NoMercy.Tests.Setup;

public class AuthTokenRequestTests
{
    private static Dictionary<string, string> ToDictionary(List<KeyValuePair<string, string>> body)
    {
        return body.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    // --- Authorization Code Body ---

    [Fact]
    public void AuthCodeBody_DoesNotContainClientSecret()
    {
        List<KeyValuePair<string, string>> body = AuthManager.BuildAuthorizationCodeBody(
            "my-client",
            "auth-code-123",
            "http://localhost:7626/sso-callback",
            "verifier123"
        );

        Dictionary<string, string> dict = ToDictionary(body);
        Assert.DoesNotContain("client_secret", dict.Keys);
    }

    [Fact]
    public void AuthCodeBody_ContainsCodeVerifier()
    {
        List<KeyValuePair<string, string>> body = AuthManager.BuildAuthorizationCodeBody(
            "my-client",
            "auth-code-123",
            "http://localhost:7626/sso-callback",
            "verifier123"
        );

        Dictionary<string, string> dict = ToDictionary(body);
        Assert.Equal("verifier123", dict["code_verifier"]);
    }

    [Fact]
    public void AuthCodeBody_ContainsAllRequiredFields()
    {
        List<KeyValuePair<string, string>> body = AuthManager.BuildAuthorizationCodeBody(
            "my-client",
            "auth-code-123",
            "http://localhost:7626/sso-callback",
            "verifier123"
        );

        Dictionary<string, string> dict = ToDictionary(body);

        Assert.Equal("authorization_code", dict["grant_type"]);
        Assert.Equal("my-client", dict["client_id"]);
        Assert.Contains("openid", dict["scope"]);
        Assert.Equal("http://localhost:7626/sso-callback", dict["redirect_uri"]);
        Assert.Equal("auth-code-123", dict["code"]);
        Assert.Equal("verifier123", dict["code_verifier"]);
    }

    // --- Refresh Token Body ---

    [Fact]
    public void RefreshBody_DoesNotContainClientSecret()
    {
        List<KeyValuePair<string, string>> body = AuthManager.BuildRefreshTokenBody(
            "my-client",
            "refresh-token-abc"
        );

        Dictionary<string, string> dict = ToDictionary(body);
        Assert.DoesNotContain("client_secret", dict.Keys);
    }

    [Fact]
    public void RefreshBody_ContainsRefreshToken()
    {
        List<KeyValuePair<string, string>> body = AuthManager.BuildRefreshTokenBody(
            "my-client",
            "refresh-token-abc"
        );

        Dictionary<string, string> dict = ToDictionary(body);
        Assert.Equal("refresh-token-abc", dict["refresh_token"]);
    }

    [Fact]
    public void RefreshBody_ContainsCorrectGrantType()
    {
        List<KeyValuePair<string, string>> body = AuthManager.BuildRefreshTokenBody(
            "my-client",
            "refresh-token-abc"
        );

        Dictionary<string, string> dict = ToDictionary(body);
        Assert.Equal("refresh_token", dict["grant_type"]);
    }

    // --- Permanent Refresh Failure Detection ---

    [Fact]
    public void PermanentRefreshFailure_TrueForInvalidGrant()
    {
        string body =
            "{\"error\":\"invalid_grant\",\"error_description\":\"Offline user session not found\"}";

        Assert.True(AuthManager.IsPermanentRefreshFailure(body));
    }

    [Fact]
    public void PermanentRefreshFailure_IsCaseInsensitive()
    {
        Assert.True(AuthManager.IsPermanentRefreshFailure("ERROR: INVALID_GRANT"));
    }

    [Fact]
    public void PermanentRefreshFailure_FalseForTransientErrors()
    {
        Assert.False(
            AuthManager.IsPermanentRefreshFailure("{\"error\":\"temporarily_unavailable\"}")
        );
        Assert.False(AuthManager.IsPermanentRefreshFailure("502 Bad Gateway"));
        Assert.False(AuthManager.IsPermanentRefreshFailure(string.Empty));
    }

    // --- Device Code Request Body ---

    [Fact]
    public void DeviceCodeBody_DoesNotContainClientSecret()
    {
        List<KeyValuePair<string, string>> body = AuthManager.BuildDeviceCodeRequestBody(
            "my-client",
            "challenge-abc"
        );

        Dictionary<string, string> dict = ToDictionary(body);
        Assert.DoesNotContain("client_secret", dict.Keys);
    }

    [Fact]
    public void DeviceCodeBody_ContainsClientIdAndScope()
    {
        List<KeyValuePair<string, string>> body = AuthManager.BuildDeviceCodeRequestBody(
            "my-client",
            "challenge-abc"
        );

        Dictionary<string, string> dict = ToDictionary(body);
        Assert.Equal("my-client", dict["client_id"]);
        Assert.Contains("openid", dict["scope"]);
    }

    [Fact]
    public void DeviceCodeBody_CarriesS256Challenge()
    {
        string verifier = AuthManager.GenerateCodeVerifier();
        string challenge = AuthManager.GenerateCodeChallenge(verifier);

        List<KeyValuePair<string, string>> body = AuthManager.BuildDeviceCodeRequestBody(
            "my-client",
            challenge
        );

        Dictionary<string, string> dict = ToDictionary(body);
        Assert.Equal(challenge, dict["code_challenge"]);
        Assert.Equal("S256", dict["code_challenge_method"]);
    }

    [Fact]
    public void DeviceTokenBody_CarriesCodeVerifier()
    {
        string verifier = AuthManager.GenerateCodeVerifier();

        List<KeyValuePair<string, string>> body = AuthManager.BuildDeviceTokenBody(
            "my-client",
            "device-code-xyz",
            verifier
        );

        Dictionary<string, string> dict = ToDictionary(body);
        Assert.Equal(verifier, dict["code_verifier"]);
    }

    // --- Device Token Body ---

    [Fact]
    public void DeviceTokenBody_DoesNotContainClientSecret()
    {
        List<KeyValuePair<string, string>> body = AuthManager.BuildDeviceTokenBody(
            "my-client",
            "device-code-xyz",
            "verifier-abc"
        );

        Dictionary<string, string> dict = ToDictionary(body);
        Assert.DoesNotContain("client_secret", dict.Keys);
    }

    [Fact]
    public void DeviceTokenBody_ContainsDeviceCode()
    {
        List<KeyValuePair<string, string>> body = AuthManager.BuildDeviceTokenBody(
            "my-client",
            "device-code-xyz",
            "verifier-abc"
        );

        Dictionary<string, string> dict = ToDictionary(body);
        Assert.Equal("device-code-xyz", dict["device_code"]);
    }

    [Fact]
    public void DeviceTokenBody_ContainsCorrectGrantType()
    {
        List<KeyValuePair<string, string>> body = AuthManager.BuildDeviceTokenBody(
            "my-client",
            "device-code-xyz",
            "verifier-abc"
        );

        Dictionary<string, string> dict = ToDictionary(body);
        Assert.Equal("urn:ietf:params:oauth:grant-type:device_code", dict["grant_type"]);
    }

    // --- PKCE Code Verifier ---

    [Fact]
    public void CodeVerifier_IsBase64UrlSafe()
    {
        string verifier = AuthManager.GenerateCodeVerifier();

        Assert.DoesNotContain("+", verifier);
        Assert.DoesNotContain("/", verifier);
        Assert.DoesNotContain("=", verifier);
    }

    [Fact]
    public void CodeVerifier_HasMinLength43()
    {
        string verifier = AuthManager.GenerateCodeVerifier();

        Assert.True(
            verifier.Length >= 43,
            $"Code verifier length {verifier.Length} is less than RFC 7636 minimum of 43"
        );
    }

    [Fact]
    public void CodeVerifier_IsUnique()
    {
        string verifier1 = AuthManager.GenerateCodeVerifier();
        string verifier2 = AuthManager.GenerateCodeVerifier();

        Assert.NotEqual(verifier1, verifier2);
    }

    // --- PKCE Code Challenge ---

    [Fact]
    public void CodeChallenge_IsBase64UrlSafe()
    {
        string verifier = AuthManager.GenerateCodeVerifier();
        string challenge = AuthManager.GenerateCodeChallenge(verifier);

        Assert.DoesNotContain("+", challenge);
        Assert.DoesNotContain("/", challenge);
        Assert.DoesNotContain("=", challenge);
    }

    [Fact]
    public void CodeChallenge_MatchesKnownS256Hash()
    {
        // Known test vector: SHA256("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk") base64url-encoded
        // This is from RFC 7636 Appendix B
        string knownVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        string expectedChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        string challenge = AuthManager.GenerateCodeChallenge(knownVerifier);

        Assert.Equal(expectedChallenge, challenge);
    }

    [Fact]
    public void CodeChallenge_DiffersForDifferentVerifiers()
    {
        string challenge1 = AuthManager.GenerateCodeChallenge("verifier-one-abc");
        string challenge2 = AuthManager.GenerateCodeChallenge("verifier-two-xyz");

        Assert.NotEqual(challenge1, challenge2);
    }
}
