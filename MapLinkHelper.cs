using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using System.Linq;

namespace HuntTrainRelay;

/// <summary>
/// Opens the local player's own map with a flag at a manually-entered location.
/// Deliberately does NOT attempt to insert a clickable link into Say/Shout chat —
/// that would need a different, unverified mechanism (Dalamud only confirms a
/// sanctioned way to open your own map with a link, not to inject a rich payload
/// into someone's live chat input box). "Copy Coordinates" is the safe stand-in
/// for actually sharing it with the party.
/// </summary>
public static class MapLinkHelper
{
    public static bool OpenMap(IGameGui gameGui, FlagEntry flag)
    {
        if (!flag.HasLocation) return false;

        var seString = SeString.CreateMapLinkWithInstance(
            flag.TerritoryId, flag.MapId, flag.Instance == 0 ? null : flag.Instance, flag.X, flag.Y);

        var mapLink = seString.Payloads.OfType<MapLinkPayload>().FirstOrDefault();
        if (mapLink == null) return false;

        return gameGui.OpenMapWithMapLink(mapLink);
    }

    public static string CoordinateText(FlagEntry flag)
    {
        var instancePart = flag.Instance > 0 ? $" (Instance {flag.Instance})" : "";
        return $"{flag.Label}{instancePart} — {flag.X:F1}, {flag.Y:F1}";
    }
}
