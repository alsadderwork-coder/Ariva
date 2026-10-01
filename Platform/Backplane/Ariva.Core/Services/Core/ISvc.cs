namespace Ariva.Core.Services;

/// <summary>
/// An application service. Ported from AMAN: Ariva.Di registers every implementation by the lifetime marker it
/// carries (<see cref="ISvcScoped"/>, <see cref="ISvcSingleton"/> or <see cref="ISvcTransient"/>).
/// </summary>
public interface ISvc
{
}

/// <summary>A service that lives for one request or one message scope.</summary>
public interface ISvcScoped : ISvc
{
}

/// <summary>A service with one instance for the life of the process. It must be thread safe and hold no user state.</summary>
public interface ISvcSingleton : ISvc
{
}

/// <summary>A service created each time it is resolved.</summary>
public interface ISvcTransient : ISvc
{
}
