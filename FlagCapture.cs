using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace HuntTrainRelay;

/// <summary>
/// Reads the player's currently-placed map flag (the one Ctrl+Right-Click
/// sets) from AgentMap. A single documented struct field rather than anything
/// resembling a live scan, but still ClientStructs rather than a first-class
/// Dalamud service — so every call is guarded and failure just means "no flag".
/// </summary>
public static unsafe class FlagCapture
{
    public static bool TryGetCurrentFlag(out uint territoryId, out uint mapId, out float x, out float y)
    {
        territoryId = 0;
        mapId = 0;
        x = 0;
        y = 0;

        try
        {
            var agentMap = AgentMap.Instance();
            if (agentMap == null) return false;
            if (agentMap->FlagMarkerCount == 0) return false;

            var flag = agentMap->FlagMapMarkers[0];
            territoryId = flag.TerritoryId;
            mapId = flag.MapId;
            x = flag.XFloat;
            y = flag.YFloat;
            return territoryId != 0 && mapId != 0;
        }
        catch
        {
            return false;
        }
    }
}
