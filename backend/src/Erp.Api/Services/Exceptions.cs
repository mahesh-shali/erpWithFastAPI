namespace Erp.Api.Services;

public class DomainException(string message) : Exception(message);
public class NotFoundException(string message) : Exception(message);
public class ConflictException(string message) : Exception(message);
/// <summary>The caller is authenticated but may not do this (hierarchy / no-escalation rules) -> 403.</summary>
public class ForbiddenException(string message) : Exception(message);
