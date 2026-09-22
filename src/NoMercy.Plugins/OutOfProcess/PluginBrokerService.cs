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

using System.Text.Json;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Ipc;
using NoMercy.PluginSdk.Runtime;
using ProtoBuf.Grpc;

namespace NoMercy.PluginSdk.OutOfProcess;

/// <summary>
/// The server side of one plugin's channel, and the trust boundary of the
/// out-of-process runtime.
/// <para>
/// Nothing the child process sends is trusted. It names a plugin id, a facade
/// and a member, and each of those is a claim: the id is checked against the
/// plugin this broker was built for, the facade against the set the server
/// actually serves, and the capability before the facade is touched at all.
/// A facade that ran and then refused has already done the thing the refusal
/// was for.
/// </para>
/// <para>
/// One broker per plugin rather than one shared broker with the id as an
/// argument. A shared one would have to trust the id on every call, and a
/// compromised child could borrow another plugin's capabilities by typing its
/// id.
/// </para>
/// </summary>
public sealed class PluginBrokerService(
    Ulid pluginId,
    IPluginCapabilityBroker capabilities,
    IPluginSecretStore secrets,
    IPluginApprovedBinaries approved,
    IPluginServerInfo server,
    IPluginStorageRoots storage,
    IPluginLibraryQuery library,
    IPluginMetadata metadata,
    IPluginNotifications notifications,
    IPluginUsers users,
    IPluginScheduler scheduler,
    IPluginSettings settings,
    IPluginUserData user,
    IPluginLibraryWriter? libraryWriter = null,
    IPluginLibraryImport? libraryImport = null,
    IPluginBundleSignature? signature = null,
    string? pluginDirectory = null,
    IPluginMedia? media = null
) : IPluginBrokerService
{
    private static readonly JsonSerializerOptions Json = PluginWireJson.Options;

    public async Task<PluginCallResponse> CallAsync(
        PluginCallRequest request,
        CallContext context = default
    )
    {
        if (request.PluginId != pluginId.ToString())
            return Refuse(
                PluginRefusalCodes.HostServicesRemoved,
                $"A plugin process called {request.Facade}.{request.Member} as another plugin.",
                "The id on the call is not the plugin this channel belongs to.",
                "This is a server fault rather than a plugin one. Report it with the server log around the call."
            );

        try
        {
            return await Route(request);
        }
        catch (PluginRefusedException refused)
        {
            // A facade that refused after the broker let the call through
            // still has to reach the plugin as a refusal. Thrown across the
            // channel it would arrive as a server that stopped answering.
            return PluginCallResponse.Refused(Wire(refused.Refusal));
        }
        catch (JsonException)
        {
            // Arguments the server cannot read are a refusal, not a channel
            // that stopped answering. Thrown here it would reach the child as
            // the latter, and the plugin author would read nothing at all.
            return PluginCallResponse.Refused(Wire(NotUnderstood(request.Facade, request.Member)));
        }
    }

    private async Task<PluginCallResponse> Route(PluginCallRequest request)
    {
        return request.Facade switch
        {
            "secrets" => await Secrets(request),
            "process" => Process(request),
            "server" => await Server(request),
            "storage" => await Storage(request),
            "net" => Net(request),
            "library" => await Library(request),
            "metadata" => await Metadata(request),
            "notifications" => await Notifications(request),
            "users" => await Users(request),
            "scheduler" => await Scheduler(request),
            "settings" => await Settings(request),
            "user" => await User(request),
            "libraryWriter" => await LibraryWriter(request),
            "libraryImport" => await LibraryImport(request),
            "native" => Native(request),
            "media.proxy" => await MediaProxy(request),
            "media.transcode" => await MediaTranscode(request),
            "media.remux" => await MediaRemux(request),
            "media.live" => await MediaLive(request),
            "media.record" => await MediaRecord(request),
            _ => Refuse(
                PluginRefusalCodes.HostServicesRemoved,
                $"The plugin asked the server for {request.Facade}.{request.Member}.",
                $"This server does not serve a facade called {request.Facade} across the process boundary.",
                "Use a facade the contract declares. Docs: /nomercy-plugins/handbook/runtime-and-isolation"
            ),
        };
    }

    private async Task<PluginCallResponse> Secrets(PluginCallRequest request)
    {
        if (capabilities.Check(pluginId, "secrets") is { } refusal)
            return PluginCallResponse.Refused(Wire(refusal));

        SecretCall call =
            JsonSerializer.Deserialize<SecretCall>(request.PayloadJson, Json) ?? new(null, null);

        switch (request.Member)
        {
            case nameof(IPluginSecretStore.GetAsync):
                return Value(await secrets.GetAsync(call.Key ?? string.Empty));

            case nameof(IPluginSecretStore.SetAsync):
                await secrets.SetAsync(call.Key ?? string.Empty, call.Value ?? string.Empty);
                return PluginCallResponse.Value("null");

            case nameof(IPluginSecretStore.DeleteAsync):
                await secrets.DeleteAsync(call.Key ?? string.Empty);
                return PluginCallResponse.Value("null");

            case nameof(IPluginSecretStore.KeysAsync):
                return Value(await secrets.KeysAsync());

            case nameof(IPluginSecretStore.GetForUserAsync):
                return Value(await secrets.GetForUserAsync(call.Key ?? string.Empty));

            case nameof(IPluginSecretStore.SetForUserAsync):
                await secrets.SetForUserAsync(call.Key ?? string.Empty, call.Value ?? string.Empty);
                return PluginCallResponse.Value("null");

            case nameof(IPluginSecretStore.DeleteForUserAsync):
                await secrets.DeleteForUserAsync(call.Key ?? string.Empty);
                return PluginCallResponse.Value("null");

            default:
                return Refuse(
                    PluginRefusalCodes.HostServicesRemoved,
                    $"The plugin asked the server for secrets.{request.Member}.",
                    "The secrets facade has no member by that name.",
                    "Use a member the contract declares. Docs: /nomercy-plugins/handbook/runtime-and-isolation"
                );
        }
    }

    /// <summary>
    /// Answers which file, and starts nothing.
    /// <para>
    /// A process the server started would be a child of the server, outside
    /// the sandbox that holds the plugin. The plugin's own process starts it,
    /// so the child inherits the job object, the cgroup or the sandbox profile
    /// that already holds its parent.
    /// </para>
    /// </summary>
    private PluginCallResponse Process(PluginCallRequest request)
    {
        if (request.Member != nameof(PluginSdk.Abstractions.IPluginProcess.SpawnAsync))
            return Refuse(
                PluginRefusalCodes.HostServicesRemoved,
                $"The plugin asked the server for process.{request.Member}.",
                "The process facade has no member by that name.",
                "Use a member the contract declares. Docs: /nomercy-plugins/handbook/runtime-and-isolation"
            );

        SpawnCall call =
            JsonSerializer.Deserialize<SpawnCall>(request.PayloadJson, Json) ?? new(null);

        string binary = call.Binary ?? string.Empty;

        // The capability says whether this plugin may start anything, with the
        // binary as the scope the owner consented to.
        if (capabilities.Check(pluginId, PluginCapabilityNames.ProcessSpawn, binary) is { } refusal)
            return PluginCallResponse.Refused(Wire(refusal));

        // The name is only a label; this is the file. A name the owner never
        // approved resolves to nothing, and nothing is what runs.
        string? path = approved.PathFor(pluginId, binary);

        if (path is null)
            return PluginCallResponse.Refused(
                Wire(PluginRefusalMessages.ProcessSpawnUndeclared(pluginId.ToString(), binary))
            );

        return Value(new PluginSpawnPermit(path));
    }

    /// <summary>
    /// What this server is, so a plugin branches on a fact.
    /// <para>
    /// No capability gate here: the version and the platform are facts about
    /// software the owner installed, and the granted paths are filtered by the
    /// grants already. The free-space probe refuses a folder the owner never
    /// granted on its own, so the refusal keeps the sentence it always had.
    /// </para>
    /// </summary>
    private async Task<PluginCallResponse> Server(PluginCallRequest request)
    {
        switch (request.Member)
        {
            case nameof(IPluginServerInfo.Version):
                return Value(server.Version.ToString());

            case nameof(IPluginServerInfo.Platform):
                return Value(server.Platform);

            case nameof(IPluginServerInfo.GrantedPaths):
                return Value(server.GrantedPaths);

            case nameof(IPluginServerInfo.FreeSpaceBytesAsync):
                FolderCall call =
                    JsonSerializer.Deserialize<FolderCall>(request.PayloadJson, Json) ?? new(null);

                return Value(await server.FreeSpaceBytesAsync(call.FolderId ?? string.Empty));

            default:
                return Refuse(
                    PluginRefusalCodes.HostServicesRemoved,
                    $"The plugin asked the server for server.{request.Member}.",
                    "The server facade has no member by that name.",
                    "Use a member the contract declares. Docs: /nomercy-plugins/handbook/runtime-and-isolation"
                );
        }
    }

    /// <summary>
    /// Which folder, never the bytes in it.
    /// <para>
    /// The plugin's own three folders need no grant: the server made them for
    /// this plugin and nothing else can reach them. One of the owner's folders
    /// needs both the capability and a grant naming that folder, so the scope
    /// is the folder id and the check runs before the path is looked up.
    /// </para>
    /// </summary>
    private async Task<PluginCallResponse> Storage(PluginCallRequest request)
    {
        switch (request.Member)
        {
            case nameof(IPluginStorage.Private):
                return Value(storage.PrivateRoot);

            case nameof(IPluginStorage.Temp):
                return Value(storage.TempRoot);

            case nameof(IPluginStorage.Derived):
                return Value(storage.DerivedRoot);

            case nameof(IPluginStorage.PathAsync):
                return await OwnerFolder(request);

            default:
                return Refuse(
                    PluginRefusalCodes.HostServicesRemoved,
                    $"The plugin asked the server for storage.{request.Member}.",
                    "The storage facade has no member by that name.",
                    "Use a member the contract declares. Docs: /nomercy-plugins/handbook/runtime-and-isolation"
                );
        }
    }

    private async Task<PluginCallResponse> OwnerFolder(PluginCallRequest request)
    {
        FolderCall call =
            JsonSerializer.Deserialize<FolderCall>(request.PayloadJson, Json) ?? new(null);

        string folderId = call.FolderId ?? string.Empty;

        if (
            capabilities.Check(pluginId, PluginCapabilityNames.StoragePath, folderId) is { } refusal
        )
            return PluginCallResponse.Refused(Wire(refusal));

        string? path = await storage.PathForAsync(folderId);

        if (path is null)
            return PluginCallResponse.Refused(
                Wire(PluginRefusalMessages.FileOutsideGrant(pluginId.ToString(), folderId))
            );

        return Value(path);
    }

    /// <summary>
    /// Whether this plugin may reach that host, and nothing more.
    /// <para>
    /// The socket is opened in the plugin's own process. A socket proxied
    /// through the server would copy every byte of a download twice and put
    /// the server in the middle of a connection it has no reason to read.
    /// </para>
    /// <para>
    /// Asked twice, because the two answers have different fixes. Without the
    /// scope the question is only whether this plugin may dial at all; with
    /// it, whether it may dial there.
    /// </para>
    /// </summary>
    private PluginCallResponse Net(PluginCallRequest request)
    {
        if (request.Member != nameof(IPluginNet.DialAsync))
            return Refuse(
                PluginRefusalCodes.HostServicesRemoved,
                $"The plugin asked the server for net.{request.Member}.",
                "The net facade does not carry that member across the process boundary yet.",
                "Run this plugin in the server's own process until it is carried across. Docs: /nomercy-plugins/handbook/runtime-and-isolation"
            );

        DialCall call =
            JsonSerializer.Deserialize<DialCall>(request.PayloadJson, Json) ?? new(null, 0);

        string host = call.Host ?? string.Empty;

        if (capabilities.Check(pluginId, PluginCapabilityNames.NetworkDial) is not null)
            return PluginCallResponse.Refused(
                Wire(PluginRefusalMessages.SocketUndeclared(pluginId.ToString(), host, call.Port))
            );

        if (capabilities.Check(pluginId, PluginCapabilityNames.NetworkDial, host) is not null)
            return PluginCallResponse.Refused(
                Wire(PluginRefusalMessages.HostNotAllowed(pluginId.ToString(), host))
            );

        return PluginCallResponse.Value("true");
    }

    /// <summary>
    /// Reads of the owner's library, which is the one facade whose answer is
    /// the data itself rather than a permit.
    /// <para>
    /// A title and an episode count are small and the plugin holds no database
    /// handle, so there is nothing to hand it a path to. The rows cross, and
    /// the capability is checked before a single one is read: a query that ran
    /// and then refused has already told the server's disk what to look for.
    /// </para>
    /// </summary>
    private async Task<PluginCallResponse> Library(PluginCallRequest request)
    {
        if (capabilities.Check(pluginId, PluginCapabilityNames.LibraryRead) is { } refusal)
            return PluginCallResponse.Refused(Wire(refusal));

        LibraryCall call =
            JsonSerializer.Deserialize<LibraryCall>(request.PayloadJson, Json) ?? new(null, 0);

        switch (request.Member)
        {
            case nameof(IPluginLibraryQuery.GetLibrariesAsync):
                return Value(await library.GetLibrariesAsync());

            case nameof(IPluginLibraryQuery.GetShowsAsync):
                return Value(await library.GetShowsAsync(call.LibraryId));

            case nameof(IPluginLibraryQuery.GetMoviesAsync):
                return Value(await library.GetMoviesAsync(call.LibraryId));

            case nameof(IPluginLibraryQuery.GetEpisodesAsync):
                return Value(await library.GetEpisodesAsync(call.ShowId));

            case nameof(IPluginLibraryQuery.GetShowFilesAsync):
                return Value(await library.GetShowFilesAsync(call.ShowId));

            default:
                return Refuse(
                    PluginRefusalCodes.HostServicesRemoved,
                    $"The plugin asked the server for library.{request.Member}.",
                    "The library facade does not carry that member across the process boundary yet.",
                    "Run this plugin in the server's own process until it is carried across. Docs: /nomercy-plugins/handbook/runtime-and-isolation"
                );
        }
    }

    /// <summary>
    /// Asking the metadata providers the owner already configured, rather than
    /// the plugin carrying a key of its own that outlives the grant.
    /// </summary>
    private async Task<PluginCallResponse> Metadata(PluginCallRequest request)
    {
        if (request.Member != nameof(IPluginMetadata.QueryAsync))
            return NoSuchMember("metadata", request.Member);

        if (capabilities.Check(pluginId, PluginCapabilityNames.MetadataQuery) is { } refusal)
            return PluginCallResponse.Refused(Wire(refusal));

        PluginMetadataQuery query =
            JsonSerializer.Deserialize<PluginMetadataQuery>(request.PayloadJson, Json)
            ?? throw new PluginRefusedException(NotUnderstood("metadata", request.Member));

        return Value(await metadata.QueryAsync(query));
    }

    /// <summary>
    /// A notification names the person it is for, and a plugin that could send
    /// to everyone by leaving that out would be a plugin that can reach the
    /// whole household from one user's page.
    /// </summary>
    private async Task<PluginCallResponse> Notifications(PluginCallRequest request)
    {
        if (request.Member != nameof(IPluginNotifications.PushAsync))
            return NoSuchMember("notifications", request.Member);

        if (capabilities.Check(pluginId, PluginCapabilityNames.NotificationsPush) is { } refusal)
            return PluginCallResponse.Refused(Wire(refusal));

        PushCall call =
            JsonSerializer.Deserialize<PushCall>(request.PayloadJson, Json)
            ?? throw new PluginRefusedException(NotUnderstood("notifications", request.Member));

        if (call.Notification is null)
            throw new PluginRefusedException(NotUnderstood("notifications", request.Member));

        await notifications.PushAsync(
            call.User is null ? null : new UserId(Ulid.Parse(call.User)),
            call.Notification
        );

        return PluginCallResponse.Value("null");
    }

    private async Task<PluginCallResponse> Users(PluginCallRequest request)
    {
        if (request.Member != nameof(IPluginUsers.ListAsync))
            return NoSuchMember("users", request.Member);

        if (capabilities.Check(pluginId, PluginCapabilityNames.UsersList) is { } refusal)
            return PluginCallResponse.Refused(Wire(refusal));

        return Value(await users.ListAsync());
    }

    /// <summary>
    /// Work the plugin asks the server to run later.
    /// <para>
    /// The queue stays the server's. A plugin holding its own timer would keep
    /// running after the owner disabled it, and nothing on the jobs page would
    /// show what was still going.
    /// </para>
    /// </summary>
    private async Task<PluginCallResponse> Scheduler(PluginCallRequest request)
    {
        if (capabilities.Check(pluginId, PluginCapabilityNames.Scheduler) is { } refusal)
            return PluginCallResponse.Refused(Wire(refusal));

        ScheduleCall call =
            JsonSerializer.Deserialize<ScheduleCall>(request.PayloadJson, Json) ?? new(null, null);

        string name = call.Name ?? string.Empty;

        switch (request.Member)
        {
            case nameof(IPluginScheduler.RunNowAsync):
                return Value(await scheduler.RunNowAsync(name));

            case nameof(IPluginScheduler.RunOnceAsync):
                return Value(
                    await scheduler.RunOnceAsync(name, call.When ?? DateTimeOffset.UtcNow)
                );

            case nameof(IPluginScheduler.StopWorkerAsync):
                await scheduler.StopWorkerAsync(name);
                return PluginCallResponse.Value("null");

            case nameof(IPluginScheduler.WorkerStateAsync):
                return Value(await scheduler.WorkerStateAsync(name));

            default:
                return NoSuchMember("scheduler", request.Member);
        }
    }

    /// <summary>
    /// The plugin's own settings, as the owner set them.
    /// <para>
    /// The value crosses as raw JSON rather than a typed object: the contract
    /// reads settings generically, and the type a plugin asks for lives only
    /// in the plugin's own assembly, which the server does not load.
    /// </para>
    /// </summary>
    private async Task<PluginCallResponse> Settings(PluginCallRequest request)
    {
        if (capabilities.Check(pluginId, PluginCapabilityNames.Settings) is { } refusal)
            return PluginCallResponse.Refused(Wire(refusal));

        SettingCall call =
            JsonSerializer.Deserialize<SettingCall>(request.PayloadJson, Json) ?? new(null, null);

        string key = call.Key ?? string.Empty;

        switch (request.Member)
        {
            case nameof(IPluginSettings.Get):
                return PluginCallResponse.Value(Raw(settings.Get<JsonElement?>(key)));

            case nameof(IPluginSettings.GetForUser):
                return PluginCallResponse.Value(Raw(settings.GetForUser<JsonElement?>(key)));

            case nameof(IPluginSettings.SetAsync):
                await settings.SetAsync(key, call.Value);
                return PluginCallResponse.Value("null");

            case nameof(IPluginSettings.SetForUserAsync):
                await settings.SetForUserAsync(key, call.Value);
                return PluginCallResponse.Value("null");

            default:
                return NoSuchMember("settings", request.Member);
        }
    }

    /// <summary>
    /// What one person has watched, saved and chosen.
    /// <para>
    /// Each answer is its own capability rather than one for the lot: a plugin
    /// that needs a display name has no business reading a watch history, and
    /// the owner's permissions page says so line by line.
    /// </para>
    /// </summary>
    private async Task<PluginCallResponse> User(PluginCallRequest request)
    {
        switch (request.Member)
        {
            case nameof(IPluginUserData.IdentityAsync):
                return await Gated(
                    PluginCapabilityNames.UserIdentity,
                    async () => Value(await user.IdentityAsync())
                );

            case nameof(IPluginUserData.WatchAsync):
                return await Gated(
                    PluginCapabilityNames.UserWatch,
                    async () => Value(await user.WatchAsync())
                );

            case nameof(IPluginUserData.PlaylistsAsync):
                return await Gated(
                    PluginCapabilityNames.UserPlaylists,
                    async () => Value(await user.PlaylistsAsync())
                );

            case nameof(IPluginUserData.PreferencesAsync):
                return await Gated(
                    PluginCapabilityNames.UserPreferences,
                    async () => Value(await user.PreferencesAsync())
                );

            default:
                return NoSuchMember("user", request.Member);
        }
    }

    private async Task<PluginCallResponse> Gated(
        string capability,
        Func<Task<PluginCallResponse>> answer
    ) =>
        capabilities.Check(pluginId, capability) is { } refusal
            ? PluginCallResponse.Refused(Wire(refusal))
            : await answer();

    private async Task<PluginCallResponse> LibraryWriter(PluginCallRequest request)
    {
        if (capabilities.Check(pluginId, PluginCapabilityNames.LibraryWrite) is { } refusal)
            return PluginCallResponse.Refused(Wire(refusal));

        if (libraryWriter is null)
            return Absent("IPluginContext.LibraryWriter");

        WriterCall call =
            JsonSerializer.Deserialize<WriterCall>(request.PayloadJson, Json) ?? new(null, null);

        switch (request.Member)
        {
            case nameof(IPluginLibraryWriter.GetWritableLibrariesAsync):
                return Value(await libraryWriter.GetWritableLibrariesAsync());

            case nameof(IPluginLibraryWriter.RecycleAsync):
                await libraryWriter.RecycleAsync(call.Path ?? string.Empty);
                return PluginCallResponse.Value("null");

            case nameof(IPluginLibraryWriter.DeleteAsync):
                await libraryWriter.DeleteAsync(call.Path ?? string.Empty);
                return PluginCallResponse.Value("null");

            case nameof(IPluginLibraryWriter.MoveAsync):
                await libraryWriter.MoveAsync(
                    call.Path ?? string.Empty,
                    call.DestinationPath ?? string.Empty
                );
                return PluginCallResponse.Value("null");

            case nameof(IPluginLibraryWriter.CanWriteAsync):
                return Value(await libraryWriter.CanWriteAsync(call.Path ?? string.Empty));

            default:
                return NoSuchMember("libraryWriter", request.Member);
        }
    }

    private async Task<PluginCallResponse> LibraryImport(PluginCallRequest request)
    {
        if (capabilities.Check(pluginId, PluginCapabilityNames.LibraryImport) is { } refusal)
            return PluginCallResponse.Refused(Wire(refusal));

        if (libraryImport is null)
            return Absent("IPluginContext.LibraryImport");

        ImportCall? call = JsonSerializer.Deserialize<ImportCall>(request.PayloadJson, Json);

        if (call?.Request is null)
            return PluginCallResponse.Refused(Wire(NotUnderstood("libraryImport", request.Member)));

        return request.Member switch
        {
            nameof(IPluginLibraryImport.RegisterAsync) => Value(
                await libraryImport.RegisterAsync(call.Request)
            ),
            nameof(IPluginLibraryImport.StreamAsync) => Value(
                await libraryImport.StreamAsync(call.Request)
            ),
            _ => NoSuchMember("libraryImport", request.Member),
        };
    }

    /// <summary>
    /// Answers which file, and loads nothing.
    /// <para>
    /// A native library loaded here would land in the server's own load
    /// context, where the plugin's <c>DllImport</c> declarations never look and
    /// where its code would run with the server's rights. The plugin's process
    /// loads it, so it lands in the load context that asked for it, inside the
    /// sandbox that already holds that process.
    /// </para>
    /// </summary>
    private PluginCallResponse Native(PluginCallRequest request)
    {
        if (request.Member != nameof(IPluginNative.LoadAsync))
            return NoSuchMember("native", request.Member);

        if (capabilities.Check(pluginId, PluginCapabilityNames.NativeCode) is { } refusal)
            return PluginCallResponse.Refused(Wire(refusal));

        NativeCall call =
            JsonSerializer.Deserialize<NativeCall>(request.PayloadJson, Json) ?? new(null);

        string library = call.Library ?? string.Empty;

        // A bare name, never a path. The signature says the marketplace built
        // the bundle; it says nothing about a file reached from outside it.
        if (string.IsNullOrWhiteSpace(library) || IsPath(library))
            return PluginCallResponse.Refused(
                Wire(PluginRefusalMessages.FileOutsideGrant(pluginId.ToString(), library))
            );

        if (signature?.IsMarketplaceSigned(pluginId) != true)
            return PluginCallResponse.Refused(
                Wire(PluginRefusalMessages.NativeCodeUnsigned(pluginId.ToString(), library))
            );

        string path = Path.Combine(pluginDirectory ?? string.Empty, NativeFileName(library));

        if (!File.Exists(path))
            return PluginCallResponse.Refused(
                Wire(PluginRefusalMessages.FileOutsideGrant(pluginId.ToString(), library))
            );

        return Value(new PluginNativePermit(path));
    }

    private static bool IsPath(string library) =>
        library.Contains('/')
        || library.Contains('\\')
        || library.Contains("..")
        || library.Contains(':');

    private static string NativeFileName(string library)
    {
        if (OperatingSystem.IsWindows())
            return $"{library}.dll";

        return OperatingSystem.IsMacOS() ? $"lib{library}.dylib" : $"lib{library}.so";
    }

    private async Task<PluginCallResponse> MediaProxy(PluginCallRequest request)
    {
        if (capabilities.Check(pluginId, PluginCapabilityNames.MediaProxy) is { } refusal)
            return PluginCallResponse.Refused(Wire(refusal));

        if (media is null)
            return Absent("IPluginContext.Media");

        ProxyCall call =
            JsonSerializer.Deserialize<ProxyCall>(request.PayloadJson, Json) ?? new(null, null);

        switch (request.Member)
        {
            case nameof(IPluginMediaProxy.MintAsync):
                if (call.Request is null)
                    return PluginCallResponse.Refused(
                        Wire(NotUnderstood("media.proxy", request.Member))
                    );

                return Value(await media.Proxy.MintAsync(call.Request));

            case nameof(IPluginMediaProxy.MintImageAsync):
                if (call.Source is null)
                    return PluginCallResponse.Refused(
                        Wire(NotUnderstood("media.proxy", request.Member))
                    );

                return Value(await media.Proxy.MintImageAsync(call.Source));

            default:
                return NoSuchMember("media.proxy", request.Member);
        }
    }

    private async Task<PluginCallResponse> MediaTranscode(PluginCallRequest request)
    {
        if (capabilities.Check(pluginId, PluginCapabilityNames.MediaTranscode) is { } refusal)
            return PluginCallResponse.Refused(Wire(refusal));

        if (media is null)
            return Absent("IPluginContext.Media");

        if (request.Member != nameof(IPluginMediaTranscode.StartAsync))
            return NoSuchMember("media.transcode", request.Member);

        RemuxCall? call = JsonSerializer.Deserialize<RemuxCall>(request.PayloadJson, Json);

        if (call?.Request is null)
            return PluginCallResponse.Refused(
                Wire(NotUnderstood("media.transcode", request.Member))
            );

        return Value(await media.Transcode.StartAsync(call.Request));
    }

    private async Task<PluginCallResponse> MediaRemux(PluginCallRequest request)
    {
        if (capabilities.Check(pluginId, PluginCapabilityNames.MediaRemux) is { } refusal)
            return PluginCallResponse.Refused(Wire(refusal));

        if (media is null)
            return Absent("IPluginContext.Media");

        if (request.Member != nameof(IPluginMediaRemux.StartAsync))
            return NoSuchMember("media.remux", request.Member);

        RemuxCall? call = JsonSerializer.Deserialize<RemuxCall>(request.PayloadJson, Json);

        if (call?.Request is null)
            return PluginCallResponse.Refused(Wire(NotUnderstood("media.remux", request.Member)));

        return Value(await media.Remux.StartAsync(call.Request));
    }

    private async Task<PluginCallResponse> MediaLive(PluginCallRequest request)
    {
        if (capabilities.Check(pluginId, PluginCapabilityNames.MediaLive) is { } refusal)
            return PluginCallResponse.Refused(Wire(refusal));

        if (media is null)
            return Absent("IPluginContext.Media");

        LiveCall call =
            JsonSerializer.Deserialize<LiveCall>(request.PayloadJson, Json)
            ?? new(null, null, null);

        switch (request.Member)
        {
            case nameof(IPluginMediaLive.PublishAsync):
                await media.Live.PublishAsync(call.Channels ?? []);
                return PluginCallResponse.Value("null");

            case nameof(IPluginMediaLive.PublishGuideAsync):
                await media.Live.PublishGuideAsync(call.Guide ?? []);
                return PluginCallResponse.Value("null");

            case nameof(IPluginMediaLive.PublishGroupsAsync):
                await media.Live.PublishGroupsAsync(call.Groups ?? []);
                return PluginCallResponse.Value("null");

            default:
                return NoSuchMember("media.live", request.Member);
        }
    }

    private async Task<PluginCallResponse> MediaRecord(PluginCallRequest request)
    {
        if (capabilities.Check(pluginId, PluginCapabilityNames.MediaRecord) is { } refusal)
            return PluginCallResponse.Refused(Wire(refusal));

        if (media is null)
            return Absent("IPluginContext.Media");

        RecordCall call =
            JsonSerializer.Deserialize<RecordCall>(request.PayloadJson, Json) ?? new(null, null);

        switch (request.Member)
        {
            case nameof(IPluginRecorder.ScheduleAsync):
                if (call.Request is null)
                    return PluginCallResponse.Refused(
                        Wire(NotUnderstood("media.record", request.Member))
                    );

                return Value(await media.Record.ScheduleAsync(call.Request));

            case nameof(IPluginRecorder.CancelAsync):
                if (call.Recording is null)
                    return PluginCallResponse.Refused(
                        Wire(NotUnderstood("media.record", request.Member))
                    );

                await media.Record.CancelAsync(call.Recording.Value);
                return PluginCallResponse.Value("null");

            case nameof(IPluginRecorder.ListAsync):
                return Value(await media.Record.ListAsync());

            default:
                return NoSuchMember("media.record", request.Member);
        }
    }

    /// <summary>
    /// The capability was granted and the facade is still not here, which is a
    /// fact about this install rather than about the plugin.
    /// </summary>
    private PluginCallResponse Absent(string facade) =>
        PluginCallResponse.Refused(
            Wire(PluginRefusalMessages.FacadeNotOnThisHost(pluginId.ToString(), facade))
        );

    /// <summary>A value that is already JSON, passed through rather than wrapped again.</summary>
    private static string Raw(JsonElement? value) =>
        value is null ? "null" : value.Value.GetRawText();

    private PluginCallResponse NoSuchMember(string facade, string member) =>
        Refuse(
            PluginRefusalCodes.HostServicesRemoved,
            $"The plugin asked the server for {facade}.{member}.",
            $"The {facade} facade does not carry that member across the process boundary yet.",
            "Run this plugin in the server's own process until it is carried across. Docs: /nomercy-plugins/handbook/runtime-and-isolation"
        );

    private PluginRefusal NotUnderstood(string facade, string member) =>
        new(
            PluginRefusalCodes.HostServicesRemoved,
            pluginId.ToString(),
            $"The plugin called {facade}.{member} with a payload the server could not read.",
            "The arguments did not arrive in the shape the contract declares.",
            "Report this with the server log around the call. Docs: /nomercy-plugins/handbook/runtime-and-isolation",
            PluginRefusalSeverity.Blocked
        );

    public Task<PluginCallResponse> PublishAsync(
        PluginCallRequest request,
        CallContext context = default
    ) => CallAsync(request, context);

    /// <summary>
    /// Deliveries to the plugin. Empty until the hooks cross in their own
    /// task; an enumerable that ended would look to the child like a server
    /// that had nothing more to say rather than one that never started.
    /// </summary>
    public async IAsyncEnumerable<PluginCallRequest> SubscribeAsync(
        PluginCallRequest request,
        CallContext context = default
    )
    {
        await Task.CompletedTask;
        yield break;
    }

    private static PluginCallResponse Value<T>(T value) =>
        PluginCallResponse.Value(JsonSerializer.Serialize(value, Json));

    private PluginCallResponse Refuse(string code, string what, string why, string fix) =>
        PluginCallResponse.Refused(
            new WireRefusal(
                code,
                pluginId.ToString(),
                what,
                why,
                fix,
                PluginRefusalSeverity.Blocked.ToString()
            )
        );

    private static WireRefusal Wire(PluginRefusal refusal) =>
        new(
            refusal.Code,
            refusal.Plugin,
            refusal.What,
            refusal.Why,
            refusal.Fix,
            refusal.Severity.ToString()
        );

    private sealed record SecretCall(string? Key, string? Value);

    private sealed record SpawnCall(string? Binary);

    private sealed record FolderCall(string? FolderId);

    private sealed record DialCall(string? Host, int Port);

    private sealed record LibraryCall(string? LibraryId, int ShowId);

    private sealed record PushCall(string? User, PluginNotification? Notification);

    private sealed record ScheduleCall(string? Name, DateTimeOffset? When);

    private sealed record SettingCall(string? Key, JsonElement? Value);

    private sealed record WriterCall(string? Path, string? DestinationPath);

    private sealed record ImportCall(PluginImportRequest? Request);

    private sealed record NativeCall(string? Library);

    private sealed record ProxyCall(PluginProxyRequest? Request, Uri? Source);

    private sealed record RemuxCall(PluginRemuxRequest? Request);

    private sealed record LiveCall(
        IReadOnlyList<PluginLiveChannel>? Channels,
        IReadOnlyList<PluginEpgProgram>? Guide,
        IReadOnlyList<PluginChannelGroup>? Groups
    );

    private sealed record RecordCall(PluginRecordingRequest? Request, JobId? Recording);
}
