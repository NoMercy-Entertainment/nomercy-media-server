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
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Media;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Codecs.Definitions;

namespace NoMercy.Api.Controllers.V1.Dashboard.Encoder;

[ApiController]
[Tags("Dashboard Server Encoder Profiles")]
[ApiVersion(1.0)]
[Authorize]
[Route("api/v{version:apiVersion}/dashboard/encoderprofiles", Order = 10)]
public class EncoderController(
    IEncodingPresetRepository encodingPresetRepository,
    CodecRegistry codecRegistry
) : BaseController
{
    [HttpGet]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Index()
    {
        List<EncodingPreset> encodingPresets = await encodingPresetRepository.ListAsync(
            pageSize: int.MaxValue
        );

        return Ok(new { data = encodingPresets });
    }

    /// <remarks>
    /// Deprecated: Use POST /api/v1/encoder/profiles instead.
    /// </remarks>
    [Obsolete("Use POST /api/v1/encoder/profiles")]
    [HttpPost]
    public Task<IActionResult> Create()
    {
        // V2.5 default-profile authoring lived here before the V2 migration
        // dropped the V2.5 EncodingProfile shape. The replacement endpoint
        // (POST /api/v1/encoder/profiles) constructs a V2 profile from a
        // built-in preset clone. This stub returns 410 Gone so any client
        // still hitting the old route discovers the new path quickly.
        IActionResult result = StatusCode(
            StatusCodes.Status410Gone,
            new
            {
                error = "endpoint_removed",
                message = "POST /api/v1/encoder is removed. Use POST /api/v1/encoder/profiles/{parentId}/clone to create a profile from a built-in V2 preset.",
            }
        );
        return Task.FromResult(result);
    }

    [HttpDelete]
    [Route("{id:ulid}")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Destroy(Ulid id)
    {
        try
        {
            bool removed = await encodingPresetRepository.DeleteAsync(id);
            if (!removed)
                return NotFoundResponse("Encoder profile not found");

            return Ok(new StatusResponseDto<string> { Status = "ok", Data = "Profile removed" });
        }
        catch (InvalidOperationException ex)
        {
            return ConflictResponse(ex.Message);
        }
    }

    [HttpGet]
    [Route("containers")]
    [Authorize(Policy = "Moderator")]
    public IActionResult Containers()
    {
        ContainerDto[] containers =
        [
            BuildContainer("HLS (Streaming)", "m3u8", "hls", true, codecRegistry),
            BuildContainer("MKV (Matroska)", "mkv", "mkv", false, codecRegistry),
            BuildContainer("MP4", "mp4", "mp4", false, codecRegistry),
        ];

        return Ok(new DataResponseDto<ContainerDto[]> { Data = containers });
    }

    [HttpGet]
    [Route("framesizes")]
    [Authorize(Policy = "Moderator")]
    public IActionResult FrameSizes()
    {
        VideoQualityDto[] frameSizes =
        [
            new()
            {
                Width = 7680,
                Height = 4320,
                Label = "8K (7680x4320)",
            },
            new()
            {
                Width = 3840,
                Height = 2160,
                Label = "4K (3840x2160)",
            },
            new()
            {
                Width = 2560,
                Height = 1440,
                Label = "1440p (2560x1440)",
            },
            new()
            {
                Width = 1920,
                Height = 1080,
                Label = "1080p (1920x1080)",
            },
            new()
            {
                Width = 1280,
                Height = 720,
                Label = "720p (1280x720)",
            },
            new()
            {
                Width = 854,
                Height = 480,
                Label = "480p (854x480)",
            },
            new()
            {
                Width = 640,
                Height = 360,
                Label = "360p (640x360)",
            },
        ];

        return Ok(new DataResponseDto<VideoQualityDto[]> { Data = frameSizes });
    }

    private static ContainerDto BuildContainer(
        string label,
        string value,
        string type,
        bool isDefault,
        CodecRegistry registry
    )
    {
        return new()
        {
            Label = label,
            Value = value,
            Type = type,
            IsDefault = isDefault,
            AvailableVideoCodecs = VideoCodecs(registry),
            AvailableAudioCodecs = AudioCodecs(),
            AvailableSubtitleCodecs = SubtitleCodecs(),
            AvailableVideoSizes =
            [
                new()
                {
                    Width = 3840,
                    Height = 2160,
                    Label = "4K",
                },
                new()
                {
                    Width = 1920,
                    Height = 1080,
                    Label = "1080p",
                },
                new()
                {
                    Width = 1280,
                    Height = 720,
                    Label = "720p",
                },
                new()
                {
                    Width = 854,
                    Height = 480,
                    Label = "480p",
                },
            ],
        };
    }

    private static VideoCodecDto[] VideoCodecs(CodecRegistry registry)
    {
        VideoCodecType[] videoTypes =
        [
            VideoCodecType.H264,
            VideoCodecType.H265,
            VideoCodecType.Av1,
            VideoCodecType.Vp9,
        ];

        return
        [
            .. videoTypes.Select(vt =>
            {
                ICodecDefinition def = registry.GetVideoDefinition(vt);
                EncoderInfo sw = def.Encoders.First(e => e.RequiredVendor is null);

                return new VideoCodecDto
                {
                    Name = vt.ToString(),
                    Value = sw.FfmpegName,
                    SimpleValue = vt.ToString().ToLowerInvariant(),
                    RequiresGpu = false,
                    IsDefault = vt == VideoCodecType.H264,
                    AvailablePresets = [.. sw.Presets.Select(p => new LabelValueDto(p))],
                    AvailableVideoProfiles = [.. sw.Profiles.Select(p => new LabelValueDto(p))],
                };
            }),
        ];
    }

    private static AudioCodecDto[] AudioCodecs()
    {
        AudioCodecType[] audioTypes =
        [
            AudioCodecType.Aac,
            AudioCodecType.Opus,
            AudioCodecType.Flac,
            AudioCodecType.Ac3,
            AudioCodecType.Eac3,
            AudioCodecType.Mp3,
        ];

        return
        [
            .. audioTypes.Select(at =>
            {
                AudioEncoderInfo enc = AudioCodecDefinitions.GetEncoder(at);
                return new AudioCodecDto
                {
                    Name = at.ToString(),
                    Value = enc.FfmpegName,
                    SimpleValue = at.ToString().ToLowerInvariant(),
                    IsDefault = at == AudioCodecType.Aac,
                };
            }),
        ];
    }

    private static SubtitleCodecDto[] SubtitleCodecs() =>
        [
            new()
            {
                Name = "WebVTT",
                Value = "webvtt",
                SimpleValue = "webvtt",
                IsDefault = true,
            },
            new()
            {
                Name = "ASS",
                Value = "ass",
                SimpleValue = "ass",
            },
            new()
            {
                Name = "SRT",
                Value = "srt",
                SimpleValue = "srt",
            },
        ];
}
