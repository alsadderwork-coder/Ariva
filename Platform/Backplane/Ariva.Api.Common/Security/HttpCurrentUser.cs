using System.Security.Claims;
using Ariva.Core.Services;
using Ariva.Infra.Security;
using Microsoft.AspNetCore.Http;

namespace Ariva.Api.Common.Security;

/// <summary>
/// <see cref="ICurrentUser"/> from the validated access token of the current request (claims sub, sid, name,
/// auth_time, amr). Without a request (jobs, consumers) the system identity set by <see cref="SetSystemUser"/> applies.
/// </summary>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private Guid? _systemId;
    private string _systemName;

    private ClaimsPrincipal Principal => accessor.HttpContext?.User;

    private bool HasUser => Principal?.Identity?.IsAuthenticated == true;

    public Guid? Id => HasUser && Guid.TryParse(Claim(ArivaClaims.Subject), out var id) ? id : _systemId;

    public string UserName => HasUser ? Claim(ArivaClaims.Name) ?? Principal.Identity?.Name : _systemName;

    public Guid? SessionId => HasUser && Guid.TryParse(Claim(ArivaClaims.SessionId), out var sid) ? sid : null;

    public IReadOnlyCollection<string> Roles => HasUser ? Principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList() : [];

    public DateTime? AuthenticatedAt =>
        HasUser && long.TryParse(Claim(ArivaClaims.AuthTime), out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : null;

    public IReadOnlyCollection<string> AuthenticationMethods =>
        HasUser ? Principal.FindAll(ArivaClaims.Methods).Select(c => c.Value).ToList() : [];

    public bool IsAuthenticated => HasUser;

    public string GetCallerIpAddress() => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public void SetSystemUser(Guid id, string userName)
    {
        _systemId = id;
        _systemName = userName;
    }

    private string Claim(string type) => Principal?.FindFirst(type)?.Value;
}
