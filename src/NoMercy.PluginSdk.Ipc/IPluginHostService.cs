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

using System.ServiceModel;
using ProtoBuf.Grpc;

namespace NoMercy.PluginSdk.Ipc;

/// <summary>What the server asks of one plugin's process.</summary>
[ServiceContract(Name = "nomercy.plugins.Host")]
public interface IPluginHostService
{
    [OperationContract]
    Task<PluginCallResponse> InitializeAsync(
        PluginCallRequest request,
        CallContext context = default
    );

    [OperationContract]
    Task<PluginCallResponse> InvokeAsync(PluginCallRequest request, CallContext context = default);

    [OperationContract]
    Task<PluginHealthSnapshot> HealthAsync(
        PluginCallRequest request,
        CallContext context = default
    );

    [OperationContract]
    Task<PluginCallResponse> ShutdownAsync(
        PluginCallRequest request,
        CallContext context = default
    );
}
