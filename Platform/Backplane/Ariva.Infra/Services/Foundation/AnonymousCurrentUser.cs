namespace Ariva.Infra.Services.Foundation;

/// <summary>
/// Fallback <see cref="ICurrentUser"/> until token authentication lands (ARV-010a): nobody is signed in. Background
/// work sets a system identity with <see cref="SetSystemUser"/>, which audit stamps then record.
/// </summary>
internal sealed class AnonymousCurrentUser : ICurrentUser
{
    public Guid? Id { get; private set; }
    public string UserName { get; private set; }
    public Guid? SessionId => null;
    public IReadOnlyCollection<string> Roles => [];
    public DateTime? AuthenticatedAt => null;
    public IReadOnlyCollection<string> AuthenticationMethods => [];
    public bool IsAuthenticated => false;

    public string GetCallerIpAddress() => null;

    public void SetSystemUser(Guid id, string userName)
    {
        Id = id;
        UserName = userName;
    }
}
