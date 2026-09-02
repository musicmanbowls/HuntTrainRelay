using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace HuntTrainRelay;

/// <summary>
/// One mark detected by our own scanning. Position is stored in in-game map
/// coordinates (the 1-42ish numbers shown on the map), not raw world position,
/// so it can be handed straight to a map link or an aetheryte distance check.
/// </summary>
public class DetectedMark
{
    public string Name = string.Empty;
    public uint NameId;
    public uint TerritoryId;
    public uint MapId;
    public uint Instance;
    public Vector2 MapPosition;
    public bool Dead;
    public DateTime FirstSeenUtc;
    public DateTime LastSeenUtc;
    public DateTime? DeathObservedAtUtc;

    /// <summary>
    /// Position in the train. Assigned incrementally as marks are first spotted,
    /// so the default order is simply the order they were scouted — and it can
    /// be rewritten freely by drag-and-drop reordering.
    /// </summary>
    public int Order;

    /// <summary>
    /// A conductor-placed flag rather than a detected mark. Behaves like any
    /// other row (teleport, dead, drag, auto-advance) but is left out of the
    /// final train report.
    /// </summary>
    public bool IsCustom;

    /// <summary>Zone label for custom entries, which have no mark data to look up.</summary>
    public string ZoneName = string.Empty;

    /// <summary>A scout intends to prep this mark before the train reaches it.</summary>
    public bool Spiced;
}

/// <summary>
/// Scans the object table for A-rank hunt marks and maintains our own train
/// list, independent of Hunt Helper. Detection runs on IObjectTable, a stable
/// first-class Dalamud service — the same tier as everything else here.
///
/// Coordinate conversion and the map-scale quirk are adapted from Hunt Helper
/// (img02/HuntHelper, MIT licensed): every zone uses a scale of 100 except the
/// Heavensward zones (territory 397-402) which use 95.
/// </summary>
/// <summary>
/// Any mark spotted while scanning, of any rank. Deliberately separate from
/// DetectedMark and from the train: sightings are "what I have seen", while
/// the train is "what I am recording". A-ranks appear in BOTH when recording
/// is on, and only here when it's paused — which is what lets the map and the
/// detection echo keep working with the train paused.
/// </summary>
public class OtherRankSighting
{
    public string Name = string.Empty;
    public uint NameId;
    public HuntRank Rank;
    public uint TerritoryId;
    public uint MapId;
    public uint Instance;
    public Vector2 MapPosition;
    public DateTime LastSeenUtc;

    /// <summary>Zone name, resolved once at first sighting.</summary>
    public string ZoneName = string.Empty;
}

public sealed class MarkDetector
{
    private readonly IObjectTable _objectTable;
    private readonly IClientState _clientState;
    private readonly IDataManager _dataManager;
    private readonly Configuration _config;

    private readonly Dictionary<(uint NameId, uint Instance), DetectedMark> _marks = new();
    private int _nextOrder;

    // Synthetic ids for custom flags, counting down from the top so they can
    // never collide with a real BNpcName row id.
    private uint _nextCustomId = uint.MaxValue;

    private readonly Dictionary<(uint NameId, uint Instance), OtherRankSighting> _otherRanks = new();

    /// <summary>
    /// Every mark seen, of every rank. Independent of the train, and populated
    /// whether or not recording is active.
    /// </summary>
    public IReadOnlyDictionary<(uint NameId, uint Instance), OtherRankSighting> OtherRanks => _otherRanks;

    /// <summary>Raised the first time any mark is spotted, regardless of rank.</summary>
    public event Action<OtherRankSighting>? OtherRankDetected;

    /// <summary>Raised once when a mark is first picked up by scanning.</summary>
    public event Action<DetectedMark>? MarkDetected;

    public IReadOnlyDictionary<(uint NameId, uint Instance), DetectedMark> Marks => _marks;

    public MarkDetector(IObjectTable objectTable, IClientState clientState, IDataManager dataManager, Configuration config)
    {
        _objectTable = objectTable;
        _clientState = clientState;
        _dataManager = dataManager;
        _config = config;
    }

    public void Clear()
    {
        _marks.Clear();
        _otherRanks.Clear();
        _nextOrder = 0;
    }

    /// <summary>
    /// Marks in scouted order (or whatever order the conductor has dragged them
    /// into), which is how the train list is always displayed.
    /// </summary>
    public List<DetectedMark> Ordered() => _marks.Values.OrderBy(m => m.Order).ToList();

    /// <summary>
    /// Rewrites every mark's Order to match the given sequence. Used after a
    /// drag swap so the new arrangement sticks.
    /// </summary>
    public void ApplyOrder(IReadOnlyList<DetectedMark> ordered)
    {
        for (var i = 0; i < ordered.Count; i++)
            ordered[i].Order = i;

        _nextOrder = ordered.Count;
    }

    public void Remove((uint NameId, uint Instance) key) => _marks.Remove(key);

    /// <summary>
    /// Removes every mark currently flagged dead — the equivalent of Hunt
    /// Helper's own "Remove Dead" tidy-up.
    /// </summary>
    public void RemoveDead()
    {
        foreach (var key in _marks.Where(kv => kv.Value.Dead).Select(kv => kv.Key).ToList())
            _marks.Remove(key);
    }

