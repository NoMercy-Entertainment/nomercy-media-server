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

using System.Runtime.Serialization;

namespace NoMercy.Plugins.Ipc;

/// <summary>
/// A refusal, with every teaching field intact across the process boundary.
/// <para>
/// The same six parts the server, the browser and the phone use. Flattened to
/// a message here so an author meets one sentence wherever they meet it, and
/// not a stack trace on the far side of an IPC hop.
/// </para>
/// </summary>
[DataContract]
public sealed record WireRefusal(
    [property: DataMember(Order = 1)] string Code,
    [property: DataMember(Order = 2)] string Plugin,
    [property: DataMember(Order = 3)] string What,
    [property: DataMember(Order = 4)] string Why,
    [property: DataMember(Order = 5)] string Fix,
    [property: DataMember(Order = 6)] string Severity
);

/// <summary>Who is asking, as the plugin process is allowed to know them.</summary>
[DataContract]
public sealed record WireCaller(
    [property: DataMember(Order = 1)] string UserId,
    [property: DataMember(Order = 2)] string Access,
    [property: DataMember(Order = 3)] string Surface,
    [property: DataMember(Order = 4)] bool IsOwner
);

/// <summary>One call, named by the facade it belongs to rather than by a type.</summary>
[DataContract]
public sealed record PluginCallRequest(
    [property: DataMember(Order = 1)] string PluginId,
    [property: DataMember(Order = 2)] string Facade,
    [property: DataMember(Order = 3)] string Member,
    [property: DataMember(Order = 4)] string PayloadJson,
    [property: DataMember(Order = 5)] WireCaller? Caller
);

[DataContract]
public sealed record PluginCallResponse
{
    [DataMember(Order = 1)]
    public bool Ok { get; init; }

    [DataMember(Order = 2)]
    public string PayloadJson { get; init; } = string.Empty;

    [DataMember(Order = 3)]
    public WireRefusal? Refusal { get; init; }

    public static PluginCallResponse Value(string payloadJson) =>
        new() { Ok = true, PayloadJson = payloadJson };

    public static PluginCallResponse Refused(WireRefusal refusal) =>
        new() { Ok = false, Refusal = refusal };
}

/// <summary>
/// The server's answer to one spawn: which file, and nothing else.
/// <para>
/// The server never starts the binary. A process it started would be a child
/// of the server and would sit beside the plugin's sandbox rather than inside
/// it, so the start happens in the plugin's own process and the new child
/// inherits the confinement by construction.
/// </para>
/// </summary>
[DataContract]
public sealed record PluginSpawnPermit([property: DataMember(Order = 1)] string ResolvedPath);

/// <summary>What the health page reports about the process itself.</summary>
[DataContract]
public sealed record PluginHealthSnapshot(
    [property: DataMember(Order = 1)] int Pid,
    [property: DataMember(Order = 2)] TimeSpan Uptime,
    [property: DataMember(Order = 3)] double CpuPercent,
    [property: DataMember(Order = 4)] long MemoryBytes,
    [property: DataMember(Order = 5)] long DiskBytes,
    [property: DataMember(Order = 6)] int Restarts,
    [property: DataMember(Order = 7)] string? LastRefusalCode
);
