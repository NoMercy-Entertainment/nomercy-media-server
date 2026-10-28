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

using NoMercy.Encoder.Errors;

namespace NoMercy.Api.Services.Visualizers;

/// <summary>Checks the fields outside the payload (contract 2a, 6.3). A null argument is not checked.</summary>
public static class VisualizerMetadataValidator
{
    private const string Fix = "Match the metadata rules of the visualizer library contract.";

    public static List<EncoderRule> Validate(
        string? name,
        string? author,
        string? license,
        string? sourceUrl,
        IReadOnlyList<string>? tags,
        bool nameRequired
    )
    {
        List<EncoderRule> rules = [];

        if (name is not null || nameRequired)
            Text(rules, "/name", name, true);
        if (author is not null || nameRequired)
            Text(rules, "/author", author, true);
        if (license is not null || nameRequired)
            Text(rules, "/license", license, true);

        if (
            sourceUrl is not null
            && (
                sourceUrl.Length > 2048
                || !Uri.TryCreate(sourceUrl, UriKind.Absolute, out Uri? uri)
                || uri.Scheme != Uri.UriSchemeHttps
            )
        )
            Add(
                rules,
                VisualizerRuleId.SchemaInvalid,
                "/source_url",
                "source_url must be an absolute https URL of up to 2048 characters."
            );

        if (tags is not null)
        {
            if (tags.Count > 32)
                Add(rules, VisualizerRuleId.SchemaInvalid, "/tags", "At most 32 tags are allowed.");
            for (int i = 0; i < tags.Count; i++)
            {
                string tag = tags[i];
                if (tag.Length is < 1 or > 64 || tag.Contains(',') || tag.Any(char.IsControl))
                    Add(
                        rules,
                        VisualizerRuleId.SchemaInvalid,
                        $"/tags/{i}",
                        "A tag is 1 to 64 characters without a comma or control character."
                    );
            }
        }

        return rules;
    }

    public static List<EncoderRule> ValidateFormat(string? format)
    {
        List<EncoderRule> rules = [];
        if (format is null || !VisualizerPayloadValidator.IsKnownFormat(format))
            Add(
                rules,
                VisualizerRuleId.UnknownFormat,
                "/format",
                "format is not one of the seven formats."
            );
        return rules;
    }

    private static void Text(List<EncoderRule> rules, string field, string? value, bool required)
    {
        string trimmed = value?.Trim() ?? "";
        if (trimmed.Length == 0 && required)
        {
            string id =
                field == "/name" ? VisualizerRuleId.SchemaInvalid : VisualizerRuleId.FieldRequired;
            Add(rules, id, field, $"{field[1..]} is required.");
        }
        else if (trimmed.Length > 256 || trimmed.Any(char.IsControl))
            Add(
                rules,
                VisualizerRuleId.SchemaInvalid,
                field,
                $"{field[1..]} is 1 to 256 characters without control characters."
            );
    }

    private static void Add(List<EncoderRule> rules, string id, string field, string message) =>
        rules.Add(new(id, EncoderRuleSeverity.Error, field, message, Fix));
}