    /// <summary>
    /// Folds an imported list into the current one. Existing entries win, so
    /// importing never overwrites a mark you've personally seen (and possibly
    /// already marked dead). Returns how many were genuinely new.
    /// </summary>
    public int Merge(IEnumerable<DetectedMark> incoming)
    {
        var added = 0;
        foreach (var mark in incoming)
        {
            var key = (mark.NameId, mark.Instance);
            if (_marks.ContainsKey(key)) continue;
            mark.Order = _nextOrder++;
            _marks[key] = mark;
            added++;
        }
        return added;
    }

    /// <summary>
    /// One scan pass. Adds any newly sighted A-rank marks and refreshes the last
    /// seen time on ones already known. Never removes anything — a mark going out
    /// of render range shouldn't drop it from the train.
    ///
    /// When recordNew is false, marks already in the list still update, but
    /// nothing new is picked up — that's the pause button, and it's deliberately
    /// narrower than switching tracking off entirely (which would also stop
    /// kill-time recording for the marks already being tracked).
    /// </summary>
    public void Scan(bool recordNew = true)
    {
        var territoryId = _clientState.TerritoryType;
        if (territoryId == 0) return;

        var mapId = GetMapId(territoryId);
        var instance = GetCurrentInstance();
        var now = DateTime.UtcNow;

        // A mark that's disappeared from somewhere we're standing has almost
        // certainly been killed — drop it so its dot goes back to grey.
        var player = _objectTable.LocalPlayer;
        if (player != null)
        {
            var playerMapPos = MapCoordinates.FromWorld(_dataManager, mapId, player.Position.X, player.Position.Z);
            // One scan interval plus a little slack: any shorter and a mark
            // simply missed by one pass would be wrongly declared dead.
            var stale = Math.Max(2, _config.PollIntervalSeconds) + 1;
            ExpireNearbySightings(playerMapPos, territoryId, instance, stale);
        }

        foreach (var obj in _objectTable)
        {
            if (obj is not Dalamud.Game.ClientState.Objects.Types.IBattleNpc mob) continue;

            var info = ExpansionData.Lookup(mob.NameId);
            if (info == null)
            {
                // B or S rank: worth showing on the map, never joins the train.
                TrackSighting(mob, territoryId, mapId, instance, now, null);
                continue;
            }

            // An A-rank is always recorded as a sighting, so the map and the
            // detection echo work even with train recording paused. Whether it
            // also joins the train is decided below.
            TrackSighting(mob, territoryId, mapId, instance, now, HuntRank.A);

            var key = (mob.NameId, instance);
            if (_marks.TryGetValue(key, out var existing))
            {
                existing.LastSeenUtc = now;
                existing.MapPosition = MapCoordinates.FromWorld(_dataManager, mapId, mob.Position.X, mob.Position.Z);
                continue;
            }

            if (!recordNew) continue;

            _marks[key] = new DetectedMark
            {
                Name = mob.Name.TextValue,
                NameId = mob.NameId,
                TerritoryId = territoryId,
                MapId = mapId,
                Instance = instance,
                MapPosition = MapCoordinates.FromWorld(_dataManager, mapId, mob.Position.X, mob.Position.Z),
                Dead = false,
                FirstSeenUtc = now,
                LastSeenUtc = now,
                Order = _nextOrder++,
            };

            MarkDetected?.Invoke(_marks[key]);
        }
    }

    /// <summary>
    /// Map ID is derived from the territory via the game's own data sheet — it
    /// is NOT a number visible anywhere in the UI, which is exactly why an
    /// earlier hand-entered version of this always produced a flag in the
    /// corner of the map.
    /// </summary>
    /// <summary>
    /// Adds a conductor-placed flag as a row in the train. Returns null if no
    /// flag is currently set.
    /// </summary>
    public DetectedMark? AddCustomFlag(string label)
    {
        if (!FlagCapture.TryGetCurrentFlag(out var territoryId, out var mapId, out var x, out var y))
            return null;

        var mark = new DetectedMark
        {
            Name = string.IsNullOrWhiteSpace(label) ? "Custom Flag" : label,
            NameId = _nextCustomId--,
            TerritoryId = territoryId,
            MapId = mapId,
            Instance = 0,
            MapPosition = MapCoordinates.FromWorld(_dataManager, mapId, x, y),
            Dead = false,
            FirstSeenUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow,
            Order = _nextOrder++,
            IsCustom = true,
            ZoneName = GetZoneName(territoryId),
        };

        _marks[(mark.NameId, mark.Instance)] = mark;
        return mark;
    }

