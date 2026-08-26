namespace HuntTrainRelay;

/// <summary>
/// Builds a ready-to-paste chat message using FFXIV's own built-in <c>&lt;flag&gt;</c>
/// placeholder — typing this literal text into a chat message and sending it
/// auto-substitutes whatever the sender's currently active map flag is. This is
/// a native game feature, not something the plugin constructs or reads itself:
/// the conductor sets their own flag the normal way (Ctrl+Right-Click) whenever
/// they're actually at (or ready to reference) the location, then copies this
/// message and sends it. No coordinates, no Dalamud map APIs needed at all.
/// </summary>
public static class FlagMessageHelper
{
    public static string BuildChatMessage(FlagEntry flag) => $"{flag.Label} — <flag>";
}
