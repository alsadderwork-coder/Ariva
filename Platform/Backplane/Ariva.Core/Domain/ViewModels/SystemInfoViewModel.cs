namespace Ariva.Core.Domain.ViewModels;

/// <summary>
/// Product name and version returned by <c>GET /api/v1/system/info</c>.
/// </summary>
/// <param name="Product">The product name, "Ariva".</param>
/// <param name="Version">The product version, without build metadata.</param>
public sealed record SystemInfoViewModel(string Product, string Version);
