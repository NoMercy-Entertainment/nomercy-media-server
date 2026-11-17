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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NoMercy.Encoder.Subscribers;

/// <summary>
/// Forces resolution of the V3 event-bus subscribers at host startup so their
/// constructors run and they subscribe to the event bus. The subscribers are
/// registered as singletons and self-subscribe in their constructors; without
/// this activator they would never be resolved and therefore never receive
/// events.
///
/// Auto-encode dispatch lives solely in
/// <c>NoMercy.MediaProcessing.EventHandlers.AutoEncodeSubscriber</c>, gated
/// per-library. Each subscriber activated here also exposes a per-class
/// enable flag on <c>EncoderOptions</c> for hard opt-out.
/// </summary>
internal sealed class V3SubscriberActivator(
    IServiceProvider services,
    ILogger<V3SubscriberActivator> logger
) : IHostedService, IDisposable
{
    private CropDetectSubscriber? _cropDetect;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _cropDetect = services.GetRequiredService<CropDetectSubscriber>();

            logger.LogInformation("V3 encoder subscribers activated (crop_detect)");
        }
        catch (Exception ex)
        {
            // Resolving any of these can throw if the encoder DI graph is
            // mid-migration or a plugin tampered with the registration. The
            // server can still serve media without the V3 subscribers; don't
            // let StopHost-on-throw take the process down for an optional
            // post-encode pipeline.
            logger.LogError(
                ex,
                "V3 subscriber activation failed; encoder pipeline runs without them"
            );
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _cropDetect = null;
    }
}
