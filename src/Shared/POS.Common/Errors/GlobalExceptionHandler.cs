using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace POS.Common.Errors;

/// <summary>
/// Last line of defence. Converts an unhandled exception into RFC 9457
/// ProblemDetails without ever leaking internals to the caller.
/// </summary>
/// <remarks>
/// The client receives a correlation ID and nothing else. Stack traces and SQL
/// in an error response are an information disclosure vulnerability, and on a
/// POS the "client" may be a till in a public-facing retail environment.
/// The detail is logged server-side against the same correlation ID.
/// </remarks>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var correlationId = httpContext.TraceIdentifier;

        logger.LogError(
            exception,
            "Unhandled exception. CorrelationId={CorrelationId} Path={Path}",
            correlationId,
            httpContext.Request.Path);

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Type = "https://api.pos.example/errors/internal",
            Title = "An unexpected error occurred.",
            Detail = "The request could not be completed. Quote the correlation ID when reporting this.",
            Instance = httpContext.Request.Path
        };

        problem.Extensions["correlationId"] = correlationId;

        httpContext.Response.StatusCode = problem.Status.Value;
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);

        return true;
    }
}
