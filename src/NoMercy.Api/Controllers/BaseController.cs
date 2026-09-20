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

using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Media;
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Authorization;
using NoMercy.Data.Repositories;
using NoMercy.NmSystem.Extensions;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Api.Controllers;

public class BaseController : Controller
{
    // Media authorization policy resolved from the request's DI container.
    // Replaces the former static ClaimsPrincipal authorization extensions.
    // Resolved from the request's DI container; falls back to a policy over the
    // shared user cache when there is no HttpContext (e.g. unit-constructed
    // controllers in tests). Both paths read the same UserCache singleton.
    protected IMediaAuthorizationPolicy AuthPolicy =>
        HttpContext?.RequestServices?.GetService<IMediaAuthorizationPolicy>()
        ?? new MediaAuthorizationPolicy(UserCache.Current);

    protected IUserCache UserCacheService =>
        HttpContext?.RequestServices?.GetService<IUserCache>() ?? UserCache.Current;

    private IActionResult ProblemWithTrace(
        string title,
        string detail,
        int statusCode,
        string type,
        [System.Runtime.CompilerServices.CallerMemberName] string caller = ""
    )
    {
        // A returned 5xx never reaches GlobalExceptionHandlerMiddleware, so it would
        // otherwise be an unlogged, invisible server error.
        if (statusCode >= 500)
        {
            ILogger? log = HttpContext
                ?.RequestServices?.GetService<ILoggerFactory>()
                ?.CreateLogger("NoMercy.Api.ServerError");
            log?.LogError(
                "[{TraceId}] {Status} returned from {Caller} for {Path}: {Detail}",
                HttpContext?.TraceIdentifier,
                statusCode,
                caller,
                HttpContext?.Request.Path.Value,
                detail
            );
        }

        ProblemDetails problemDetails = new()
        {
            Type = type,
            Title = title.Localize(),
            Detail = detail.Localize(),
            Instance = HttpContext?.Request.Path.Value,
            Status = statusCode,
            Extensions = { { "traceId", HttpContext?.TraceIdentifier } },
        };

        return StatusCode(statusCode, problemDetails);
    }

    protected IActionResult UnauthenticatedResponse(string detail)
    {
        return ProblemWithTrace(
            title: "Unauthenticated.",
            detail: detail,
            statusCode: StatusCodes.Status401Unauthorized,
            type: "/docs/errors/unauthenticated"
        );
    }

    protected IActionResult UnauthorizedResponse(string detail)
    {
        return ProblemWithTrace(
            title: "Unauthorized.",
            detail: detail,
            statusCode: StatusCodes.Status403Forbidden,
            type: "/docs/errors/forbidden"
        );
    }

    protected IActionResult NotFoundResponse(string detail)
    {
        return ProblemWithTrace(
            title: "Not Found.",
            detail: detail,
            statusCode: StatusCodes.Status404NotFound,
            type: "/docs/errors/not-found"
        );
    }

    protected IActionResult BadRequestResponse(string detail)
    {
        return ProblemWithTrace(
            title: "Bad Request.",
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            type: "/docs/errors/bad-request"
        );
    }

    protected IActionResult InternalServerErrorResponse(string detail)
    {
        return ProblemWithTrace(
            title: "Internal Server Error.",
            detail: detail,
            statusCode: StatusCodes.Status500InternalServerError,
            type: "/docs/errors/internal-server-error"
        );
    }

    protected IActionResult ConflictResponse(string detail)
    {
        return ProblemWithTrace(
            title: "Conflict.",
            detail: detail,
            statusCode: StatusCodes.Status409Conflict,
            type: "/docs/errors/conflict"
        );
    }

    protected IActionResult NotImplementedResponse(string detail)
    {
        return ProblemWithTrace(
            title: "Not Implemented.",
            detail: detail,
            statusCode: StatusCodes.Status501NotImplemented,
            type: "/docs/errors/not-implemented"
        );
    }

    protected IActionResult ServiceUnavailableResponse(string detail)
    {
        return ProblemWithTrace(
            title: "Service Unavailable.",
            detail: detail,
            statusCode: StatusCodes.Status503ServiceUnavailable,
            type: "/docs/errors/service-unavailable"
        );
    }

    protected IActionResult GatewayTimeoutResponse(string detail)
    {
        return ProblemWithTrace(
            title: "Gateway Timeout.",
            detail: detail,
            statusCode: StatusCodes.Status504GatewayTimeout,
            type: "/docs/errors/gateway-timeout"
        );
    }

    protected IActionResult UnprocessableEntityResponse(string detail)
    {
        return ProblemWithTrace(
            title: "Unprocessable Entity.",
            detail: detail,
            statusCode: StatusCodes.Status422UnprocessableEntity,
            type: "/docs/errors/unprocessable-entity"
        );
    }

    /// <summary>
    /// A refusal as the error body rather than a sentence about it, so the
    /// owner reads the same three lines the server logs and a client can show
    /// the fix without knowing what went wrong.
    /// </summary>
    protected IActionResult UnprocessableEntityResponse(PluginRefusal refusal)
    {
        return UnprocessableEntity(new DataResponseDto<PluginRefusal> { Data = refusal });
    }

    protected IActionResult TooManyRequestsResponse(string detail)
    {
        return ProblemWithTrace(
            title: "Too Many Requests.",
            detail: detail,
            statusCode: StatusCodes.Status429TooManyRequests,
            type: "/docs/errors/too-many-requests"
        );
    }

