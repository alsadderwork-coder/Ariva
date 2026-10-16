namespace Ariva.Core.Domain.Enums;

/// <summary>
/// Which displays an instruction slide applies to (ARV-141); stored by name. Site: every display of the slide's site. Zone:
/// the displays that show one of the slide's queue zones. Display: the displays named by code.
/// </summary>
public enum InstructionScope
{
    Site,
    Zone,
    Display
}
