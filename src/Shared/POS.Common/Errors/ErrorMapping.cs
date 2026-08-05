using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using POS.SharedKernel;

namespace POS.Common.Errors;

/// <summary>
/// Maps a domain <see cref="Error"/> to an HTTP response.
/// </summary>
/// <remarks>
/// This is the only place in the system that knows both about domain errors and
/// about status codes. Keeping the mapping here is what allows the same handlers
/// to run inside the Terminal Agent, which has no HTTP pipeline at all.
/// </remarks>
public static class ErrorMapping
{
    public static IResult ToHttpResult(this Error error) => Results.Problem(
        statusCode: ToStatusCode(error.Type),
        type: $"https://api.pos.example/errors/{error.Code}",
        title: error.Message,
        extensions: new Dictionary<string, object?> { ["code"] = error.Code });

    public static IResult ToHttpResult<T>(this Result<T> result, Func<T, IResult> onSuccess) =>
        result.Match(onSuccess, error => error.ToHttpResult());

    private static int ToStatusCode(ErrorType type) => type switch
    {
        ErrorType.Validation   => StatusCodes.Status400BadRequest,
        ErrorType.NotFound     => StatusCodes.Status404NotFound,
        ErrorType.Conflict     => StatusCodes.Status409Conflict,
        ErrorType.Forbidden    => StatusCodes.Status403Forbidden,
        ErrorType.BusinessRule => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status500InternalServerError
    };
}
