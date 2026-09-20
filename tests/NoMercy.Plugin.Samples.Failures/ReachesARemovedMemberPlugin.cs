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

using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.Samples.Failures;

// Real assembly fixture for the failure a real server produced: the torrent
// downloader reaching IPluginContext.EventBus, which the contract removed.
//
// The exception is raised rather than caused, because a plugin genuinely built
// against the older contract cannot be compiled from this source: the member is
// gone, so there is nothing to compile against. What the runtime hands the
// loader is this exact type with this exact message, which is what the loader
// has to turn into something the author can act on.
public sealed class ReachesARemovedMemberPlugin : IPlugin
{
    public static readonly Ulid FixedId = Ulid.Parse("01SAMPLE000000000000000005");

    public const string RemovedMember =
        "Method not found: 'NoMercy.Events.IEventBus NoMercy.Plugins.Abstractions.IPluginContext.get_EventBus()'.";

    public string Name => "ReachesARemovedMember";
    public string Description =>
        "Constructs fine, Initialize reaches a member the contract removed";
    public Ulid Id => FixedId;
    public Version Version { get; } = new(0, 6, 5);

    public void Initialize(IPluginContext context) =>
        throw new MissingMethodException(RemovedMember);

    public void Dispose() { }
}
