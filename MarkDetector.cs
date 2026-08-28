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
public sealed class MarkDetector
{
    private readonly IObjectTable _objectTable;
    private readonly IClientState _clientState;
    private readonly IDataManager _dataManager;

    private readonly Dictionary<(uint NameId, uint Instance), DetectedMark> _marks = new();
    private int _nextOrder;

    public IReadOnlyDictionary<(uint NameId, uint Instance), DetectedMark> Marks => _marks;

    public MarkDetector(IObjectTable objectTable, IClientState clientState, IDataManager dataManager)
    {
        _objectTable = objectTable;
        _clientState = clientState;
        _dataManager = dataManager;
    }

    public void Clear()
    {
        _marks.Clear();
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
        var scale = GetMapZoneScale(territoryId);
        var instance = GetCurrentInstance();
        var now = DateTime.UtcNow;

        foreach (var obj in _objectTable)
        {
            if (obj is not Dalamud.Game.ClientState.Objects.Types.IBattleNpc mob) continue;

            var info = ExpansionData.Lookup(mob.NameId);
            if (info == null) continue; // not a tracked A-rank

            var key = (mob.NameId, instance);
            if (_marks.TryGetValue(key, out var existing))
            {
                existing.LastSeenUtc = now;
                existing.MapPosition = ToMapCoordinate(mob.Position, scale);
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
                MapPosition = ToMapCoordinate(mob.Position, scale),
                Dead = false,
                FirstSeenUtc = now,
                LastSeenUtc = now,
                Order = _nextOrder++,
            };
        }
    }

    /// <summary>
    /// Map ID is derived from the territory via the game's own data sheet — it
    /// is NOT a number visible anywhere in the UI, which is exactly why an
    /// earlier hand-entered version of this always produced a flag in the
    /// corner of the map.
    /// </summary>
    public uint GetMapId(uint territoryId) =>
        _dataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryId)?.Map.RowId ?? 0;

    // Everything is scale 100 except the Heavensward zones, which are 95.
    private static float GetMapZoneScale(uint territoryId) =>
        territoryId is >= 397 and <= 402 ? 95f : 100f;

    private static Vector2 ToMapCoordinate(Vector3 worldPos, float scale) =>
        new(ToMapCoordinate(worldPos.X, scale), ToMapCoordinate(worldPos.Z, scale));

    private static float ToMapCoordinate(float pos, float scale) =>
        2048f / scale + pos / 50f + 1f;

    private static unsafe uint GetCurrentInstance()
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
