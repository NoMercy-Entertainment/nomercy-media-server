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

/// <summary>
/// What a plugin process may ask the server for.
/// <para>
/// Every route into the server is one of these three, so the owner can see and
/// revoke what a plugin reaches rather than discovering it in a stack trace.
/// </para>
/// </summary>
[ServiceContract(Name = "nomercy.plugins.Broker")]
public interface IPluginBrokerService
{
    [OperationContract]
    Task<PluginCallResponse> CallAsync(PluginCallRequest request, CallContext context = default);

    [OperationContract]
    Task<PluginCallResponse> PublishAsync(PluginCallRequest request, CallContext context = default);

    [OperationContract]
    IAsyncEnumerable<PluginCallRequest> SubscribeAsync(
        PluginCallRequest request,
        CallContext context = default
    );
}
