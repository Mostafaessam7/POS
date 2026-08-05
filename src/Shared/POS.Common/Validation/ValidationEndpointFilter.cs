using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace POS.Common.Validation;

/// <summary>
/// Runs FluentValidation against the request before the handler is invoked.
/// </summary>
/// <remarks>
/// This is the replacement for a MediatR validation pipeline behaviour. Endpoint
/// filters run in the ASP.NET Core pipeline with no runtime reflection over a
/// mediator's handler graph, which makes them both faster and easier to debug —
/// "go to implementation" still works.
///
/// Usage:  app.MapPost("/products", CreateProduct).AddValidation&lt;CreateProductCommand&gt;();
/// </remarks>
public sealed class ValidationEndpointFilter<TRequest>(IValidator<TRequest> validator) : IEndpointFilter
    where TRequest : class
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var request = context.Arguments.OfType<TRequest>().FirstOrDefault();

        if (request is null)
            return await next(context);

        var result = await validator.ValidateAsync(request, context.HttpContext.RequestAborted);

        if (result.IsValid)
            return await next(context);

        var errors = result.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());

        return Results.ValidationProblem(
            errors,
            type: "https://api.pos.example/errors/validation",
            title: "One or more validation errors occurred.");
    }
}

public static class ValidationEndpointFilterExtensions
{
    public static RouteHandlerBuilder AddValidation<TRequest>(this RouteHandlerBuilder builder)
        where TRequest : class
        => builder
            .AddEndpointFilter<ValidationEndpointFilter<TRequest>>()
            .ProducesValidationProblem();
}
