using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HuntTrainRelay;

public readonly record struct WorldEntry(uint RowId, string Name, uint DataCenterId);

/// <summary>
/// Data centres and their worlds, read from the game's own sheets so the list
/// stays correct as Square adds or moves worlds. Used by the Scout tab's
/// counter view, where a scout may want to look at counts for a world they
/// aren't currently on.
/// </summary>
public sealed class WorldData
{
    public IReadOnlyList<(uint Id, string Name)> DataCenters { get; }
    private readonly List<WorldEntry> _worlds = new();

    public WorldData(IDataManager dataManager)
    {
        var dcs = new List<(uint, string)>();

        try
        {
            foreach (var world in dataManager.GetExcelSheet<World>())
            {
                // Skip test/internal worlds, which aren't playable.
                if (!world.IsPublic) continue;

                var name = world.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name)) continue;

                _worlds.Add(new WorldEntry(world.RowId, name, world.DataCenter.RowId));
            }

            foreach (var dc in dataManager.GetExcelSheet<WorldDCGroupType>())
            {
                var name = dc.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!_worlds.Any(w => w.DataCenterId == dc.RowId)) continue;
                dcs.Add((dc.RowId, name));
            }
        }
        catch
        {
            // A missing list just means the picker is empty; counts still work.
        }

        DataCenters = dcs.OrderBy(d => d.Item2).ToList();
    }

    public IReadOnlyList<WorldEntry> WorldsIn(uint dataCenterId) =>
        _worlds.Where(w => w.DataCenterId == dataCenterId).OrderBy(w => w.Name).ToList();

    /// <summary>
    /// Resolves a world to its position in the picker: which data centre index
    /// and which world index within that centre. Returns null when the world
    /// isn't in the list (unknown id, or the sheets failed to load).
    /// </summary>
    public (int DcIndex, int WorldIndex)? LocateWorld(uint worldId)
    {
        var world = _worlds.FirstOrDefault(w => w.RowId == worldId);
        if (world.RowId == 0) return null;

        var dcIndex = -1;
        for (var i = 0; i < DataCenters.Count; i++)
        {
            if (DataCenters[i].Id != world.DataCenterId) continue;
            dcIndex = i;
            break;
        }
        if (dcIndex < 0) return null;

        var worlds = WorldsIn(world.DataCenterId);
        var worldIndex = -1;
        for (var i = 0; i < worlds.Count; i++)
        {
            if (worlds[i].RowId != worldId) continue;
            worldIndex = i;
            break;
        }
        if (worldIndex < 0) return null;

        return (dcIndex, worldIndex);
    }

    public string NameOf(uint worldId) =>
        _worlds.FirstOrDefault(w => w.RowId == worldId).Name ?? $"World {worldId}";
}
