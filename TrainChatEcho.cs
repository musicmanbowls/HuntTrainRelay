using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using System.Numerics;

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
    // Palette indices taken from Hunt Helper's own HuntManager (MIT licensed):
    // 12 = pinkish red (what they use for A-ranks), 506 = gold, 16 = dark red,
    // 34 = blue, 64 = white.
    public const ushort ARankColour = 12;
    public const ushort GoldColour = 506;
    public const ushort RedColour = 16;

    private const ushort TextColour = 24;
    public const ushort FlagColour = 559;
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

    /// <summary>
    /// A short coloured line when any mark is first spotted, so a scout working
    /// with the window closed gets confirmation. Fires for B, A and S alike and
    /// is independent of whether the train is recording.
    /// </summary>
    public static void SendSighting(IChatGui chatGui, OtherRankSighting sighting)
    {
        // Zone comes from the sighting itself. Looking it up in ExpansionData
        // only worked for A-ranks, which is why B and S echoes showed "?".
        var zone = !string.IsNullOrWhiteSpace(sighting.ZoneName)
            ? sighting.ZoneName
            : ExpansionData.Lookup(sighting.NameId)?.Location ?? "?";
        var glyph = ExpansionData.InstanceGlyph(sighting.Instance);

        var colour = sighting.Rank switch
        {
            HuntRank.S => GoldColour,
            HuntRank.A => ARankColour,
            _ => (ushort)34, // blue, matching the B-rank dot
        };

        var sb = new SeStringBuilder();
        sb.AddUiForeground(colour);
        sb.AddIcon(BitmapFontIcon.ExclamationRectangle);
        sb.AddText($"{sighting.Name}{glyph}  ({sighting.Rank} rank)");
        sb.AddUiForegroundOff();

        sb.AddUiForeground(GoldColour);
        sb.AddText(" found at ");
        sb.AddUiForegroundOff();

        // A real map link rather than plain text, so it can be clicked to set
        // a flag — the same thing the train echo does.
        if (sighting.MapId != 0 && sighting.TerritoryId != 0)
        {
            sb.AddUiForeground(FlagColour);
            sb.Append(SeString.CreateMapLink(
                sighting.TerritoryId, sighting.MapId,
                sighting.MapPosition.X, sighting.MapPosition.Y));
            sb.AddUiForegroundOff();
        }
        else
        {
            sb.AddUiForeground(GoldColour);
            sb.AddText($"{zone} ({sighting.MapPosition.X:F1}, {sighting.MapPosition.Y:F1})");
            sb.AddUiForegroundOff();
        }

        chatGui.Print(sb.BuiltString);
    }
}
