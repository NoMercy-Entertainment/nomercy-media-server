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
namespace NoMercy.Api.Services;

/// <inheritdoc cref="LiveResultKind" />
public sealed record LiveResult
{
    public LiveResultKind Kind { get; init; }

    /// <summary>Error message for the failure kinds; null for <see cref="LiveResultKind.Ok"/>.</summary>
    public string? Message { get; init; }

    /// <summary>Success payload for <see cref="LiveResultKind.Ok"/> (DTO, playlist string, or segment stream).</summary>
    public object? Payload { get; init; }

    /// <summary>HTTP status code carried by an <see cref="LiveResultKind.EncoderError"/>.</summary>
    public int EncoderStatusCode { get; init; }

    /// <summary>Error-shape payload carried by an <see cref="LiveResultKind.EncoderError"/>.</summary>
    public object? EncoderShape { get; init; }

    public static LiveResult Ok(object? payload) =>
        new() { Kind = LiveResultKind.Ok, Payload = payload };

    public static LiveResult BadRequest(string message) =>
        new() { Kind = LiveResultKind.BadRequest, Message = message };

    public static LiveResult NotFound(string message) =>
        new() { Kind = LiveResultKind.NotFound, Message = message };

    public static LiveResult Gone(string message) =>
        new() { Kind = LiveResultKind.Gone, Message = message };

    public static LiveResult ServiceUnavailable(string message) =>
        new() { Kind = LiveResultKind.ServiceUnavailable, Message = message };

    public static LiveResult InternalError(string message) =>
        new() { Kind = LiveResultKind.InternalError, Message = message };

    public static LiveResult Encoder(int statusCode, object? shape) =>
        new()
        {
            Kind = LiveResultKind.EncoderError,
            EncoderStatusCode = statusCode,
            EncoderShape = shape,
        };
}
