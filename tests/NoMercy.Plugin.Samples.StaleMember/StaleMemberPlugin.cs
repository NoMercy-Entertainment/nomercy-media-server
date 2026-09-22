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

using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.Samples.StaleMember;

// The only plugin in this assembly, which is the whole point of it existing.
// The manifest path loads one assembly and walks every plugin type in it, so a
// fixture that shares an assembly with other failing plugins reports whichever
// one fails first and can never pin this path.
//
// The exception is raised rather than caused, because a plugin genuinely built
// against the older contract cannot be compiled from this source: the member is
// gone, so there is nothing to compile against. This is the exact type and
// message the runtime handed the loader on a real server.
public sealed class StaleMemberPlugin : IPlugin
{
    public static readonly Ulid FixedId = Ulid.Parse("01SAMPLE000000000000000006");

    public const string RemovedMember =
        "Method not found: 'NoMercy.Events.IEventBus NoMercy.PluginSdk.Abstractions.IPluginContext.get_EventBus()'.";

    public string Name => "StaleMember";
    public string Description => "Initialize reaches a member the contract removed";
    public Ulid Id => FixedId;
    public Version Version { get; } = new(0, 6, 5);

    public void Initialize(IPluginContext context) =>
        throw new MissingMethodException(RemovedMember);

    public void Dispose() { }
}
