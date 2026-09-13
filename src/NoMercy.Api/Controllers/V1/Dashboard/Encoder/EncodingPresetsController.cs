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
using Newtonsoft.Json;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Media;
using NoMercy.Encoder.Errors;
using NoMercy.Encoder.Profiles;
using NoMercy.NmSystem.Networking;

namespace NoMercy.Api.Controllers.V1.Dashboard.Encoder;

[ApiController]
[Tags("Dashboard Encoding Presets")]
[ApiVersion(1.0)]
[Authorize]
[Route("api/v{version:apiVersion}/dashboard/encoding/presets")]
public class EncodingPresetsController(
    IEncodingPresetRepository presetRepository,
    INamePresetResolver presetResolver,
    IProfileValidator profileValidator,
    IHttpClientFactory httpClientFactory
) : BaseController
{
    [Obsolete("Use GET /api/v1/encoder/profiles")]
    [HttpGet]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> List(
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

    [Obsolete("Use GET /api/v1/encoder/profiles/{id}")]
    [HttpGet("{id}")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Get(string id)
    {
        if (!Ulid.TryParse(id, out Ulid presetId))
            return BadRequestResponse("Invalid preset id");

        EncodingPreset? preset = await presetRepository.GetByIdAsync(presetId);
        if (preset is null)
            return NotFoundResponse("Preset not found");

        return Ok(preset);
    }

    [Obsolete("Use POST /api/v1/encoder/profiles")]
    [HttpPost]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Create([FromBody] CreatePresetRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequestResponse("name is required");
        if (string.IsNullOrWhiteSpace(request.ProfileJson))
            return BadRequestResponse("profile_json is required");

        EncodingPreset? existing = await presetRepository.GetByNameAsync(request.Name);
        if (existing is not null)
            return ConflictResponse($"A preset named '{request.Name}' already exists");

        EncodingPreset preset = new()
        {
            Name = request.Name,
            Description = request.Description,
            Author = request.Author,
            Tags = request.Tags,
            ProfileJson = request.ProfileJson,
            ParentPresetId = request.ParentPresetId,
            IsBuiltIn = false,
        };

        EncodingPreset saved = await presetRepository.CreateAsync(preset);
        return Ok(saved);
    }

    [Obsolete("Use PUT /api/v1/encoder/profiles/{id}")]
    [HttpPut("{id}")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdatePresetRequest request)
    {
        if (!Ulid.TryParse(id, out Ulid presetId))
            return BadRequestResponse("Invalid preset id");

        // Guard: load the row first so we can return a structured 422 for
        // built-in presets instead of the generic ConflictResponse the repo
        // would otherwise produce via InvalidOperationException.
        EncodingPreset? existing = await presetRepository.GetByIdAsync(presetId);
        if (existing is null)
            return NotFoundResponse("Preset not found");

        if (existing.IsBuiltIn)
        {
            ValidationEnvelope envelope = ValidationEnvelope.FromRules([
                new(
                    EncoderRuleId.ProfileBuiltinReadonly,
                    EncoderRuleSeverity.Error,
                    "id",
                    "Built-in presets are read-only — clone the preset to edit it.",
                    $"POST /api/v1/dashboard/encoding/presets/{id}/clone to make an editable copy."
                ),
            ]);
            return UnprocessableEntity(envelope);
        }

        try
        {
            EncodingPreset? updated = await presetRepository.UpdateAsync(
                presetId,
                preset =>
                {
                    if (request.Name is not null)
                        preset.Name = request.Name;
                    if (request.Description is not null)
                        preset.Description = request.Description;
                    if (request.Author is not null)
                        preset.Author = request.Author;
                    if (request.Tags is not null)
                        preset.Tags = request.Tags;
                    if (request.ProfileJson is not null)
                        preset.ProfileJson = request.ProfileJson;
                    if (request.ParentPresetId.HasValue)
                        preset.ParentPresetId = request.ParentPresetId.Value;
                }
            );

            if (updated is null)
                return NotFoundResponse("Preset not found");

            return Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return ConflictResponse(ex.Message);
        }
    }

    [Obsolete("Use GET /api/v1/encoder/profiles/tags")]
    [HttpGet("tags")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> ListAllTags()
    {
        IReadOnlyList<string> tags = await presetRepository.GetAllTagsAsync();
        return Ok(new { data = tags });
    }

    [Obsolete("Use POST /api/v1/encoder/profiles/{id}/clone")]
    [HttpPost("{id}/clone")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Clone(string id, [FromBody] ClonePresetRequest request)
    {
        if (!Ulid.TryParse(id, out Ulid presetId))
            return BadRequestResponse("Invalid preset id");

        EncodingPreset? source = await presetRepository.GetByIdAsync(presetId);
        if (source is null)
            return NotFoundResponse("Preset not found");

        string name = string.IsNullOrWhiteSpace(request.Name)
            ? await FindUnusedCloneNameAsync(source.Name)
            : request.Name;

        if (await presetRepository.GetByNameAsync(name) is not null)
            return ConflictResponse($"A preset named '{name}' already exists");

        EncodingPreset clone = new()
        {
            Name = name,
            Description = source.Description,
            Author = request.Author ?? source.Author,
            Tags = source.Tags,
            ProfileJson = source.ProfileJson,
            ParentPresetId = source.ParentPresetId,
            // Cloning a built-in produces an editable user preset. That's
            // the whole point — lets users tweak base presets without
            // touching the seeded rows.
            IsBuiltIn = false,
        };

        EncodingPreset saved = await presetRepository.CreateAsync(clone);
        return Ok(saved);
    }

    private async Task<string> FindUnusedCloneNameAsync(string sourceName)
    {
        string candidate = $"{sourceName} (copy)";
        int suffix = 1;
        while (await presetRepository.GetByNameAsync(candidate) is not null)
        {
            suffix++;
            candidate = $"{sourceName} (copy {suffix})";
        }
        return candidate;
    }

    [Obsolete("Use GET /api/v1/encoder/profiles/{id}/resolve")]
    [HttpGet("{id}/resolve")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Resolve(string id)
    {
        if (!Ulid.TryParse(id, out Ulid presetId))
            return BadRequestResponse("Invalid preset id");

        EncodingPreset? leaf = await presetRepository.GetByIdAsync(presetId);
        if (leaf is null)
            return NotFoundResponse("Preset not found");

        string? parentName = await ResolveParentNameAsync(leaf.ParentPresetId);
        PresetResolveRequest request = new(leaf.Name, leaf.ProfileJson, parentName);

        try
        {
            EncodingProfile resolved = presetResolver.Resolve(
                request,
                new RepositoryNamePresetLookup(presetRepository)
            );
            return Ok(resolved);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequestResponse($"Preset could not be resolved: {ex.Message}");
        }
    }

    [Obsolete("Use GET /api/v1/encoder/profiles/{id}/export")]
    [HttpGet("{id}/export")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Export(string id)
    {
        if (!Ulid.TryParse(id, out Ulid presetId))
            return BadRequestResponse("Invalid preset id");

        EncodingPreset? preset = await presetRepository.GetByIdAsync(presetId);
        if (preset is null)
            return NotFoundResponse("Preset not found");

        PresetExport export = new(
            Name: preset.Name,
            Description: preset.Description,
            Author: preset.Author,
            Tags: preset.Tags,
            ProfileJson: preset.ProfileJson
        );

        return Ok(export);
    }

    [Obsolete("Use POST /api/v1/encoder/profiles/import")]
    [HttpPost("import")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Import([FromBody] PresetExport import)
    {
        if (string.IsNullOrWhiteSpace(import.Name))
            return BadRequestResponse("name is required");
        if (string.IsNullOrWhiteSpace(import.ProfileJson))
            return BadRequestResponse("profile_json is required");

        // Collision rename: append "(imported N)" until we find an unused name.
        // Users can rename afterwards — we'd rather keep both copies than
        // silently overwrite an existing preset.
        string finalName = import.Name;
        int suffix = 1;
        while (await presetRepository.GetByNameAsync(finalName) is not null)
        {
            finalName = $"{import.Name} (imported {suffix++})";
        }

        EncodingPreset preset = new()
        {
            Name = finalName,
            Description = import.Description,
            Author = import.Author,
            Tags = import.Tags,
            ProfileJson = import.ProfileJson,
            IsBuiltIn = false,
        };

        EncodingPreset saved = await presetRepository.CreateAsync(preset);
        return Ok(saved);
    }

    [Obsolete("Use POST /api/v1/encoder/profiles/import")]
    [HttpPost("import-url")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> ImportFromUrl(
        [FromBody] ImportFromUrlRequest request,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(request.Url))
            return BadRequestResponse("url is required");

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out Uri? parsed))
            return BadRequestResponse("url is not a valid absolute URL");

        // Only allow HTTPS for community presets — HTTP means a MITM attacker
        // can inject arbitrary encoding profiles onto the server. HTTPS shifts
        // trust to certificate validation, which is what we want.
        if (parsed.Scheme != Uri.UriSchemeHttps)
            return BadRequestResponse("Only https:// URLs are supported for preset imports");

        // Even over https, don't let a Moderator make the server fetch an internal
        // host (LAN / loopback / cloud link-local metadata) — reject non-public hosts.
        if (!await ServerSideRequestGuard.IsSafePublicHttpUrlAsync(request.Url, ct))
            return BadRequestResponse("Preset URL must resolve to a publicly routable host");

        PresetExport? export;
        try
        {
            HttpClient client = httpClientFactory.CreateClient("preset-import");
            client.Timeout = TimeSpan.FromSeconds(15);
            string body = await client.GetStringAsync(parsed, ct);
            export = JsonConvert.DeserializeObject<PresetExport>(body);
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return BadRequestResponse($"Could not fetch or parse preset from URL: {ex.Message}");
        }

        if (export is null || string.IsNullOrWhiteSpace(export.Name))
            return BadRequestResponse("URL response did not contain a valid preset payload");

        return await Import(export);
    }

    private async Task<string?> ResolveParentNameAsync(Ulid? parentId)
    {
        if (parentId is null)
            return null;

        EncodingPreset? parent = await presetRepository.GetByIdAsync(parentId.Value);
        return parent?.Name;
    }

    /// <summary>
    /// Validates a preset's profile JSON at save time so the UI can surface
    /// errors and warnings before the user ships a broken preset into an
    /// encode. Uses the canonical <see cref="IProfileValidator"/> — same
    /// rules the encode pipeline enforces at run time — so what validates
    /// here will not be rejected during encoding.
    ///
    /// Response shape:
    ///   {
    ///     valid: bool,          // true when no ERROR-severity issues
    ///     errors: [{ field, message }],
    ///     warnings: [{ field, message }]
    ///   }
    ///
    /// Warnings never block. They flag the "this will encode but might not
    /// be what you want" cases (preset mismatch, inverted ABR ladder,
    /// segment-keyframe misalignment, etc).
    /// </summary>
    /// <summary>
    /// Validates an encoding profile.
    /// </summary>
    /// <remarks>
    /// Deprecated: Use POST /api/v1/encoder/profiles/validate for the richer
    /// <see cref="NoMercy.Encoder.Errors.ValidationEnvelope"/> shape with stable rule IDs and fix hints.
    /// </remarks>
    [Obsolete("Use POST /api/v1/encoder/profiles/validate")]
    [HttpPost("validate")]
    [Authorize(Policy = "Moderator")]
    public IActionResult Validate([FromBody] ValidatePresetRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProfileJson))
            return BadRequestResponse("profile_json is required");

        EncodingProfile? profile;
        try
        {
            profile = JsonConvert.DeserializeObject<EncodingProfile>(request.ProfileJson);
        }
        catch (JsonException ex)
        {
            return Ok(
                new
                {
                    valid = false,
                    errors = new[]
                    {
                        new
                        {
                            field = "ProfileJson",
                            message = $"Profile JSON is malformed: {ex.Message}",
                        },
                    },
                    warnings = Array.Empty<object>(),
                }
            );
        }

        if (profile is null)
        {
            return Ok(
                new
                {
                    valid = false,
                    errors = new[]
                    {
                        new
                        {
                            field = "ProfileJson",
                            message = "Profile JSON deserialized to null — check the outer object is present",
                        },
                    },
                    warnings = Array.Empty<object>(),
                }
            );
        }

        // Forward to the new envelope-producing path, then project back to the
        // legacy (IsValid, ValidationError[]) shape so existing dashboard
        // clients keep working without modification.
        ValidationEnvelope envelope = profileValidator.ValidateAsEnvelope(profile);

        object[] errors =
        [
            .. envelope.Errors.Select(e => (object)new { field = e.Field, message = e.Message }),
        ];
        object[] warnings =
        [
            .. envelope.Warnings.Select(e => (object)new { field = e.Field, message = e.Message }),
        ];

        return Ok(
            new
            {
                valid = envelope.Valid,
                errors,
                warnings,
            }
        );
    }

    /// <summary>
    /// Previews what the encoder will do with a specific source file under a
    /// profile. Returns the per-stream plan: copy / transcode / extract /
    /// drop, plus a human-readable rationale for each decision. Lets the UI
    /// tell users things like "your DTS 5.1 track will be transcoded to AAC
    /// stereo because HLS doesn't carry DTS" before they kick off a job.
    ///
    /// Accepts any ffmpeg-parseable input — source codec/container
    /// combinations that aren't in our output set still work, we just
    /// transcode them automatically.
    /// </summary>
    [Obsolete("Use POST /api/v1/encoder/profiles/{id}/preview")]
    [HttpPost("preview")]
    public Task<IActionResult> Preview([FromBody] PreviewRequest request, CancellationToken ct)
    {
        return Task.FromResult<IActionResult>(
            StatusCode(
                501,
                new
                {
                    error = "This endpoint has been superseded by POST /api/v1/encoder/profiles/{id}/preview",
                }
            )
        );
    }

    [Obsolete("Use DELETE /api/v1/encoder/profiles/{id}")]
    [HttpDelete("{id}")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Delete(string id)
    {
        if (!Ulid.TryParse(id, out Ulid presetId))
            return BadRequestResponse("Invalid preset id");

        try
        {
            bool removed = await presetRepository.DeleteAsync(presetId);
            if (!removed)
                return NotFoundResponse("Preset not found");

            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return ConflictResponse(ex.Message);
        }
    }
}

public record CreatePresetRequest(
    string Name,
    string ProfileJson,
    string? Description = null,
    string? Author = null,
    string? Tags = null,
    Ulid? ParentPresetId = null
);

public record PresetExport(
    string Name,
    string ProfileJson,
    string? Description = null,
    string? Author = null,
    string? Tags = null
);

public record ImportFromUrlRequest(string Url);

public record ClonePresetRequest(string? Name = null, string? Author = null);

public record ValidatePresetRequest(string ProfileJson);

public record PreviewRequest(
    [property: JsonProperty("profile_json")] string ProfileJson,
    [property: JsonProperty("video_file_id")] string VideoFileId
);

public record UpdatePresetRequest(
    string? Name = null,
    string? Description = null,
    string? Author = null,
    string? Tags = null,
    string? ProfileJson = null,
    Ulid? ParentPresetId = null
);
