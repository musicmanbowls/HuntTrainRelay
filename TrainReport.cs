using System;
using System.Collections.Generic;
using System.Linq;

namespace HuntTrainRelay;

public record TrainReportEntry(
    DateTime KillTimeUtc,
    string Expansion,
    string? Location,
    string Name,
    uint Instance,
    double? MinHours,
    double? MaxHours);

/// <summary>
/// Builds the kill-ordered entry list and Assumed Sniped groups from a set of
/// tracked marks. Pure data — no Discord formatting and no ImGui — so both the
/// webhook message and the in-game "Marks Slain" preview read from exactly the
/// same computation and can never show different results.
/// </summary>
public static class TrainReport
{
    public static List<TrainReportEntry> BuildEntries(List<TrackedMark> marks)
    {
        return marks
            .Select(m =>
            {
                var info = ExpansionData.Lookup(m.ModelId);
                var killTime = EnsureUtc(m.DeathObservedAtUtc ?? m.LastSeenUtc);
                return new TrainReportEntry(
                    killTime,
                    info?.Expansion ?? "No fixed timer",
                    info?.Location,
                    m.Name,
                    m.Instance,
                    info?.MinHours,
                    info?.MaxHours);
            })
            .OrderBy(e => e.KillTimeUtc)
            .ToList();
    }

    /// <summary>
    /// Named marks belonging to any expansion actually represented in this train
    /// that were never observed at all — most likely killed by someone else
    /// before the train got there.
    /// </summary>
    public static List<(string Expansion, List<string> Marks)> BuildSniped(List<TrackedMark> marks)
    {
        var seenModelIds = marks.Select(m => m.ModelId).ToHashSet();
        var touchedExpansions = marks
            .Select(m => ExpansionData.Lookup(m.ModelId)?.Expansion)
            .Where(e => e != null)
            .Select(e => e!)
            .Distinct();

        var result = new List<(string, List<string>)>();
        foreach (var expansion in touchedExpansions)
        {
            var sniped = ExpansionData.ModelIdToMark
                .Where(kv => kv.Value.Expansion == expansion && !seenModelIds.Contains(kv.Key))
                .OrderBy(kv => kv.Value.ZoneOrder)
                .Select(kv => $"{kv.Value.Name} ({kv.Value.Location})")
                .ToList();

            if (sniped.Count > 0)
                result.Add((expansion, sniped));
        }

        return result;
    }

    private static DateTime EnsureUtc(DateTime dt) =>
        dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
}
