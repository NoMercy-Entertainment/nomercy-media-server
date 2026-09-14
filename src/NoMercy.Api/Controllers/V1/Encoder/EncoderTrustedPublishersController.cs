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

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using NoMercy.Authorization;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Media;
using NoMercy.Encoder.Errors;
using NoMercy.Encoder.Profiles;

namespace NoMercy.Api.Controllers.V1.Encoder;

/// <summary>
/// Admin-only endpoints for managing Ed25519 trusted publisher keys used to
/// verify signatures on imported encoding profiles. All operations require the
/// requesting user to be the server owner.
/// </summary>
[ApiController]
[Tags("Encoder Trusted Publishers")]
[ApiVersion(1.0)]
[Authorize(Policy = "Owner")]
[Route("api/v{version:apiVersion}/encoder/trusted-publishers")]
public class EncoderTrustedPublishersController(ITrustedPublisherKeyRepository trustedKeys)
    : BaseController
{
    /// <summary>
    /// Returns all registered trusted publisher keys.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        List<TrustedPublisherKey> keys = await trustedKeys.GetAllAsync();

        return Ok(new { data = keys });
    }

    /// <summary>
    /// Registers a new trusted Ed25519 public key.
    /// The fingerprint (SHA-256 hex of the raw key bytes) is computed server-side
    /// so the caller only needs to supply the base64-encoded public key and a label.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] AddTrustedPublisherRequest request)
    {
        // --- Validate base64 decodes to exactly 32 bytes (Ed25519 key length) ---
        byte[] publicKeyBytes;
        try
        {
            publicKeyBytes = Convert.FromBase64String(request.PublicKeyBase64);
        }
        catch (FormatException)
        {
            return InvalidPublicKey();
        }

        if (publicKeyBytes.Length != 32)
        {
            return InvalidPublicKey();
        }

        // --- Compute fingerprint ---
        string fingerprint = PublicKeyFingerprint.Compute(publicKeyBytes);

        // --- Conflict check ---
        if (await trustedKeys.ExistsAsync(fingerprint))
        {
            ValidationEnvelope conflictError = ValidationEnvelope.FromRules([
                new(
                    EncoderRuleId.TrustedPublisherAlreadyTrusted,
                    EncoderRuleSeverity.Error,
                    "public_key_base64",
                    $"A trusted key with fingerprint '{fingerprint}' is already registered.",
                    "This public key is already trusted. No action needed."
                ),
            ]);
            return Conflict(conflictError);
        }

        // --- Persist ---
        TrustedPublisherKey row = new()
        {
            Fingerprint = fingerprint,
            Label = request.Label,
            PublicKeyBase64 = request.PublicKeyBase64,
            AddedAt = DateTime.UtcNow,
            AddedBy = User.UserId().ToString(),
        };

        await trustedKeys.AddAsync(row);

        return CreatedAtAction(nameof(Create), new { fingerprint = row.Fingerprint }, row);
    }

    /// <summary>
    /// Removes a trusted publisher key by its fingerprint. Returns 404 when
    /// the fingerprint is not registered.
    /// </summary>
    [HttpDelete("{fingerprint}")]
    public async Task<IActionResult> Delete(string fingerprint)
    {
        if (!await trustedKeys.DeleteAsync(fingerprint))
            return NotFoundResponse($"No trusted key with fingerprint '{fingerprint}' found");

        return NoContent();
    }

    private UnprocessableEntityObjectResult InvalidPublicKey() =>
        UnprocessableEntity(
            ValidationEnvelope.FromRules([
                new(
                    EncoderRuleId.TrustedPublisherPublicKeyInvalid,
                    EncoderRuleSeverity.Error,
                    "public_key_base64",
                    "Public key must be a 32-byte Ed25519 key, base64-encoded.",
                    "Re-export the publisher's key with `openssl pkey -in key.pem -pubout -outform DER | tail -c 32 | base64`."
                ),
            ])
        );
}
