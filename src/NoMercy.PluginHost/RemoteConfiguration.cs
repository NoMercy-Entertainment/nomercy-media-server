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

namespace NoMercy.PluginHost;

/// <summary>
/// The plugin's own settings document, fetched and stored across the channel.
/// <para>
/// The type parameter stays on this side: the server holds the document as
/// JSON and has no reference to the plugin's assembly, so it could not
/// construct the plugin's own settings type even if it wanted to.
/// </para>
/// </summary>
internal sealed class RemoteConfiguration(RemoteCall call) : IPluginConfiguration
{
    public T? GetConfiguration<T>()
        where T : class, new() => call.Ask<T>("configuration", nameof(GetConfiguration));

    public Task<T?> GetConfigurationAsync<T>(CancellationToken ct = default)
        where T : class, new() => call.AskAsync<T>("configuration", nameof(GetConfiguration))!;

    public void SaveConfiguration<T>(T configuration)
        where T : class =>
        call.Tell("configuration", nameof(SaveConfiguration), new { configuration });

    public Task SaveConfigurationAsync<T>(T configuration, CancellationToken ct = default)
        where T : class =>
        call.TellAsync("configuration", nameof(SaveConfiguration), new { configuration });

    public bool HasConfiguration() => call.Ask<bool>("configuration", nameof(HasConfiguration));

    public void DeleteConfiguration() => call.Tell("configuration", nameof(DeleteConfiguration));
}
