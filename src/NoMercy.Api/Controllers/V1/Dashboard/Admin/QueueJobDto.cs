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

using System.Collections.Immutable;
using System.Diagnostics;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using NoMercy.Api.Controllers.V1.Music;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Api.DTOs.Music;
using NoMercy.Api.Services;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Encoder;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.Music;
using NoMercy.Database.Models.Queue;
using NoMercy.Database.Models.TvShows;
using NoMercy.Encoder.Execution;
using NoMercy.Encoder.Profiles;
using NoMercy.Events;
using NoMercy.Events.Encoding;
using NoMercy.MediaProcessing.AudioAnalysis;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.MediaProcessing.Jobs.MediaJobs.Support;
using NoMercy.MediaProcessing.Jobs.SubtitleJobs;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.NewtonSoftConverters;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Queue.MediaServer;
using NoMercyQueue;
using NoMercyQueue.Core;
using MediaJobDispatcher = NoMercy.MediaProcessing.Jobs.JobDispatcher;

namespace NoMercy.Api.Controllers.V1.Dashboard.Admin;

public class QueueJobDto
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("payload_id")]
    public string PayloadId { get; set; } = string.Empty;

    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// The item's own artwork — a movie backdrop or an episode still. Carried on
    /// the queue row, not only on progress events, so a card can show the title
    /// it is about from the moment the job is queued rather than waiting for the
    /// first frame of encoding to arrive.
    /// </summary>
    /// <para>Usually a bare provider path that the client roots at
    /// <c>/images/original</c>. A release's cover is not served from there — it
    /// lives under <c>/images/music</c> — so an album card states the rooted
    /// path instead, and a value already starting <c>/images/</c> is used as it
    /// stands.</para>
    [JsonProperty("backdrop")]
    public string? Backdrop { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    [JsonProperty("status")]
    public string Status { get; set; } = string.Empty;

    [JsonProperty("input_file")]
    public string InputFile { get; set; } = string.Empty;

    [JsonProperty("profile")]
    public string? Profile { get; set; }

    /// <summary>
    /// What this encode is going to produce, from the preset it will run with.
    /// Null on maintenance rows, and on an encode whose preset no longer
    /// resolves — a client shows the rest of the card either way.
    /// </summary>
    [JsonProperty("plan")]
    public QueueJobPlanDto? Plan { get; set; }

    [JsonProperty("priority")]
    public int Priority { get; set; }

    /// <summary>
    /// What kind of work this row is, when it is not an encode: <c>preview</c>
    /// for a scrub-sheet rebuild, <c>subtitles</c> for an OCR backfill. Null on a
    /// real encode, which is what every existing client already assumes it is
    /// reading — so an older build ignores this and keeps working.
    ///
    /// <para>These share the encoder queue on purpose, so they must not be shown
    /// as encodes: they have no media id, no artwork and no progress, and pushing
    /// them through the encode shape produced cards for titles that did not
    /// exist. Filtering them out instead went too far the other way — the server
    /// spends hours on this work and the panel claimed nothing was running.</para>
    /// </summary>
    [JsonProperty("kind")]
    public string? Kind { get; set; }

    /// <summary>
    /// How far a card made of several jobs has got, as a percentage. An album is
    /// one card over many track encodes, and its progress is how many of them are
    /// done — the only progress a card like that can honestly report, because the
    /// per-track progress events key on a track and this card is not one.
    ///
    /// <para>Null on a single-job card, where live progress events are the
    /// authority and an older client already reads them. Additive, so a client
    /// that has never heard of it keeps working.</para>
    /// </summary>
    [JsonProperty("progress")]
    public double? Progress { get; set; }

    /// <summary>Jobs of this card already finished. Null when the card is one job.</summary>
    [JsonProperty("completed_items")]
    public int? CompletedItems { get; set; }

    /// <summary>Jobs this card covers in total. Null when the card is one job.</summary>
    [JsonProperty("total_items")]
    public int? TotalItems { get; set; }
}
