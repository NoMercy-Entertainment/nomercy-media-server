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

namespace NoMercy.Api.Services.Visualizers;

/// <summary>Stable rule ids of the visualizer library (contract 2a, section 7).</summary>
public static class VisualizerRuleId
{
    public const string BuiltinReadonly = "visualizer.builtin_readonly";
    public const string SchemaInvalid = "visualizer.schema_invalid";
    public const string PayloadHasCode = "visualizer.payload_has_code";
    public const string FormatMismatch = "visualizer.format_mismatch";
    public const string PassOrder = "visualizer.pass_order";
    public const string ChannelUnknownPass = "visualizer.channel_unknown_pass";
    public const string UnknownFormat = "visualizer.unknown_format";
    public const string SchemaVersionUnsupported = "visualizer.schema_version_unsupported";
    public const string FieldRequired = "visualizer.field_required";
}
