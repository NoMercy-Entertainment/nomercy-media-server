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
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Api.Middleware;
using NoMercy.Api.Services;
using NoMercy.Authorization;
using NoMercy.Authorization.LiveIngest;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Storage;
using NoMercy.Database.Models.Users;
using NoMercy.Service.Authorization;
using Xunit;

namespace NoMercy.Tests.Api.Authorization;

[Trait("Category", "Authorization")]
public sealed class AuthorizationDenyPrecisionTests
{
    private static readonly Guid OwnerId = Guid.Parse("aa000000-0000-0000-0000-000000000001");
    private static readonly Guid ModeratorId = Guid.Parse("bb000000-0000-0000-0000-000000000002");
    private static readonly Guid AllowedId = Guid.Parse("cc000000-0000-0000-0000-000000000003");
    private static readonly Guid BlockedId = Guid.Parse("dd000000-0000-0000-0000-000000000004");

    private static User OwnerUser =>
        new()
        {
            Id = OwnerId,
            Owner = true,
            Manage = false,
            Allowed = false,
            Name = "Owner",
            Email = "owner@nm.tv",
        };

    private static User ModeratorUser =>
        new()
        {
            Id = ModeratorId,
            Owner = false,
            Manage = true,
            Allowed = true,
            Name = "Mod",
            Email = "mod@nm.tv",
        };

    private static User AllowedUser =>
        new()
        {
            Id = AllowedId,
            Owner = false,
            Manage = false,
            Allowed = true,
            Name = "Allowed",
            Email = "allowed@nm.tv",
        };

    private static User BlockedUser =>
        new()
        {
            Id = BlockedId,
            Owner = false,
            Manage = false,
            Allowed = false,
            Name = "Blocked",
            Email = "blocked@nm.tv",
        };

