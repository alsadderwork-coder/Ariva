namespace Ariva.Core.Domain.Common;

/// <summary>
/// Entity with created and modified metadata, ported from AMAN. Instants are UTC; the stamp methods refuse local or
/// unspecified times so a server clock in local time can never leak into the data (CWE-613 risk noted for AMAN).
/// The unit of work stamps entities before commit (ARV-005).
/// </summary>
public abstract class BaseAuditableEntity<T> : EntityBase<T>, IEntity<Guid?>, IAuditable where T : BaseAuditableEntity<T>
{
    #region Auditable Properties

    public virtual string CreatedBy { get; set; }
    public virtual DateTime? CreatedOn { get; set; }
    public virtual Guid CreatedById { get; set; }
    public virtual string ModifiedBy { get; set; }
    public virtual DateTime? ModifiedOn { get; set; }
    public virtual Guid? ModifiedById { get; set; }

    #endregion

    #region Business Logic Methods

    public virtual void StampCreated(Guid userId, string userName, DateTime utcNow)
    {
        EnsureUtc(utcNow);
        CreatedById = userId;
        CreatedBy = userName;
        CreatedOn = utcNow;
    }

    public virtual void StampModified(Guid userId, string userName, DateTime utcNow)
    {
        EnsureUtc(utcNow);
        if (CreatedOn is { } created && utcNow < created)
            throw new ArgumentOutOfRangeException(nameof(utcNow), "A modification cannot precede the creation.");
        ModifiedById = userId;
        ModifiedBy = userName;
        ModifiedOn = utcNow;
    }

    protected static void EnsureUtc(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Audit instants must be UTC (DateTimeKind.Utc).", nameof(value));
    }

    #endregion
}

/// <summary>
/// Auditable entity with logical deletion, ported from AMAN.
/// </summary>
public abstract class BaseSoftDeletableEntity<T> : BaseAuditableEntity<T>, ISoftDeletable where T : BaseSoftDeletableEntity<T>
{
    #region Soft Delete Properties

    public virtual string DeletedBy { get; set; }
    public virtual DateTime? DeletedOn { get; set; }
    public virtual bool IsDeleted => DeletedOn is not null;

    #endregion

    #region Business Logic Methods

    /// <summary>Marks the entity deleted. Deleting twice keeps the first deletion (who and when).</summary>
    public virtual void SoftDelete(string deletedBy, DateTime utcNow)
    {
        EnsureUtc(utcNow);
        if (IsDeleted)
            return;
        DeletedBy = deletedBy;
        DeletedOn = utcNow;
    }

    public virtual void Restore()
    {
        DeletedBy = null;
        DeletedOn = null;
    }

    #endregion
}
