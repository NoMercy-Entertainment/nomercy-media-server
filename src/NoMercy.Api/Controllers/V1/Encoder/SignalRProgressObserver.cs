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

using System.Security.Cryptography;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using NoMercy.Api.Hubs;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.TvShows;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.ContentAnalysis;
using NoMercy.Encoder.ContentAnalysis.Fingerprinting;
using NoMercy.Encoder.Errors;
using NoMercy.Encoder.Progress;
using NoMercy.Encoder.Subtitles;
using NoMercy.Storage;
using ContentSegment = NoMercy.Database.Models.Media.ContentSegment;
using ContentSegmentType = NoMercy.Database.Models.Media.ContentSegmentType;

namespace NoMercy.Api.Controllers.V1.Encoder;

/// <summary>
/// Bridges <see cref="IProgressObserver"/> callbacks from the Whisper
/// transcriber into <c>WhisperProgress</c> broadcasts on
/// <see cref="ContentAnalysisHub"/>.
/// </summary>
internal sealed class SignalRProgressObserver(
    IHubContext<ContentAnalysisHub> hub,
    string videoFileId
) : IProgressObserver
{
    public void OnProgress(EncodingProgress progress)
    {
        // Fire-and-forget — we are inside a synchronous callback.
        _ = hub.Clients.All.SendAsync(
            "WhisperProgress",
            new { video_file_id = videoFileId, percent_done = progress.PercentComplete }
        );
    }

    public void OnStageStarted(string stageName) { }

    public void OnStageCompleted(string stageName, TimeSpan duration) { }

    public void OnCompleted() { }

    public void OnError(EncodingError error) { }

    public void OnPlanResolved(
        List<string> videoStreams,
        List<string> audioStreams,
        List<string> subtitleStreams,
        bool hasGpu,
        bool isHdr
    ) { }
}
