using NHibernate;
using NHibernate.Type;

namespace Ariva.Infra.NHibernate.Interceptors;

/// <summary>
/// Stamps created and modified fields on <see cref="IAuditable"/> entities, ported from AMAN's
/// NHibernateGenericInterceptor. Differences: the entity object is stamped too, not only the row state (AMAN left
/// the in-memory entity without its audit values until reloaded); time comes from <see cref="TimeProvider"/> in UTC;
/// a modification is never stamped earlier than the creation (two pods with slightly different clocks).
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
        var now = Now();
        if (auditable.CreatedOn is { } created && now < created)
            now = created;

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

    private static void Set(object[] state, string[] propertyNames, string propertyName, object value)
    {
        var index = Array.IndexOf(propertyNames, propertyName);
        if (index < 0)
            throw new InvalidOperationException($"Audit property {propertyName} is not mapped.");
        state[index] = value;
    }
}
