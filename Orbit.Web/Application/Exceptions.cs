namespace Orbit.Application;

public abstract class OrbitException(string message) : Exception(message);

public sealed class NotFoundException(string message) : OrbitException(message);

public sealed class ForbiddenException(string message) : OrbitException(message);

public sealed class ValidationException(string message) : OrbitException(message);

/// <summary>
/// Some of a request's answers failed their checks (§6.20): a message per question, keyed by question id, so the flow can show
/// each one beside its question. Anywhere else it is an ordinary flash message.
/// </summary>
public sealed class RequestAnswersException(IReadOnlyDictionary<Guid, string> errors)
    : OrbitException(errors.Count == 1 ? "One answer needs attention." : $"{errors.Count} answers need attention.")
{
    public IReadOnlyDictionary<Guid, string> Errors { get; } = errors;
}
