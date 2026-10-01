namespace Ariva.Core.Domain.Contracts;

/// <summary>
/// Created and modified metadata. Every instant is UTC. Ported from AMAN.
/// </summary>
public interface IAuditable : IDomain
{
    string CreatedBy { get; set; }
    DateTime? CreatedOn { get; set; }
    Guid CreatedById { get; set; }
    string ModifiedBy { get; set; }
    DateTime? ModifiedOn { get; set; }
    Guid? ModifiedById { get; set; }
}

/// <summary>
/// Logical deletion: the row stays for audit and is filtered out of normal queries. Ported from AMAN.
/// </summary>
public interface ISoftDeletable : IDomain
{
    string DeletedBy { get; set; }
    DateTime? DeletedOn { get; set; }
    bool IsDeleted { get; }
}
