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

namespace NoMercy.Plugins.Abstractions;

/// <summary>The refusals the host raises often, written once so the wording does not drift.</summary>
public static class PluginRefusalMessages
{
    public static PluginRefusal HostServicesRemoved(string plugin, string service)
    {
        return new PluginRefusal(
            PluginRefusalCodes.HostServicesRemoved,
            plugin,
            $"The plugin asked the host container for {service}.",
            "Contract v3 has no host container. Every route into the server is a facade on IPluginContext, so the owner can see and revoke it.",
            "Use context.Metadata.QueryAsync (capability metadata.query). Docs: /nomercy-plugins/capabilities/metadata-query",
            PluginRefusalSeverity.Blocked
        );
    }

    /// <summary>
    /// A plugin compiled against contract v2 calling a member v3 took away.
    /// <para>
    /// The runtime raises this the first time the method runs, not at load, so
    /// the plugin installs and enables and then fails on one route. Naming the
    /// member is the whole value: the exception alone says a method is missing
    /// and not which contract it belonged to.
    /// </para>
    /// </summary>
    public static PluginRefusal RemovedContractMember(string plugin, string missingMember)
    {
        return new PluginRefusal(
            PluginRefusalCodes.HostServicesRemoved,
            plugin,
            $"The plugin called a member contract v3 removed: {missingMember}",
            "Contract v3 hands a plugin facades on IPluginContext instead of the host's own container and event bus, so the owner can see and revoke every route into the server.",
            "Rebuild against NoMercy.Plugins.Abstractions 11.0 and replace the call with the facade for what it needed. Docs: /nomercy-plugins/migration/v2-to-v3",
            PluginRefusalSeverity.Blocked
        );
    }

    /// <summary>
    /// A plugin opening a client socket without <c>network.dial</c>.
    /// <para>
    /// The port is named as well as the host, because a plugin that declared a
    /// glob and still refuses is usually one whose tracker moved to a port the
    /// owner's grant does not cover, and "eztv.re was refused" sends the author
    /// looking at the wrong line.
    /// </para>
    /// </summary>
    public static PluginRefusal SocketUndeclared(string plugin, string host, int port)
    {
        return new PluginRefusal(
            PluginRefusalCodes.SocketUndeclared,
            plugin,
            $"The plugin opened a socket to {host} on port {port}.",
            "Raw sockets reach anything on the owner's network and beyond it, so they are a capability the owner grants per host rather than a thing every plugin has.",
            $"Declare the network.dial capability in plugin.json with a host glob that matches {host}, and dial through context.Net.DialAsync. Docs: /nomercy-plugins/capabilities/network-dial",
            PluginRefusalSeverity.Blocked
        );
    }

    /// <summary>A plugin holding a port open without <c>network.listen</c>.</summary>
    public static PluginRefusal ListenerUndeclared(string plugin, int port)
    {
        return new PluginRefusal(
            PluginRefusalCodes.ListenerUndeclared,
            plugin,
            $"The plugin listened on port {port}.",
            "A listening port accepts connections the owner never started, so the owner grants the port range rather than the plugin choosing it.",
            $"Declare the network.listen capability in plugin.json with a port or range covering {port}, and listen through context.Net.ListenAsync. Docs: /nomercy-plugins/capabilities/network-listen",
            PluginRefusalSeverity.Blocked
        );
    }

    /// <summary>
    /// A plugin reading or writing outside every folder it was granted.
    /// <para>
    /// This fires on an absolute path and on a <c>..</c> segment as well as on
    /// an unGranted folder id, because all three are the same mistake: a path
    /// the owner cannot see on the permissions page.
    /// </para>
    /// </summary>
    public static PluginRefusal FileOutsideGrant(string plugin, string path)
    {
        return new PluginRefusal(
            PluginRefusalCodes.FileOutsideGrant,
            plugin,
            $"The plugin reached a path outside every folder it was granted: {path}",
            "A plugin writes where the owner put it, and a path the owner never picked is a path the owner cannot audit, move or revoke.",
            "Write to context.Storage.Private for the plugin's own files, or ask the owner for a folder and open it with context.Storage.PathAsync. Paths are relative to the scope; an absolute path or a .. segment is never resolved. Docs: /nomercy-plugins/capabilities/storage",
            PluginRefusalSeverity.Blocked
        );
    }

