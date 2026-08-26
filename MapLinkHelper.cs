using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using System.Linq;

namespace HuntTrainRelay;

/// <summary>
/// Opens the local player's own map with a flag at a saved location. This isn't
/// just a visual — it actually sets the player's real in-game flag to that spot,
/// the same as Ctrl+Right-Clicking it manually. That's what makes chaining into
/// FlagMessageHelper.BuildChatMessage work: ping first (sets your flag for
/// real), then copy the message — the &lt;flag&gt; placeholder now resolves to
/// the correct location when pasted and sent.
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

    public static string CoordinateSummary(FlagEntry flag)
    {
        if (!flag.HasLocation) return "No location saved yet.";
        var instancePart = flag.Instance > 0 ? $", Instance {flag.Instance}" : "";
        return $"Territory {flag.TerritoryId}, Map {flag.MapId}{instancePart} — ({flag.X:F1}, {flag.Y:F1})";
    }
}
