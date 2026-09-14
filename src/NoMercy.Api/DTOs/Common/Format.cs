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

using Mono.Nat;
using Newtonsoft.Json;
using NoMercy.Providers.Helpers;

namespace NoMercy.Api.DTOs.Common;

public class Format
{
    [JsonProperty("format_id")]
    public string? FormatId { get; set; }

    [JsonProperty("format_note", NullValueHandling = NullValueHandling.Ignore)]
    public string? FormatNote { get; set; }

    [JsonProperty("ext")]
    public string? Ext { get; set; }

    [JsonProperty("protocol")]
    public Protocol Protocol { get; set; }

    [JsonProperty("acodec", NullValueHandling = NullValueHandling.Ignore)]
    public string? Acodec { get; set; }

    [JsonProperty("vcodec")]
    public string? Vcodec { get; set; }

    [JsonProperty("url")]
    public Uri? Url { get; set; }

    [JsonProperty("width")]
    public long? Width { get; set; }

    [JsonProperty("height")]
    public long? Height { get; set; }

    [JsonProperty("fps")]
    public double? Fps { get; set; }

    [JsonProperty("rows", NullValueHandling = NullValueHandling.Ignore)]
    public long? Rows { get; set; }

    [JsonProperty("columns", NullValueHandling = NullValueHandling.Ignore)]
    public long? Columns { get; set; }

    [JsonProperty("fragments", NullValueHandling = NullValueHandling.Ignore)]
    public Fragment[] Fragments { get; set; } = [];

    [JsonProperty("resolution")]
    public string? Resolution { get; set; }

    [JsonProperty("aspect_ratio")]
    public double? AspectRatio { get; set; }

    [JsonProperty("filesize_approx")]
    public long? FilesizeApprox { get; set; }

    [JsonProperty("http_headers")]
    public HttpHeaders? HttpHeaders { get; set; }

    [JsonProperty("audio_ext")]
    public string? AudioExt { get; set; }

    [JsonProperty("video_ext")]
    public string? VideoExt { get; set; }

    [JsonProperty("vbr")]
    public double Vbr { get; set; }

    [JsonProperty("abr")]
    public double? Abr { get; set; }

    [JsonProperty("tbr")]
    public double? Tbr { get; set; }

    [JsonProperty("format")]
    public string? FormatFormat { get; set; }

    [JsonProperty("format_index")]
    public object? FormatIndex { get; set; }

    [JsonProperty("manifest_url", NullValueHandling = NullValueHandling.Ignore)]
    public Uri? ManifestUrl { get; set; }

    [JsonProperty("language")]
    public object? Language { get; set; }

    [JsonProperty("preference")]
    public object? Preference { get; set; }

    [JsonProperty("quality", NullValueHandling = NullValueHandling.Ignore)]
    public long? Quality { get; set; }

    [JsonProperty("has_drm", NullValueHandling = NullValueHandling.Ignore)]
    public bool? HasDrm { get; set; }

    [JsonProperty("source_preference", NullValueHandling = NullValueHandling.Ignore)]
    public long? SourcePreference { get; set; }

    [JsonProperty("asr")]
    public long? Asr { get; set; }

    [JsonProperty("filesize", NullValueHandling = NullValueHandling.Ignore)]
    public long? Filesize { get; set; }

    [JsonProperty("audio_channels")]
    public long? AudioChannels { get; set; }

    [JsonProperty("language_preference", NullValueHandling = NullValueHandling.Ignore)]
    public long? LanguagePreference { get; set; }

    [JsonProperty("dynamic_range")]
    public string? DynamicRange { get; set; }

    [JsonProperty("container", NullValueHandling = NullValueHandling.Ignore)]
    public string? Container { get; set; }

    [JsonProperty("downloader_options", NullValueHandling = NullValueHandling.Ignore)]
    public DownloaderOptions? DownloaderOptions { get; set; }
}
