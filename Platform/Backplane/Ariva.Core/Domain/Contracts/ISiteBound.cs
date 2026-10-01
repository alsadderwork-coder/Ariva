namespace Ariva.Core.Domain.Contracts;

/// <summary>
/// An entity or view that belongs to one site (ARV-012). Queries over it go through <c>ISiteScope</c> so a caller only
/// sees the sites it is bound to (CWE-863). The site code is set when the record is created and never bound from a
/// request body.
/// </summary>
public interface ISiteBound
{
    string SiteCode { get; }
}
