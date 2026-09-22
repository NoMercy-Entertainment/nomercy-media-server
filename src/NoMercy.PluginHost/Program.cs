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

using System.Collections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using NoMercy.NmSystem.Information;
using NoMercy.PluginHost;
using NoMercy.PluginSdk.Ipc;
using ProtoBuf.Grpc.Server;

Dictionary<string, string?> environment = Environment
    .GetEnvironmentVariables()
    .Cast<DictionaryEntry>()
    .ToDictionary(entry => (string)entry.Key, entry => entry.Value as string);

// Said on stderr and gone. A process that cannot know which plugin it is has
// nowhere to send a refusal, and the server reads the exit code.
if (!PluginHostLaunch.TryRead(environment, out PluginHostLaunch? launch, out WireRefusal? refusal))
{
    await Console.Error.WriteLineAsync(
        $"{refusal!.Code}: {refusal.What} {refusal.Why} {refusal.Fix}"
    );

    return 78;
}

SelfLimits.Apply(environment);

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

builder.WebHost.ConfigureKestrel(options =>
{
    // Local only, both ways. A TCP port would be reachable from the network
    // this process is not allowed to serve on.
    if (Software.IsWindows)
    {
        options.ListenNamedPipe(
            launch!.HostEndpoint,
            listen => listen.Protocols = HttpProtocols.Http2
        );

        return;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(launch!.HostEndpoint)!);
    File.Delete(launch.HostEndpoint);
    options.ListenUnixSocket(launch.HostEndpoint, listen => listen.Protocols = HttpProtocols.Http2);
});

builder.Services.AddCodeFirstGrpc();
builder.Services.AddSingleton(launch!);

// The only way this process reaches the server. Without it the context is
// built with no broker and every facade throws on resolution, which reads to
// the owner as a plugin that would not start rather than a channel nobody
// dialed.
builder.Services.AddSingleton<BrokerChannel>();
builder.Services.AddSingleton<IPluginBrokerService>(provider =>
    provider.GetRequiredService<BrokerChannel>()
);
builder.Services.AddSingleton<RemotePluginContext>();
builder.Services.AddSingleton<IHostedPlugin>(provider => new HostedPlugin(
    launch!,
    provider.GetRequiredService<RemotePluginContext>()
));
builder.Services.AddSingleton<IPluginHostService>(provider => new PluginHostService(
    provider.GetRequiredService<IHostedPlugin>(),
    launch!.Token
));

WebApplication app = builder.Build();
app.MapGrpcService<PluginHostService>();

await app.RunAsync(PluginHostLifetime.Stopping);

return 0;
