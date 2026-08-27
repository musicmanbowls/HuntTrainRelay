using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using System.Linq;

namespace HuntTrainRelay;

/// <summary>
/// Opens the player's map with a flag on a detected mark, which also sets their
/// real in-game flag (same as Ctrl+Right-Click).
///
/// An earlier version of this always landed in the corner of the map. The cause
/// was the Map ID: it was being hand-entered, but Map ID isn't visible anywhere
/// in the game UI — it's derived from the territory via the game's data sheet.
/// A wrong (or zero) Map ID produces exactly that corner behaviour. Here it's
/// computed by MarkDetector.GetMapId, so it's always right.
/// </summary>
public static class MapFlagHelper
{
    public static bool FlagMark(IGameGui gameGui, DetectedMark mark)
    {
        if (mark.MapId == 0 || mark.TerritoryId == 0) return false;

        var seString = SeString.CreateMapLinkWithInstance(
            mark.TerritoryId,
            mark.MapId,
            mark.Instance == 0 ? null : (int)mark.Instance,
            mark.MapPosition.X,
            mark.MapPosition.Y);

        var mapLink = seString.Payloads.OfType<MapLinkPayload>().FirstOrDefault();
        if (mapLink == null) return false;

        return gameGui.OpenMapWithMapLink(mapLink);
    }
}
