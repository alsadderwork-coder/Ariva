namespace Ariva.Core.Domain.Enums;

/// <summary>
/// The built-in icons of instruction slides (ARV-141); stored by name. The player draws each from its own icon set, so no
/// uploaded image or markup ever reaches a display. The names say what the icon means, not how it is drawn.
/// </summary>
public enum InstructionIcon
{
    Information,
    Passport,
    EGate,
    HatAndGlasses,
    Fingerprint,
    Family,
    Accessibility,
    Baggage,
    NoPhone,
    FollowSigns
}
