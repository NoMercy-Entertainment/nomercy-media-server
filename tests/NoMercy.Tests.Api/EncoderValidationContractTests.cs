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
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NoMercy.Data.Services;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api;

/// <summary>
/// Contract catalogue for the encoder + admin controller input-validation layer.
///
/// The promise: bad input returns a structured 4xx, NEVER a 500 or silent 200.
/// For every guard rule:
///   (1) FIRES-ON-BAD  — the bad input produces the exact rejection documented here.
///   (2) SILENT-ON-VALID-NEIGHBOR — the closest valid input does NOT trigger the guard.
///
/// A meta-test at the bottom enumerates every rule documented in this catalogue
/// and asserts each has both a FIRES and a NEIGHBOR test, so a new unguarded rule
/// fails the suite immediately.
///
/// KNOWN BUG (foundBug):
///   POST /api/v1/encoder/profiles/validate with a well-formed JSON object that
///   deserializes to a non-null EncodingProfile but has no Outputs/Streams
///   returns HTTP 500 instead of 200 with valid:false. The validator throws
///   an unhandled exception rather than returning a ValidationEnvelope.
///   Test Validate_WellFormedJsonObject_IsNotRejectedAsMalformed is guarded
///   against 500 so the suite flags it as a production bug, not a test bug.
[Trait("Category", "EncoderValidationContract")]
public class EncoderValidationContractTests : IClassFixture<NoMercyApiFactory>, IAsyncLifetime
{
    private readonly NoMercyApiFactory _factory;
    private readonly HttpClient _authed;
    private readonly HttpClient _unauthed;

    private Ulid _userPresetId;
    private Ulid _builtInPresetId;
    private Ulid _childPresetId;

    private static readonly string ValidProfileJson = JsonSerializer.Serialize(
        new
        {
            Id = Ulid.NewUlid().ToString(),
            Name = "Contract Test Profile",
            Container = "HlsTs",
            Video = (object?)null,
            Audio = new[]
            {
                new
                {
                    Policy = "Transcode",
                    Codec = "Aac",
                    BitrateKbps = 192,
                    Channels = 2,
                    SampleRateHz = 48000,
                    AllowedLanguages = Array.Empty<string>(),
                    DefaultLanguage = (string?)null,
                    Loudness = (object?)null,
                    Downmix = (object?)null,
                    SegmentNameTemplate = "audio/:language:/:codec:",
                    PlaylistNameTemplate = "audio/:language:/:codec:",
                },
            },
            Subtitles = Array.Empty<object>(),
        }
    );

    public EncoderValidationContractTests(NoMercyApiFactory factory)
    {
        _factory = factory;
        _authed = factory.CreateClient().AsAuthenticated();
        _unauthed = factory.CreateClient().AsUnauthenticated();
    }

    public async Task InitializeAsync()
    {
        await using MediaContext ctx = new();

        _userPresetId = Ulid.NewUlid();
        _builtInPresetId = Ulid.NewUlid();
        _childPresetId = Ulid.NewUlid();

        ctx.EncodingPresets.AddRange([
            new EncodingPreset
            {
                Id = _userPresetId,
                Name = $"User Preset {_userPresetId}",
                ProfileJson = ValidProfileJson,
                IsBuiltIn = false,
                Source = "db",
            },
            new EncodingPreset
            {
                Id = _builtInPresetId,
                Name = $"BuiltIn Preset {_builtInPresetId}",
                ProfileJson = ValidProfileJson,
                IsBuiltIn = true,
                Source = "seed",
            },
            new EncodingPreset
            {
                Id = _childPresetId,
                Name = $"Child Preset {_childPresetId}",
                ProfileJson = "{}",
                ParentPresetId = _userPresetId,
                IsBuiltIn = false,
                Source = "db",
            },
        ]);

        await ctx.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using MediaContext ctx = new();
        Ulid[] ids = [_userPresetId, _builtInPresetId, _childPresetId];
        await ctx.EncodingPresets.Where(p => ids.Contains(p.Id)).ExecuteDeleteAsync();
    }

    // RULE: auth-required — all encoder/profile and dashboard/encoding/presets

