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
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Activity;
using NoMercy.Database.Models.Media;
using NoMercy.Encoder.Errors;
using NoMercy.Encoder.Profiles;

namespace NoMercy.Data.Services;

/// <summary>
/// Business logic extracted from <see cref="NoMercy.Api.Controllers.V1.Encoder.EncoderProfilesController"/>.
/// The controller owns auth checks and HTTP result mapping; this service owns
/// validation, persistence, and activity logging.
/// </summary>
public class EncoderProfileService(
    IEncodingPresetRepository presetRepository,
    IActivityLogger activityLogger,
    IHttpClientFactory httpClientFactory,
    MediaContext mediaContext,
    ILogger<EncoderProfileService> logger
)
{
    // -------------------------------------------------------------------------
    // Create
    // -------------------------------------------------------------------------

    public sealed record CreateResult
    {
        public EncodingPreset? Saved { get; init; }
        public string? ErrorCode { get; init; }
        public string? ErrorMessage { get; init; }

        public bool IsConflict => ErrorCode == "conflict";
        public bool IsValidation => ErrorCode == "validation_error";
        public bool IsSuccess => Saved is not null;
    }

    public async Task<CreateResult> CreateAsync(
        string? name,
        string? profileJson,
        string? description,
        string? author,
        string? tags,
        Ulid? parentPresetId,
        Guid userId
    )
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            await TryLogFailureAsync(userId, "validation_error", "name is required");
            return new()
            {
                ErrorCode = "validation_error",
                ErrorMessage = "name is required",
            };
        }

        if (string.IsNullOrWhiteSpace(profileJson))
        {
            await TryLogFailureAsync(userId, "validation_error", "profile_json is required");
            return new()
            {
                ErrorCode = "validation_error",
                ErrorMessage = "profile_json is required",
            };
        }

        EncodingPreset? existing = await presetRepository.GetByNameAsync(name);
        if (existing is not null)
            return new()
            {
                ErrorCode = "conflict",
                ErrorMessage = $"A profile named '{name}' already exists",
            };

        EncodingPreset preset = new()
        {
            Name = name,
            Description = description,
            Author = author,
            Tags = tags,
            ProfileJson = profileJson,
            ParentPresetId = parentPresetId,
            IsBuiltIn = false,
        };

        EncodingPreset saved = await presetRepository.CreateAsync(preset);

        await TryLogConfigAsync(
            userId,
            $"encoder.profile.{saved.Id}",
            oldValue: null,
            newValue: new
            {
                id = saved.Id.ToString(),
                name = saved.Name,
                action = "created",
            }
        );

        return new() { Saved = saved };
    }

    // -------------------------------------------------------------------------
    // Preview
    // -------------------------------------------------------------------------

    public sealed record PreviewParseResult
    {
        public EncodingProfile? Profile { get; init; }

        /// <summary>Non-null means the caller should return an early 200 with this response.</summary>
        public PreviewResponse? EarlyResponse { get; init; }
    }

    /// <summary>
    /// Validates and deserialises the profile JSON for a preview request.
    /// Returns an <see cref="PreviewParseResult"/> with either a parsed profile
    /// or a populated <see cref="PreviewResponse"/> for the early-return cases.
    /// </summary>
    public PreviewParseResult ParseProfileForPreview(
        string id,
        string? profileJson,
        string? sourcePath
    )
    {
        if (string.IsNullOrWhiteSpace(profileJson))
        {
            return new()
            {
                EarlyResponse = BuildPreviewErrorResponse(
                    id,
                    sourcePath,
                    EncoderRuleId.ImportSourceMissing,
                    "profile_json",
                    "profile_json is required.",
                    "Supply the full profile JSON in the profile_json field."
                ),
            };
        }

        EncodingProfile? profile;
        try
        {
            profile = JsonConvert.DeserializeObject<EncodingProfile>(profileJson);
        }
        catch (JsonException ex)
        {
            return new()
            {
                EarlyResponse = BuildPreviewErrorResponse(
                    id,
                    sourcePath,
                    EncoderRuleId.ImportJsonMalformed,
                    "profile_json",
                    $"Profile JSON is malformed: {ex.Message}",
                    "Fix the JSON syntax error and resubmit."
                ),
            };
        }

        if (profile is null)
        {
            return new()
            {
                EarlyResponse = BuildPreviewErrorResponse(
                    id,
                    sourcePath,
                    EncoderRuleId.ImportJsonMalformed,
                    "profile_json",
                    "Profile JSON deserialized to null — check the outer object is present.",
                    "Ensure the JSON root is an object, not null or an array."
                ),
            };
        }

        return new() { Profile = profile };
    }

    // -------------------------------------------------------------------------
    // Import
    // -------------------------------------------------------------------------

    public sealed record ImportResult
    {
        public EncodingPreset? Saved { get; init; }
        public EncodingProfile? ImportedProfile { get; init; }

        /// <summary>Non-null means return UnprocessableEntity with this envelope.</summary>
        public ValidationEnvelope? ValidationError { get; init; }

        public bool IsSuccess => Saved is not null;
    }

    public async Task<ImportResult> ImportAsync(
        string? inlineProfileJson,
        string? url,
        bool trustUnsigned,
        IProfileSignatureVerifier signatureVerifier,
        Guid userId,
        CancellationToken ct
    )
    {
        // --- 1. Resolve JSON source ------------------------------------------
        string profileJson;

        if (!string.IsNullOrWhiteSpace(inlineProfileJson))
        {
            profileJson = inlineProfileJson;
        }
        else if (!string.IsNullOrWhiteSpace(url))
        {
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return new()
                {
                    ValidationError = ValidationEnvelope.FromRules([
                        new(
                            EncoderRuleId.ImportHttpNotHttps,
                            EncoderRuleSeverity.Error,
                            "url",
                            "Profile URLs must use HTTPS — plain HTTP is not permitted.",
                            "Replace the URL scheme with https:// before importing."
                        ),
                    ]),
                };
            }

            HttpClient client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.MaxResponseContentBufferSize = 256 * 1024;

            HttpResponseMessage response;
            try
            {
                using HttpRequestMessage httpReq = new(HttpMethod.Get, url);
                response = await client.SendAsync(httpReq, ct);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                return new()
                {
                    ValidationError = ValidationEnvelope.FromRules([
                        new(
                            EncoderRuleId.ImportFetchFailed,
                            EncoderRuleSeverity.Error,
                            "url",
                            $"Failed to fetch profile from URL: {ex.Message}",
                            "Verify the URL is reachable and returns valid JSON."
                        ),
                    ]),
                };
            }

            profileJson = await response.Content.ReadAsStringAsync(ct);
        }
        else
        {
            return new()
            {
                ValidationError = ValidationEnvelope.FromRules([
                    new(
                        EncoderRuleId.ImportSourceMissing,
                        EncoderRuleSeverity.Error,
                        "profile_json",
                        "Either profile_json or url must be provided.",
                        "Supply profile_json with the raw JSON or url with an HTTPS URL pointing to the profile."
                    ),
                ]),
            };
        }

        // --- 2. Deserialise --------------------------------------------------
        EncodingProfile? profile;
        try
        {
            profile = JsonConvert.DeserializeObject<EncodingProfile>(profileJson);
        }
        catch (JsonException ex)
        {
            return new()
            {
                ValidationError = ValidationEnvelope.FromRules([
                    new(
                        EncoderRuleId.ImportJsonMalformed,
                        EncoderRuleSeverity.Error,
                        "profile_json",
                        $"Profile JSON is malformed: {ex.Message}",
                        "Fix the JSON syntax error and resubmit."
                    ),
                ]),
            };
        }

        if (profile is null)
        {
            return new()
            {
                ValidationError = ValidationEnvelope.FromRules([
                    new(
                        EncoderRuleId.ImportJsonMalformed,
                        EncoderRuleSeverity.Error,
                        "profile_json",
                        "Profile JSON deserialized to null — check the outer object is present.",
                        "Ensure the JSON root is an object, not null or an array."
                    ),
                ]),
            };
        }

        // --- 3. Signature verification ---------------------------------------
        // V2 EncodingProfile no longer carries PublisherKeyFingerprint /
        // Signature inline — those move into the trusted-publisher pipeline as
        // a separate envelope when V2 grows publisher signing support. For
        // now, skip signature verification on V2 profiles.
        bool hasSigning = false;

        if (hasSigning)
        {
            EncoderRule? rejection = signatureVerifier.Verify(
                profileJson,
                string.Empty,
                string.Empty,
                fingerprint =>
                    mediaContext
                        .TrustedPublisherKeys.AsNoTracking()
                        .FirstOrDefault(k => k.Fingerprint == fingerprint)
            );

            if (rejection is not null)
            {
                return new()
                {
                    ValidationError = ValidationEnvelope.FromRules([rejection]),
                };
            }
        }
        else
        {
            if (!trustUnsigned)
            {
                return new()
                {
                    ValidationError = ValidationEnvelope.FromRules([
                        new(
                            EncoderRuleId.ImportUnsignedRequiresFlag,
                            EncoderRuleSeverity.Error,
                            "trust_unsigned",
                            "This profile has no publisher signature. Pass ?trust_unsigned=true to import it anyway.",
                            "Add ?trust_unsigned=true to the request URL to accept unsigned profiles."
                        ),
                    ]),
                };
            }
        }

        // --- 4. Persist ------------------------------------------------------
        Ulid newId = Ulid.NewUlid();
        EncodingProfile importedProfile = profile with { Id = newId, IsBuiltin = false };
        string savedJson = JsonConvert.SerializeObject(importedProfile);

        EncodingPreset preset = new()
        {
            Id = newId,
            Name = importedProfile.Name,
            Description = importedProfile.Description,
            ProfileJson = savedJson,
            IsBuiltIn = false,
        };

        EncodingPreset saved = await presetRepository.CreateAsync(preset);

        await TryLogConfigAsync(
            userId,
            $"encoder.profile.{saved.Id}",
            oldValue: null,
            newValue: new
            {
                id = saved.Id.ToString(),
                name = saved.Name,
                action = "imported",
                source = url ?? "inline",
            }
        );

        return new() { Saved = saved, ImportedProfile = importedProfile };
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static PreviewResponse BuildPreviewErrorResponse(
        string id,
        string? sourcePath,
        string ruleId,
        string field,
        string message,
        string suggestion
    )
    {
        return new(
            ProfileId: id,
            SourceVideoFileId: sourcePath ?? string.Empty,
            SourceAnalysis: new(
                string.Empty,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                false,
                null,
                null,
                null
            ),
            PerStreamPlan: new([], [], []),
            SourceWarnings:
            [
                new(ruleId, EncoderRuleSeverity.Error, field, message, suggestion),
            ],
            EstimatedFps: 0,
            EstimatedDurationSeconds: 0,
            EncoderHandle: "auto"
        );
    }

    private async Task TryLogFailureAsync(Guid userId, string errorCode, string message)
    {
        try
        {
            await activityLogger.LogFailureAsync(
                "failure.config_save",
                userId,
                Ulid.Empty,
                errorCode: errorCode,
                message: message
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to log failure.config_save: {Message}", ex.Message);
        }
    }

    private async Task TryLogConfigAsync(
        Guid userId,
        string configKey,
        object? oldValue,
        object newValue
    )
    {
        try
        {
            await activityLogger.LogConfigurationAsync(
                "config.encoder_default_changed",
                userId,
                Ulid.Empty,
                configKey: configKey,
                oldValue: oldValue,
                newValue: newValue
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to log encoder profile config: {Message}", ex.Message);
        }
    }
}
