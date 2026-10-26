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

using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using NoMercy.Data.Visualizers;
using NoMercy.Database;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Controllers;

[Trait("Category", "Visualizers")]
public class VisualizersControllerTests : IClassFixture<NoMercyApiFactory>
{
    private const string Base = "/api/v1/visualizers";

    private readonly NoMercyApiFactory _factory;
    private readonly HttpClient _moderator;
    private readonly HttpClient _viewer;

    public VisualizersControllerTests(NoMercyApiFactory factory)
    {
        _factory = factory;
        _moderator = factory.CreateClient().AsAuthenticated();
        _viewer = factory.CreateClient().AsSecondaryUser();

        using IServiceScope scope = factory.Services.CreateScope();
        MediaContext context = scope.ServiceProvider.GetRequiredService<MediaContext>();
        new VisualizerBundleSeeder(context).SeedAsync().GetAwaiter().GetResult();
    }

    private static JObject ShaderPayload() =>
        new()
        {
            ["schema"] = 1,
            ["format"] = "twigl-geek",
            ["source"] = "made up",
            ["passes"] = new JArray
            {
                new JObject
                {
                    ["name"] = "image",
                    ["channels"] = new JObject { ["0"] = "music" },
                    ["program"] = "#version 300 es\nvoid main() {}",
                },
            },
        };

    private static StringContent Json(object body) =>
        new(JsonConvertText(body), Encoding.UTF8, "application/json");

    private static string JsonConvertText(object body) =>
        body is JToken token
            ? token.ToString(Newtonsoft.Json.Formatting.None)
            : JObject.FromObject(body).ToString();

    private static async Task<JObject> BodyOf(HttpResponseMessage response) =>
        JObject.Parse(await response.Content.ReadAsStringAsync());

    private async Task<string> CreateEntry(string name = "Made up entry")
    {
        HttpResponseMessage response = await _moderator.PostAsync(
            Base,
            Json(
                new JObject
                {
                    ["name"] = name,
                    ["author"] = "Test Author",
                    ["license"] = "CC0",
                    ["tags"] = new JArray("test"),
                    ["format"] = "twigl-geek",
                    ["payload"] = ShaderPayload(),
                }
            )
        );
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (string)(await BodyOf(response))["id"]!;
    }

    private async Task<string> BuiltInId(string format = "milkdrop")
    {
        HttpResponseMessage response = await _moderator.GetAsync($"{Base}?format={format}&take=1");
        return (string)(await BodyOf(response))["data"]![0]!["id"]!;
    }

    [Fact]
    public async Task List_ReturnsPagedSummariesWithoutPayload()
    {
        HttpResponseMessage response = await _viewer.GetAsync($"{Base}?take=5&page=0");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("private", response.Headers.CacheControl!.ToString());
        JObject body = await BodyOf(response);
        Assert.Equal(5, ((JArray)body["data"]!).Count);
        Assert.True((bool)body["has_more"]!);
        Assert.Equal(1, (int)body["next_page"]!);
        JObject first = (JObject)body["data"]![0]!;
        Assert.Null(first["payload"]);
        Assert.Equal(26, ((string)first["id"]!).Length);
        foreach (
            string key in new[]
            {
                "slug",
                "content_hash",
                "is_favorite",
                "last_used_at",
                "created_at",
                "renderable",
                "is_built_in",
            }
        )
            Assert.True(first.ContainsKey(key), key);
    }

