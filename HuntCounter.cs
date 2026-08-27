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
        new() { MarkName = "Ixtab", Expansion = "Shadowbringers",
                MobNames = new[] { "Cracked Ronkan Doll", "Cracked Ronkan Thorn", "Cracked Ronkan Vessel" } },
        new() { MarkName = "Forgiven Pedantry", Expansion = "Shadowbringers",
                MobNames = new[] { "Dwarven Cotton Boll" } },
        new() { MarkName = "Sphatika", Expansion = "Endwalker",
                MobNames = new[] { "Asvattha", "Pisaca", "Vajralangula" } },
        new() { MarkName = "Ruminator", Expansion = "Endwalker",
                MobNames = new[] { "Thinker", "Wanderer", "Weeper" } },
        new() { MarkName = "Okina", Expansion = "Stormblood",
                MobNames = new[] { "Naked Yumemi", "Yumemi" } },
        new() { MarkName = "Udumbara", Expansion = "Stormblood",
                MobNames = new[] { "Leshy", "Diakka" } },
        new() { MarkName = "Salt and Light", Expansion = "Stormblood",
                MobNames = new[] { "Throw" } },
        new() { MarkName = "Leucrotta", Expansion = "Heavensward",
                MobNames = new[] { "Allagan Chimera", "Lesser Hydra", "Meracydian Vouivre" } },
        new() { MarkName = "Minhocao", Expansion = "ARR",
                MobNames = new[] { "Earth Sprite" } },
    };

    private readonly IChatGui _chatGui;
    private readonly Dictionary<string, int> _tallies = new();
    private readonly List<(Regex Pattern, string MobName)> _patterns = new();

    public IReadOnlyDictionary<string, int> Tallies => _tallies;

    public HuntCounter(IChatGui chatGui)
    {
        _chatGui = chatGui;

        foreach (var def in Definitions)
        {
            foreach (var mob in def.MobNames)
            {
                _tallies[mob] = 0;
                // Longer names first so "Naked Yumemi" can't be eaten by "Yumemi".
                _patterns.Add((new Regex(BattleRegexBase + Regex.Escape(mob), RegexOptions.Compiled), mob));
            }
        }

        _patterns.Sort((a, b) => b.MobName.Length.CompareTo(a.MobName.Length));

        _chatGui.ChatMessage += OnChatMessage;
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
        foreach (var (pattern, mobName) in _patterns)
        {
            if (!pattern.IsMatch(text)) continue;
            _tallies[mobName]++;
            break; // one line is one kill
        }
    }

    public void Reset()
    {
        foreach (var key in _tallies.Keys.ToList())
            _tallies[key] = 0;
    }

    public void ResetFor(CounterDefinition def)
    {
        foreach (var mob in def.MobNames)
            _tallies[mob] = 0;
    }

    public void Dispose()
    {
        _chatGui.ChatMessage -= OnChatMessage;
    }
}
