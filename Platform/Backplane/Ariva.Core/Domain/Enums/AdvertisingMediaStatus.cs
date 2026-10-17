namespace Ariva.Core.Domain.Enums;

/// <summary>
/// An advertisement's state (ARV-142a): a Draft waits for a second person's approval; an Approved item plays within its
/// dates; a Retired one is kept, with its history, and never plays again.
/// </summary>
public enum AdvertisingMediaStatus
{
    Draft,
    Approved,
    Retired
}
