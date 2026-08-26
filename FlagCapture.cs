using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace HuntTrainRelay;

/// <summary>
/// Reads the player's currently-active map flag (the same one Ctrl+Right-Click
/// sets) directly from AgentMap via FFXIVClientStructs. This is a single,
/// documented struct field (AgentMap.FlagMapMarkers / FlagMarkerCount) — not a
/// live-scanning radar system — so it's a much narrower, more stable piece of
/// interop than something like Hunt Helper's own detection. Returns false if
/// no flag is currently set (FlagMarkerCount == 0).
/// </summary>
public static unsafe class FlagCapture
{
    public static bool TryGetCurrentFlag(out uint territoryId, out uint mapId, out float x, out float y)
    {
        territoryId = 0;
        mapId = 0;
        x = 0;
        y = 0;

        var agentMap = AgentMap.Instance();
        if (agentMap == null) return false;
        if (agentMap->FlagMarkerCount == 0) return false;

        var flag = agentMap->FlagMapMarkers[0];
        territoryId = flag.TerritoryId;
        mapId = flag.MapId;
        x = flag.XFloat;
        y = flag.YFloat;
        return true;
    }
}
