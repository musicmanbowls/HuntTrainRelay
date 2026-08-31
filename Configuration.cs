using Dalamud.Configuration;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;

namespace HuntTrainRelay;

public enum SpawnStatus { Unknown, Spawned, NotSpawned }

[Serializable]
public class WebhookEntry
{
    public bool Enabled { get; set; } = true;
    public string Label { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

/// <summary>
/// An S-rank watch for the current train. Label is the display text (mark
/// name, or mark name + which known spawn spot for Narrow-rift specifically).
/// </summary>
[Serializable]
public class FlagEntry
{
    public string Label { get; set; } = string.Empty;
    public SpawnStatus SpawnStatus { get; set; } = SpawnStatus.Unknown;

    /// <summary>Zone this watch belongs to, so the zone-entry reminder can match it.</summary>
    public uint TerritoryId { get; set; }

    /// <summary>Map coordinates of the chosen spawn spot, if one was picked.</summary>
    public bool HasLocation { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 4;

    /// <summary>
    /// Discord webhooks. Enabled controls whether this one gets posted to at
    /// all — e.g. a testing channel can sit here disabled without needing to be
    /// removed. Label is just for your own reference (which server is which).
    /// </summary>
    public List<WebhookEntry> Webhooks { get; set; } = new() { new WebhookEntry() };

    /// <summary>Legacy field from before per-webhook Enabled/Label. Migrated once.</summary>
    [Obsolete("Use Webhooks instead. Kept only for migrating old saved configs.")]
    public List<string>? WebhookUrls { get; set; }

    /// <summary>
    /// S-rank watches for the current train. Empty at the start of every train —
    /// conductors add what they want, it clears on Reset or a successful End
    /// Train Now, same lifecycle as tracking itself.
    /// </summary>
    public List<FlagEntry> Flags { get; set; } = new();

    /// <summary>
    /// Only the conductor actively recording the train should have this on,
    /// to avoid two clients both posting the same "train complete" message.
    /// </summary>
    public bool TrackingEnabled { get; set; } = false;

    /// <summary>
    /// How often (in seconds) to check Hunt Helper's train list for changes.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 3;

    /// <summary>
    /// When Hunt Tally (kihtli/HuntTally) is installed, automatically mark a
    /// tracked mark dead the moment the game confirms you were credited with
    /// the kill — with Hunt Tally's exact kill timestamp rather than our
    /// poll-observed approximation. Has no effect if Hunt Tally isn't loaded.
    /// </summary>
    public bool AutoMarkDeadEnabled { get; set; } = true;

    /// <summary>
    /// Print a local chat reminder on entering Lakeland (Tyger), Ultima Thule
    /// (Narrow-rift) or Elpis (Ophioneus).
    /// </summary>
    /// <summary>
    /// Use our own mark detection for reports instead of Hunt Helper's list.
    /// Defaults to false so updating changes nothing until deliberately switched
    /// — both lists are always populated, so they can be compared side by side
    /// on the Train tab first.
    /// </summary>
    public bool UseOwnTrainList { get; set; } = false;

    /// <summary>
    /// Pauses picking up NEW marks, without stopping anything else — marks
    /// already in the list keep updating and still auto-mark dead from Hunt
    /// Tally. For detours into zones whose A-ranks shouldn't join the train.
    /// </summary>
    public bool ScanningPaused { get; set; } = false;

    /// <summary>Echo a mark to chat when its row is clicked in the train list.</summary>
    public bool EchoOnMarkClick { get; set; } = true;

    /// <summary>Echo to chat each time a new A-rank is picked up while scouting.</summary>
    public bool EchoOnDetection { get; set; } = false;

    /// <summary>Teleporting to a mark also drops the map flag on it.</summary>
    public bool TeleportAlsoFlags { get; set; } = true;

    /// <summary>Drop the zone column in the train popout to keep it narrow.</summary>
    public bool HideZonesInPopout { get; set; } = false;

    /// <summary>Show how long ago each mark was last seen, on its row.</summary>
    public bool ShowMarkAge { get; set; } = true;

    /// <summary>
    /// When the current mark dies, move the pointer to the next live mark
    /// automatically — so a conductor can work through a train without
    /// touching the list.
    /// </summary>
    public bool AutoAdvance { get; set; } = true;

    /// <summary>Echo (and flag) the new current mark when the pointer advances.</summary>
    public bool EchoOnAdvance { get; set; } = true;

    /// <summary>
    /// Hide marks already killed from the train list display. They stay in the
    /// train and still appear in reports with their kill times — this only
    /// shortens what's on screen.
    /// </summary>
    public bool HideDeadMarks { get; set; } = false;

    /// <summary>
    /// Aetheryte ids never to route to — e.g. The Macarenses Angle in The
    /// Tempest, which looks close on a flat map but is far below everything.
    /// </summary>
    public List<uint> BlacklistedAetherytes { get; set; } = new();

    /// <summary>Height of a train list row, in pixels.</summary>
    public int TrainRowHeight { get; set; } = 22;

    public bool SRankZoneReminderEnabled { get; set; } = true;

    /// <summary>
    /// Also play a sound with that reminder. Separate toggle because the sound
    /// needs a ClientStructs call, which is a slightly less stable API surface
    /// than the rest of the plugin — if it ever misbehaves, the message can stay.
    /// </summary>
    public bool SRankZoneReminderSound { get; set; } = true;

    /// <summary>
    /// Extra names credited alongside the submitting character on a scouting
    /// report — e.g. a friend who scouted one expansion and sent you their
    /// Hunt Helper export code privately to fold into the combined report.
    /// Capped at 3 in the UI.
    /// </summary>
    public List<string> AdditionalScouts { get; set; } = new() { string.Empty };

    [NonSerialized]
    private IDalamudPluginInterface? _pluginInterface;

    public void Initialize(IDalamudPluginInterface pluginInterface)
    {
        _pluginInterface = pluginInterface;

#pragma warning disable CS0618 // reading the obsolete field deliberately, once, to migrate it
        if ((Webhooks == null || Webhooks.Count == 0) && WebhookUrls is { Count: > 0 })
        {
            Webhooks = new List<WebhookEntry>();
            foreach (var url in WebhookUrls)
            {
                if (!string.IsNullOrWhiteSpace(url))
                    Webhooks.Add(new WebhookEntry { Enabled = true, Label = string.Empty, Url = url });
            }
            WebhookUrls = null;
            Save();
        }
#pragma warning restore CS0618

        if (Webhooks == null || Webhooks.Count == 0)
        {
            Webhooks = new List<WebhookEntry> { new WebhookEntry() };
        }
    }

    public void Save()
    {
        _pluginInterface?.SavePluginConfig(this);
    }
}
