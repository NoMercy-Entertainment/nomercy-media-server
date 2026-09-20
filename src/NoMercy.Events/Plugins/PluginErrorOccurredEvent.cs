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

namespace NoMercy.Events.Plugins;

public sealed class PluginErrorOccurredEvent : EventBase, IEventExplainsItself
{
    public override string Source => "PluginManager";

    public required string PluginId { get; init; }
    public required string PluginName { get; init; }
    public required string ErrorMessage { get; init; }
    public string? ExceptionType { get; init; }

    public string Why =>
        ExceptionType is null
            ? $"{PluginName}: {ErrorMessage}"
            : $"{PluginName}: {ErrorMessage} ({ExceptionType})";
}
