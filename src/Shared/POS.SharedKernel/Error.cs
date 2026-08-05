namespace POS.SharedKernel;

/// <summary>
/// Classifies a failure so the transport layer can map it to a status code
/// without the domain knowing anything about HTTP.
/// </summary>
public enum ErrorType
{
    Validation,
    NotFound,
    Conflict,
    Forbidden,
    BusinessRule
}

/// <summary>
/// An expected failure. <see cref="Code"/> is a stable, machine-readable
/// identifier — clients switch on it, so it must never change once published.
/// <see cref="Message"/> is for humans and may be localised freely.
/// </summary>
public sealed record Error(string Code, string Message, ErrorType Type)
{
    public static Error Validation(string code, string message) => new(code, message, ErrorType.Validation);
    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);
    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);
    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);
    public static Error BusinessRule(string code, string message) => new(code, message, ErrorType.BusinessRule);
}