    [Fact]
    public async Task List_FiltersAndRefusesUnknownValues()
    {
        JObject shaders = await BodyOf(
            await _viewer.GetAsync($"{Base}?format=shadertoy,twigl-geek&take=1000")
        );
        Assert.All(
            (JArray)shaders["data"]!,
            row => Assert.Contains((string)row["format"]!, new[] { "shadertoy", "twigl-geek" })
        );

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await _viewer.GetAsync($"{Base}?format=winamp")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await _viewer.GetAsync($"{Base}?sort=random")).StatusCode
        );
    }

    [Fact]
    public async Task Get_SendsAnEtagAndAnswers304ForIt()
    {
        string id = await BuiltInId();
        HttpResponseMessage first = await _viewer.GetAsync($"{Base}/{id}");
        string etag = first.Headers.ETag!.Tag;

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.NotNull((await BodyOf(first))["payload"]);

        using HttpRequestMessage again = new(HttpMethod.Get, $"{Base}/{id}");
        again.Headers.TryAddWithoutValidation("If-None-Match", etag);
        HttpResponseMessage second = await _viewer.SendAsync(again);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }

    [Fact]
    public async Task Create_RefusesJavaScriptWithTheEnvelopeInCamelCase()
    {
        JObject payload = (JObject)
            (await BodyOf(await _moderator.GetAsync($"{Base}/{await BuiltInId()}")))["payload"]!;
        payload["preset"]!["frame_eqs_str"] = "a=1;";

        HttpResponseMessage response = await _moderator.PostAsync(
            Base,
            Json(
                new JObject
                {
                    ["name"] = "Planted",
                    ["author"] = "Test Author",
                    ["license"] = "CC0",
                    ["format"] = "milkdrop",
                    ["payload"] = payload,
                }
            )
        );

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        JObject envelope = await BodyOf(response);
        Assert.False((bool)envelope["valid"]!);
        JObject rule = (JObject)envelope["errors"]![0]!;
        Assert.Equal("visualizer.payload_has_code", (string)rule["id"]!);
        Assert.Equal("/payload/preset/frame_eqs_str", (string)rule["field"]!);
        Assert.Equal("Error", (string)rule["severity"]!);
        Assert.True(
            rule.ContainsKey("message")
                && rule.ContainsKey("fix")
                && envelope.ContainsKey("warnings")
        );
    }

    [Fact]
    public async Task Create_RefusesARepeatedKeyAndAnOversizedBody()
    {
        HttpResponseMessage repeated = await _moderator.PostAsync(
            Base,
            new StringContent("{\"name\":\"a\",\"name\":\"b\"}", Encoding.UTF8, "application/json")
        );
        Assert.Equal(HttpStatusCode.UnprocessableEntity, repeated.StatusCode);

        HttpResponseMessage huge = await _moderator.PostAsync(
            Base,
            new StringContent(
                "{\"name\":\"" + new string('a', 300000) + "\"}",
                Encoding.UTF8,
                "application/json"
            )
        );
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);
        Assert.Contains("traceId", await huge.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Update_IsPartialAnd204AndHonorsIfMatch()
    {
        string id = await CreateEntry("Before");
        JObject before = await BodyOf(await _moderator.GetAsync($"{Base}/{id}"));

        HttpResponseMessage renamed = await _moderator.PutAsync(
            $"{Base}/{id}",
            Json(new JObject { ["name"] = "After", ["tags"] = new JArray("a", "b") })
        );
        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);

        JObject after = await BodyOf(await _moderator.GetAsync($"{Base}/{id}"));
        Assert.Equal("After", (string)after["name"]!);
        Assert.Equal(2, ((JArray)after["tags"]!).Count);
        Assert.Equal((string)before["content_hash"]!, (string)after["content_hash"]!);
        Assert.Equal("Test Author", (string)after["author"]!);

        using HttpRequestMessage stale = new(HttpMethod.Put, $"{Base}/{id}")
        {
            Content = Json(new JObject { ["name"] = "Stale" }),
        };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"0000\"");
        Assert.Equal(
            HttpStatusCode.PreconditionFailed,
            (await _moderator.SendAsync(stale)).StatusCode
        );
    }

    [Fact]
    public async Task BuiltInEntriesRefuseEditAndDeleteButCanBeCloned()
    {
        string id = await BuiltInId();

        HttpResponseMessage put = await _moderator.PutAsync(
            $"{Base}/{id}",
            Json(new JObject { ["name"] = "x" })
        );
        HttpResponseMessage delete = await _moderator.DeleteAsync($"{Base}/{id}");
        foreach (HttpResponseMessage response in new[] { put, delete })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal(
                "visualizer.builtin_readonly",
                (string)(await BodyOf(response))["errors"]![0]!["id"]!
            );
        }

        HttpResponseMessage clone = await _moderator.PostAsync(
            $"{Base}/{id}/clone",
            new StringContent("", Encoding.UTF8, "application/json")
        );
        Assert.Equal(HttpStatusCode.Created, clone.StatusCode);
        JObject copy = await BodyOf(clone);
        Assert.False((bool)copy["is_built_in"]!);
        Assert.EndsWith("(copy)", (string)copy["name"]!);
        Assert.NotEqual(id, (string)copy["id"]!);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await _moderator.DeleteAsync($"{Base}/{(string)copy["id"]!}")).StatusCode
        );
    }

    [Fact]
    public async Task WritesNeedTheModeratorPolicy()
    {
        string id = await CreateEntry("Guarded");

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await _viewer.DeleteAsync($"{Base}/{id}")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await _viewer.PostAsync(Base, Json(new JObject()))).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (
                await _viewer.PutAsync(
                    $"{Base}/{id}/enabled",
                    Json(new JObject { ["enabled"] = false })
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await _viewer.PutAsync($"{Base}/{id}/favorite", null)).StatusCode
        );
    }

    [Fact]
    public async Task DisabledEntriesShowOnlyToModeratorsAndKeepTheirStateAfterAReseed()
    {
        string id = await CreateEntry("Hidden");

        Assert.Equal(
            HttpStatusCode.NoContent,
            (
                await _moderator.PutAsync(
                    $"{Base}/{id}/enabled",
                    Json(new JObject { ["enabled"] = false })
                )
            ).StatusCode
        );
        Assert.Equal(HttpStatusCode.NotFound, (await _viewer.GetAsync($"{Base}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _moderator.GetAsync($"{Base}/{id}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (
                await _moderator.PutAsync(
                    $"{Base}/current",
                    Json(new JObject { ["visualizer_id"] = id })
                )
            ).StatusCode
        );

        using (IServiceScope scope = _factory.Services.CreateScope())
            await new VisualizerBundleSeeder(
                scope.ServiceProvider.GetRequiredService<MediaContext>()
            ).SeedAsync();
        string builtIn = await BuiltInId();
        await _moderator.PutAsync(
            $"{Base}/{builtIn}/enabled",
            Json(new JObject { ["enabled"] = false })
        );
        using (IServiceScope scope = _factory.Services.CreateScope())
            await new VisualizerBundleSeeder(
                scope.ServiceProvider.GetRequiredService<MediaContext>()
            ).SeedAsync();

        Assert.False(
            (bool)(await BodyOf(await _moderator.GetAsync($"{Base}/{builtIn}")))["enabled"]!
        );
        await _moderator.PutAsync(
            $"{Base}/{builtIn}/enabled",
            Json(new JObject { ["enabled"] = true })
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await _moderator.PutAsync($"{Base}/{id}/enabled", Json(new JObject()))).StatusCode
        );
    }

    [Fact]
    public async Task FavoritesAndTheCurrentChoiceBelongToOneUser()
    {
        string id = await CreateEntry("Mine");
        await _moderator.PutAsync($"{Base}/{id}/favorite", null);

        JObject mine = await BodyOf(await _moderator.GetAsync($"{Base}?favorites=true"));
        JObject theirs = await BodyOf(await _viewer.GetAsync($"{Base}?favorites=true"));
        Assert.Contains(
            ((JArray)mine["data"]!),
            row => (string)row["id"]! == id && (bool)row["is_favorite"]!
        );
        Assert.DoesNotContain(((JArray)theirs["data"]!), row => (string)row["id"]! == id);

        HttpResponseMessage set = await _moderator.PutAsync(
            $"{Base}/current",
            Json(new JObject { ["visualizer_id"] = id })
        );
        Assert.Equal(id, (string)(await BodyOf(set))["visualizer_id"]!);
        Assert.Equal(
            JTokenType.Null,
            (await BodyOf(await _viewer.GetAsync($"{Base}/current")))["visualizer_id"]!.Type
        );

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await _moderator.DeleteAsync($"{Base}/{id}/favorite")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (
                await _moderator.PutAsync(
                    $"{Base}/current",
                    Json(new JObject { ["visualizer_id"] = Ulid.NewUlid().ToString() })
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task ImportAppliesTheDeviceChoiceOnlyWhenTheServerHasNone()
    {
        JObject device = new()
        {
            ["choice"] = "bars",
            ["favorites"] = new JArray("nope"),
            ["recents"] = new JArray(),
        };

        JObject result = await BodyOf(
            await _viewer.PostAsync($"{Base}/import-device-state", Json(device))
        );

        Assert.False((bool)result["choice_applied"]!);
        Assert.Equal(0, (int)result["favorites_added"]!);
        Assert.Equal(
            new[] { "bars", "unknown" }.Order(),
            ((JArray)result["skipped"]!).Select(s => (string)s["reason"]!).Order()
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await _viewer.PostAsync(
                    $"{Base}/import-device-state",
                    Json(new JObject { ["recents"] = new JArray(Enumerable.Repeat("x", 11)) })
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task TexturesAreImmutableAndCacheable()
    {
        HttpResponseMessage first = await _viewer.GetAsync($"{Base}/textures/clouds");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("image/jpeg", first.Content.Headers.ContentType!.MediaType);
        Assert.Contains("immutable", first.Headers.CacheControl!.ToString());

        using HttpRequestMessage again = new(HttpMethod.Get, $"{Base}/textures/clouds");
        again.Headers.TryAddWithoutValidation("If-None-Match", first.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.NotModified, (await _viewer.SendAsync(again)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await _viewer.GetAsync($"{Base}/textures/not_a_texture")).StatusCode
        );
    }
}
