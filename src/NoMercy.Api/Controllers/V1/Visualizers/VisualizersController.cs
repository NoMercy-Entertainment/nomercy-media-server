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
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Media;
using NoMercy.Api.DTOs.Visualizers;
using NoMercy.Api.Services.Visualizers;
using NoMercy.Authorization;
using NoMercy.Data.Visualizers;
using NoMercy.Database.Models.Media;
using NoMercy.Encoder.Errors;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.Controllers.V1.Visualizers;

[ApiController]
[Tags("Visualizers")]
[ApiVersion(1.0)]
[Authorize]
[Route("api/v{version:apiVersion}/visualizers")]
public class VisualizersController(IVisualizerRepository repository) : BaseController
{
    private const int MaxBodyBytes = 262144;
    private const int MaxImportFavorites = 500;
    private const int MaxImportRecents = 10;
    private const int MaxImportValue = 128;

    private bool IsModerator => AuthPolicy.IsModerator(User);

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] PageRequestDto request,
        [FromQuery] string[] format,
        [FromQuery] string? tag = null,
        [FromQuery] string? author = null,
        [FromQuery] string? q = null,
        [FromQuery] bool favorites = false,
        [FromQuery] string sort = VisualizerListQuery.SortName
    )
    {
        string[] formats = format
            .SelectMany(f =>
                f.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            )
            .ToArray();
        if (formats.Any(f => !VisualizerPayloadValidator.IsKnownFormat(f)))
            return BadRequestResponse("Unknown format.");
        if (
            sort
            is not (
                VisualizerListQuery.SortName
                or VisualizerListQuery.SortAuthor
                or VisualizerListQuery.SortRecent
            )
        )
            return BadRequestResponse("Unknown sort.");

        Guid userId = User.UserId();
        VisualizerPage result = await repository.ListAsync(
            new(
                userId,
                IsModerator,
                formats,
                tag,
                author,
                q,
                favorites,
                sort,
                request.Page,
                request.Take
            )
        );

        Response.Headers.CacheControl = "private";
        return Ok(
            new PaginatedResponse<VisualizerSummaryDto>
            {
                Data = result.Items.Select(v =>
                    VisualizerSummaryDto.From(v, result.Links.GetValueOrDefault(v.Id))
                ),
                NextPage = result.HasMore ? request.Page + 1 : null,
                HasMore = result.HasMore,
            }
        );
    }

    [HttpGet("bundle")]
    public async Task<IActionResult> GetBundleVersion()
    {
        VisualizerBundleVersion version = await repository.GetBundleVersionAsync();

        string etag = ETagOf(version.BundleHash);
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "private, no-cache";
        if (Matches(Request.Headers.IfNoneMatch, etag))
            return StatusCode(StatusCodes.Status304NotModified);

        return Ok(
            new VisualizerBundleVersionDto
            {
                BundleHash = version.BundleHash,
                Count = version.Count,
                UpdatedAt = version.UpdatedAt is { } at
                    ? DateTime.SpecifyKind(at, DateTimeKind.Utc)
                    : null,
            }
        );
    }

    [HttpGet("{id:ulid}")]
    public async Task<IActionResult> Get(Ulid id)
    {
        Visualizer? entity = await repository.GetAsync(id, IsModerator);
        if (entity is null)
            return NotFoundResponse("Visualizer not found.");

        string etag = ETagOf(entity.ContentHash);
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "private, no-cache";
        if (Matches(Request.Headers.IfNoneMatch, etag))
            return StatusCode(StatusCodes.Status304NotModified);

        return Ok(VisualizerDto.From(entity));
    }

    [HttpPost]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Create()
    {
        (JObject? body, IActionResult? failure) = await ReadBodyAsync(required: true);
        if (failure is not null)
            return failure;

        CreateVisualizerRequest request = body!.ToObject<CreateVisualizerRequest>() ?? new();
        List<EncoderRule> rules = VisualizerMetadataValidator.ValidateFormat(request.Format);
        rules.AddRange(
            VisualizerMetadataValidator.Validate(
                request.Name,
                request.Author,
                request.License,
                request.SourceUrl,
                request.Tags,
                nameRequired: true
            )
        );
        if (rules.Count == 0)
            rules.AddRange(VisualizerPayloadValidator.Validate(request.Payload, request.Format!));
        if (rules.Count > 0)
            return UnprocessableEntity(ValidationEnvelope.FromRules(rules));

        Visualizer entity = new()
        {
            Slug = VisualizerSlug.Create(request.Format!, request.Name!.Trim()),
            Name = request.Name.Trim(),
            Author = request.Author!.Trim(),
            License = request.License!.Trim(),
            SourceUrl = request.SourceUrl,
            Format = request.Format!,
            Tags = JoinTags(request.Tags),
            ContentHash = "",
            PayloadJson = "",
        };
        ApplyPayload(entity, request.Payload!);

        Visualizer saved = await repository.CreateAsync(entity);
        return Created($"/api/v1/visualizers/{saved.Id}", VisualizerDto.From(saved));
    }

    [HttpPut("{id:ulid}")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Update(Ulid id)
    {
        Visualizer? existing = await repository.GetAsync(id, true);
        if (existing is null)
            return NotFoundResponse("Visualizer not found.");
        if (existing.IsBuiltIn)
            return BuiltInRefusal(id);
        if (
            Request.Headers.IfMatch.Count > 0
            && !Matches(Request.Headers.IfMatch, ETagOf(existing.ContentHash))
        )
            return PreconditionFailedResponse("The visualizer changed since it was read.");

        (JObject? body, IActionResult? failure) = await ReadBodyAsync(required: true);
        if (failure is not null)
            return failure;

        UpdateVisualizerRequest request = body!.ToObject<UpdateVisualizerRequest>() ?? new();
        List<EncoderRule> rules = VisualizerMetadataValidator.Validate(
            request.Name,
            request.Author,
            request.License,
            request.SourceUrl,
            request.Tags,
            nameRequired: false
        );
        if (request.Payload is not null && request.Payload.Type != JTokenType.Null)
            rules.AddRange(VisualizerPayloadValidator.Validate(request.Payload, existing.Format));
        if (rules.Count > 0)
            return UnprocessableEntity(ValidationEnvelope.FromRules(rules));

        Visualizer? updated = await repository.UpdateAsync(
            id,
            entity =>
            {
                if (request.Name is not null)
                    entity.Name = request.Name.Trim();
                if (request.Author is not null)
                    entity.Author = request.Author.Trim();
                if (request.License is not null)
                    entity.License = request.License.Trim();
                if (request.SourceUrl is not null)
                    entity.SourceUrl = request.SourceUrl;
                if (request.Tags is not null)
                    entity.Tags = JoinTags(request.Tags);
                if (request.Payload is not null && request.Payload.Type != JTokenType.Null)
                    ApplyPayload(entity, request.Payload);
            }
        );
        if (updated is null)
            return NotFoundResponse("Visualizer not found.");

        Response.Headers.ETag = ETagOf(updated.ContentHash);
        return NoContent();
    }

    [HttpDelete("{id:ulid}")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Delete(Ulid id)
    {
        Visualizer? existing = await repository.GetAsync(id, true);
        if (existing is null)
            return NotFoundResponse("Visualizer not found.");
        if (existing.IsBuiltIn)
            return BuiltInRefusal(id);
        if (
            Request.Headers.IfMatch.Count > 0
            && !Matches(Request.Headers.IfMatch, ETagOf(existing.ContentHash))
        )
            return PreconditionFailedResponse("The visualizer changed since it was read.");

        await repository.DeleteAsync(id);
        return NoContent();
    }

    [HttpPost("{id:ulid}/clone")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Clone(Ulid id)
    {
        Visualizer? source = await repository.GetAsync(id, true);
        if (source is null)
            return NotFoundResponse("Visualizer not found.");

        (JObject? body, IActionResult? failure) = await ReadBodyAsync(required: false);
        if (failure is not null)
            return failure;

        CloneVisualizerRequest request = body?.ToObject<CloneVisualizerRequest>() ?? new();
        string name = string.IsNullOrWhiteSpace(request.Name)
            ? $"{(source.Name.Length > 240 ? source.Name[..240] : source.Name)} (copy)"
            : request.Name.Trim();
        string author = string.IsNullOrWhiteSpace(request.Author)
            ? source.Author
            : request.Author.Trim();

        List<EncoderRule> rules = VisualizerMetadataValidator.Validate(
            name,
            author,
            null,
            null,
            null,
            false
        );
        if (rules.Count > 0)
            return UnprocessableEntity(ValidationEnvelope.FromRules(rules));

        Visualizer clone = new()
        {
            Slug = VisualizerSlug.Create(source.Format, name),
            Name = name,
            Author = author,
            License = source.License,
            SourceUrl = source.SourceUrl,
            SourceSha1 = source.SourceSha1,
            Format = source.Format,
            Tags = source.Tags,
            Textures = source.Textures,
            Schema = source.Schema,
            ContentHash = source.ContentHash,
            PayloadBytes = source.PayloadBytes,
            PayloadJson = source.PayloadJson,
            RenderableWebgl2 = source.RenderableWebgl2,
        };

        Visualizer saved = await repository.CreateAsync(clone);
        return Created($"/api/v1/visualizers/{saved.Id}", VisualizerDto.From(saved));
    }

    [HttpPut("{id:ulid}/enabled")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> SetEnabled(Ulid id)
    {
        (JObject? body, IActionResult? failure) = await ReadBodyAsync(required: true);
        if (failure is not null)
            return failure;
        if (body!.ToObject<SetVisualizerEnabledRequest>()?.Enabled is not { } enabled)
            return BadRequestResponse("enabled is required.");

        return await repository.SetEnabledAsync(id, enabled)
            ? NoContent()
            : NotFoundResponse("Visualizer not found.");
    }

    [HttpPut("{id:ulid}/favorite")]
    public Task<IActionResult> AddFavorite(Ulid id) => SetFavorite(id, true);

    [HttpDelete("{id:ulid}/favorite")]
    public Task<IActionResult> RemoveFavorite(Ulid id) => SetFavorite(id, false);

    private async Task<IActionResult> SetFavorite(Ulid id, bool favorite)
    {
        VisualizerWriteResult result = await repository.SetFavoriteAsync(
            User.UserId(),
            id,
            favorite,
            IsModerator
        );
        return result switch
        {
            VisualizerWriteResult.NotFound => NotFoundResponse("Visualizer not found."),
            VisualizerWriteResult.LimitReached => UnprocessableEntity(
                ValidationEnvelope.FromRules([
                    new(
                        VisualizerRuleId.SchemaInvalid,
                        EncoderRuleSeverity.Error,
                        "/id",
                        "A user holds at most 500 favorites.",
                        "Remove a favorite first."
                    ),
                ])
            ),
            _ => NoContent(),
        };
    }

    [HttpGet("current")]
    public async Task<IActionResult> GetCurrent()
    {
        VisualizerChoiceState state = await repository.GetChoiceAsync(User.UserId());
        return Ok(ToChoice(state));
    }

    [HttpPut("current")]
    public async Task<IActionResult> SetCurrent([FromBody] SetVisualizerChoiceRequest request)
    {
        Ulid? id = null;
        if (request.VisualizerId is not null)
        {
            if (!Ulid.TryParse(request.VisualizerId, out Ulid parsed))
                return BadRequestResponse("visualizer_id is not a valid id.");
            id = parsed;
        }

        Guid userId = User.UserId();
        if (await repository.SetChoiceAsync(userId, id) == VisualizerWriteResult.NotFound)
            return NotFoundResponse("Visualizer not found.");

        return Ok(ToChoice(await repository.GetChoiceAsync(userId)));
    }

    [HttpPost("import-device-state")]
    public async Task<IActionResult> ImportDeviceState()
    {
        (JObject? body, IActionResult? failure) = await ReadBodyAsync(required: true);
        if (failure is not null)
            return failure;

        ImportDeviceStateRequest request = body!.ToObject<ImportDeviceStateRequest>() ?? new();
        List<string> favorites = request.Favorites ?? [];
        List<string> recents = request.Recents ?? [];
        if (favorites.Count > MaxImportFavorites || recents.Count > MaxImportRecents)
            return BadRequestResponse("Too many favorites or recents.");
        if (
            favorites
                .Concat(recents)
                .Append(request.Choice ?? "")
                .Any(v => v.Length > MaxImportValue)
        )
            return BadRequestResponse("A value is longer than 128 characters.");

        VisualizerImportResult result = await repository.ImportDeviceStateAsync(
            User.UserId(),
            request.Choice,
            favorites,
            recents
        );
        return Ok(
            new ImportDeviceStateResponse
            {
                ChoiceApplied = result.ChoiceApplied,
                FavoritesAdded = result.FavoritesAdded,
                RecentsAdded = result.RecentsAdded,
                Skipped = result
                    .Skipped.Select(s => new ImportSkippedDto
                    {
                        Value = s.Value,
                        Reason = s.Reason,
                    })
                    .ToList(),
            }
        );
    }

    [HttpGet("textures/{name}")]
    public IActionResult GetTexture(string name)
    {
        VisualizerTexture? texture = VisualizerTextureStore.Find(name);
        if (texture is null)
            return NotFoundResponse("Texture not found.");

        string etag = ETagOf(texture.Sha256);
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        if (Matches(Request.Headers.IfNoneMatch, etag))
            return StatusCode(StatusCodes.Status304NotModified);

        return File(texture.Bytes, texture.ContentType);
    }

    [HttpGet("{id:ulid}/preview/{kind}")]
    public async Task<IActionResult> GetPreview(Ulid id, string kind)
    {
        VisualizerPreview? preview = VisualizerPreviewStore.Find(id, kind);
        if (preview is null || await repository.GetAsync(id, IsModerator) is null)
            return NotFoundResponse("Preview not found.");

        string etag = ETagOf(preview.Sha256);
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        if (Matches(Request.Headers.IfNoneMatch, etag))
            return StatusCode(StatusCodes.Status304NotModified);

        return File(preview.Bytes, preview.ContentType);
    }

    private static VisualizerChoiceDto ToChoice(VisualizerChoiceState state) =>
        new()
        {
            VisualizerId = state.VisualizerId?.ToString(),
            UpdatedAt = state.UpdatedAt is { } at
                ? DateTime.SpecifyKind(at, DateTimeKind.Utc)
                : null,
        };

    private static string ETagOf(string hash) => $"\"{hash}\"";

    private static bool Matches(StringValues header, string etag) =>
        header
            .SelectMany(h => (h ?? "").Split(',', StringSplitOptions.TrimEntries))
            .Any(v => v == "*" || v == etag || v == "W/" + etag);

    private static string JoinTags(IEnumerable<string>? tags) =>
        string.Join(
            ',',
            (tags ?? []).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
        );

    private static void ApplyPayload(Visualizer entity, JToken payload)
    {
        string text = VisualizerCanonicalJson.Serialize(payload);
        entity.PayloadJson = text;
        entity.ContentHash = VisualizerCanonicalJson.Hash(text);
        entity.PayloadBytes = Encoding.UTF8.GetByteCount(text);
        entity.Schema = 1;
        entity.Textures =
            entity.Format == "milkdrop" && payload["textures"] is JArray textures
                ? string.Join(',', textures.Select(t => t.Value<string>()))
                : "";
    }

    private IActionResult BuiltInRefusal(Ulid id) =>
        UnprocessableEntity(
            ValidationEnvelope.FromRules([
                new(
                    VisualizerRuleId.BuiltinReadonly,
                    EncoderRuleSeverity.Error,
                    "id",
                    "Built-in visualizers are read-only.",
                    $"POST /api/v1/visualizers/{id}/clone to make an editable copy."
                ),
            ])
        );

    /// <summary>Reads the JSON body under the byte cap. Repeated keys are a schema refusal (S5).</summary>
    private async Task<(JObject? Body, IActionResult? Failure)> ReadBodyAsync(bool required)
    {
        if (Request.ContentLength > MaxBodyBytes)
            return (null, RequestEntityTooLargeResponse("The body is over 256 KB."));

        byte[] buffer = new byte[MaxBodyBytes + 1];
        int read = 0;
        while (read < buffer.Length)
        {
            int count = await Request.Body.ReadAsync(
                buffer.AsMemory(read),
                HttpContext.RequestAborted
            );
            if (count == 0)
                break;
            read += count;
        }
        if (read > MaxBodyBytes)
            return (null, RequestEntityTooLargeResponse("The body is over 256 KB."));

        string text = Encoding.UTF8.GetString(buffer, 0, read);
        if (string.IsNullOrWhiteSpace(text))
            return required ? (null, BadRequestResponse("A JSON body is required.")) : (null, null);

        JObject body;
        try
        {
            body = JObject.Parse(text);
        }
        catch (JsonReaderException)
        {
            return (null, BadRequestResponse("The body is not a JSON object."));
        }

        try
        {
            JObject.Parse(
                text,
                new() { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error }
            );
        }
        catch (JsonReaderException)
        {
            return (
                null,
                UnprocessableEntity(
                    ValidationEnvelope.FromRules([
                        new(
                            VisualizerRuleId.SchemaInvalid,
                            EncoderRuleSeverity.Error,
                            "/",
                            "A JSON object repeats a key.",
                            "Send each key once."
                        ),
                    ])
                )
            );
        }

        return (body, null);
    }
}
