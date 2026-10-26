namespace Ariva.Infra.Staffing;

/// <summary>
/// One rule for which queue zones are the arrivals hall's lanes and what lane each has, read by the staffing recommendation
/// (<see cref="StaffingInputs"/>) and the predicted-breach wave (<c>ProjectedArrivalWave</c>) alike (owner decision
/// 2026-10-26, ARV-185). A lane is the queue zone's own lane category, as the immigration screen and the nowcast desk term
/// read it. The arrivals hall: a queue zone of the site's published profile with a lane category, on a level with an
/// Immigration checkpoint, but a queue whose service zones stand at desks and only at desks of Emigration checkpoints (a
/// departures queue on a level the two halls share, as ARV-191 found). Each zone's lane then takes its categories of the
/// arrival wave through <see cref="Core.Flights.LaneDemand"/>, shared equally among the hall's zones of that lane.
/// </summary>
internal static class ArrivalsHallLanes
{
    /// <summary>
    /// The body of a common table expression (<c>WITH hall AS (...)</c>) over the arrivals hall's queue zones of the site
    /// <c>:site</c>, one row per zone: <c>id</c>, <c>profile_id</c>, <c>zone</c> (its name), <c>lane</c> and <c>level_id</c>.
    /// Constant SQL with one named parameter (CWE-89); a service zone's desk and checkpoint count only at this site (CWE-863: a
    /// link to another site's desk, which only the service refuses, decides nothing here).
    /// </summary>
    internal const string QueueZones = """
        SELECT z.id, z.profile_id, z.name AS zone, z.lane_category AS lane, z.level_id
          FROM zone z JOIN zone_profile p ON p.id = z.profile_id
         WHERE p.site_code = :site AND p.status = 'Published' AND z.kind = 'Queue' AND z.lane_category IS NOT NULL
           AND EXISTS (SELECT 1 FROM checkpoint i
                        WHERE i.level_id = z.level_id AND i.site_code = :site AND i.deleted_on IS NULL AND i.kind = 'Immigration')
           AND NOT (EXISTS (SELECT 1 FROM zone s JOIN desk d ON d.id = s.desk_id AND d.site_code = :site AND d.deleted_on IS NULL
                              JOIN checkpoint c ON c.id = d.checkpoint_id AND c.site_code = :site AND c.deleted_on IS NULL
                             WHERE s.profile_id = z.profile_id AND s.queue_zone_id = z.id)
                    AND NOT EXISTS (SELECT 1 FROM zone s JOIN desk d ON d.id = s.desk_id AND d.site_code = :site AND d.deleted_on IS NULL
                                      JOIN checkpoint c ON c.id = d.checkpoint_id AND c.site_code = :site AND c.deleted_on IS NULL
                                     WHERE s.profile_id = z.profile_id AND s.queue_zone_id = z.id AND c.kind <> 'Emigration'))
        """;
}
