namespace Ariva.Core.Domain.Enums;

/// <summary>
/// How a passenger display divides its screen (ARV-140); stored by name. Full: the queue view fills the screen (ARV-058).
/// Split: the queue region comes first in the reading order and keeps its share of the screen; the second region takes the
/// rest, beside it on a landscape screen and below it on a portrait one.
/// </summary>
public enum DisplayLayout
{
    Full,
    Split
}
