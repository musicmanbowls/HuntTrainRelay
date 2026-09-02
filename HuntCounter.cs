using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace HuntTrainRelay;

/// <summary>
/// One S-rank whose spawn requires killing a number of specific lesser mobs.
/// Names and the match pattern are adapted from Hunt Helper's Constants.cs
/// (img02/HuntHelper, MIT licensed), English only.
/// </summary>
public sealed class CounterDefinition
{
    public string MarkName { get; init; } = string.Empty;
    public string Expansion { get; init; } = string.Empty;
    public string Zone { get; init; } = string.Empty;
    public uint TerritoryId { get; init; }
    public string[] MobNames { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Counts kills of the lesser mobs that trigger certain S-ranks, by matching
/// the game's own battle log lines. Purely passive — it reads chat, never
/// sends anything.
///
/// Deliberately excludes marks triggered by gathering (e.g. Gandarewa) or by
/// non-kill mechanics (e.g. Narrow-rift's Wee Ea minions), since those don't
/// match a "defeats the X" line and would silently never tick.
/// </summary>
public sealed class HuntCounter : IDisposable
{
    // Matches the game's English battle log for a defeated mob.
    private const string BattleRegexBase = "(?i)(defeat|defeats) the ";

    public static readonly List<CounterDefinition> Definitions = new()
    {
        new() { MarkName = "Ixtab", Expansion = "Shadowbringers", Zone = "The Rak'tika Greatwood", TerritoryId = 817,
                MobNames = new[] { "Cracked Ronkan Doll", "Cracked Ronkan Thorn", "Cracked Ronkan Vessel" } },
        new() { MarkName = "Forgiven Pedantry", Expansion = "Shadowbringers", Zone = "Kholusia", TerritoryId = 814,
                MobNames = new[] { "Dwarven Cotton Boll" } },
        new() { MarkName = "Sphatika", Expansion = "Endwalker", Zone = "Thavnair", TerritoryId = 957,
                MobNames = new[] { "Asvattha", "Pisaca", "Vajralangula" } },
        new() { MarkName = "Ruminator", Expansion = "Endwalker", Zone = "Mare Lamentorum", TerritoryId = 959,
                MobNames = new[] { "Thinker", "Wanderer", "Weeper" } },
        new() { MarkName = "Okina", Expansion = "Stormblood", Zone = "The Ruby Sea", TerritoryId = 613,
                MobNames = new[] { "Naked Yumemi", "Yumemi" } },
        new() { MarkName = "Udumbara", Expansion = "Stormblood", Zone = "The Fringes", TerritoryId = 612,
                MobNames = new[] { "Leshy", "Diakka" } },
        new() { MarkName = "Salt and Light", Expansion = "Stormblood", Zone = "The Lochs", TerritoryId = 621,
                MobNames = new[] { "Throw" } },
        new() { MarkName = "Leucrotta", Expansion = "Heavensward", Zone = "Azys Lla", TerritoryId = 402,
                MobNames = new[] { "Allagan Chimera", "Lesser Hydra", "Meracydian Vouivre" } },
        new() { MarkName = "Squonk", Expansion = "Heavensward", Zone = "The Sea of Clouds", TerritoryId = 401,
                MobNames = new[] { "Chirp" } },
        new() { MarkName = "Minhocao", Expansion = "ARR", Zone = "Northern Thanalan", TerritoryId = 147,
                MobNames = new[] { "Earth Sprite" } },
    };

    // "You defeat the X" — only your own kills.
    private const string PersonalRegexBase = "(?i)^you defeat the ";

    private readonly IChatGui _chatGui;
    private readonly IObjectTable _objectTable;
    private readonly Configuration _config;

    private readonly List<(Regex Personal, Regex Nearby, string MobName, string MarkName)> _patterns = new();

    public HuntCounter(IChatGui chatGui, IObjectTable objectTable, Configuration config)
    {
        _chatGui = chatGui;
        _objectTable = objectTable;
        _config = config;

        foreach (var def in Definitions)
        {
            foreach (var mob in def.MobNames)
            {
                _patterns.Add((
                    new Regex(PersonalRegexBase + Regex.Escape(mob), RegexOptions.Compiled),
                    new Regex(BattleRegexBase + Regex.Escape(mob), RegexOptions.Compiled),
                    mob,
                    def.MarkName));
            }
        }

        // Longer names first so "Naked Yumemi" can't be eaten by "Yumemi".
        _patterns.Sort((a, b) => b.MobName.Length.CompareTo(a.MobName.Length));

        _chatGui.ChatMessage += OnChatMessage;
    }

    /// <summary>Row id of the world the player is on, or 0 if unknown.</summary>
    public uint CurrentWorldId()
    {
        try
        {
            return _objectTable.LocalPlayer?.CurrentWorld.RowId ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Display name of the player's world, for labelling counts.</summary>
    public string CurrentWorldName()
    {
        try
        {
            var name = _objectTable.LocalPlayer?.CurrentWorld.Value.Name.ExtractText();
            return string.IsNullOrWhiteSpace(name) ? "Unknown" : name;
        }
        catch
        {
            return "Unknown";
        }
    }

    private static string TallyKey(uint worldId, uint instance, string mobName) =>
        $"{worldId}:{instance}:{mobName}";

    private static string MarkKey(uint worldId, uint instance, string markName) =>
        $"{worldId}:{instance}:{markName}";

    /// <summary>Current tally for a mob on a given world.</summary>
    public int GetTally(uint worldId, uint instance, string mobName) =>
        _config.CounterTallies.TryGetValue(TallyKey(worldId, instance, mobName), out var n) ? n : 0;

    /// <summary>When this counter last had a kill on that world, if ever.</summary>
    public DateTime? GetLastKill(uint worldId, uint instance, string markName) =>
        _config.CounterLastKill.TryGetValue(MarkKey(worldId, instance, markName), out var t) ? t : null;

    /// <summary>Auto-reset settings for a mark, created on first access.</summary>
    public CounterSettings SettingsFor(string markName)
    {
        if (!_config.CounterConfig.TryGetValue(markName, out var settings))
        {
            settings = new CounterSettings();
            _config.CounterConfig[markName] = settings;
        }
        return settings;
    }

    /// <summary>
    /// Clears any counter whose last contribution is older than its configured
    /// window. Measured from the last kill, so an active grind is never reset
    /// out from under the player.
    /// </summary>
    public void ApplyAutoResets()
    {
        var now = DateTime.UtcNow;
        var changed = false;

        foreach (var def in Definitions)
        {
            var settings = SettingsFor(def.MarkName);
            if (!settings.AutoResetEnabled) continue;

            var window = TimeSpan.FromHours(Math.Clamp(settings.AutoResetHours, 1, 9));

            // Every world tracked for this mark, since a count on a world the
            // player has left should still age out.
            var stale = _config.CounterLastKill
                .Where(kv => kv.Key.EndsWith($":{def.MarkName}", StringComparison.Ordinal)
                             && now - kv.Value >= window)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var markKey in stale)
            {
                var prefix = markKey[..(markKey.LastIndexOf(':') + 1)];
                foreach (var mob in def.MobNames)
                {
                    if (_config.CounterTallies.Remove(prefix + mob)) changed = true;
                }

                _config.CounterLastKill.Remove(markKey);
                changed = true;
            }
        }

        if (changed) _config.Save();
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        // Kill lines land in these channels; anything else can't be a defeat
        // message, so skip the regex work entirely.
        var kind = message.LogKind;
        if (kind is not XivChatType.SystemError
            and not XivChatType.SystemMessage
            and not XivChatType.Gathering
            and not XivChatType.Action) return;

        var text = message.OriginalMessage.ToString();
        var worldId = CurrentWorldId();
        var instance = MarkDetector.GetCurrentInstance();

        foreach (var (personal, nearby, mobName, markName) in _patterns)
        {
            var pattern = _config.CountOnlyMyKills ? personal : nearby;
            if (!pattern.IsMatch(text)) continue;

            var key = TallyKey(worldId, instance, mobName);
            _config.CounterTallies[key] = (_config.CounterTallies.TryGetValue(key, out var n) ? n : 0) + 1;
            _config.CounterLastKill[MarkKey(worldId, instance, markName)] = DateTime.UtcNow;
            _config.Save();
            break; // one line is one kill
        }
    }

    /// <summary>Clears every count on every world.</summary>
    public void Reset()
    {
        _config.CounterTallies.Clear();
        _config.CounterLastKill.Clear();
        _config.Save();
    }

    /// <summary>Clears one mark's counts on the given world.</summary>
    public void ResetFor(CounterDefinition def, uint worldId, uint instance)
    {
        foreach (var mob in def.MobNames)
            _config.CounterTallies.Remove(TallyKey(worldId, instance, mob));

        _config.CounterLastKill.Remove(MarkKey(worldId, instance, def.MarkName));
        _config.Save();
    }

    public void Dispose()
    {
        _chatGui.ChatMessage -= OnChatMessage;
    }
}
