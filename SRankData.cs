using System.Collections.Generic;

namespace HuntTrainRelay;

public record SRankInfo(string Name, string Expansion, int Order);

/// <summary>
/// Every S-rank in the game, name + expansion only (no zone/location data yet -
/// unlike A-ranks, S-ranks often have multiple possible spawn points and varied
/// trigger conditions rather than one fixed zone, so that's deferred rather than
/// guessed at). Names sourced from Hunt Helper's own bundled Data/*-S.json files
/// for consistency with what conductors already see there.
/// </summary>
public static class SRankData
{
    public static readonly List<SRankInfo> All = new()
    {
        // ARR
        new("Laideronnette", "ARR", 0), new("Wulgaru", "ARR", 0), new("Mindflayer", "ARR", 0),
        new("Thousand-cast Theda", "ARR", 0), new("Zona Seeker", "ARR", 0), new("Brontes", "ARR", 0),
        new("Lampalagua", "ARR", 0), new("Nunyunuwi", "ARR", 0), new("Minhocao", "ARR", 0),
        new("Croque-mitaine", "ARR", 0), new("Croakadile", "ARR", 0), new("The Garlok", "ARR", 0),
        new("Bonnacon", "ARR", 0), new("Nandi", "ARR", 0), new("Chernobog", "ARR", 0),
        new("Safat", "ARR", 0), new("Agrippa The Mighty", "ARR", 0),

        // Heavensward
        new("Kaiser Behemoth", "Heavensward", 1), new("Senmurv", "Heavensward", 1),
        new("The Pale Rider", "Heavensward", 1), new("Gandarewa", "Heavensward", 1),
        new("Bird of Paradise", "Heavensward", 1), new("Leucrotta", "Heavensward", 1),

        // Stormblood
        new("Okina", "Stormblood", 2), new("Gamma", "Stormblood", 2), new("Orghana", "Stormblood", 2),
        new("Udumbara", "Stormblood", 2), new("Bone Crawler", "Stormblood", 2), new("Salt and Light", "Stormblood", 2),

        // Shadowbringers
        new("Aglaope", "Shadowbringers", 3), new("Ixtab", "Shadowbringers", 3), new("Gunitt", "Shadowbringers", 3),
        new("Tarchia", "Shadowbringers", 3), new("Tyger", "Shadowbringers", 3),
        new("Forgiven Pedantry", "Shadowbringers", 3), new("Forgiven Rebellion", "Shadowbringers", 3),
        new("Forgiven Gossip", "Shadowbringers", 3),

        // Endwalker
        new("Armstrong", "Endwalker", 4), new("Narrow-rift", "Endwalker", 4), new("Ophioneus", "Endwalker", 4),
        new("Ruminator", "Endwalker", 4), new("Sphatika", "Endwalker", 4),
        new("Burfurlur the Canny", "Endwalker", 4), new("Ker", "Endwalker", 4), new("Ker Shroud", "Endwalker", 4),

        // Dawntrail
        new("Kirlirger the Abhorrent", "Dawntrail", 5), new("Ihnuxokiy", "Dawntrail", 5),
        new("Neyoozoteel", "Dawntrail", 5), new("Sansheya", "Dawntrail", 5),
        new("Atticus the Primogenitor", "Dawntrail", 5), new("the Forecaster", "Dawntrail", 5),
        new("arch aethereater", "Dawntrail", 5), new("crystal incarnation", "Dawntrail", 5),
    };
}
