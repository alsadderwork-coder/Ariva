namespace Ariva.Core.Domain.Enums;

/// <summary>
/// What the second region of a split display shows (ARV-140); stored by name. Instructions by default; Advertising only
/// where the site's advertising policy allows it for every zone the display shows (ARV-145, off by default). A display set
/// to Advertising whose site or zone no longer allows it keeps the setting and shows instructions.
/// </summary>
public enum DisplaySecondRegion
{
    Instructions,
    Advertising
}
