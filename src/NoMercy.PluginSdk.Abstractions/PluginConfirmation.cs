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

using System.Text.Json.Serialization;

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// The prompt a client shows before running an action that cannot be undone.
/// </summary>
public class PluginConfirmation
{
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("confirmLabel")]
    public string? ConfirmLabel { get; init; }

    [JsonPropertyName("cancelLabel")]
    public string? CancelLabel { get; init; }

    /// <summary>
    /// Whether the confirm button reads as destructive. Separate from the
    /// prompt existing at all, because "leave without saving" and "delete these
    /// files" both need confirming and only one of them should be red.
    /// </summary>
    [JsonPropertyName("destructive")]
    public bool Destructive { get; init; } = true;
}
