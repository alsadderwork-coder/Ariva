using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ariva.Core.Services.Imports;
using Microsoft.AspNetCore.DataProtection;

namespace Ariva.Infra.Imports;

/// <summary>
/// The preview token of a CSV import (ARV-123a), as the SSIM import's (ARV-046): Data Protection with one purpose per kind
/// of import, so a device token never opens a mapping commit; it binds the file's SHA-256, the site, the user and the time
/// it was issued, and is good for <see cref="CsvImportLimits.PreviewLifetime"/> on Ariva's clock (the time-limited
/// protector would read the system clock instead). Nothing else a client sends back is trusted: the commit hashes the file
/// it receives and checks every row again.
/// </summary>
internal sealed class ImportPreviewTokens(IDataProtector protector, string kind)
{
    public const string DevicePurpose = "Ariva.CsvImport.Devices.Preview.v1";
    public const string MappingPurpose = "Ariva.CsvImport.DeskCodeMappings.Preview.v1";

    /// <summary>A clock a few minutes behind another host's must not refuse a fresh token.</summary>
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    public static ImportPreviewTokens Devices(IDataProtectionProvider provider) => new(provider.CreateProtector(DevicePurpose), "devices");

    public static ImportPreviewTokens Mappings(IDataProtectionProvider provider) => new(provider.CreateProtector(MappingPurpose), "deskCodeMappings");

    public string Issue(string fileSha256, string siteCode, Guid userId, DateTime utcNow) =>
        protector.Protect(Binding(fileSha256, siteCode, userId) + "|" + utcNow.Ticks.ToString(CultureInfo.InvariantCulture));

    /// <summary>True only for a token of this kind, issued at most the preview lifetime ago, for this file, site and user.</summary>
    public bool Matches(string token, string fileSha256, string siteCode, Guid? userId, DateTime utcNow)
    {
        if (string.IsNullOrEmpty(token) || token.Length > CsvImportLimits.MaxTokenLength || fileSha256 is null || siteCode is null || userId is not { } user)
            return false;
        try
        {
            var bound = protector.Unprotect(token);
            var cut = bound.LastIndexOf('|');
            if (cut < 0 || !long.TryParse(bound.AsSpan(cut + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                return false;
            var age = utcNow - new DateTime(ticks, DateTimeKind.Utc);
            return age <= CsvImportLimits.PreviewLifetime && age >= -Skew &&
                   CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(bound[..cut]), Encoding.UTF8.GetBytes(Binding(fileSha256, siteCode, user)));
        }
        catch (CryptographicException)
        {
            return false; // altered, of another purpose, or of another key ring
        }
    }

    private string Binding(string fileSha256, string siteCode, Guid userId) => string.Join('|', "v1", kind, fileSha256, siteCode, userId.ToString("N"));
}
