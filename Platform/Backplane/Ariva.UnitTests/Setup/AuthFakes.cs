using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services.Security;

namespace Ariva.UnitTests.Setup;

/// <summary>
/// Session check for in-process hosts, which have no database: every session is active unless a test marks it
/// otherwise.
/// </summary>
public sealed class FakeSessionValidator : ISessionValidator
{
    private readonly Dictionary<Guid, SessionState> _states = [];

    public SessionState this[Guid sessionId]
    {
        set => _states[sessionId] = value;
    }

    public Task<SessionState> CheckAsync(Guid sessionId, CancellationToken ct = default) =>
        Task.FromResult(_states.TryGetValue(sessionId, out var state) ? state : SessionState.Active);
}

/// <summary>
/// Sign-in service for in-process hosts. By default every call fails the way the real service fails for the
/// permission matrix bodies (unknown user, wrong current password, unknown id); a test can make sign-in and refresh
/// succeed with a fixed refresh token.
/// </summary>
public sealed class FakeAuthenticator : ISvcAuthenticator
{
    public SignInResult NextSignIn { get; set; }

    public List<SignInContext> Contexts { get; } = [];

    public Task<Fluentx.Result<SignInResult>> LoginAsync(LoginRequest request, SignInContext context, CancellationToken ct = default) =>
        Answer(context, ISvcAuthenticator.InvalidCredentials);

    public Task<Fluentx.Result<SignInResult>> RefreshAsync(SignInContext context, CancellationToken ct = default) =>
        Answer(context, ISvcAuthenticator.SessionExpired);

    public Task<Fluentx.Result<bool>> LogoutAsync(SignInContext context, CancellationToken ct = default)
    {
        Contexts.Add(context);
        return Task.FromResult(new Fluentx.Result<bool>(true));
    }

    public Task<Fluentx.Result<TokenViewModel>> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default) =>
        Task.FromResult(Fluentx.Result.Error<TokenViewModel>(ISvcAuthenticator.InvalidCredentials));

    public Task<Fluentx.Result<bool>> UnlockAsync(Guid userId, CancellationToken ct = default) => NotFound();

    public Task<Fluentx.Result<bool>> DisableAsync(Guid userId, CancellationToken ct = default) => NotFound();

    public Task<Fluentx.Result<bool>> EnableAsync(Guid userId, CancellationToken ct = default) => NotFound();

    private Task<Fluentx.Result<SignInResult>> Answer(SignInContext context, string error)
    {
        Contexts.Add(context);
        return Task.FromResult(NextSignIn is null ? Fluentx.Result.Error<SignInResult>(error) : new Fluentx.Result<SignInResult>(NextSignIn));
    }

    private static Task<Fluentx.Result<bool>> NotFound() => Task.FromResult(Fluentx.Result.Error<bool>(ISvcAuthenticator.UserNotFound));
}