    private void TrackSighting(
        Dalamud.Game.ClientState.Objects.Types.IBattleNpc mob,
        uint territoryId, uint mapId, uint instance, DateTime now, HuntRank? knownRank)
    {
        HuntRank rank;
        if (knownRank is { } r)
        {
            rank = r;
        }
        else
        {
            var other = OtherRankData.Lookup(mob.NameId);
            if (other == null) return;
            rank = other.Rank;
        }

        var key = (mob.NameId, instance);
        if (_otherRanks.TryGetValue(key, out var existing))
        {
            existing.LastSeenUtc = now;
            existing.MapPosition = MapCoordinates.FromWorld(_dataManager, mapId, mob.Position.X, mob.Position.Z);
            return;
        }

        var sighting = new OtherRankSighting
        {
            Name = mob.Name.TextValue,
            NameId = mob.NameId,
            Rank = rank,
            TerritoryId = territoryId,
            MapId = mapId,
            Instance = instance,
            MapPosition = MapCoordinates.FromWorld(_dataManager, mapId, mob.Position.X, mob.Position.Z),
            LastSeenUtc = now,
            ZoneName = GetZoneName(territoryId),
        };

        _otherRanks[key] = sighting;
        OtherRankDetected?.Invoke(sighting);
    }

    /// <summary>Clears all sightings.</summary>
    public void ClearOtherRanks() => _otherRanks.Clear();

    /// <summary>Forgets one sighting, e.g. when a mark is known to be dead.</summary>
    public void RemoveSighting(uint nameId, uint instance) => _otherRanks.Remove((nameId, instance));

    /// <summary>
    /// Drops sightings for marks that have gone from a spot we're still
    /// standing next to — almost always because they were just killed.
    ///
    /// Distance matters here: a sighting going stale while we're far away just
    /// means we flew off, and that's exactly the scouting information we want
    /// to keep. Only a mark missing from somewhere we can currently see counts
    /// as gone.
    /// </summary>
    public void ExpireNearbySightings(Vector2 playerMapPos, uint territoryId, uint instance, double staleSeconds)
    {
        const float visibleMapUnits = 3f; // roughly the game's render range

        var now = DateTime.UtcNow;
        foreach (var (key, sighting) in _otherRanks.ToList())
        {
            if (sighting.TerritoryId != territoryId) continue;
            if (sighting.Instance != instance) continue;
            if ((now - sighting.LastSeenUtc).TotalSeconds < staleSeconds) continue;
            if (Vector2.Distance(sighting.MapPosition, playerMapPos) > visibleMapUnits) continue;

            _otherRanks.Remove(key);
        }
    }

    /// <summary>Zone name straight from the game's own data, so it's always correct.</summary>
    public string GetZoneName(uint territoryId)
    {
        try
        {
            var row = _dataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryId);
            var name = row?.PlaceName.ValueNullable?.Name.ExtractText();
            return string.IsNullOrWhiteSpace(name) ? $"Territory {territoryId}" : name;
        }
        catch
        {
            return $"Territory {territoryId}";
        }
    }

    /// <summary>Snapshot of the whole train for saving to disk.</summary>
    public List<PersistedMark> ToPersisted() =>
        Ordered().Select(m => new PersistedMark
        {
            Name = m.Name,
            NameId = m.NameId,
            TerritoryId = m.TerritoryId,
            MapId = m.MapId,
            Instance = m.Instance,
            X = m.MapPosition.X,
            Y = m.MapPosition.Y,
            Dead = m.Dead,
            FirstSeenUtc = m.FirstSeenUtc,
            LastSeenUtc = m.LastSeenUtc,
            DeathObservedAtUtc = m.DeathObservedAtUtc,
            Order = m.Order,
            IsCustom = m.IsCustom,
            ZoneName = m.ZoneName,
            Spiced = m.Spiced,
        }).ToList();

    /// <summary>
    /// Restores a saved train, replacing whatever's currently held. Custom
    /// flags keep their synthetic ids, and the id counter is moved below the
    /// lowest one so new flags can't collide with restored ones.
    /// </summary>
    public void LoadPersisted(List<PersistedMark> saved)
    {
        _marks.Clear();
        _nextOrder = 0;

        foreach (var p in saved)
        {
            var mark = new DetectedMark
            {
                Name = p.Name,
                NameId = p.NameId,
                TerritoryId = p.TerritoryId,
                MapId = p.MapId,
                Instance = p.Instance,
                MapPosition = new Vector2(p.X, p.Y),
                Dead = p.Dead,
                FirstSeenUtc = p.FirstSeenUtc,
                LastSeenUtc = p.LastSeenUtc,
                DeathObservedAtUtc = p.DeathObservedAtUtc,
                Order = p.Order,
                IsCustom = p.IsCustom,
                ZoneName = p.ZoneName,
                Spiced = p.Spiced,
            };

            _marks[(mark.NameId, mark.Instance)] = mark;
            if (mark.Order >= _nextOrder) _nextOrder = mark.Order + 1;
            if (mark.IsCustom && mark.NameId <= _nextCustomId) _nextCustomId = mark.NameId - 1;
        }
    }

    public uint GetMapId(uint territoryId) =>
        _dataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryId)?.Map.RowId ?? 0;


    public static unsafe uint GetCurrentInstance()
    {
        try
        {
            var uiState = UIState.Instance();
            return uiState == null ? 0 : uiState->PublicInstance.InstanceId;
        }
        catch
        {
            return 0;
        }
    }
}
