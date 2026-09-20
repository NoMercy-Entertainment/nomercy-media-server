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
            "There is no host container. Every route into the server is a facade on IPluginContext, so the owner can see and revoke it.",
            "Use context.Metadata.QueryAsync (capability metadata.query). Docs: /nomercy-plugins/capabilities/metadata-query",
            PluginRefusalSeverity.Blocked
        );
    }

    /// <summary>
    /// A plugin built against an older SDK calling a member that does not
    /// exist in this server's build.
    /// <para>
    /// The runtime raises this the first time the method runs, not at load, so
    /// the plugin installs and enables and then fails on one route. Naming the
    /// member is the whole value: the exception alone says a method is missing
    /// and not which SDK it belonged to.
    /// </para>
    /// </summary>
    public static PluginRefusal RemovedContractMember(string plugin, string missingMember)
    {
        return new PluginRefusal(
            PluginRefusalCodes.HostServicesRemoved,
            plugin,
            $"The plugin called a member that does not exist: {missingMember}",
            "This server hands a plugin facades on IPluginContext instead of the host's own container and event bus, so the owner can see and revoke every route into the server.",
            $"Rebuild against NoMercy.Plugins.Abstractions {PluginAbi.Current} and replace the call with the facade for what it needed. Docs: /nomercy-plugins/migration",
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
    /// <summary>
    /// A plugin that put a credential in a URL it built itself.
    /// <para>
    /// The radio plugin kept the viewer's bearer token in a mutable static and
    /// appended it to every stream URL, which put a live credential into the
    /// server log, the client's history and every link a viewer shared.
    /// </para>
    /// </summary>
    /// <summary>
    /// A recording that cannot be written because the disk is full.
    /// </summary>
    public static PluginRefusal RecordingDiskFull(string plugin, string channelId)
    {
        return new PluginRefusal(
            PluginRefusalCodes.RecordingDiskFull,
            plugin,
            $"The recording of {channelId} stopped because the disk it writes to is full.",
            "A recording writes for as long as the program runs, so the space it needs is not known when it is scheduled. What was captured before the disk filled is kept.",
            "Free space on the recording library's disk, or lower the retention on this plugin so older recordings are removed sooner. Docs: /nomercy-plugins/capabilities/media-record",
            PluginRefusalSeverity.Blocked
        );
    }

    /// <summary>
    /// One upstream out of several that stopped answering.
    /// <para>
    /// Degraded rather than blocked: the point of an ordered link list is that
    /// the next one is tried, so one dead mirror is not a reason to end the
    /// channel. It is still reported, because a provider that always falls
    /// through to its last mirror is failing quietly.
    /// </para>
    /// </summary>
    public static PluginRefusal LiveLinkFailed(string plugin, string channelId, int linkIndex)
    {
        return new PluginRefusal(
            PluginRefusalCodes.LiveLinkFailed,
            plugin,
            $"Link {linkIndex} for channel {channelId} did not answer.",
            "The host fell through to the next link in the list, so playback continued. A link that keeps failing means the provider changed something or the credential behind it expired.",
            "Check the upstream this link points at and remove it if the provider retired it. Docs: /nomercy-plugins/capabilities/media-live",
            PluginRefusalSeverity.Degraded
        );
    }

    /// <summary>
    /// A hub method reached on a connection with no resolved caller.
    /// </summary>
    public static PluginRefusal HubCallerNotResolved(string plugin, string method)
    {
        return new PluginRefusal(
            PluginRefusalCodes.HubCallerNotResolved,
            plugin,
            $"The hub method {method} was reached on a connection the host could not identify.",
            "A handler that runs without knowing who asked cannot tell the owner from a guest, and the plugin has no way to find out afterwards, so it ends up trusting whoever connected.",
            "Nothing to change in the plugin. The connection reached the hub without an identity the server recognises, which means the client connected without signing in. Docs: /nomercy-plugins/tour/callers-and-access",
            PluginRefusalSeverity.Blocked
        );
    }

    /// <summary>
    /// A worker the host stopped restarting.
    /// </summary>
    public static PluginRefusal WorkerCrashed(string plugin, string worker, int restarts)
    {
        return new PluginRefusal(
            PluginRefusalCodes.SchedulerWorkerCrashed,
            plugin,
            $"The worker {worker} was disabled after crashing {restarts} times in an hour.",
            "A worker that crashes three times in an hour is crashing on startup rather than hitting something passing, and restarting it for ever costs more than the worker was doing.",
            "Fix what the worker throws on, then enable the plugin again to start it. The log line above this one carries the exception. Docs: /nomercy-plugins/capabilities/scheduler",
            PluginRefusalSeverity.Degraded
        );
    }

    /// <summary>
    /// A password read back from settings rather than from the secret store.
    /// </summary>
    public static PluginRefusal SecretFieldInSettings(string plugin, string key)
    {
        return new PluginRefusal(
            PluginRefusalCodes.SecretFieldInSettings,
            plugin,
            $"The field {key} is a password and was read from settings.",
            "Settings are written to a file the owner can open, travel in an export and appear in any log line that dumps them. A password kept there is a password in all three.",
            "Read it with context.Secrets.GetAsync instead. The host already stored it there when the owner filled the field in. Docs: /nomercy-plugins/capabilities/settings",
            PluginRefusalSeverity.Blocked
        );
    }

    /// <summary>
    /// A write to a field the plugin's own schema marks read only.
    /// </summary>
    public static PluginRefusal SettingsFieldReadOnly(string plugin, string key)
    {
        return new PluginRefusal(
            PluginRefusalCodes.SettingsFieldReadOnly,
            plugin,
            $"The plugin wrote to {key}, which its own schema marks read only.",
            "A field the plugin can overwrite is one the owner cannot keep set: whatever they chose is replaced the next time the plugin runs, and nothing tells them it happened.",
            "Mark the field writable in the settings schema if the plugin is meant to change it. Docs: /nomercy-plugins/capabilities/settings",
            PluginRefusalSeverity.Blocked
        );
    }

    /// <summary>
    /// A per-user secret reached where the host resolved nobody.
    /// </summary>
    public static PluginRefusal SecretHasNoCaller(string plugin, string key)
    {
        return new PluginRefusal(
            PluginRefusalCodes.SecretHasNoCaller,
            plugin,
            $"The per-user secret {key} was reached on a call with no caller.",
            "Falling back to the server's own slot would put one member's provider login where every member reads it, and the member who set it could never revoke it on their own.",
            "Reach per-user secrets from a request or a hub call, where the host has resolved who is asking. Background work has no caller, so use context.Secrets.GetAsync for values the server owns. Docs: /nomercy-plugins/capabilities/secrets",
            PluginRefusalSeverity.Blocked
        );
    }

    /// <summary>
    /// User data held somewhere other than the per-user scope.
    /// </summary>
    public static PluginRefusal UserScopeRequired(string plugin, string path)
    {
        return new PluginRefusal(
            PluginRefusalCodes.UserScopeRequired,
            plugin,
            $"The plugin wrote user data to {path}, which is not a per-user scope.",
            "Data about a person mixed into the plugin's own files cannot be handed to that person, and cannot be removed when they leave. Nobody finds out until one of them asks.",
            "Write it through context.Storage.ForUser instead. The host exports and erases that scope on its own, so the plugin has nothing to remember. Docs: /nomercy-plugins/capabilities/user-scope",
            PluginRefusalSeverity.Blocked
        );
    }

    /// <summary>
    /// User data sent off the server.
    /// </summary>
    public static PluginRefusal UserDataEgress(string plugin, string host)
    {
        return new PluginRefusal(
            PluginRefusalCodes.UserDataEgress,
            plugin,
            $"The plugin sent user data to {host}.",
            "There is no capability for this, and there is not going to be one. Once a person's data is on someone else's server the owner cannot export it, cannot erase it, and cannot tell the person where it went.",
            "Keep it in context.Storage.ForUser. If the plugin needs to ask an upstream something, send what the question needs and not who asked it. Docs: /nomercy-plugins/capabilities/user-scope",
            PluginRefusalSeverity.Blocked
        );
    }

    /// <summary>
    /// A route claiming a path the host keeps for itself.
    /// </summary>
    public static PluginRefusal RouteReservedPrefix(string plugin, string path)
    {
        return new PluginRefusal(
            PluginRefusalCodes.RouteReservedPrefix,
            plugin,
            $"The route {path} starts with an underscore, which the host keeps.",
            "Paths beginning with an underscore are where the host adds pages to every plugin. A plugin that claims one keeps working until the host adds that page, and then stops for a reason its author had nothing to do with.",
            "Rename the route without the leading underscore. Every other path is the plugin's. Docs: /nomercy-plugins/tour/routes",
            PluginRefusalSeverity.Blocked
        );
    }

    public static PluginRefusal TokenInUrl(string plugin, string url)
    {
        return new PluginRefusal(
            PluginRefusalCodes.TokenInUrl,
            plugin,
            $"The plugin built a media URL carrying a credential: {url}",
            "A credential in a URL is written to the server log, kept in the client's history and travels with every link a viewer shares. It also outlives the session, because nothing revokes a query string.",
            "Hand the upstream to context.Media.Proxy.MintAsync and play the URL it returns. The host mints one bound to this user and this session, and keeps the upstream's own credentials on the server. Docs: /nomercy-plugins/capabilities/media-proxy",
            PluginRefusalSeverity.Blocked
        );
    }

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

    /// <summary>
    /// A plugin using a facade its manifest never asked for.
    /// <para>
    /// The one builder every capability can use, so a new capability does not
    /// need a new sentence written for it and cannot be given a docs link that
    /// points at a page nobody wrote. The link is the generated one.
    /// </para>
    /// </summary>
    public static PluginRefusal CapabilityNotDeclared(string plugin, string capability, string what)
    {
        PluginCapabilityDescriptor? descriptor = PluginCapabilityVocabulary.ByName(capability);

        return new PluginRefusal(
            PluginRefusalCodes.CapabilityNotDeclared,
            plugin,
            what,
            $"The plugin did not declare {capability}, and the owner grants what the manifest asks for rather than what the code turns out to use.",
            $"Declare {capability} in plugin.json and publish the new version. The owner is asked to approve it on update. Docs: {descriptor?.DocsUrl ?? "/nomercy-plugins/capabilities"}",
            PluginRefusalSeverity.Blocked
        );
    }

    /// <summary>
    /// A page navigating to a host the plugin's network globs do not match.
    /// <para>
    /// A challenge page redirecting to a host nobody listed is the ordinary way
    /// this fires, so the message says the browser rides on the network grant
    /// rather than replacing it. An author who reads it as a browser bug adds a
    /// retry loop and never adds the host.
    /// </para>
    /// </summary>
    public static PluginRefusal BrowserNavigationBlocked(string plugin, string url)
    {
        return new PluginRefusal(
            PluginRefusalCodes.BrowserNavigationBlocked,
            plugin,
            $"The plugin navigated the headless browser to {url}.",
            "The browser does not widen the network grant, it rides on it: a page may only reach the hosts the plugin's network.fetch globs already match. Otherwise a page would be the way around a host list the owner reviewed.",
            $"Add a glob matching {url} to the network capability in plugin.json, beside browser.headless. Docs: /nomercy-plugins/capabilities/browser-headless",
            PluginRefusalSeverity.Blocked
        );
    }
}
