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

using System.Reflection;
using NoMercy.Encoder.Errors;

namespace NoMercy.Tests.Encoder.Errors;

/// <summary>
/// Generator test — Phase 4.19 of encoder-v3-alignment.md.
///
/// Asserts that:
///   1. Every const on <see cref="EncoderRuleId"/> exists on
///      <see cref="EncoderRuntimeErrorId"/> (if it is a runtime error ID).
///   2. Every const on <see cref="EncoderRuntimeErrorId"/> exists on
///      <see cref="EncoderRuleId"/> (alias integrity check).
///   3. Every const on both classes has a matching entry in
///      <c>docs/encoder-errors.md</c> in the repository root.
/// </summary>
public class RuntimeErrorCatalogTests
{
    private static readonly string[] RuntimeErrorIds =
    [
        .. typeof(EncoderRuntimeErrorId)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false })
            .Select(f => (string)f.GetValue(null)!),
    ];

    private static readonly string[] AllRuleIds =
    [
        .. typeof(EncoderRuleId)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false })
            .Select(f => (string)f.GetValue(null)!),
    ];

    // ---- helper: locate docs/encoder-errors.md relative to the solution root ----

    private static string FindDocsFile()
    {
        // Walk up from the test assembly until we find the docs directory.
        DirectoryInfo? dir = new FileInfo(
            typeof(RuntimeErrorCatalogTests).Assembly.Location
        ).Directory;

        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "docs", "encoder-errors.md");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate docs/encoder-errors.md. "
                + "Expected it at the repository root next to apps/, docs/, infra/ etc."
        );
    }

    // ---- 1. EncoderRuntimeErrorId values are all present in EncoderRuleId ------

    [Fact]
    public void Every_runtime_error_constant_is_aliased_from_EncoderRuleId()
    {
        foreach (string id in RuntimeErrorIds)
        {
            AllRuleIds
                .Should()
                .Contain(
                    id,
                    $"EncoderRuntimeErrorId.{id} must be an alias of an EncoderRuleId constant"
                );
        }
    }

    // ---- 2. EncoderRuntimeErrorId covers the required 21 runtime error IDs -----

    [Theory]
    [InlineData("hardware.forced_but_unavailable")]
    [InlineData("hardware.gpu_telemetry_unsupported")]
    [InlineData("gpu_capacity_exhausted")]
    [InlineData("encoder.init_failed")]
    [InlineData("source.not_accessible")]
    [InlineData("source.read_error")]
    [InlineData("output.write_error")]
    [InlineData("output.path_not_allowed")]
    [InlineData("license.revoked")]
    [InlineData("license.unreachable")]
    [InlineData("capability.fpcalc_missing")]
    [InlineData("capability.whisper_missing")]
    [InlineData("capability.stemsplit_missing")]
    [InlineData("capability.tesseract_model_missing")]
    [InlineData("disc.drive_busy")]
    [InlineData("disc.aacs_cert_missing")]
    [InlineData("disc.bdplus_converter_missing")]
    [InlineData("disc.read_error")]
    [InlineData("job.interrupted_no_checkpoint")]
    [InlineData("distribution.hmac_invalid")]
    [InlineData("distribution.timestamp_replay")]
    [InlineData("distribution.worker_not_registered")]
    public void Required_runtime_error_id_is_present(string expectedId)
    {
        RuntimeErrorIds
            .Should()
            .Contain(
                expectedId,
                $"EncoderRuntimeErrorId must declare the required runtime error ID '{expectedId}'"
            );
    }

    // ---- 3. Every EncoderRuleId constant has a docs entry ----------------------

    [Fact]
    public void Every_EncoderRuleId_constant_has_a_docs_entry()
    {
        string docsFile = FindDocsFile();
        string docsContent = File.ReadAllText(docsFile);

        List<string> missing = [.. AllRuleIds.Where(id => !docsContent.Contains($"`{id}`"))];

        missing
            .Should()
            .BeEmpty(
                "The following EncoderRuleId constants are missing from docs/encoder-errors.md: "
                    + string.Join(", ", missing)
            );
    }

    // ---- 4. Every EncoderRuntimeErrorId constant has a docs entry --------------

    [Fact]
    public void Every_EncoderRuntimeErrorId_constant_has_a_docs_entry()
    {
        string docsFile = FindDocsFile();
        string docsContent = File.ReadAllText(docsFile);

        List<string> missing = [.. RuntimeErrorIds.Where(id => !docsContent.Contains($"`{id}`"))];

        missing
            .Should()
            .BeEmpty(
                "The following EncoderRuntimeErrorId constants are missing from docs/encoder-errors.md: "
                    + string.Join(", ", missing)
            );
    }

    // ---- 5. Constant count guard — catches accidental deletions ----------------

    [Fact]
    public void EncoderRuntimeErrorId_declares_exactly_22_constants()
    {
        RuntimeErrorIds.Should().HaveCount(22);
    }
}
