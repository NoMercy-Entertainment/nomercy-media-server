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

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Api.Services;
using NoMercy.Authorization;
using NoMercy.Data.Repositories;
using NoMercy.Data.Services;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Encoder.Errors;
using NoMercy.Encoder.Profiles;
using V2EncodingProfile = NoMercy.Encoder.Profiles.EncodingProfile;
using V2IPresetLookup = NoMercy.Encoder.Profiles.IPresetLookup;
using V2PresetResolver = NoMercy.Encoder.Profiles.PresetResolver;
using V2ProfileDiffer = NoMercy.Encoder.Profiles.ProfileDiffer;
using V2ProfileValidationResult = NoMercy.Encoder.Profiles.ProfileValidationResult;
using V2ProfileValidator = NoMercy.Encoder.Profiles.ProfileValidator;

namespace NoMercy.Api.Controllers.V1.Encoder;

/// <summary>
/// Phase 2 primary controller — supersedes the legacy
/// /api/v1/dashboard/encoding/presets controller. The dashboard should migrate
/// to these routes. Legacy endpoints remain operational but carry
/// <c>[Obsolete]</c> markers.
/// </summary>
[ApiController]
[Tags("Encoder Profiles")]
[ApiVersion(1.0)]
[Authorize(Policy = "Moderator")]
[Route("api/v{version:apiVersion}/encoder/profiles")]
public class EncoderProfilesController(
    IProfileValidator profileValidator,
    IProfileSignatureVerifier signatureVerifier,
    MediaContext mediaContext,
    IEncodingPresetRepository presetRepository,
    EncoderProfileService encoderProfileService
) : BaseController
{
    /// <summary>
    /// Returns a paginated list of encoding profiles, built-in rows first
    /// then user-created alphabetically. Optionally filtered by <paramref name="tag"/>.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Index(
        [FromQuery] int pageSize = 100,
        [FromQuery] int pageIndex = 0,
        [FromQuery] string? tag = null
    )
    {
        pageSize = Math.Clamp(pageSize, 1, 500);
        if (pageIndex < 0)
            pageIndex = 0;

        List<EncodingPreset> presets = await presetRepository.ListAsync(pageSize, pageIndex, tag);
        int total = await presetRepository.GetTotalCountAsync();

        return Ok(
            new
            {
                data = presets,
                meta = new
                {
                    total,
                    pageSize,
                    pageIndex,
                    totalPages = (int)Math.Ceiling((double)total / pageSize),
                },
            }
        );
    }

    /// <summary>
    /// Returns the sparse DB row for a single encoding preset by its <see cref="Ulid"/> id.
    /// </summary>
    [HttpGet("{id:ulid}")]
    public async Task<IActionResult> Get(Ulid id, CancellationToken ct)
    {
        EncodingPreset? preset = await presetRepository.GetByIdAsync(id);
        if (preset is null)
            return NotFoundResponse("Preset not found.");

        return Ok(
            new EncoderProfileDto
            {
                Id = preset.Id,
                Name = preset.Name,
                Description = preset.Description,
                Tags = preset.Tags,
                ParentPresetId = preset.ParentPresetId,
                IsBuiltIn = preset.IsBuiltIn,
                Source = preset.Source,
                ProfileJson = preset.ProfileJson,
            }
        );
    }

    /// <summary>
    /// Resolves a preset by walking its parent chain and merging all layers,
    /// returning the fully-effective <see cref="V2EncodingProfile"/>.
    /// </summary>
    [HttpGet("{id:ulid}/resolved")]
    public IActionResult GetResolved(Ulid id)
    {
        DbPresetLookup lookup = new(mediaContext);
        V2EncodingProfile resolved;
        try
        {
            resolved = V2PresetResolver.Resolve(id, lookup);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequestResponse(ex.Message);
        }

        V2ProfileValidationResult validation = V2ProfileValidator.Validate(resolved);
        if (!validation.IsValid)
            return UnprocessableEntityResponse(string.Join("; ", validation.Errors));

        return Ok(resolved);
    }

    /// <summary>
    /// Creates a new user-owned encoding profile. <c>IsBuiltIn</c> is always
    /// stamped <c>false</c> regardless of what the caller sends.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateEncoderProfileRequest request)
    {
        EncoderProfileService.CreateResult result = await encoderProfileService.CreateAsync(
            request.Name,
            request.ProfileJson,
            request.Description,
            request.Author,
            request.Tags,
            request.ParentPresetId,
            User.UserId()
        );

        if (result.IsValidation)
            return BadRequestResponse(result.ErrorMessage!);

        if (result.IsConflict)
            return ConflictResponse(result.ErrorMessage!);

        return Ok(result.Saved);
    }

    /// <summary>
    /// Returns the distinct set of tags in use across all profiles, sorted
    /// alphabetically. Tags are stored as comma-separated values on each row.
    /// </summary>
    [HttpGet("tags")]
    public async Task<IActionResult> Tags()
    {
        IReadOnlyList<string> tags = await presetRepository.GetAllTagsAsync();
        return Ok(new { data = tags });
    }

    /// <summary>
    /// Resolves a profile by walking its parent chain via
    /// <see cref="INamePresetResolver"/>, returning the fully-merged
    /// <see cref="EncodingProfile"/>. Superseded by
    /// <c>GET /{id:ulid}/resolved</c> which uses the V2 resolver and validates
    /// the merged result before returning.
    /// </summary>
    [Obsolete("Use GET /{id:ulid}/resolved — V2 resolver with post-merge validation.")]
    [HttpGet("{id}/resolve")]
    public async Task<IActionResult> Resolve(
        string id,
        [FromServices] INamePresetResolver presetResolver
    )
    {
        if (!Ulid.TryParse(id, out Ulid presetId))
            return BadRequestResponse("Invalid profile id");

        EncodingPreset? leaf = await presetRepository.GetByIdAsync(presetId);
        if (leaf is null)
            return NotFoundResponse("Profile not found");

        string? parentName = null;
        if (leaf.ParentPresetId is Ulid parentId)
        {
            EncodingPreset? parent = await presetRepository.GetByIdAsync(parentId);
            parentName = parent?.Name;
        }

        PresetResolveRequest resolveRequest = new(leaf.Name, leaf.ProfileJson, parentName);

        try
        {
            EncodingProfile resolved = presetResolver.Resolve(
                resolveRequest,
                new RepositoryNamePresetLookup(presetRepository)
            );
            return Ok(resolved);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequestResponse($"Profile could not be resolved: {ex.Message}");
        }
    }

    /// <summary>
    /// Permanently deletes a user-created preset. Rejects deletion when the
    /// preset is built-in or when other presets inherit from it — callers must
    /// reparent or delete children first.
    /// </summary>
    [HttpDelete("{id:ulid}")]
    public async Task<IActionResult> Delete(Ulid id, CancellationToken ct)
    {
        EncodingPreset? row = await presetRepository.GetByIdAsync(id);
        if (row is null)
            return NotFoundResponse("Preset not found.");
        if (row.IsBuiltIn)
            return BadRequestResponse("Built-in presets cannot be deleted.");

        if (await presetRepository.HasChildrenAsync(id, ct))
            return BadRequestResponse(
                "Preset has children that inherit from it; reparent or delete them first."
            );

        await presetRepository.DeleteAsync(id);
        return NoContent();
    }

    /// <summary>
    /// Validates an encoding profile and returns a structured envelope with
    /// errors (blocking) and warnings (non-blocking) bucketed separately.
    ///
    /// Always returns 200 — <c>valid: false</c> flows in the body. Validation
    /// informs; it never gates the HTTP request.
    ///
    /// Note: uses the V2.5 <see cref="IProfileValidator"/> pipeline. V2 profiles
    /// stored via <c>PUT /{id:ulid}</c> are validated by
    /// <see cref="V2ProfileValidator"/> inside <c>GET /{id:ulid}/resolved</c>.
    /// </summary>
    [HttpPost("validate")]
    public IActionResult Validate([FromBody] ValidateEncoderProfileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProfileJson))
        {
            ValidationEnvelope empty = ValidationEnvelope.FromRules([
                new(
                    EncoderRuleId.ProfileNameMissing,
                    EncoderRuleSeverity.Error,
                    "profile_json",
                    "profile_json is required",
                    "Supply the full profile JSON in the profile_json field."
                ),
            ]);
            return Ok(empty);
        }

        EncodingProfile? profile;
        try
        {
            profile = JsonConvert.DeserializeObject<EncodingProfile>(request.ProfileJson);
        }
        catch (JsonException ex)
        {
            ValidationEnvelope parseError = ValidationEnvelope.FromRules([
                new(
                    EncoderRuleId.ProfileNameMissing,
                    EncoderRuleSeverity.Error,
                    "profile_json",
                    $"Profile JSON is malformed: {ex.Message}",
                    "Fix the JSON syntax error and resubmit."
                ),
            ]);
            return Ok(parseError);
        }

        if (profile is null)
        {
            ValidationEnvelope nullError = ValidationEnvelope.FromRules([
                new(
                    EncoderRuleId.ProfileNameMissing,
                    EncoderRuleSeverity.Error,
                    "profile_json",
                    "Profile JSON deserialized to null — check the outer object is present",
                    "Ensure the JSON root is an object, not null or an array."
                ),
            ]);
            return Ok(nullError);
        }

        ValidationEnvelope envelope = profileValidator.ValidateAsEnvelope(profile);
        return Ok(envelope);
    }

    /// <summary>
    /// Returns a per-stream action plan and source-level warnings for the
    /// given profile applied to the given source file.
    ///
    /// <para>Always returns 200 when the source is accessible — warnings surface in the
    /// body as <c>source_warnings</c> chips, never as HTTP error codes.</para>
    /// <para>Returns 404 when the source file cannot be read (via
    /// <see cref="RuntimeErrors.SourceNotAccessible"/>).</para>
    /// </summary>
    [HttpPost("{id}/preview")]
    public async Task<IActionResult> Preview(
        string id,
        [FromBody] PreviewEncoderProfileRequest request,
        CancellationToken ct
    )
    {
        EncoderProfileService.PreviewParseResult parseResult =
            encoderProfileService.ParseProfileForPreview(
                id,
                request.ProfileJson,
                request.SourcePath
            );

        if (parseResult.EarlyResponse is not null)
            return Ok(parseResult.EarlyResponse);

        // PreviewEngine was removed in V2 migration — V2 preview not yet implemented.
        return NotImplementedResponse("Encode preview not yet implemented for V2 profiles.");
    }

    /// <summary>
    /// Updates a user-owned preset. When the preset has a parent, the saved
    /// <see cref="EncodingPreset.ProfileJson"/> is the sparse diff against the
    /// resolved parent rather than the full profile — keeping the inheritance
    /// chain intact and compact.
    /// </summary>
    [HttpPut("{id:ulid}")]
    public async Task<IActionResult> Update(
        Ulid id,
        [FromBody] V2EncodingProfile incoming,
        CancellationToken ct
    )
    {
        EncodingPreset? row = await presetRepository.GetByIdAsync(id);
        if (row is null)
            return NotFoundResponse("Preset not found.");
        if (row.IsBuiltIn)
            return BadRequestResponse("Built-in presets are read-only.");

        Newtonsoft.Json.Linq.JObject sparseJson;
        if (row.ParentPresetId.HasValue)
        {
            DbPresetLookup lookup = new(mediaContext);
            V2EncodingProfile resolvedParent = V2PresetResolver.Resolve(
                row.ParentPresetId.Value,
                lookup
            );
            sparseJson = V2ProfileDiffer.Diff(incoming, resolvedParent);
        }
        else
        {
            sparseJson = Newtonsoft.Json.Linq.JObject.FromObject(incoming);
        }

        string profileJson = sparseJson.ToString(Formatting.None);
        await presetRepository.UpdateAsync(id, preset => preset.ProfileJson = profileJson);

        return NoContent();
    }

    /// <summary>
    /// Clones a preset into a new pure-inheritance child. The clone's
    /// <see cref="EncodingPreset.ProfileJson"/> starts as <c>{}</c> — all
    /// effective values flow from the parent chain — so only intentional
    /// overrides need to be set via PUT.
    /// </summary>
    [HttpPost("{parentId:ulid}/clone")]
    public async Task<IActionResult> Clone(
        Ulid parentId,
        [FromBody] CloneRequest request,
        CancellationToken ct
    )
    {
        EncodingPreset? parent = await presetRepository.GetByIdAsync(parentId);
        if (parent is null)
            return NotFoundResponse("Parent preset not found.");
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequestResponse("Name required.");

        EncodingPreset clone = new()
        {
            Id = Ulid.NewUlid(),
            Name = request.Name,
            Description = request.Description,
            ProfileJson = "{}",
            ParentPresetId = parent.Id,
            IsBuiltIn = false,
            Source = "db",
        };
        await presetRepository.CreateAsync(clone);

        return CreatedAtAction(nameof(Get), new { id = clone.Id }, new { id = clone.Id });
    }

    /// <summary>
    /// Imports an encoding profile from either a raw JSON body or a remote HTTPS URL.
    ///
    /// <para>If the profile carries <c>PublisherKeyFingerprint</c> and <c>Signature</c>
    /// the Ed25519 signature is verified against the <c>trusted_publisher_keys</c> table
    /// before the profile is persisted.</para>
    ///
    /// <para>Unsigned profiles are accepted only when <c>?trust_unsigned=true</c> is
    /// supplied — this is an explicit opt-in so administrators cannot accidentally
    /// import tampered community profiles.</para>
    /// </summary>
    [HttpPost("import")]
    public async Task<IActionResult> Import(
        [FromBody] ImportProfileRequest request,
        [FromQuery] bool trust_unsigned = false,
        CancellationToken ct = default
    )
    {
        EncoderProfileService.ImportResult result = await encoderProfileService.ImportAsync(
            request.ProfileJson,
            request.Url,
            trust_unsigned,
            signatureVerifier,
            User.UserId(),
            ct
        );

        if (result.ValidationError is not null)
            return UnprocessableEntity(result.ValidationError);

        return CreatedAtAction(
            nameof(Import),
            new { id = result.Saved!.Id.ToString() },
            new
            {
                id = result.Saved.Id,
                name = result.Saved.Name,
                profile = result.ImportedProfile,
            }
        );
    }

    /// <summary>
    /// Exports a profile as a JSON file download. The exported JSON includes all
    /// Phase 2.1 fields (<c>PublisherKeyFingerprint</c>, <c>Signature</c>) if
    /// present on the stored profile. Signing is the publisher's responsibility
    /// — this endpoint does not add a signature.
    /// </summary>
    [HttpGet("{id}/export")]
    public async Task<IActionResult> Export(string id)
    {
        if (!Ulid.TryParse(id, out Ulid presetId))
            return BadRequestResponse("Invalid profile id");

        EncodingPreset? preset = await presetRepository.GetByIdAsync(presetId);
        if (preset is null)
            return NotFoundResponse("Profile not found");

        string fileName = preset.Name.Replace(" ", "_").Replace("/", "-") + ".json";

        Response.Headers["Content-Disposition"] = $"attachment; filename=\"{fileName}\"";

        return Content(preset.ProfileJson, "application/json");
    }
}
