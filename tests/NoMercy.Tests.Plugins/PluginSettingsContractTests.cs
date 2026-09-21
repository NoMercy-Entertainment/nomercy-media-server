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

using FluentAssertions;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginSettingsContractTests
{
    [Fact]
    public void A_field_defaults_to_the_server_scope_and_read_only()
    {
        PluginSettingsField field = new()
        {
            Key = "catalog.url",
            LabelKey = "radio.settings.catalog",
            Type = PluginFormFieldType.Text,
        };

        field.Scope.Should().Be(PluginSettingsScope.Server);
        field
            .Writable.Should()
            .BeFalse("a field the plugin overwrites is one the owner cannot keep set");
    }

    [Fact]
    public void A_per_user_field_says_so_so_a_member_may_open_the_page()
    {
        PluginSettingsField field = new()
        {
            Key = "favorites.sort",
            LabelKey = "radio.settings.sort",
            Type = PluginFormFieldType.Select,
            Scope = PluginSettingsScope.User,
        };

        field.Scope.Should().Be(PluginSettingsScope.User);
    }

    [Fact]
    public void A_password_field_is_backed_by_secrets_and_nothing_else_is()
    {
        PluginSettingsField password = new()
        {
            Key = "provider.password",
            LabelKey = "iptv.settings.password",
            Type = PluginFormFieldType.Password,
        };
        PluginSettingsField text = password with { Type = PluginFormFieldType.Text };

        password.BackedBySecrets.Should().BeTrue();
        text.BackedBySecrets.Should()
            .BeFalse("everything in the secret store is one more thing to migrate and back up");
    }

    [Fact]
    public void Reading_a_password_from_settings_refuses_and_points_at_secrets()
    {
        PluginRefusal refusal = PluginRefusalMessages.SecretFieldInSettings(
            "Live TV 1.0.0",
            "provider.password"
        );

        refusal.Code.Should().Be(PluginRefusalCodes.SecretFieldInSettings);
        refusal.Fix.Should().Contain("context.Secrets");
        refusal.What.Should().Contain("provider.password");
    }

    [Fact]
    public void Writing_a_field_the_schema_marks_read_only_refuses()
    {
        PluginRefusal refusal = PluginRefusalMessages.SettingsFieldReadOnly(
            "Live TV 1.0.0",
            "provider.url"
        );

        refusal.Code.Should().Be(PluginRefusalCodes.SettingsFieldReadOnly);
        refusal.Why.Should().Contain("the owner");
    }

    [Fact]
    public void Secrets_can_be_held_per_user()
    {
        typeof(IPluginSecretStore).GetMethod("GetForUserAsync").Should().NotBeNull();
        typeof(IPluginSecretStore).GetMethod("SetForUserAsync").Should().NotBeNull();
        typeof(IPluginSecretStore).GetMethod("DeleteForUserAsync").Should().NotBeNull();
    }

    [Fact]
    public void A_per_user_secret_takes_no_user_id_because_it_is_the_callers()
    {
        typeof(IPluginSecretStore)
            .GetMethod("GetForUserAsync")!
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .Should()
            .Equal(typeof(string), typeof(CancellationToken));
    }

    [Fact]
    public void The_schema_is_generated_and_carries_the_scope_and_writable_rules()
    {
        PluginSettingsSchema.Json.Should().Contain("\"scope\"");
        PluginSettingsSchema.Json.Should().Contain("\"writable\"");
        PluginSettingsSchema
            .Json.Should()
            .Contain("\"password\"", "the host has to know which field to put in the secret store");
    }

    [Fact]
    public void Settings_announce_a_change_so_a_held_connection_is_rebuilt()
    {
        typeof(IPluginSettings)
            .GetEvent("SettingsChanged")
            .Should()
            .NotBeNull(
                "a plugin holding a connection built from a setting serves the old one until a restart otherwise"
            );
    }
}
