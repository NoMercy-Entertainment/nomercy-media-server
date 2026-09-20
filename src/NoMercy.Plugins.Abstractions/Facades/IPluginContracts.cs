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

/// <summary>
/// Typed, versioned calls between plugins. The called plugin decides; the host
/// only carries serializable data, the calling plugin's id and the calling user.
/// </summary>
public interface IPluginContracts
{
    void Handle<TRequest, TResponse>(
        string contract,
        int version,
        Func<PluginContractCall<TRequest>, CancellationToken, Task<TResponse>> handler
    );

    Task<PluginContractResult<TResponse>> CallAsync<TRequest, TResponse>(
        PluginId target,
        string contract,
        int version,
        TRequest request,
        CancellationToken ct = default
    );
}