    private static ClaimsPrincipal PrincipalFor(Guid userId)
    {
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, userId.ToString())];
        return new(new ClaimsIdentity(claims, "TestScheme"));
    }

    private static (MediaAuthorizationHandler handler, UserCache cache) BuildHandler()
    {
        UserCache cache = new();
        MediaAuthorizationPolicy policy = new(cache);
        MediaAuthorizationHandler handler = new(
            policy,
            NullLogger<MediaAuthorizationHandler>.Instance
        );
        return (handler, cache);
    }

    private static AuthorizationHandlerContext HandlerContext(
        ClaimsPrincipal user,
        IAuthorizationRequirement requirement
    ) => new([requirement], user, null);

    private static IAuthorizationService BuildNamedPolicyService(UserCache cache)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IUserCache>(cache);
        services.AddSingleton<IMediaAuthorizationPolicy, MediaAuthorizationPolicy>();
        services.AddScoped<IAuthorizationHandler, MediaAuthorizationHandler>();
        services
            .AddAuthorizationBuilder()
            .AddPolicy(
                "Owner",
                policy =>
                {
                    policy.RequireAuthenticatedUser();
                    policy.AddRequirements(new OwnerRequirement());
                }
            )
            .AddPolicy(
                "Moderator",
                policy =>
                {
                    policy.RequireAuthenticatedUser();
                    policy.AddRequirements(new ModeratorRequirement());
                }
            )
            .AddPolicy(
                "MediaAccess",
                policy =>
                {
                    policy.RequireAuthenticatedUser();
                    policy.AddRequirements(new MediaAccessRequirement());
                }
            );
        ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IAuthorizationService>();
    }

    [Fact]
    public async Task OwnerRequirement_Denies_WhenPrincipalIsModeratorNotOwner()
    {
        (MediaAuthorizationHandler handler, UserCache cache) = BuildHandler();
        cache.AddUser(OwnerUser);
        cache.AddUser(ModeratorUser);
        AuthorizationHandlerContext ctx = HandlerContext(
            PrincipalFor(ModeratorId),
            new OwnerRequirement()
        );

        await handler.HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task OwnerRequirement_Admits_WhenPrincipalIsOwnerRegardlessOfManageFlag()
    {
        (MediaAuthorizationHandler handler, UserCache cache) = BuildHandler();
        cache.AddUser(OwnerUser);
        AuthorizationHandlerContext ctx = HandlerContext(
            PrincipalFor(OwnerId),
            new OwnerRequirement()
        );

        await handler.HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task ModeratorRequirement_Admits_WhenPrincipalIsOwnerWithoutManageFlag()
    {
        (MediaAuthorizationHandler handler, UserCache cache) = BuildHandler();
        cache.AddUser(OwnerUser);
        AuthorizationHandlerContext ctx = HandlerContext(
            PrincipalFor(OwnerId),
            new ModeratorRequirement()
        );

        await handler.HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task ModeratorRequirement_Denies_WhenPrincipalHasAllowedButNotManageOrOwner()
    {
        (MediaAuthorizationHandler handler, UserCache cache) = BuildHandler();
        cache.AddUser(AllowedUser);
        AuthorizationHandlerContext ctx = HandlerContext(
            PrincipalFor(AllowedId),
            new ModeratorRequirement()
        );

        await handler.HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task MediaAccessRequirement_Admits_WhenPrincipalIsOwnerWithoutAllowedFlag()
    {
        (MediaAuthorizationHandler handler, UserCache cache) = BuildHandler();
        cache.AddUser(OwnerUser);
        AuthorizationHandlerContext ctx = HandlerContext(
            PrincipalFor(OwnerId),
            new MediaAccessRequirement()
        );

        await handler.HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task MediaAccessRequirement_Denies_WhenPrincipalIsInCacheButAllowedFalse()
    {
        (MediaAuthorizationHandler handler, UserCache cache) = BuildHandler();
        cache.AddUser(BlockedUser);
        AuthorizationHandlerContext ctx = HandlerContext(
            PrincipalFor(BlockedId),
            new MediaAccessRequirement()
        );

        await handler.HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task OwnerPolicy_Denies_WhenPrincipalIsModeratorNotOwner()
    {
        UserCache cache = new();
        cache.AddUser(OwnerUser);
        cache.AddUser(ModeratorUser);
        IAuthorizationService authService = BuildNamedPolicyService(cache);

        AuthorizationResult result = await authService.AuthorizeAsync(
            PrincipalFor(ModeratorId),
            "Owner"
        );

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task OwnerPolicy_Admits_WhenPrincipalIsOwner()
    {
        UserCache cache = new();
        cache.AddUser(OwnerUser);
        IAuthorizationService authService = BuildNamedPolicyService(cache);

        AuthorizationResult result = await authService.AuthorizeAsync(
            PrincipalFor(OwnerId),
            "Owner"
        );

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task ModeratorPolicy_Denies_WhenPrincipalHasAllowedButNotManageOrOwner()
    {
        UserCache cache = new();
        cache.AddUser(AllowedUser);
        IAuthorizationService authService = BuildNamedPolicyService(cache);

        AuthorizationResult result = await authService.AuthorizeAsync(
            PrincipalFor(AllowedId),
            "Moderator"
        );

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task ModeratorPolicy_Admits_WhenPrincipalIsModeratorWithManageFlag()
    {
        UserCache cache = new();
        cache.AddUser(ModeratorUser);
        IAuthorizationService authService = BuildNamedPolicyService(cache);

        AuthorizationResult result = await authService.AuthorizeAsync(
            PrincipalFor(ModeratorId),
            "Moderator"
        );

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task ModeratorPolicy_Admits_WhenPrincipalIsOwnerWithoutManageFlag()
    {
        UserCache cache = new();
        cache.AddUser(OwnerUser);
        IAuthorizationService authService = BuildNamedPolicyService(cache);

        AuthorizationResult result = await authService.AuthorizeAsync(
            PrincipalFor(OwnerId),
            "Moderator"
        );

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task MediaAccessPolicy_Denies_WhenPrincipalIsInCacheButAllowedFalse()
    {
        UserCache cache = new();
        cache.AddUser(BlockedUser);
        IAuthorizationService authService = BuildNamedPolicyService(cache);

        AuthorizationResult result = await authService.AuthorizeAsync(
            PrincipalFor(BlockedId),
            "MediaAccess"
        );

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task MediaAccessPolicy_Admits_WhenPrincipalHasAllowedFlag()
    {
        UserCache cache = new();
        cache.AddUser(AllowedUser);
        IAuthorizationService authService = BuildNamedPolicyService(cache);

        AuthorizationResult result = await authService.AuthorizeAsync(
            PrincipalFor(AllowedId),
            "MediaAccess"
        );

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task MediaAccessPolicy_Admits_WhenPrincipalIsOwnerWithoutAllowedFlag()
    {
        UserCache cache = new();
        cache.AddUser(OwnerUser);
        IAuthorizationService authService = BuildNamedPolicyService(cache);

        AuthorizationResult result = await authService.AuthorizeAsync(
            PrincipalFor(OwnerId),
            "MediaAccess"
        );

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task IsOwner_Denies_WhenPrincipalIdMatchesNonOwnerInMultiUserCache()
    {
        UserCache cache = new();
        cache.AddUser(OwnerUser);
        cache.AddUser(ModeratorUser);
        MediaAuthorizationPolicy policy = new(cache);

        bool result = policy.IsOwner(PrincipalFor(ModeratorId));

        result.Should().BeFalse();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task IsOwner_Admits_WhenPrincipalIdMatchesOwnerInMultiUserCache()
    {
        UserCache cache = new();
        cache.AddUser(ModeratorUser);
        cache.AddUser(AllowedUser);
        cache.AddUser(OwnerUser);
        MediaAuthorizationPolicy policy = new(cache);

        bool result = policy.IsOwner(PrincipalFor(OwnerId));

        result.Should().BeTrue();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Policy_SeesUserAddedAfterConstruction()
    {
        UserCache cache = new();
        MediaAuthorizationPolicy policy = new(cache);

        bool beforeAdd = policy.IsAllowed(PrincipalFor(AllowedId));
        cache.AddUser(AllowedUser);
        bool afterAdd = policy.IsAllowed(PrincipalFor(AllowedId));

        beforeAdd.Should().BeFalse();
        afterAdd.Should().BeTrue();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Policy_DeniesAfterAllowedFlagRevokedViaUpdateUser()
    {
        UserCache cache = new();
        cache.AddUser(AllowedUser);
        MediaAuthorizationPolicy policy = new(cache);

        bool beforeRevoke = policy.IsAllowed(PrincipalFor(AllowedId));

        User revoked = new()
        {
            Id = AllowedId,
            Owner = false,
            Manage = false,
            Allowed = false,
            Name = "Allowed",
            Email = "allowed@nm.tv",
        };
        cache.UpdateUser(revoked);

        bool afterRevoke = policy.IsAllowed(PrincipalFor(AllowedId));

        beforeRevoke.Should().BeTrue();
        afterRevoke.Should().BeFalse();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Policy_DeniesAfterUserRemovedFromCache()
    {
        UserCache cache = new();
        cache.AddUser(AllowedUser);
        MediaAuthorizationPolicy policy = new(cache);

        bool beforeRemove = policy.IsAllowed(PrincipalFor(AllowedId));
        cache.RemoveUser(AllowedUser);
        bool afterRemove = policy.IsAllowed(PrincipalFor(AllowedId));

        beforeRemove.Should().BeTrue();
        afterRemove.Should().BeFalse();
        await Task.CompletedTask;
    }

    [Fact]
    public void CatalogueCompleteness_AllThreeRequirementsHaveFiresOnBadAndSilentOnValidNeighborCoverage()
    {
        Type[] requirements =
        [
            typeof(OwnerRequirement),
            typeof(ModeratorRequirement),
            typeof(MediaAccessRequirement),
        ];

        IReadOnlyList<string> documentedCoverage =
        [
            nameof(OwnerRequirement_Denies_WhenPrincipalIsModeratorNotOwner),
            nameof(OwnerRequirement_Admits_WhenPrincipalIsOwnerRegardlessOfManageFlag),
            nameof(ModeratorRequirement_Denies_WhenPrincipalHasAllowedButNotManageOrOwner),
            nameof(ModeratorRequirement_Admits_WhenPrincipalIsOwnerWithoutManageFlag),
            nameof(MediaAccessRequirement_Denies_WhenPrincipalIsInCacheButAllowedFalse),
            nameof(MediaAccessRequirement_Admits_WhenPrincipalIsOwnerWithoutAllowedFlag),
        ];

        requirements
            .Should()
            .AllSatisfy(req =>
            {
                string reqName = req.Name;
                IEnumerable<string> firesOnBad = documentedCoverage.Where(n =>
                    n.StartsWith(reqName, StringComparison.Ordinal) && n.Contains("Denies")
                );
                IEnumerable<string> silentOnValid = documentedCoverage.Where(n =>
                    n.StartsWith(reqName, StringComparison.Ordinal) && n.Contains("Admits")
                );

                firesOnBad
                    .Should()
                    .NotBeEmpty(
                        because: $"{reqName} must have at least one fires-on-bad deny test"
                    );
                silentOnValid
                    .Should()
                    .NotBeEmpty(
                        because: $"{reqName} must have at least one silent-on-valid-neighbor admit test"
                    );
            });
    }
}

[Trait("Category", "Authorization")]
public sealed class FolderAccessDenyPrecisionTests : IAsyncLifetime, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _dbOptions;

    private static readonly Ulid KnownFolderId = Ulid.NewUlid();
    private static readonly Guid KnownUserId = Guid.NewGuid();

    public FolderAccessDenyPrecisionTests()
    {
        _connection = new($"DataSource={Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<MediaContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new SqliteNormalizeSearchInterceptor())
            .Options;

        using MediaContext ctx = new(_dbOptions);
        ctx.Database.EnsureCreated();

        Driver driver = new()
        {
            Id = Driver.SystemLocalDriverId,
            Name = "Local",
            Type = "local",
            Config = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        ctx.Drivers.Add(driver);

        Folder folder = new()
        {
            Id = KnownFolderId,
            Path = "/media",
            DriverId = Driver.SystemLocalDriverId,
        };
        ctx.Folders.Add(folder);

        User user = new()
        {
            Id = KnownUserId,
            Name = "Known",
            Email = "k@nm.tv",
            Allowed = true,
        };
        ctx.Users.Add(user);
        ctx.SaveChanges();
    }

    public async Task InitializeAsync()
    {
        UserCache.Current.Reset();
        await using MediaContext ctx = new(_dbOptions);
        await UserCache.Current.InitializeAsync(ctx);
    }

    public Task DisposeAsync()
    {
        UserCache.Current.Reset();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    private static FolderAccessMiddleware BuildMiddleware(RequestDelegate next) =>
        new(
            next,
            UserCache.Current,
            new LiveIngestKeyStore(),
            NullLogger<FolderAccessMiddleware>.Instance
        );

    private static HttpContext BuildContext(string path, ClaimsPrincipal? user = null)
    {
        DefaultHttpContext context = new();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        if (user is not null)
            context.User = user;
        return context;
    }

    private static ClaimsPrincipal PrincipalWithSub(string sub)
    {
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, sub)];
        return new(new ClaimsIdentity(claims, "TestScheme"));
    }

    [Fact]
    public async Task Denies_WithForbidden_WhenSubIsGuidEmptyString()
    {
        bool nextCalled = false;
        FolderAccessMiddleware middleware = BuildMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        HttpContext context = BuildContext(
            $"/{KnownFolderId}/some-file.mkv",
            user: PrincipalWithSub(Guid.Empty.ToString())
        );

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Allows_WhenFolderIdsEmptyAndPathLooksLikeUlid()
    {
        UserCache.Current.Reset();
        bool nextCalled = false;
        FolderAccessMiddleware middleware = BuildMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        Ulid ulidLookingPath = Ulid.NewUlid();
        HttpContext context = BuildContext($"/{ulidLookingPath}/some-file.mkv");

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
    }
}