    /// <summary>A plugin starting a child process without <c>process.spawn</c>.</summary>
    public static PluginRefusal ProcessSpawnUndeclared(string plugin, string binary)
    {
        return new PluginRefusal(
            PluginRefusalCodes.ProcessSpawnUndeclared,
            plugin,
            $"The plugin started a child process: {binary}",
            "A child process runs outside everything the host mediates, with the server's own user, so starting one is a capability the owner grants per binary.",
            $"Declare the process.spawn capability in plugin.json naming {binary}, and start it through context.Process.SpawnAsync. Docs: /nomercy-plugins/capabilities/process-spawn",
            PluginRefusalSeverity.Blocked
        );
    }

    /// <summary>
    /// A plugin reaching a host its granted globs do not cover. Distinct from
    /// <see cref="SocketUndeclared" />: the capability is there and the host is
    /// not, which is a one-line manifest change rather than a redesign.
    /// </summary>
    public static PluginRefusal HostNotAllowed(string plugin, string host)
    {
        return new PluginRefusal(
            PluginRefusalCodes.HostNotAllowed,
            plugin,
            $"The plugin reached {host}, which none of its granted host globs match.",
            "The owner consented to a list of hosts, not to outbound traffic in general, so a host that arrived later needs the owner's word before it is reached.",
            $"Add a glob matching {host} to the network capability in plugin.json and publish the new version. The owner is asked to approve the added host on update. Docs: /nomercy-plugins/capabilities/network-fetch",
            PluginRefusalSeverity.Blocked
        );
    }

    /// <summary>
    /// A plugin loading native code out of a bundle the marketplace did not sign.
    /// <para>
    /// The only refusal here that no capability lifts. Machine code cannot be
    /// inspected, sandboxed or refused once it is running, so the gate is who
    /// built the bundle rather than what the owner ticked.
    /// </para>
    /// </summary>
    public static PluginRefusal NativeCodeUnsigned(string plugin, string library)
    {
        return new PluginRefusal(
            PluginRefusalCodes.NativeCodeUnsigned,
            plugin,
            $"The plugin loaded the native library {library} from a bundle the marketplace has not signed.",
            "Native code runs outside every guard the host has, so it is allowed only when the marketplace built and signed the bundle it came in.",
            "Publish the plugin through the marketplace so the bundle is built and signed there, or replace the native library with a managed one. Docs: /nomercy-plugins/capabilities/native",
            PluginRefusalSeverity.Blocked
        );
    }

    /// <summary>
    /// A facade the contract declares that this server build does not wire yet.
    /// <para>
    /// The alternative was a nullable member, and a null taught an author to
    /// branch and then quietly do nothing: the plugin looked idle, the owner
    /// saw no error, and nobody learned the server was the old one. A refusal
    /// names the server version to update to instead.
    /// </para>
    /// </summary>
    public static PluginRefusal FacadeNotOnThisHost(string plugin, string facade)
    {
        return new PluginRefusal(
            PluginRefusalCodes.ContractVersionMismatch,
            plugin,
            $"The plugin used {facade}, which this server does not offer.",
            "The contract the plugin was built against is newer than this server. The member exists on the contract and there is nothing behind it here.",
            "Update the server to a build that offers this facade, or declare a lower minimum server version in plugin.json and check context.Server.Version before using it. Docs: /nomercy-plugins/handbook/versioning",
            PluginRefusalSeverity.Blocked
        );
    }
}
