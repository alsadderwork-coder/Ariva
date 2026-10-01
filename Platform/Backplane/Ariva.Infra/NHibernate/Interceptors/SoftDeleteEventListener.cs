using NHibernate.Engine;
using NHibernate.Event;
using NHibernate.Event.Default;
using NHibernate.Persister.Entity;

namespace Ariva.Infra.NHibernate.Interceptors;

/// <summary>
/// Turns a delete of an <see cref="ISoftDeletable"/> entity into a mark (deleted by, deleted on) that is written as an
/// update at flush; other entities are deleted normally. Ported from AMAN, with two fixes: DeletedBy holds the user
/// name like CreatedBy and ModifiedBy (AMAN stored the user id there), and a second delete keeps the first deletion
/// (same rule as <c>BaseSoftDeletableEntity.SoftDelete</c>).
/// </summary>
internal sealed class SoftDeleteEventListener(ICurrentUser currentUser, TimeProvider timeProvider) : DefaultDeleteEventListener
{
    protected override void DeleteEntity(IEventSource session, object entity, EntityEntry entityEntry, bool isCascadeDeleteEnabled, IEntityPersister persister, ISet<object> transientEntities)
    {
        if (entity is ISoftDeletable deletable)
        {
            MarkDeleted(deletable);
            CascadeBeforeDelete(session, persister, entity, entityEntry, transientEntities);
            CascadeAfterDelete(session, persister, entity, transientEntities);
            return;
        }

        base.DeleteEntity(session, entity, entityEntry, isCascadeDeleteEnabled, persister, transientEntities);
    }

    protected override async Task DeleteEntityAsync(IEventSource session, object entity, EntityEntry entityEntry, bool isCascadeDeleteEnabled, IEntityPersister persister, ISet<object> transientEntities, CancellationToken cancellationToken)
    {
        if (entity is ISoftDeletable deletable)
        {
            MarkDeleted(deletable);
            await CascadeBeforeDeleteAsync(session, persister, entity, entityEntry, transientEntities, cancellationToken);
            await CascadeAfterDeleteAsync(session, persister, entity, transientEntities, cancellationToken);
            return;
        }

        await base.DeleteEntityAsync(session, entity, entityEntry, isCascadeDeleteEnabled, persister, transientEntities, cancellationToken);
    }

    private void MarkDeleted(ISoftDeletable deletable)
    {
        if (deletable.IsDeleted)
            return;

        deletable.DeletedBy = currentUser?.Id is { } id
            ? (string.IsNullOrWhiteSpace(currentUser.UserName) ? id.ToString() : currentUser.UserName)
            : AuditInterceptor.AnonymousUserName;
        deletable.DeletedOn = timeProvider.GetUtcNow().UtcDateTime;
    }
}
