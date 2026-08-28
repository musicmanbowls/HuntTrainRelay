using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HuntTrainRelay;

/// <summary>
/// Prints a local reminder (and optionally plays a sound) when the player
/// enters one of the zones holding an S-rank the group actually checks for
/// during trains.
///
/// Territory IDs are taken from Hunt Helper's own Enums.cs (MIT licensed,
/// img02/HuntHelper), so they're authoritative rather than guessed.
///
/// The message is printed with IChatGui.Print, which is local-only by nature —
/// nothing is ever sent to other players.
/// </summary>
public sealed class SRankZoneReminder : IDisposable
{
    private static readonly Dictionary<uint, string> ZoneToSRank = new()
    {
        [813] = "Tyger",        // Lakeland
        [960] = "Narrow-rift",  // Ultima Thule
        [961] = "Ophioneus",    // Elpis
    };

    // Don't re-fire for the same zone within this window — instance switches and
    // quick zone bounces would otherwise spam the reminder.
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(2);

    private readonly IClientState _clientState;
    private readonly IChatGui _chatGui;
    private readonly IPluginLog _log;
    private readonly Configuration _config;
    private readonly MarkDetector _detector;

    private readonly Dictionary<uint, DateTime> _lastFiredUtc = new();

    public SRankZoneReminder(IClientState clientState, IChatGui chatGui, IPluginLog log, Configuration config, MarkDetector detector)
    {
        _clientState = clientState;
        _chatGui = chatGui;
        _log = log;
        _config = config;
        _detector = detector;

        _clientState.TerritoryChanged += OnTerritoryChanged;
    }

    public void Dispose()
    {
        _clientState.TerritoryChanged -= OnTerritoryChanged;
    }

    private void OnTerritoryChanged(uint territoryId)
    {
        if (!_config.SRankZoneReminderEnabled) return;
        if (!ZoneToSRank.TryGetValue(territoryId, out var markName)) return;

        // Only remind about marks the conductor has actually added to the watch
        // list — otherwise this fires every time anyone passes through Elpis,
        // Lakeland or Ultima Thule for any reason at all.
        var watch = _config.Flags.FirstOrDefault(f => f.TerritoryId == territoryId);
        if (watch == null) return;

        var now = DateTime.UtcNow;
        if (_lastFiredUtc.TryGetValue(territoryId, out var last) && now - last < Cooldown) return;
        _lastFiredUtc[territoryId] = now;

        // If the conductor added a watch for this mark with a specific spawn
        // spot chosen, include it — and make it a clickable flag so they can
        // jump straight there rather than reading coordinates off the screen.
        if (watch.HasLocation)
        {
            var mapId = _detector.GetMapId(territoryId);
            if (mapId != 0)
            {
                var link = SeString.CreateMapLink(territoryId, mapId, watch.X, watch.Y);
                var sb = new SeStringBuilder();
                sb.AddUiForeground(TrainChatEcho.GoldColour);
                sb.AddText("REMINDER TO CHECK ");
                sb.AddUiForegroundOff();
                sb.AddUiForeground(TrainChatEcho.ARankColour);
                sb.AddText(markName.ToUpperInvariant());
                sb.AddUiForegroundOff();
                sb.AddUiForeground(TrainChatEcho.GoldColour);
                sb.AddText(" — ");
                sb.AddUiForegroundOff();
                sb.Append(link);
                _chatGui.Print(sb.BuiltString);
            }
            else
            {
                _chatGui.Print(BuildReminder(markName, $" ({watch.X:F1}, {watch.Y:F1})"));
            }
        }
        else
        {
            _chatGui.Print(BuildReminder(markName, null));
        }

        if (_config.SRankZoneReminderSound)
            PlayReminderSound();
    }

    /// <summary>
    /// "REMINDER TO CHECK" in gold with the mark name in red, so it stands out
    /// in a busy echo log. Colour indices come from Hunt Helper's own palette.
    /// </summary>
    private static SeString BuildReminder(string markName, string? suffix)
    {
        var sb = new SeStringBuilder();
        sb.AddUiForeground(TrainChatEcho.GoldColour);
        sb.AddText("REMINDER TO CHECK ");
        sb.AddUiForegroundOff();

        sb.AddUiForeground(TrainChatEcho.ARankColour);
        sb.AddText(markName.ToUpperInvariant());
        sb.AddUiForegroundOff();

        if (!string.IsNullOrEmpty(suffix))
        {
            sb.AddUiForeground(TrainChatEcho.GoldColour);
            sb.AddText(suffix);
            sb.AddUiForegroundOff();
        }

        return sb.BuiltString;
    }

    /// <summary>
    /// Printing "&lt;se.6&gt;" into chat does NOT work: Dalamud deliberately
    /// flattens that payload to plain text so plugins can't spam sounds every
    /// frame. The supported route is UIGlobals.PlayChatSoundEffect from
    /// ClientStructs. Wrapped defensively so a failure here never costs the
    /// reminder message itself.
    /// </summary>
    private void PlayReminderSound()
    {
        try
        {
            FFXIVClientStructs.FFXIV.Client.UI.UIGlobals.PlayChatSoundEffect(6);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not play the S-rank reminder sound; the chat reminder still fired.");
        }
    }
}
