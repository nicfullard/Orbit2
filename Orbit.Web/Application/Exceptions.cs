namespace Orbit.Application;

public abstract class OrbitException(string message) : Exception(message);

public sealed class NotFoundException(string message) : OrbitException(message);

public sealed class ForbiddenException(string message) : OrbitException(message);

public sealed class ValidationException(string message) : OrbitException(message);
