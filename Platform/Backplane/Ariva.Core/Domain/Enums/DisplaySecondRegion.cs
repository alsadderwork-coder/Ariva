namespace Ariva.Core.Domain.Enums;

/// <summary>
/// What the second region of a split display shows (ARV-140); stored by name. Instructions by default; Advertising only
/// where the site's advertising policy allows it (ARV-145), and no site allows it until that policy exists.
/// </summary>
public enum DisplaySecondRegion
{
    Instructions,
    Advertising
}