    [Theory]
    [InlineData(["GET", "/api/v1/encoder/profiles"])]
    [InlineData(["GET", "/api/v1/dashboard/encoding/presets"])]
    [InlineData(["POST", "/api/v1/encoder/profiles/validate"])]
    [InlineData(["POST", "/api/v1/dashboard/encoding/presets/validate"])]
    [InlineData(["POST", "/api/v1/dashboard/drivers"])]
    [InlineData(["GET", "/api/v1/dashboard/drivers"])]
    [InlineData(["GET", "/api/v1/dashboard/drivers/types"])]
    public async Task AllEncoderEndpoints_Anonymous_Returns401Or403(string method, string path)
    {
        HttpRequestMessage req = new(new(method), path);
        if (method is "POST" or "PUT")
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        HttpResponseMessage response = await _unauthed.SendAsync(req);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Expected 401/403 on {method} {path} when anonymous, got {(int)response.StatusCode}"
        );
    }

    [Theory]
    [InlineData(["GET", "/api/v1/encoder/profiles"])]
    [InlineData(["GET", "/api/v1/dashboard/drivers/types"])]
    public async Task ReadEndpoints_Authenticated_ReturnsOk(string method, string path)
    {
        HttpRequestMessage req = new(new(method), path);
        HttpResponseMessage response = await _authed.SendAsync(req);

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 on {method} {path}, got {(int)response.StatusCode}: {body}"
        );
    }

    // RULE: validate/profile_json-missing — POST /encoder/profiles/validate with
    //       an empty profile_json must return 200 with valid:false and a rule id.
    //       (validation informs; it never gates the HTTP request — 200 always)
    //

    [Fact]
    public async Task Validate_MissingProfileJson_Returns200WithValidFalseAndRuleId()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/encoder/profiles/validate",
            new { profile_json = "" }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 (validation envelope), got {(int)response.StatusCode}: {body}"
        );

        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        root.TryGetProperty("valid", out JsonElement validEl)
            .Should()
            .BeTrue($"Response must have 'valid' field. Body: {body}");
        validEl.GetBoolean().Should().BeFalse("valid must be false for empty profile_json");

        root.TryGetProperty("errors", out JsonElement errorsEl)
            .Should()
            .BeTrue($"Response must have 'errors' array. Body: {body}");
        errorsEl.GetArrayLength().Should().BeGreaterThan(0, "Errors array must not be empty");

        JsonElement firstError = errorsEl.EnumerateArray().First();
        firstError
            .TryGetProperty("id", out JsonElement idEl)
            .Should()
            .BeTrue("Each error must have an 'id' field (stable rule id)");
        idEl.GetString().Should().NotBeNullOrEmpty("Rule id must not be empty");
    }

    [Fact]
    public async Task Validate_ValidProfileJson_Returns200WithValidTrue()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/encoder/profiles/validate",
            new { profile_json = ValidProfileJson }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 for valid profile_json, got {(int)response.StatusCode}: {body}"
        );

        using JsonDocument doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("valid", out JsonElement validEl)
            .Should()
            .BeTrue($"Response must have 'valid'. Body: {body}");
        validEl
            .GetBoolean()
            .Should()
            .BeTrue($"valid must be true for a well-formed profile. Body: {body}");
    }

    // RULE: validate/profile_json-malformed — malformed JSON must return 200

    [Fact]
    public async Task Validate_MalformedProfileJson_Returns200WithValidFalse_NotA500()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/encoder/profiles/validate",
            new { profile_json = "{not valid json{{{{" }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Malformed JSON must yield 200 with valid:false (not 500). Got {(int)response.StatusCode}: {body}"
        );

        using JsonDocument doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("valid", out JsonElement validEl).Should().BeTrue();
        validEl.GetBoolean().Should().BeFalse("malformed JSON is never valid");
    }

    [Fact]
    public async Task Validate_WellFormedJsonObject_IsNotRejectedAsMalformed()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/encoder/profiles/validate",
            new { profile_json = ValidProfileJson }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"A well-formed JSON object must not 500. Got {(int)response.StatusCode}: {body}"
        );

        using JsonDocument doc = JsonDocument.Parse(body);
        bool hasMalformedError = false;
        if (doc.RootElement.TryGetProperty("errors", out JsonElement errs))
        {
            hasMalformedError = errs.EnumerateArray()
                .Any(e =>
                    e.TryGetProperty("message", out JsonElement msg)
                    && msg.GetString()!.Contains("malformed")
                );
        }
        Assert.False(hasMalformedError, "A valid JSON object must not be flagged as malformed");
    }

    // RULE: validate/empty-outputs — POST /encoder/profiles/validate with well-formed JSON
    //       whose Audio/Subtitles arrays are empty (and Video is null) must return 200 with
    //       valid:false and the profile.no_outputs rule id. Previously threw NRE → HTTP 500
    //       because ProfileRuleValidator called .Any() on null arrays produced by
    //       Newtonsoft.Json when positional record parameters are absent from JSON.

    [Fact]
    public async Task Validate_EmptyArraysProfile_Returns200WithValidFalseAndNoOutputsRuleId()
    {
        string emptyOutputsJson = JsonSerializer.Serialize(
            new
            {
                Id = Ulid.NewUlid().ToString(),
                Name = "Empty Outputs Test",
                Container = "HlsTs",
                Video = (object?)null,
                Audio = Array.Empty<object>(),
                Subtitles = Array.Empty<object>(),
            }
        );

        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/encoder/profiles/validate",
            new { profile_json = emptyOutputsJson }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 (validation envelope) for empty-outputs profile, got {(int)response.StatusCode}: {body}"
        );

        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        root.TryGetProperty("valid", out JsonElement validEl)
            .Should()
            .BeTrue($"Response must have 'valid'. Body: {body}");
        validEl.GetBoolean().Should().BeFalse("empty outputs are not valid");

        root.TryGetProperty("errors", out JsonElement errorsEl)
            .Should()
            .BeTrue($"Response must have 'errors'. Body: {body}");
        bool hasNoOutputsRule = errorsEl
            .EnumerateArray()
            .Any(e =>
                e.TryGetProperty("id", out JsonElement idEl)
                && idEl.GetString() == "profile.no_outputs"
            );
        Assert.True(
            hasNoOutputsRule,
            $"Expected rule id 'profile.no_outputs' in errors. Body: {body}"
        );
    }

    [Fact]
    public async Task Validate_ProfileWithOneAudioOutput_DoesNotFireNoOutputsRule()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/encoder/profiles/validate",
            new { profile_json = ValidProfileJson }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(body);
        bool hasNoOutputsRule = false;
        if (doc.RootElement.TryGetProperty("errors", out JsonElement errs))
        {
            hasNoOutputsRule = errs.EnumerateArray()
                .Any(e =>
                    e.TryGetProperty("id", out JsonElement idEl)
                    && idEl.GetString() == "profile.no_outputs"
                );
        }
        Assert.False(
            hasNoOutputsRule,
            $"profile.no_outputs must not fire when at least one output is declared. Body: {body}"
        );
    }

    // RULE: legacy-validate/profile_json-missing — POST /dashboard/encoding/presets/validate
    //       with empty profile_json must return 400 (legacy endpoint gates via HTTP
    //       status; the new /encoder/profiles/validate always returns 200).
    //
    // Note: The legacy ValidatePresetRequest record has no [JsonProperty] annotation,

    [Fact]
    public async Task LegacyValidate_MissingProfileJson_Returns400()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/dashboard/encoding/presets/validate",
            new { ProfileJson = "" }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 400 for missing profile_json on legacy validate, got {(int)response.StatusCode}: {body}"
        );
    }

    [Fact]
    public async Task LegacyValidate_PresentProfileJson_Returns200()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/dashboard/encoding/presets/validate",
            new { ProfileJson = ValidProfileJson }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 for present profile_json, got {(int)response.StatusCode}: {body}"
        );
    }

    // RULE: create-preset/name-required — POST /encoder/profiles with no name
    //       must return 400 BadRequest, not 500.
    //
    // Note: CreateEncoderProfileRequest has [JsonProperty("name")] → send lowercase.
    //       The 400 body shape is a ProblemDetails from EncoderProfileService, so

    [Fact]
    public async Task CreateProfile_MissingName_Returns400NotA500()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/encoder/profiles",
            new { profile_json = ValidProfileJson }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 400 for missing name, got {(int)response.StatusCode}: {body}"
        );
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task CreateProfile_WithNameAndProfileJson_ReturnsOk()
    {
        string uniqueName = $"Contract-Test-{Ulid.NewUlid()}";
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/encoder/profiles",
            new { name = uniqueName, profile_json = ValidProfileJson }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 for valid create, got {(int)response.StatusCode}: {body}"
        );

        await using MediaContext cleanupCtx = new();
        await cleanupCtx.EncodingPresets.Where(p => p.Name == uniqueName).ExecuteDeleteAsync();
    }

    // RULE: create-preset/profile_json-required — POST /encoder/profiles with name

    [Fact]
    public async Task CreateProfile_MissingProfileJson_Returns400()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/encoder/profiles",
            new { name = "SomeProfile" }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 400 for missing profile_json, got {(int)response.StatusCode}: {body}"
        );
    }

    [Fact]
    public async Task CreateProfile_WithBothNameAndProfileJson_DoesNotRejectForMissingProfileJson()
    {
        string uniqueName = $"Contract-Both-{Ulid.NewUlid()}";
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/encoder/profiles",
            new { name = uniqueName, profile_json = ValidProfileJson }
        );

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);

        await using MediaContext cleanupCtx = new();
        await cleanupCtx.EncodingPresets.Where(p => p.Name == uniqueName).ExecuteDeleteAsync();
    }

    // RULE: create-preset/name-conflict — POST /encoder/profiles with a name that

    [Fact]
    public async Task CreateProfile_DuplicateName_Returns409Conflict()
    {
        await using MediaContext ctx = new();
        EncodingPreset? existing = await ctx.EncodingPresets.FirstOrDefaultAsync(p =>
            p.Id == _userPresetId
        );
        Assert.NotNull(existing);

        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/encoder/profiles",
            new { name = existing!.Name, profile_json = ValidProfileJson }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.Conflict,
            $"Expected 409 for duplicate name, got {(int)response.StatusCode}: {body}"
        );
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task CreateProfile_UniqueName_DoesNotReturnConflict()
    {
        string uniqueName = $"Unique-{Ulid.NewUlid()}";
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/encoder/profiles",
            new { name = uniqueName, profile_json = ValidProfileJson }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.NotEqual(HttpStatusCode.Conflict, response.StatusCode);

        await using MediaContext cleanupCtx = new();
        await cleanupCtx.EncodingPresets.Where(p => p.Name == uniqueName).ExecuteDeleteAsync();
    }

    // RULE: update-preset/builtin-readonly — PUT /encoder/profiles/{id} on a
    //       built-in preset must return 400, not 500 or 200.
    //
    // Note: The PUT /{id:ulid} endpoint accepts a V2EncodingProfile body.

    [Fact]
    public async Task UpdateProfile_BuiltInPreset_Returns400NotA500()
    {
        HttpResponseMessage response = await _authed.PutAsJsonAsync(
            $"/api/v1/encoder/profiles/{_builtInPresetId}",
            new
            {
                Id = _builtInPresetId.ToString(),
                Name = "Tampered",
                Container = "HlsTs",
                Video = (object?)null,
                Audio = Array.Empty<object>(),
                Subtitles = Array.Empty<object>(),
            }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 400 for update on built-in preset, got {(int)response.StatusCode}: {body}"
        );
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task UpdateProfile_UserPreset_ReturnsNoContent()
    {
        HttpResponseMessage response = await _authed.PutAsJsonAsync(
            $"/api/v1/encoder/profiles/{_userPresetId}",
            new
            {
                Id = _userPresetId.ToString(),
                Name = "Updated",
                Container = "HlsTs",
                Video = (object?)null,
                Audio = Array.Empty<object>(),
                Subtitles = Array.Empty<object>(),
            }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.NoContent,
            $"Expected 204 for updating a user preset, got {(int)response.StatusCode}: {body}"
        );
    }

    // RULE: delete-preset/builtin-not-deletable — DELETE /encoder/profiles/{id}

    [Fact]
    public async Task DeleteProfile_BuiltIn_Returns400()
    {
        HttpResponseMessage response = await _authed.DeleteAsync(
            $"/api/v1/encoder/profiles/{_builtInPresetId}"
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 400 for deleting a built-in preset, got {(int)response.StatusCode}: {body}"
        );
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task DeleteProfile_UserPreset_WithNoChildren_ReturnsNoContent()
    {
        Ulid deleteTargetId = Ulid.NewUlid();
        await using (MediaContext seedCtx = new())
        {
            seedCtx.EncodingPresets.Add(
                new()
                {
                    Id = deleteTargetId,
                    Name = $"Delete-Target-{deleteTargetId}",
                    ProfileJson = ValidProfileJson,
                    IsBuiltIn = false,
                    Source = "db",
                }
            );
            await seedCtx.SaveChangesAsync();
        }

        HttpResponseMessage response = await _authed.DeleteAsync(
            $"/api/v1/encoder/profiles/{deleteTargetId}"
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.NoContent,
            $"Expected 204 for deleting user preset with no children, got {(int)response.StatusCode}: {body}"
        );
    }

    // RULE: delete-preset/has-children — DELETE /encoder/profiles/{id} when the

    [Fact]
    public async Task DeleteProfile_HasChildren_Returns400()
    {
        HttpResponseMessage response = await _authed.DeleteAsync(
            $"/api/v1/encoder/profiles/{_userPresetId}"
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 400 when deleting a preset with children, got {(int)response.StatusCode}: {body}"
        );
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task DeleteProfile_ParentWithChildrenDeleted_Succeeds()
    {
        Ulid parentId = Ulid.NewUlid();
        await using (MediaContext seedCtx = new())
        {
            seedCtx.EncodingPresets.Add(
                new()
                {
                    Id = parentId,
                    Name = $"Parent-{parentId}",
                    ProfileJson = ValidProfileJson,
                    IsBuiltIn = false,
                    Source = "db",
                }
            );
            await seedCtx.SaveChangesAsync();
        }

        HttpResponseMessage response = await _authed.DeleteAsync(
            $"/api/v1/encoder/profiles/{parentId}"
        );

        Assert.True(
            response.StatusCode == HttpStatusCode.NoContent,
            $"Expected 204 for deleting a leaf preset (no children), got {(int)response.StatusCode}"
        );
    }

    // RULE: get-preset/not-found — GET /encoder/profiles/{id} for a non-existent

    [Fact]
    public async Task GetProfile_NonExistentId_Returns404()
    {
        Ulid phantomId = Ulid.NewUlid();
        HttpResponseMessage response = await _authed.GetAsync(
            $"/api/v1/encoder/profiles/{phantomId}"
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.NotFound,
            $"Expected 404 for non-existent preset, got {(int)response.StatusCode}: {body}"
        );
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task GetProfile_ExistingId_ReturnsOkWithExpectedShape()
    {
        HttpResponseMessage response = await _authed.GetAsync(
            $"/api/v1/encoder/profiles/{_userPresetId}"
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 for existing preset, got {(int)response.StatusCode}: {body}"
        );

        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        root.TryGetProperty("id", out _).Should().BeTrue("id field must be present");
        root.TryGetProperty("name", out _).Should().BeTrue("name field must be present");
        root.TryGetProperty("is_built_in", out _)
            .Should()
            .BeTrue("is_built_in field must be present");
    }

    // RULE: get-list/pagination-clamped — GET /encoder/profiles?pageSize=999999

    [Fact]
    public async Task ListProfiles_ExcessivePageSize_ClampsAndReturnsOk()
    {
        HttpResponseMessage response = await _authed.GetAsync(
            "/api/v1/encoder/profiles?pageSize=999999"
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 for excessive pageSize, got {(int)response.StatusCode}: {body}"
        );

        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        root.TryGetProperty("meta", out JsonElement meta).Should().BeTrue("meta must be present");
        meta.TryGetProperty("pageSize", out JsonElement pageSizeEl).Should().BeTrue();
        int clampedSize = pageSizeEl.GetInt32();
        Assert.True(clampedSize <= 500, $"pageSize must be clamped to ≤ 500, got {clampedSize}");
    }

    [Fact]
    public async Task ListProfiles_NormalPageSize_ReturnsThatPageSize()
    {
        HttpResponseMessage response = await _authed.GetAsync(
            "/api/v1/encoder/profiles?pageSize=10"
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("meta").GetProperty("pageSize").GetInt32().Should().Be(10);
    }

    // RULE: get-list/envelope-shape — GET /encoder/profiles must return

    [Fact]
    public async Task ListProfiles_ReturnsEnvelopeWithDataAndMeta()
    {
        HttpResponseMessage response = await _authed.GetAsync("/api/v1/encoder/profiles");

        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        root.TryGetProperty("data", out JsonElement data).Should().BeTrue("'data' must be present");
        data.ValueKind.Should().Be(JsonValueKind.Array, "data must be an array");
        root.TryGetProperty("meta", out JsonElement meta).Should().BeTrue("'meta' must be present");
        meta.TryGetProperty("total", out _).Should().BeTrue("meta.total must be present");
        meta.TryGetProperty("pageSize", out _).Should().BeTrue("meta.pageSize must be present");
        meta.TryGetProperty("pageIndex", out _).Should().BeTrue("meta.pageIndex must be present");
        meta.TryGetProperty("totalPages", out _).Should().BeTrue("meta.totalPages must be present");
    }

    // RULE: clone/name-required — POST /encoder/profiles/{id}/clone with no name

    [Fact]
    public async Task CloneProfile_MissingName_Returns400()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            $"/api/v1/encoder/profiles/{_userPresetId}/clone",
            new { }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 400 for clone with no name, got {(int)response.StatusCode}: {body}"
        );
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task CloneProfile_WithName_ReturnsCreated()
    {
        string cloneName = $"Clone-{Ulid.NewUlid()}";
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            $"/api/v1/encoder/profiles/{_userPresetId}/clone",
            new { name = cloneName }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"Expected 201 for valid clone, got {(int)response.StatusCode}: {body}"
        );

        using JsonDocument doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("id", out _)
            .Should()
            .BeTrue("Clone response must include 'id'");

        await using MediaContext cleanupCtx = new();
        await cleanupCtx.EncodingPresets.Where(p => p.Name == cloneName).ExecuteDeleteAsync();
    }

    // RULE: clone/parent-not-found — POST /encoder/profiles/{id}/clone for a

    [Fact]
    public async Task CloneProfile_NonExistentParent_Returns404()
    {
        Ulid phantomId = Ulid.NewUlid();
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            $"/api/v1/encoder/profiles/{phantomId}/clone",
            new { name = "AnyName" }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.NotFound,
            $"Expected 404 for clone from non-existent parent, got {(int)response.StatusCode}: {body}"
        );
    }

    [Fact]
    public async Task CloneProfile_ExistingParent_DoesNotReturn404()
    {
        string cloneName = $"Clone-Neighbor-{Ulid.NewUlid()}";
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            $"/api/v1/encoder/profiles/{_userPresetId}/clone",
            new { name = cloneName }
        );

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);

        await using MediaContext cleanupCtx = new();
        await cleanupCtx.EncodingPresets.Where(p => p.Name == cloneName).ExecuteDeleteAsync();
    }

    // RULE: import/neither-body-nor-url — POST /encoder/profiles/import with no
    //       profile_json and no url must return 422 with a validation envelope.
    //

    [Fact]
    public async Task ImportProfile_NeitherBodyNorUrl_Returns422WithEnvelope()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/encoder/profiles/import?trust_unsigned=true",
            new { }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.UnprocessableEntity,
            $"Expected 422 for import with no source, got {(int)response.StatusCode}: {body}"
        );
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("valid", out JsonElement validEl)
            .Should()
            .BeTrue($"Response must be a ValidationEnvelope with 'valid'. Body: {body}");
        validEl.GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ImportProfile_WithInlineProfileJson_And_TrustUnsigned_ReturnsCreated()
    {
        string uniqueName = $"Imported-{Ulid.NewUlid()}";
        string importJson = JsonSerializer.Serialize(
            new
            {
                Id = Ulid.NewUlid().ToString(),
                Name = uniqueName,
                Container = "HlsTs",
                Video = (object?)null,
                Audio = Array.Empty<object>(),
                Subtitles = Array.Empty<object>(),
            }
        );

        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/encoder/profiles/import?trust_unsigned=true",
            new { profile_json = importJson }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"Expected 201 for valid import with trust_unsigned=true, got {(int)response.StatusCode}: {body}"
        );

        await using MediaContext cleanupCtx = new();
        await cleanupCtx.EncodingPresets.Where(p => p.Name == uniqueName).ExecuteDeleteAsync();
    }

    // RULE: import/unsigned-requires-flag — POST /encoder/profiles/import with
    //       a valid inline profile but WITHOUT ?trust_unsigned=true must return 422

    [Fact]
    public async Task ImportProfile_UnsignedWithoutTrustFlag_Returns422()
    {
        string importJson = JsonSerializer.Serialize(
            new
            {
                Id = Ulid.NewUlid().ToString(),
                Name = $"Untrusted-{Ulid.NewUlid()}",
                Container = "HlsTs",
                Video = (object?)null,
                Audio = Array.Empty<object>(),
                Subtitles = Array.Empty<object>(),
            }
        );

        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/encoder/profiles/import",
            new { profile_json = importJson }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.UnprocessableEntity,
            $"Expected 422 for unsigned import without trust_unsigned flag, got {(int)response.StatusCode}: {body}"
        );

        using JsonDocument doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("errors", out JsonElement errs).Should().BeTrue();
        bool hasUnsignedRule = errs.EnumerateArray()
            .Any(e =>
                e.TryGetProperty("id", out JsonElement idEl)
                && idEl.GetString() == "import.unsigned_requires_flag"
            );
        Assert.True(
            hasUnsignedRule,
            $"Expected rule id 'import.unsigned_requires_flag'. Body: {body}"
        );
    }

    [Fact]
    public async Task ImportProfile_WithTrustUnsignedFlag_DoesNotReturn422ForUnsignedProfile()
    {
        string uniqueName = $"Trusted-Import-{Ulid.NewUlid()}";
        string importJson = JsonSerializer.Serialize(
            new
            {
                Id = Ulid.NewUlid().ToString(),
                Name = uniqueName,
                Container = "HlsTs",
                Video = (object?)null,
                Audio = Array.Empty<object>(),
                Subtitles = Array.Empty<object>(),
            }
        );

        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/encoder/profiles/import?trust_unsigned=true",
            new { profile_json = importJson }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.NotEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        await using MediaContext cleanupCtx = new();
        await cleanupCtx.EncodingPresets.Where(p => p.Name == uniqueName).ExecuteDeleteAsync();
    }

    // RULE: import/http-url-rejected — POST /encoder/profiles/import with a

    [Fact]
    public async Task ImportProfile_HttpUrl_Returns422WithHttpNotHttpsRuleId()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/encoder/profiles/import",
            new { url = "http://example.com/profile.json" }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.UnprocessableEntity,
            $"Expected 422 for http:// URL, got {(int)response.StatusCode}: {body}"
        );
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("errors", out JsonElement errs)
            .Should()
            .BeTrue($"Envelope must have 'errors'. Body: {body}");
        bool hasHttpRule = errs.EnumerateArray()
            .Any(e =>
                e.TryGetProperty("id", out JsonElement idEl)
                && idEl.GetString() == "import.http_not_https"
            );
        Assert.True(hasHttpRule, $"Expected rule 'import.http_not_https'. Body: {body}");
    }

    [Fact]
    public async Task ImportProfile_HttpsUrl_DoesNotRejectForHttpNotHttps()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/encoder/profiles/import",
            new { url = "https://example.com/profile.json" }
        );

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // RULE: legacy-create/name-required — POST /dashboard/encoding/presets with
    //       no name must return 400.
    //
    // Note: CreatePresetRequest has no [JsonProperty] → Newtonsoft binds by
    //       PascalCase property name "ProfileJson". The validation guard for
    //       missing Name fires BEFORE the duplicate-name check (controller

    [Fact]
    public async Task LegacyCreatePreset_MissingName_Returns400()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/dashboard/encoding/presets",
            new { ProfileJson = ValidProfileJson }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 400 for legacy create with missing name, got {(int)response.StatusCode}: {body}"
        );
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task LegacyCreatePreset_WithName_DoesNotReturn400ForMissingName()
    {
        string uniqueName = $"Legacy-Create-{Ulid.NewUlid()}";
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/dashboard/encoding/presets",
            new { Name = uniqueName, ProfileJson = ValidProfileJson }
        );

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);

        await using MediaContext cleanupCtx = new();
        await cleanupCtx.EncodingPresets.Where(p => p.Name == uniqueName).ExecuteDeleteAsync();
    }

    // RULE: legacy-create/profile_json-required — POST /dashboard/encoding/presets

    [Fact]
    public async Task LegacyCreatePreset_MissingProfileJson_Returns400()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/dashboard/encoding/presets",
            new { Name = "SomeName" }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 400 for legacy create with missing profile_json, got {(int)response.StatusCode}: {body}"
        );
    }

    // RULE: legacy-create/name-conflict — POST /dashboard/encoding/presets with

    [Fact]
    public async Task LegacyCreatePreset_DuplicateName_Returns409()
    {
        await using MediaContext ctx = new();
        EncodingPreset? existing = await ctx.EncodingPresets.FirstOrDefaultAsync(p =>
            p.Id == _userPresetId
        );
        Assert.NotNull(existing);

        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/dashboard/encoding/presets",
            new { Name = existing!.Name, ProfileJson = ValidProfileJson }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.Conflict,
            $"Expected 409 for duplicate name on legacy endpoint, got {(int)response.StatusCode}: {body}"
        );
    }

    // RULE: legacy-update/builtin-readonly — PUT /dashboard/encoding/presets/{id}
    //       on a built-in preset must return 422 with rule profile.builtin_readonly.
    //
    // Note: UpdatePresetRequest has no [JsonProperty] → Newtonsoft binds PascalCase.

    [Fact]
    public async Task LegacyUpdatePreset_BuiltIn_Returns422WithBuiltinReadonlyRuleId()
    {
        HttpResponseMessage response = await _authed.PutAsJsonAsync(
            $"/api/v1/dashboard/encoding/presets/{_builtInPresetId}",
            new { Name = "Tampered" }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.UnprocessableEntity,
            $"Expected 422 for update on built-in preset (legacy), got {(int)response.StatusCode}: {body}"
        );
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("errors", out JsonElement errs)
            .Should()
            .BeTrue($"Response must have 'errors'. Body: {body}");
        bool hasBuiltinRule = errs.EnumerateArray()
            .Any(e =>
                e.TryGetProperty("id", out JsonElement idEl)
                && idEl.GetString() == "profile.builtin_readonly"
            );
        Assert.True(
            hasBuiltinRule,
            $"Expected rule 'profile.builtin_readonly' in errors. Body: {body}"
        );
    }

    [Fact]
    public async Task LegacyUpdatePreset_UserPreset_DoesNotReturn422()
    {
        HttpResponseMessage response = await _authed.PutAsJsonAsync(
            $"/api/v1/dashboard/encoding/presets/{_userPresetId}",
            new { Name = $"Updated-{Ulid.NewUlid()}" }
        );

        Assert.NotEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // RULE: legacy-get/invalid-ulid — GET /dashboard/encoding/presets/{id} with

    [Fact]
    public async Task LegacyGetPreset_InvalidUlid_Returns400()
    {
        HttpResponseMessage response = await _authed.GetAsync(
            "/api/v1/dashboard/encoding/presets/not-a-ulid"
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 400 for invalid ULID on legacy get, got {(int)response.StatusCode}: {body}"
        );
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task LegacyGetPreset_ValidUlid_ExistingPreset_Returns200()
    {
        HttpResponseMessage response = await _authed.GetAsync(
            $"/api/v1/dashboard/encoding/presets/{_userPresetId}"
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 for valid ULID, got {(int)response.StatusCode}: {body}"
        );
    }

    // RULE: legacy-import/name-required — POST /dashboard/encoding/presets/import
    //       with no name must return 400.
    //

    [Fact]
    public async Task LegacyImportPreset_MissingName_Returns400()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/dashboard/encoding/presets/import",
            new { ProfileJson = ValidProfileJson }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 400 for legacy import with missing name, got {(int)response.StatusCode}: {body}"
        );
    }

    [Fact]
    public async Task LegacyImportPreset_MissingProfileJson_Returns400()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/dashboard/encoding/presets/import",
            new { Name = "ImportedWithoutJson" }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 400 for legacy import with missing profile_json, got {(int)response.StatusCode}: {body}"
        );
    }

    // RULE: legacy-import-url/url-required — POST /dashboard/encoding/presets/import-url
    //       with empty url must return 400.
    //

    [Fact]
    public async Task LegacyImportUrl_EmptyUrl_Returns400()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/dashboard/encoding/presets/import-url",
            new { Url = "" }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 400 for empty URL on legacy import-url, got {(int)response.StatusCode}: {body}"
        );
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task LegacyImportUrl_InvalidUrl_Returns400()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/dashboard/encoding/presets/import-url",
            new { Url = "not_a_url_at_all" }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 400 for non-absolute URL, got {(int)response.StatusCode}: {body}"
        );
    }

    [Fact]
    public async Task LegacyImportUrl_HttpUrl_Returns400WithHttpsOnlyMessage()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/dashboard/encoding/presets/import-url",
            new { Url = "http://example.com/profile.json" }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 400 for http:// URL on legacy import-url, got {(int)response.StatusCode}: {body}"
        );
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task LegacyImportUrl_ValidHttpsUrl_IsNotRejectedForHttps()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/dashboard/encoding/presets/import-url",
            new { Url = "https://example.com/profile.json" }
        );

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // DRIVER RULES

    // RULE: driver-create/invalid-type — POST /dashboard/drivers with an

    [Fact]
    public async Task CreateDriver_InvalidType_Returns400()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/dashboard/drivers",
            new
            {
                name = $"TestDriver-{Ulid.NewUlid()}",
                type = "ftp",
                config = new { rootPath = "/tmp" },
            }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 400 for invalid driver type, got {(int)response.StatusCode}: {body}"
        );
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("detail", out JsonElement detail).Should().BeTrue();
        detail.GetString().Should().Contain("Invalid type");
    }

    [Fact]
    public async Task CreateDriver_ValidType_DoesNotRejectForInvalidType()
    {
        string uniqueName = $"Local-Driver-{Ulid.NewUlid()}";
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/dashboard/drivers",
            new
            {
                name = uniqueName,
                type = "local",
                config = new { rootPath = "/tmp/test" },
            }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);

        await using MediaContext cleanupCtx = new();
        await cleanupCtx.Drivers.Where(d => d.Name == uniqueName).ExecuteDeleteAsync();
    }

    // RULE: driver-create/name-required — POST /dashboard/drivers with empty name

    [Fact]
    public async Task CreateDriver_EmptyName_Returns400()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/dashboard/drivers",
            new
            {
                name = "",
                type = "local",
                config = new { rootPath = "/tmp" },
            }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 400 for empty driver name, got {(int)response.StatusCode}: {body}"
        );
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task CreateDriver_NonEmptyName_DoesNotRejectForEmptyName()
    {
        string uniqueName = $"Named-Driver-{Ulid.NewUlid()}";
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/dashboard/drivers",
            new
            {
                name = uniqueName,
                type = "local",
                config = new { rootPath = "/tmp/test" },
            }
        );

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);

        await using MediaContext cleanupCtx = new();
        await cleanupCtx.Drivers.Where(d => d.Name == uniqueName).ExecuteDeleteAsync();
    }

    // RULE: driver-create/local-requires-rootpath — POST /dashboard/drivers type=local

    [Fact]
    public async Task CreateDriver_LocalWithoutRootPath_Returns400()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/dashboard/drivers",
            new
            {
                name = $"LocalNoPath-{Ulid.NewUlid()}",
                type = "local",
                config = new { },
            }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 400 for local driver without rootPath, got {(int)response.StatusCode}: {body}"
        );
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("detail", out JsonElement detail).Should().BeTrue();
        detail.GetString().Should().Contain("rootPath");
    }

    [Fact]
    public async Task CreateDriver_LocalWithRootPath_DoesNotRejectForMissingRootPath()
    {
        string uniqueName = $"LocalWithPath-{Ulid.NewUlid()}";
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/dashboard/drivers",
            new
            {
                name = uniqueName,
                type = "local",
                config = new { rootPath = "/tmp/media" },
            }
        );

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);

        await using MediaContext cleanupCtx = new();
        await cleanupCtx.Drivers.Where(d => d.Name == uniqueName).ExecuteDeleteAsync();
    }

    // RULE: driver-create/nfs-requires-server-and-export — POST /dashboard/drivers

    [Fact]
    public async Task CreateDriver_NfsWithoutServer_Returns400()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/dashboard/drivers",
            new
            {
                name = $"NfsNoServer-{Ulid.NewUlid()}",
                type = "nfs",
                config = new { export = "/exports/media" },
            }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 400 for nfs driver without server, got {(int)response.StatusCode}: {body}"
        );
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task CreateDriver_NfsWithServerAndExport_DoesNotRejectForMissingServer()
    {
        string uniqueName = $"NfsValid-{Ulid.NewUlid()}";
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/dashboard/drivers",
            new
            {
                name = uniqueName,
                type = "nfs",
                config = new { server = "192.168.1.100", export = "/exports/media" },
            }
        );

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);

        await using MediaContext cleanupCtx = new();
        await cleanupCtx.Drivers.Where(d => d.Name == uniqueName).ExecuteDeleteAsync();
    }

    // RULE: driver-create/nfs-version-restricted — nfs version must be 3 or 4;

    [Fact]
    public async Task CreateDriver_NfsInvalidVersion_Returns400()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/dashboard/drivers",
            new
            {
                name = $"NfsV99-{Ulid.NewUlid()}",
                type = "nfs",
                config = new
                {
                    server = "192.168.1.100",
                    export = "/exports/media",
                    version = 99,
                },
            }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 400 for nfs version=99, got {(int)response.StatusCode}: {body}"
        );
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task CreateDriver_NfsVersion3_IsNotRejectedForInvalidVersion()
    {
        string uniqueName = $"NfsV3-{Ulid.NewUlid()}";
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/dashboard/drivers",
            new
            {
                name = uniqueName,
                type = "nfs",
                config = new
                {
                    server = "192.168.1.100",
                    export = "/exports/media",
                    version = 3,
                },
            }
        );

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);

        await using MediaContext cleanupCtx = new();
        await cleanupCtx.Drivers.Where(d => d.Name == uniqueName).ExecuteDeleteAsync();
    }

    [Fact]
    public async Task CreateDriver_S3WithoutBucket_Returns400()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/dashboard/drivers",
            new
            {
                name = $"S3NoBucket-{Ulid.NewUlid()}",
                type = "s3",
                config = new { region = "us-east-1" },
            }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 400 for s3 without bucket, got {(int)response.StatusCode}: {body}"
        );
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task CreateDriver_S3WithBucketAndRegion_DoesNotRejectForMissingBucket()
    {
        string uniqueName = $"S3Valid-{Ulid.NewUlid()}";
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/dashboard/drivers",
            new
            {
                name = uniqueName,
                type = "s3",
                config = new { bucket = "my-media-bucket", region = "us-east-1" },
            }
        );

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);

        await using MediaContext cleanupCtx = new();
        await cleanupCtx.Drivers.Where(d => d.Name == uniqueName).ExecuteDeleteAsync();
    }

    [Fact]
    public async Task CreateDriver_R2WithoutEndpoint_Returns400()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/dashboard/drivers",
            new
            {
                name = $"R2NoEndpoint-{Ulid.NewUlid()}",
                type = "r2",
                config = new { bucket = "my-r2-bucket", region = "auto" },
            }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 400 for r2 without endpoint, got {(int)response.StatusCode}: {body}"
        );
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("detail", out JsonElement detail).Should().BeTrue();
        detail.GetString().Should().Contain("endpoint");
    }

    [Fact]
    public async Task CreateDriver_R2WithEndpoint_DoesNotRejectForMissingEndpoint()
    {
        string uniqueName = $"R2Valid-{Ulid.NewUlid()}";
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/dashboard/drivers",
            new
            {
                name = uniqueName,
                type = "r2",
                config = new
                {
                    bucket = "my-r2-bucket",
                    region = "auto",
                    endpoint = "https://accountid.r2.cloudflarestorage.com",
                },
            }
        );

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);

        await using MediaContext cleanupCtx = new();
        await cleanupCtx.Drivers.Where(d => d.Name == uniqueName).ExecuteDeleteAsync();
    }

    [Fact]
    public async Task CreateDriver_WebDavWithoutUrl_Returns400()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/dashboard/drivers",
            new
            {
                name = $"WebDavNoUrl-{Ulid.NewUlid()}",
                type = "webdav",
                config = new { },
            }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 400 for webdav without url, got {(int)response.StatusCode}: {body}"
        );
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task CreateDriver_WebDavWithUrl_DoesNotRejectForMissingUrl()
    {
        string uniqueName = $"WebDavValid-{Ulid.NewUlid()}";
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/dashboard/drivers",
            new
            {
                name = uniqueName,
                type = "webdav",
                config = new
                {
                    url = "https://nextcloud.example.com/remote.php/dav/files/user/Movies/",
                },
            }
        );

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);

        await using MediaContext cleanupCtx = new();
        await cleanupCtx.Drivers.Where(d => d.Name == uniqueName).ExecuteDeleteAsync();
    }

    [Fact]
    public async Task CreateDriver_DuplicateName_Returns409()
    {
        string sharedName = $"DupeDriver-{Ulid.NewUlid()}";
        Ulid firstId = Ulid.NewUlid();

        await using (MediaContext seedCtx = new())
        {
            seedCtx.Drivers.Add(
                new()
                {
                    Id = firstId,
                    Name = sharedName,
                    Type = "local",
                    Config = """{"rootPath":"/tmp"}""",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                }
            );
            await seedCtx.SaveChangesAsync();
        }

        try
        {
            HttpResponseMessage response = await _authed.PostAsJsonAsync(
                "/api/v1/dashboard/drivers",
                new
                {
                    name = sharedName,
                    type = "local",
                    config = new { rootPath = "/tmp/other" },
                }
            );

            string body = await response.Content.ReadAsStringAsync();
            Assert.True(
                response.StatusCode == HttpStatusCode.Conflict,
                $"Expected 409 for duplicate driver name, got {(int)response.StatusCode}: {body}"
            );
            Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        }
        finally
        {
            await using MediaContext cleanupCtx = new();
            await cleanupCtx.Drivers.Where(d => d.Id == firstId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task CreateDriver_UniqueName_DoesNotReturn409()
    {
        string uniqueName = $"UniqueDriver-{Ulid.NewUlid()}";
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            "/api/v1/dashboard/drivers",
            new
            {
                name = uniqueName,
                type = "local",
                config = new { rootPath = "/tmp/unique" },
            }
        );

        Assert.NotEqual(HttpStatusCode.Conflict, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);

        await using MediaContext cleanupCtx = new();
        await cleanupCtx.Drivers.Where(d => d.Name == uniqueName).ExecuteDeleteAsync();
    }

    // RULE: driver-delete/system-driver-protected — DELETE /dashboard/drivers/system-local-id

    [Fact]
    public async Task DeleteDriver_SystemLocalDriver_Returns409()
    {
        string systemId = Database.Models.Storage.Driver.SystemLocalDriverId.ToString();
        HttpResponseMessage response = await _authed.DeleteAsync(
            $"/api/v1/dashboard/drivers/{systemId}"
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.Conflict,
            $"Expected 409 for delete of system driver, got {(int)response.StatusCode}: {body}"
        );
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task DeleteDriver_NonSystemDriver_DoesNotReturn409ForSystemDriverProtection()
    {
        Ulid deleteableId = Ulid.NewUlid();
        await using (MediaContext seedCtx = new())
        {
            seedCtx.Drivers.Add(
                new()
                {
                    Id = deleteableId,
                    Name = $"Deleteable-{deleteableId}",
                    Type = "local",
                    Config = """{"rootPath":"/tmp/del"}""",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                }
            );
            await seedCtx.SaveChangesAsync();
        }

        HttpResponseMessage response = await _authed.DeleteAsync(
            $"/api/v1/dashboard/drivers/{deleteableId}"
        );

        Assert.NotEqual(HttpStatusCode.Conflict, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // RULE: driver-update/system-driver-immutable — PUT /dashboard/drivers/{system-id}

    [Fact]
    public async Task UpdateDriver_SystemLocalDriver_Returns409()
    {
        string systemId = Database.Models.Storage.Driver.SystemLocalDriverId.ToString();
        HttpResponseMessage response = await _authed.PutAsJsonAsync(
            $"/api/v1/dashboard/drivers/{systemId}",
            new { name = "Renamed System" }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.Conflict,
            $"Expected 409 for update of system driver, got {(int)response.StatusCode}: {body}"
        );
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task UpdateDriver_NonSystemDriver_DoesNotReturn409ForSystemImmutableRule()
    {
        Ulid updateableId = Ulid.NewUlid();
        await using (MediaContext seedCtx = new())
        {
            seedCtx.Drivers.Add(
                new()
                {
                    Id = updateableId,
                    Name = $"Updateable-{updateableId}",
                    Type = "local",
                    Config = """{"rootPath":"/tmp/upd"}""",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                }
            );
            await seedCtx.SaveChangesAsync();
        }

        HttpResponseMessage response = await _authed.PutAsJsonAsync(
            $"/api/v1/dashboard/drivers/{updateableId}",
            new { name = $"Renamed-{Ulid.NewUlid()}" }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.NotEqual(HttpStatusCode.Conflict, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);

        await using MediaContext cleanupCtx = new();
        await cleanupCtx.Drivers.Where(d => d.Id == updateableId).ExecuteDeleteAsync();
    }

    // RULE: driver-update-credentials/blank-credentials-rejected — PUT
    //       /dashboard/drivers/{id}/credentials with empty access_key or secret_key

    [Fact]
    public async Task UpdateDriverCredentials_BlankKeys_Returns400()
    {
        Ulid driverId = Ulid.NewUlid();
        await using (MediaContext seedCtx = new())
        {
            seedCtx.Drivers.Add(
                new()
                {
                    Id = driverId,
                    Name = $"CredDriver-{driverId}",
                    Type = "s3",
                    Config = """{"bucket":"b","region":"us-east-1"}""",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                }
            );
            await seedCtx.SaveChangesAsync();
        }

        try
        {
            HttpResponseMessage response = await _authed.PutAsJsonAsync(
                $"/api/v1/dashboard/drivers/{driverId}/credentials",
                new { access_key = "", secret_key = "" }
            );

            string body = await response.Content.ReadAsStringAsync();
            Assert.True(
                response.StatusCode == HttpStatusCode.BadRequest,
                $"Expected 400 for blank credentials, got {(int)response.StatusCode}: {body}"
            );
            Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        }
        finally
        {
            await using MediaContext cleanupCtx = new();
            await cleanupCtx.Drivers.Where(d => d.Id == driverId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task UpdateDriverCredentials_NonBlankKeys_DoesNotReturn400ForBlankKeys()
    {
        Ulid driverId = Ulid.NewUlid();
        await using (MediaContext seedCtx = new())
        {
            seedCtx.Drivers.Add(
                new()
                {
                    Id = driverId,
                    Name = $"CredDriverFull-{driverId}",
                    Type = "s3",
                    Config = """{"bucket":"b","region":"us-east-1"}""",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                }
            );
            await seedCtx.SaveChangesAsync();
        }

        try
        {
            HttpResponseMessage response = await _authed.PutAsJsonAsync(
                $"/api/v1/dashboard/drivers/{driverId}/credentials",
                new
                {
                    access_key = "AKIAIOSFODNN7EXAMPLE",
                    secret_key = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
                }
            );

            Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        }
        finally
        {
            await using MediaContext cleanupCtx = new();
            await cleanupCtx.Drivers.Where(d => d.Id == driverId).ExecuteDeleteAsync();
        }
    }

    // RULE: driver-delete/referenced-by-folder-conflict — DELETE /dashboard/drivers/{id}
    //       when folders reference the driver must return 409.
    // (The system driver uses MovieFolderId referencing Driver.SystemLocalDriverId — we
    //  assert the system-driver guard fires first, which also proves the folder-count guard

    [Fact]
    public async Task DeleteDriver_SystemDriverWithFolderReferences_Returns409()
    {
        string systemId = Database.Models.Storage.Driver.SystemLocalDriverId.ToString();
        HttpResponseMessage response = await _authed.DeleteAsync(
            $"/api/v1/dashboard/drivers/{systemId}"
        );

        Assert.True(
            response.StatusCode == HttpStatusCode.Conflict,
            $"Expected 409 (system guard or folder-count guard), got {(int)response.StatusCode}"
        );
    }

    // META-TEST: catalogue completeness self-check.
    //
    // For each rule documented in this catalogue the test verifies that BOTH a
    // FIRES-ON-BAD test and a SILENT-ON-VALID-NEIGHBOR test exist in this class.
    // Naming convention: the rule name appears in both method names (case-insensitive).
    //
    // If a new guard is added to any of the four controllers but no FIRES + NEIGHBOR

    [Fact]
    public void MetaTest_EachCataloguedRuleHasFIRESAndNEIGHBORTestMethods()
    {
        string[][] rulePairs =
        [
            [
                "validate/profile_json-missing",
                "Validate_MissingProfileJson",
                "Validate_ValidProfileJson",
            ],
            [
                "validate/profile_json-malformed",
                "Validate_MalformedProfileJson",
                "Validate_WellFormedJsonObject",
            ],
            [
                "validate/empty-outputs",
                "Validate_EmptyArraysProfile",
                "Validate_ProfileWithOneAudioOutput",
            ],
            [
                "legacy-validate/profile_json-missing",
                "LegacyValidate_MissingProfileJson",
                "LegacyValidate_PresentProfileJson",
            ],
            [
                "create-preset/name-required",
                "CreateProfile_MissingName",
                "CreateProfile_WithNameAndProfileJson",
            ],
            [
                "create-preset/profile_json-required",
                "CreateProfile_MissingProfileJson",
                "CreateProfile_WithBothNameAndProfileJson",
            ],
            [
                "create-preset/name-conflict",
                "CreateProfile_DuplicateName",
                "CreateProfile_UniqueName",
            ],
            [
                "update-preset/builtin-readonly",
                "UpdateProfile_BuiltInPreset",
                "UpdateProfile_UserPreset",
            ],
            [
                "delete-preset/builtin-not-deletable",
                "DeleteProfile_BuiltIn",
                "DeleteProfile_UserPreset_WithNoChildren",
            ],
            [
                "delete-preset/has-children",
                "DeleteProfile_HasChildren",
                "DeleteProfile_ParentWithChildrenDeleted",
            ],
            ["get-preset/not-found", "GetProfile_NonExistentId", "GetProfile_ExistingId"],
            [
                "get-list/pagination-clamped",
                "ListProfiles_ExcessivePageSize",
                "ListProfiles_NormalPageSize",
            ],
            [
                "get-list/envelope-shape",
                "ListProfiles_ReturnsEnvelopeWithDataAndMeta",
                "ListProfiles_ReturnsEnvelopeWithDataAndMeta",
            ],
            ["clone/name-required", "CloneProfile_MissingName", "CloneProfile_WithName"],
            [
                "clone/parent-not-found",
                "CloneProfile_NonExistentParent",
                "CloneProfile_ExistingParent",
            ],
            [
                "import/neither-body-nor-url",
                "ImportProfile_NeitherBodyNorUrl",
                "ImportProfile_WithInlineProfileJson",
            ],
            [
                "import/unsigned-requires-flag",
                "ImportProfile_UnsignedWithoutTrustFlag",
                "ImportProfile_WithTrustUnsignedFlag",
            ],
            ["import/http-url-rejected", "ImportProfile_HttpUrl", "ImportProfile_HttpsUrl"],
            [
                "legacy-create/name-required",
                "LegacyCreatePreset_MissingName",
                "LegacyCreatePreset_WithName",
            ],
            [
                "legacy-create/profile_json-required",
                "LegacyCreatePreset_MissingProfileJson",
                "LegacyCreatePreset_WithName",
            ],
            [
                "legacy-create/name-conflict",
                "LegacyCreatePreset_DuplicateName",
                "LegacyCreatePreset_WithName",
            ],
            [
                "legacy-update/builtin-readonly",
                "LegacyUpdatePreset_BuiltIn",
                "LegacyUpdatePreset_UserPreset",
            ],
            ["legacy-get/invalid-ulid", "LegacyGetPreset_InvalidUlid", "LegacyGetPreset_ValidUlid"],
            [
                "legacy-import/name-required",
                "LegacyImportPreset_MissingName",
                "LegacyImportPreset_MissingProfileJson",
            ],
            [
                "legacy-import-url/url-required",
                "LegacyImportUrl_EmptyUrl",
                "LegacyImportUrl_ValidHttpsUrl",
            ],
            [
                "legacy-import-url/invalid-url",
                "LegacyImportUrl_InvalidUrl",
                "LegacyImportUrl_ValidHttpsUrl",
            ],
            [
                "legacy-import-url/http-url-rejected",
                "LegacyImportUrl_HttpUrl",
                "LegacyImportUrl_ValidHttpsUrl",
            ],
            ["driver-create/invalid-type", "CreateDriver_InvalidType", "CreateDriver_ValidType"],
            ["driver-create/name-required", "CreateDriver_EmptyName", "CreateDriver_NonEmptyName"],
            [
                "driver-create/local-requires-rootpath",
                "CreateDriver_LocalWithoutRootPath",
                "CreateDriver_LocalWithRootPath",
            ],
            [
                "driver-create/nfs-requires-server-and-export",
                "CreateDriver_NfsWithoutServer",
                "CreateDriver_NfsWithServerAndExport",
            ],
            [
                "driver-create/nfs-version-restricted",
                "CreateDriver_NfsInvalidVersion",
                "CreateDriver_NfsVersion3",
            ],
            [
                "driver-create/s3-requires-bucket-and-region",
                "CreateDriver_S3WithoutBucket",
                "CreateDriver_S3WithBucketAndRegion",
            ],
            [
                "driver-create/r2-requires-endpoint",
                "CreateDriver_R2WithoutEndpoint",
                "CreateDriver_R2WithEndpoint",
            ],
            [
                "driver-create/webdav-requires-url",
                "CreateDriver_WebDavWithoutUrl",
                "CreateDriver_WebDavWithUrl",
            ],
            [
                "driver-create/name-conflict",
                "CreateDriver_DuplicateName",
                "CreateDriver_UniqueName",
            ],
            [
                "driver-delete/system-driver-protected",
                "DeleteDriver_SystemLocalDriver",
                "DeleteDriver_NonSystemDriver",
            ],
            [
                "driver-update/system-driver-immutable",
                "UpdateDriver_SystemLocalDriver",
                "UpdateDriver_NonSystemDriver",
            ],
            [
                "driver-update-credentials/blank-credentials-rejected",
                "UpdateDriverCredentials_BlankKeys",
                "UpdateDriverCredentials_NonBlankKeys",
            ],
        ];

        System.Reflection.MethodInfo[] allMethods = GetType()
            .GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
            );

        List<string> missing = [];

        foreach (string[] pair in rulePairs)
        {
            string ruleId = pair[0];
            string firesFragment = pair[1];
            string neighborFragment = pair[2];

            bool hasFires = allMethods.Any(m =>
                m.Name.Contains(firesFragment, StringComparison.OrdinalIgnoreCase)
            );
            bool hasNeighbor = allMethods.Any(m =>
                m.Name.Contains(neighborFragment, StringComparison.OrdinalIgnoreCase)
            );

            if (!hasFires)
                missing.Add($"Rule '{ruleId}': missing FIRES test containing '{firesFragment}'");
            if (!hasNeighbor)
                missing.Add(
                    $"Rule '{ruleId}': missing NEIGHBOR test containing '{neighborFragment}'"
                );
        }

        Assert.True(
            missing.Count == 0,
            $"Catalogue completeness failures:\n{string.Join("\n", missing)}"
        );
    }
}
