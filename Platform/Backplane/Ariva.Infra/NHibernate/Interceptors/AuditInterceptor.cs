using NHibernate;
using NHibernate.Type;

namespace Ariva.Infra.NHibernate.Interceptors;

/// <summary>
/// Stamps created and modified fields on <see cref="IAuditable"/> entities, ported from AMAN's
/// NHibernateGenericInterceptor. Differences: the entity object is stamped too, not only the row state (AMAN left
/// the in-memory entity without its audit values until reloaded); time comes from <see cref="TimeProvider"/> in UTC;
/// a modification is never stamped earlier than the creation (two pods with slightly different clocks). ARV-273: nor at or
/// before the row's last stamp, to the microsecond PostgreSQL keeps, so the last change's time is a version no two stored
/// states of a row share (an advertising policy's change names it): a pod whose clock is behind stamps one microsecond after.
/// SQL text is not logged here; NHibernate's own logger does that when ShowSql is on, without parameter values.
/// </summary>
internal sealed class AuditInterceptor(ICurrentUser currentUser, TimeProvider timeProvider) : EmptyInterceptor
{
    internal const string AnonymousUserName = "anonymous";

    public override bool OnSave(object entity, object id, object[] state, string[] propertyNames, IType[] types)
    {
        if (entity is not IAuditable auditable)
            return false;

        var (userId, userName) = Caller();
        var now = Now();

        auditable.CreatedById = userId;
        auditable.CreatedBy = userName;
        auditable.CreatedOn = now;

        Set(state, propertyNames, nameof(IAuditable.CreatedById), userId);
        Set(state, propertyNames, nameof(IAuditable.CreatedBy), userName);
        Set(state, propertyNames, nameof(IAuditable.CreatedOn), now);
        return true;
    }

    public override bool OnFlushDirty(object entity, object id, object[] currentState, object[] previousState, string[] propertyNames, IType[] types)
    {
        if (entity is not IAuditable auditable)
            return false;

        var (userId, userName) = Caller();
        var now = After(LastStamp(auditable.ModifiedOn, auditable.CreatedOn), Now());

        auditable.ModifiedById = userId;
        auditable.ModifiedBy = userName;
        auditable.ModifiedOn = now;

        Set(currentState, propertyNames, nameof(IAuditable.ModifiedById), userId);
        Set(currentState, propertyNames, nameof(IAuditable.ModifiedBy), userName);
        Set(currentState, propertyNames, nameof(IAuditable.ModifiedOn), now);
        return true;
    }

    /// <summary>The signed-in user or the system identity of a background job; otherwise anonymous with an empty id.</summary>
    private (Guid UserId, string UserName) Caller() =>
        currentUser?.Id is { } id
            ? (id, string.IsNullOrWhiteSpace(currentUser.UserName) ? id.ToString() : currentUser.UserName)
            : (Guid.Empty, AnonymousUserName);

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// ARV-273: the instant a change is stamped with: now, or one microsecond after the row's last stamp (its last change, or
    /// its creation) when now is not later than it at PostgreSQL's resolution.
    /// </summary>
    internal static DateTime After(DateTime? last, DateTime now)
    {
        if (last is not { } previous)
            return now;
        var floor = Microseconds(previous);
        return Microseconds(now) > floor ? now : floor.AddTicks(TimeSpan.TicksPerMicrosecond);
    }

    /// <summary>The row's last stamp: the later of its last change and its creation (a modification written by SQL may be earlier).</summary>
    internal static DateTime? LastStamp(DateTime? modified, DateTime? created) =>
        modified is null ? created : created is null || modified > created ? modified : created;

    private static DateTime Microseconds(DateTime at) => new(at.Ticks - (at.Ticks % TimeSpan.TicksPerMicrosecond), DateTimeKind.Utc);

    private static void Set(object[] state, string[] propertyNames, string propertyName, object value)
    {
        var index = Array.IndexOf(propertyNames, propertyName);
        if (index < 0)
            throw new InvalidOperationException($"Audit property {propertyName} is not mapped.");
        state[index] = value;
    }
}
