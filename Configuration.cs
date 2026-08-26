using Dalamud.Configuration;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HuntTrainRelay;

public enum SpawnStatus { Unknown, Spawned, NotSpawned }

[Serializable]
public class WebhookEntry
{
    public bool Enabled { get; set; } = true;
    public string Label { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

[Serializable]
public class FlagEntry
{
    public string Label { get; set; } = string.Empty;
    public bool IsSRank { get; set; } = false;
    public SpawnStatus SpawnStatus { get; set; } = SpawnStatus.Unknown;

    public bool HasLocation { get; set; } = false;
    public uint TerritoryId { get; set; } = 0;
    public uint MapId { get; set; } = 0;
    public int Instance { get; set; } = 0;
    public float X { get; set; } = 0;
    public float Y { get; set; } = 0;
}

/// <summary>
/// A reusable, named location — set up once with real coordinates, then picked
/// from a dropdown on every future train instead of retyping numbers each time.
/// Persists forever (unlike the per-train Flags list, which resets every train).
/// </summary>
[Serializable]
public class SavedLocation
{
    public string Name { get; set; } = string.Empty;
    public uint TerritoryId { get; set; } = 0;
    public uint MapId { get; set; } = 0;
    public int Instance { get; set; } = 0;
    public float X { get; set; } = 0;
    public float Y { get; set; } = 0;
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 3;

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
    /// S-rank watches and Rally Flags for the current train. Empty at the start
    /// of every train — conductors add what they want, it clears on Reset or a
    /// successful End Train Now, same lifecycle as tracking itself.
    /// </summary>
    public List<FlagEntry> Flags { get; set; } = new();

    /// <summary>
    /// The reusable location library — persists across every train, unlike Flags.
    /// </summary>
    public List<SavedLocation> SavedLocations { get; set; } = new();

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

        // Seed Narrow-rift's known spawn points once (Territory 960 / Map 699,
        // Ultima Thule — confirmed via arealmremapped.com; coordinates from
        // ffxiv.consolegameswiki.com/wiki/Narrow-rift's own Coordinates table).
        // Only adds these if none exist yet, so it never duplicates or disturbs
        // anything already saved.
        if (!SavedLocations.Any(l => l.Name.StartsWith("Narrow-rift", StringComparison.OrdinalIgnoreCase)))
        {
            var narrowRiftSpawns = new (float X, float Y)[]
            {
                (8.3f, 20.2f), (12.0f, 21.9f), (13.3f, 10.4f), (14.7f, 36.1f), (16.5f, 26.2f),
                (17.6f, 30.3f), (19.2f, 9.8f), (20.7f, 34.0f), (27.9f, 12.6f),
            };
            for (var i = 0; i < narrowRiftSpawns.Length; i++)
            {
                SavedLocations.Add(new SavedLocation
                {
                    Name = $"Narrow-rift Spawn {i + 1}",
                    TerritoryId = 960,
                    MapId = 699,
                    Instance = 0,
                    X = narrowRiftSpawns[i].X,
                    Y = narrowRiftSpawns[i].Y,
                });
            }
            Save();
        }
    }

    public void Save()
    {
        _pluginInterface?.SavePluginConfig(this);
    }
}