    protected IActionResult GoneResponse(string detail)
    {
        return ProblemWithTrace(
            title: "Gone.",
            detail: detail,
            statusCode: StatusCodes.Status410Gone,
            type: "/docs/errors/gone"
        );
    }

    protected IActionResult PaymentRequiredResponse(string detail)
    {
        return ProblemWithTrace(
            title: "Payment Required.",
            detail: detail,
            statusCode: StatusCodes.Status402PaymentRequired,
            type: "/docs/errors/payment-required"
        );
    }

    protected IActionResult LengthRequiredResponse(string detail)
    {
        return ProblemWithTrace(
            title: "Length Required.",
            detail: detail,
            statusCode: StatusCodes.Status411LengthRequired,
            type: "/docs/errors/length-required"
        );
    }

    protected IActionResult PreconditionFailedResponse(string detail)
    {
        return ProblemWithTrace(
            title: "Precondition Failed.",
            detail: detail,
            statusCode: StatusCodes.Status412PreconditionFailed,
            type: "/docs/errors/precondition-failed"
        );
    }

    protected IActionResult RequestEntityTooLargeResponse(string detail)
    {
        return ProblemWithTrace(
            title: "Request Entity Too Large.",
            detail: detail,
            statusCode: StatusCodes.Status413RequestEntityTooLarge,
            type: "/docs/errors/request-entity-too-large"
        );
    }

    protected IActionResult RequestUriTooLongResponse(string detail)
    {
        return ProblemWithTrace(
            title: "Request-URI Too Long.",
            detail: detail,
            statusCode: StatusCodes.Status414RequestUriTooLong,
            type: "/docs/errors/request-uri-too-long"
        );
    }

    protected IActionResult UnsupportedMediaTypeResponse(string detail)
    {
        return ProblemWithTrace(
            title: "Unsupported Media Type.",
            detail: detail,
            statusCode: StatusCodes.Status415UnsupportedMediaType,
            type: "/docs/errors/unsupported-media-libraryType"
        );
    }

    protected IActionResult RequestedRangeNotSatisfiableResponse(string detail)
    {
        return ProblemWithTrace(
            title: "Requested Range Not Satisfiable.",
            detail: detail,
            statusCode: StatusCodes.Status416RequestedRangeNotSatisfiable,
            type: "/docs/errors/requested-range-not-satisfiable"
        );
    }

    protected IActionResult ExpectationFailedResponse(string detail)
    {
        return ProblemWithTrace(
            title: "Expectation Failed.",
            detail: detail,
            statusCode: StatusCodes.Status417ExpectationFailed,
            type: "/docs/errors/expectation-failed"
        );
    }

    protected IActionResult MisdirectedRequestResponse(string detail)
    {
        return ProblemWithTrace(
            title: "Misdirected Request.",
            detail: detail,
            statusCode: StatusCodes.Status421MisdirectedRequest,
            type: "/docs/errors/misdirected-request"
        );
    }

    protected IActionResult UnavailableForLegalReasonsResponse(string detail)
    {
        return ProblemWithTrace(
            title: "Unavailable For Legal Reasons.",
            detail: detail,
            statusCode: StatusCodes.Status451UnavailableForLegalReasons,
            type: "/docs/errors/unavailable-for-legal-reasons"
        );
    }

    protected IActionResult GetPaginatedResponse<T>(
        IEnumerable<T> data,
        [FromQuery] PageRequestDto request
    )
    {
        List<T> newData = [.. data];
        bool hasMore = newData.Count >= request.Take;

        newData = [.. newData.Take(request.Take)];

        PaginatedResponse<T> response = new()
        {
            Data = newData,
            NextPage = hasMore ? request.Page + 1 : null,
            HasMore = hasMore,
        };

        return Ok(response);
    }

    /// <summary>A grid of movie and show cards, ordered by title.</summary>
    protected static ContainerComponentBuilder TitleCardGrid(
        string id,
        IEnumerable<HomeMovieCardDto> movies,
        IEnumerable<HomeTvCardDto> shows
    ) =>
        Component
            .Grid()
            .WithId(id)
            .WithItems(
                movies
                    .Select(movie => new CardData(movie))
                    .Concat(shows.Select(show => new CardData(show)))
                    .OrderBy(card => card.TitleSort)
                    .Select(card => Component.Card().WithData(card))
            );

    protected string Language()
    {
        return HttpContext
                .Request.Headers.AcceptLanguage.FirstOrDefault()
                ?.Split("_")
                .FirstOrDefault()
            ?? LocalizationHelper.CurrentLocalizer.TargetLanguage;
    }

    protected string Country()
    {
        return HttpContext.Request.Headers["country"].FirstOrDefault()
            ?? RegionInfo.CurrentRegion.TwoLetterISORegionName;
    }

    protected static readonly string[] Numbers =
    [
        "*",
        "#",
        "'",
        "\"",
        "1",
        "2",
        "3",
        "4",
        "5",
        "6",
        "7",
        "8",
        "9",
        "0",
    ];

    protected static readonly string[] Letters =
    [
        "#",
        "A",
        "B",
        "C",
        "D",
        "E",
        "F",
        "G",
        "H",
        "I",
        "J",
        "K",
        "L",
        "M",
        "N",
        "O",
        "P",
        "Q",
        "R",
        "S",
        "T",
        "U",
        "V",
        "W",
        "X",
        "Y",
        "Z",
    ];
}
