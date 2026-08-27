using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;

namespace HuntTrainRelay;

/// <summary>
/// Prints a train mark to the local echo log in Hunt Helper's own format —
/// icon + name, a clickable map link for the zone and coordinates, then a
/// position counter. Colours match Hunt Helper's defaults so the output looks
/// familiar to anyone who's used it (adapted from TrainManager.SendTrainFlag,
/// img02/HuntHelper, MIT licensed).
///
/// This only prints locally. Nothing is sent to other players.
/// </summary>
public static class TrainChatEcho
{
    private const ushort TextColour = 24;
    private const ushort FlagColour = 559;
    private const ushort CountColour = 502;

    public static void Send(IChatGui chatGui, IGameGui gameGui, DetectedMark mark, int index, int total, bool openMap = true)
    {
        var mapLink = new MapLinkPayload(
            mark.TerritoryId, mark.MapId, mark.MapPosition.X, mark.MapPosition.Y);

        var glyph = ExpansionData.InstanceGlyph(mark.Instance);

        var sb = new SeStringBuilder();
        sb.AddUiForeground(TextColour);
        sb.AddIcon(BitmapFontIcon.ExclamationRectangle);
        sb.AddText($"{mark.Name}{glyph}---");
        sb.AddUiForegroundOff();

        sb.AddUiForeground(FlagColour);
        sb.Append(SeString.CreateMapLink(mark.TerritoryId, mark.MapId, mark.MapPosition.X, mark.MapPosition.Y));
        sb.AddUiForegroundOff();

        sb.AddUiForeground(CountColour);
        sb.AddText($" --- {index + 1}/{total}");
        sb.AddUiForegroundOff();

        chatGui.Print(sb.BuiltString);

        if (openMap) gameGui.OpenMapWithMapLink(mapLink);
    }
}
