using Ariva.Core.Domain.Enums;
using Ariva.Infra.Services.Administration;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Seed;

/// <summary>
/// A starter set of arrivals instructions (ARV-141) for the demo airport (DMO) and the illustrative AUH Terminal A
/// (AUH-TA), in English, Arabic, Portuguese and Swahili, each for the arrival queue zones it concerns: the passport at the
/// photo page and the hats and glasses at the counters, the e-gates where residents and GCC nationals queue, families,
/// and phones. Demo content: the Arabic, Portuguese and Swahili wording awaits a native review before any pilot.
/// </summary>
internal static class DemoInstructionSlides
{
    private static InstructionText T(string title, string body = null) => new(title, body);

    /// <summary>The starter slides for a site's arrival counter queues and the queues that may use the e-gates.</summary>
    public static IReadOnlyList<InstructionSlideValues> Values(IReadOnlyList<string> counters, IReadOnlyList<string> gateUsers)
    {
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(gateUsers);
        return
        [
            new(InstructionScope.Zone, counters, InstructionIcon.Passport, 10, false, new Dictionary<string, InstructionText>
            {
                ["en"] = T("Have your passport open at the photo page"),
                ["ar"] = T("افتح جواز سفرك على صفحة الصورة"),
                ["pt"] = T("Tenha o passaporte aberto na página da fotografia"),
                ["sw"] = T("Fungua pasipoti yako kwenye ukurasa wa picha")
            }),
            new(InstructionScope.Zone, gateUsers, InstructionIcon.EGate, 12, false, new Dictionary<string, InstructionText>
            {
                ["en"] = T("E-gates for UAE residents and GCC nationals", "Scan your passport or Emirates ID, then look at the camera."),
                ["ar"] = T("البوابات الذكية للمقيمين في الإمارات ومواطني دول مجلس التعاون", "امسح جواز سفرك أو بطاقة الهوية الإماراتية، ثم انظر إلى الكاميرا."),
                ["pt"] = T("E-gates para residentes nos EAU e cidadãos do CCG", "Digitalize o passaporte ou o Emirates ID e olhe para a câmara."),
                ["sw"] = T("Milango ya kielektroniki kwa wakazi wa UAE na raia wa GCC", "Changanua pasipoti yako au kitambulisho cha Emirates, kisha tazama kamera.")
            }),
            new(InstructionScope.Zone, counters, InstructionIcon.HatAndGlasses, 8, false, new Dictionary<string, InstructionText>
            {
                ["en"] = T("Remove hats and glasses at the counter"),
                ["ar"] = T("انزع القبعة والنظارات عند الكاونتر"),
                ["pt"] = T("Retire o chapéu e os óculos no balcão"),
                ["sw"] = T("Vua kofia na miwani ukifika dirishani")
            }),
            new(InstructionScope.Zone, counters, InstructionIcon.Family, 8, false, new Dictionary<string, InstructionText>
            {
                ["en"] = T("Families may come to the counter together"),
                ["ar"] = T("يمكن للعائلات التقدم إلى الكاونتر معاً"),
                ["pt"] = T("As famílias podem dirigir-se ao balcão juntas"),
                ["sw"] = T("Familia zinaweza kufika dirishani pamoja")
            }),
            new(InstructionScope.Zone, counters, InstructionIcon.NoPhone, 8, false, new Dictionary<string, InstructionText>
            {
                ["en"] = T("Please do not use your phone at the counter"),
                ["ar"] = T("يرجى عدم استخدام الهاتف عند الكاونتر"),
                ["pt"] = T("Não utilize o telemóvel no balcão"),
                ["sw"] = T("Tafadhali usitumie simu ukiwa dirishani")
            })
        ];
    }

    /// <summary>
    /// Seeds the starter slides once: only while the site never had a slide (one someone retired stays retired; the table
    /// is read directly) and only when the published profile has every queue zone they name. Each is audited with its
    /// values, as the API records a slide it creates. Returns how many it saved.
    /// </summary>
    public static async Task<int> SeedAsync(IStorageProvider storage, AuditTrail audit, string siteCode, IReadOnlyList<InstructionSlideValues> values,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(values);
        var existing = (await storage.ExecuteSqlAsync<CountRow>("""SELECT CAST(count(*) AS integer) AS "Value" FROM instruction_slide WHERE site_code = :site""",
            new Dictionary<string, object> { ["site"] = siteCode }, ct)).Single().Value;
        if (existing > 0)
            return 0;

        await storage.FlushAsync(ct);
        var named = values.SelectMany(v => v.Targets).Distinct(StringComparer.Ordinal).ToList();
        var published = await storage.Query<Zone>()
            .Where(z => z.Profile.SiteCode == siteCode && z.Profile.Status == ZoneProfileStatus.Published && z.Kind == ZoneKind.Queue && named.Contains(z.Name))
            .Select(z => z.Name)
            .ToListAsync(ct);
        if (published.Distinct(StringComparer.Ordinal).Count() != named.Count)
            return 0;

        var position = 0;
        foreach (var value in values)
        {
            var slide = await storage.SaveAsync(new InstructionSlide(siteCode, ++position, value), ct);
            await audit.RecordAsync("InstructionSlide.Created", "InstructionSlide", slide.Id, $"{siteCode}:{slide.Position}", null, slide.AuditSummary(), ct);
        }

        return position;
    }

    private sealed class CountRow
    {
        public int Value { get; set; }
    }
}
