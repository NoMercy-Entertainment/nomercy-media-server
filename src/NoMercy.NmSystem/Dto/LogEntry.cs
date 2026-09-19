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

using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Newtonsoft.Json;
using Serilog.Events;

namespace NoMercy.NmSystem.Dto;

public class LogEntry
{
    [JsonProperty("type")]
    [JsonPropertyName("Type")]
    public string Type { get; set; } = string.Empty;

    [JsonProperty("color")]
    [JsonPropertyName("Color")]
    public string Color { get; set; } = string.Empty;

    [JsonProperty("threadId")]
    [JsonPropertyName("ThreadId")]
    public int ThreadId { get; set; }

    [JsonProperty("time")]
    [JsonPropertyName("@t")]
    public DateTime Time { get; set; }

    // Two ignores: System.Text.Json reads the log file, Newtonsoft writes the
    // API. Only the second keeps this off a live log stream.
    [Newtonsoft.Json.JsonIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public dynamic LogMessage { get; set; } = default!;

    [NotMapped]
    [JsonProperty("message")]
    [JsonPropertyName("Message")]
    public string Message
    {
        get => LogMessage;
        set => LogMessage = value;
    }

    [Newtonsoft.Json.JsonIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public LogEventLevel LogLevel { get; set; }

    [NotMapped]
    [JsonProperty("level")]
    [JsonPropertyName("Level")]
    public string Level
    {
        get => LogLevel.ToString();
        set => LogLevel = Enum.Parse<LogEventLevel>(value);
    }
}
