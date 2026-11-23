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

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NoMercy.Api.DTOs.Music;

/// <summary>The audio of a <see cref="PrepareTransitionMessage" /> and where it sits.</summary>
public sealed class MusicTransitionDto
{
    /// <summary>
    /// Where to fetch the file, relative to this server. The link is made for
    /// the one user the message is sent to and plays for nobody else.
    /// </summary>
    [JsonProperty("url")]
    public required string Url { get; init; }

    [JsonProperty("mime")]
    public required string Mime { get; init; }

    [JsonProperty("duration_ms")]
    public required int DurationMs { get; init; }

    /// <summary>Where the playing track hands over to the file.</summary>
    [JsonProperty("outgoing_stop_ms")]
    public required int OutgoingStopMs { get; init; }

    /// <summary>Where the next track goes on after the file.</summary>
    [JsonProperty("incoming_resume_ms")]
    public required int IncomingResumeMs { get; init; }

    /// <summary>
    /// A JSON object from whoever supplied the file, passed on unread, for
    /// what a client shows during the join. Null when there is none.
    /// </summary>
    [JsonProperty("data")]
    public JRaw? Data { get; init; }
}
