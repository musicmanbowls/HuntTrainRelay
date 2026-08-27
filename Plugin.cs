using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace HuntTrainRelay;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "Hunt Train Relay";

    private const string ConfigCommand = "/htr";
    private const int MaxWebhooks = 5;
    private const int MaxAdditionalScouts = 3;

    // The only S-ranks the group actually checks for during trains.
    private static readonly string[] SimpleSRanks = { "Ophioneus", "Tyger" };

    // Narrow-rift's known spawn points (Territory 960 / Map 699, Ultima Thule —
    // confirmed via arealmremapped.com; coordinates from Narrow-rift's own
    // Coordinates table on ffxiv.consolegameswiki.com). Used only to label which
    // spot is being watched — no location system attached anymore, just text.
    private static readonly (float X, float Y)[] NarrowRiftSpawns =
    {
        (8.3f, 20.2f), (12.0f, 21.9f), (13.3f, 10.4f), (14.7f, 36.1f), (16.5f, 26.2f),
        (17.6f, 30.3f), (19.2f, 9.8f), (20.7f, 34.0f), (27.9f, 12.6f),
    };

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ICommandManager _commandManager;
    private readonly IChatGui _chatGui;
    private readonly IObjectTable _objectTable;
    private readonly IPluginLog _log;

    private readonly Configuration _config;
    private readonly HuntHelperIpc _ipc;
    private readonly HuntTallyIpc _huntTally;
    private readonly TrainWatcher _watcher;

    private bool _configWindowVisible;
    private string _lastPostResult = string.Empty;
    private int _selectedNarrowRiftSpawn;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        ICommandManager commandManager,
        IChatGui chatGui,
        IObjectTable objectTable,
        IPluginLog pluginLog)
    {
        _pluginInterface = pluginInterface;
        _commandManager = commandManager;
        _chatGui = chatGui;
        _objectTable = objectTable;
        _log = pluginLog;

        _config = _pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        _config.Initialize(_pluginInterface);

        _ipc = new HuntHelperIpc(_pluginInterface);
        _huntTally = new HuntTallyIpc(_pluginInterface, _log);
        _watcher = new TrainWatcher(framework, _ipc, _huntTally, _config);

        _commandManager.AddHandler(ConfigCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Hunt Train Relay settings.",
        });

        _pluginInterface.UiBuilder.Draw += DrawUI;
        _pluginInterface.UiBuilder.OpenConfigUi += OnOpenConfigUi;
    }

    private void OnCommand(string command, string args) => _configWindowVisible = true;

    private void OnOpenConfigUi() => _configWindowVisible = true;

    /// <summary>
    /// Builds the current merged mark set — Hunt Helper's live list plus anything
    /// the background tracker already recorded that's no longer in that live list
    /// (e.g. cleared away mid-train with Remove Dead). Returns null if Hunt Helper
    /// isn't detected at all.
    /// </summary>
    private List<TrackedMark>? BuildCurrentMarks()
    {
        var list = _ipc.TryGetTrainList();
        if (list == null) return null;

        var now = DateTime.UtcNow;
        var tracked = _watcher.GetTrackedSnapshot();

        var marks = list.Select(m => new TrackedMark
        {
            Name = m.Name,
            ModelId = m.MobID,
            Instance = m.Instance,
            Dead = m.Dead,
            LastSeenUtc = m.LastSeenUTC,
            DeathObservedAtUtc = m.Dead
                ? (tracked.TryGetValue((m.MobID, m.Instance), out var t) ? t.DeathObservedAtUtc : null) ?? now
                : null,
        }).ToList();

        var seenKeys = marks.Select(m => (m.ModelId, m.Instance)).ToHashSet();
        foreach (var (key, trackedMark) in tracked)
        {
            if (!seenKeys.Contains(key))
                marks.Add(trackedMark);
        }

        return marks;
    }

    private async Task SendTestAsync()
    {
        var (success, message) = await DiscordRelay.PostTestAsync(_config.Webhooks);
        _lastPostResult = message;
        if (!success) _log.Error($"Hunt Train Relay test post failed: {message}");
    }

    private async Task SendScoutingReportAsync()
    {
        var list = _ipc.TryGetTrainList();
        if (list == null)
        {
            _lastPostResult = "Hunt Helper not detected — can't build a scouting report.";
            return;
        }

        var names = new List<string>();
        var selfName = _objectTable.LocalPlayer?.Name?.TextValue;
        if (!string.IsNullOrWhiteSpace(selfName)) names.Add(selfName);
        names.AddRange(_config.AdditionalScouts.Where(n => !string.IsNullOrWhiteSpace(n)));

        var (success, message) = await DiscordRelay.PostScoutingReportAsync(_config.Webhooks, list, names);
        _lastPostResult = message;
        if (!success) _log.Error($"Hunt Train Relay scouting report failed: {message}");
    }

    /// <summary>
    /// The only way a "Train Complete" report ever gets posted — reads the
    /// current merged mark set and posts it sorted by the actual order things
    /// died, plus any S-rank check results. Tracking and the watch list only
    /// clear once the post is confirmed to have actually succeeded — if it
    /// fails, everything stays put so this can just be tried again.
    /// </summary>
    private async Task EndTrainNowAsync()
    {
        var marks = BuildCurrentMarks();
        if (marks == null)
        {
            _lastPostResult = "Hunt Helper not detected — nothing to post.";
            return;
        }

        if (marks.Count == 0)
        {
            _lastPostResult = "Nothing to post — Hunt Helper's train list is empty.";
            return;
        }

        var endedBy = _objectTable.LocalPlayer?.Name?.TextValue;

        var (success, message) = await DiscordRelay.PostTrainCompleteAsync(_config.Webhooks, marks, endedBy, _config.Flags);
        _lastPostResult = message;

        if (success)
        {
            _chatGui.Print($"[Hunt Train Relay] Posted train summary to Discord ({marks.Count} marks).");
            _watcher.ResetNow();
            _config.Flags.Clear();
            _config.Save();
        }
        else
        {
            _chatGui.PrintError($"[Hunt Train Relay] Failed to post to Discord: {message}");
            _log.Error($"Hunt Train Relay manual end-train post failed: {message}");
        }
    }

    private void DrawUI()
    {
        if (!_configWindowVisible) return;

        ImGui.SetNextWindowSize(new Vector2(460, 480), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Hunt Train Relay", ref _configWindowVisible))
        {
            if (ImGui.BeginTabBar("HuntTrainRelayTabs"))
            {
                if (ImGui.BeginTabItem("Conductor"))
                {
                    DrawConductorTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Scout"))
                {
                    DrawScoutTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Marks Slain"))
                {
                    DrawMarksSlainTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Settings"))
                {
                    DrawSettingsTab();
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }

            if (!string.IsNullOrEmpty(_lastPostResult))
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.TextWrapped($"Last post: {_lastPostResult}");
            }
        }
        ImGui.End();
    }

    private void DrawConductorTab()
    {
        ImGui.Spacing();

        var tracking = _config.TrackingEnabled;
        if (ImGui.Checkbox("Tracking this train (records exact kill times)", ref tracking))
        {
            _config.TrackingEnabled = tracking;
            _config.Save();
        }
        ImGui.TextDisabled("Turn this on at the start of a train for accurate per-mark kill times. Nothing posts automatically — use End Train Now when it's actually finished.");

        ImGui.Spacing();
        ImGui.TextWrapped($"Status: {_watcher.LastStatus}");

        ImGui.Spacing();
        var autoMark = _config.AutoMarkDeadEnabled;
        if (ImGui.Checkbox("Auto-mark dead using Hunt Tally", ref autoMark))
        {
            _config.AutoMarkDeadEnabled = autoMark;
            _config.Save();
        }
        ImGui.TextDisabled(_huntTally.Status);
        ImGui.TextDisabled("Marks are recorded dead here automatically, with Hunt Tally's exact kill time. Your Hunt Helper list still needs clicking yourself for its own navigation.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("End Train Now"))
        {
            _ = EndTrainNowAsync();
        }
        ImGui.TextDisabled("Posts the report, sorted by the order marks actually died, plus any S-rank checks below. Only clears once the post actually succeeds.");

        ImGui.Spacing();
        if (ImGui.Button("Reset train tracking now"))
        {
            _watcher.ResetNow();
            _config.Flags.Clear();
            _config.Save();
        }
        ImGui.TextDisabled("Clears tracking and S-rank watches without posting anything — use if you need to abandon a train.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextWrapped("S-Rank Watches");

        foreach (var name in SimpleSRanks)
        {
            if (ImGui.Button($"Watch {name}"))
            {
                _config.Flags.Add(new FlagEntry { Label = name });
                _config.Save();
            }
            ImGui.SameLine();
        }

        var spawnLabels = NarrowRiftSpawns.Select((s, i) => $"Spawn {i + 1} ({s.X:F1}, {s.Y:F1})").ToArray();
        ImGui.SetNextItemWidth(180);
        ImGui.Combo("##narrowRiftSpawn", ref _selectedNarrowRiftSpawn, spawnLabels, spawnLabels.Length);
        ImGui.SameLine();
        if (ImGui.Button("Watch Narrow-rift"))
        {
            var spot = NarrowRiftSpawns[_selectedNarrowRiftSpawn];
            _config.Flags.Add(new FlagEntry
            {
                Label = $"Narrow-rift — Spawn {_selectedNarrowRiftSpawn + 1} ({spot.X:F1}, {spot.Y:F1})",
            });
            _config.Save();
        }

        ImGui.Spacing();

        int? toRemove = null;
        for (var i = 0; i < _config.Flags.Count; i++)
        {
            var flag = _config.Flags[i];
            ImGui.PushID(i);

            ImGui.TextWrapped(flag.Label);

            var spawned = flag.SpawnStatus == SpawnStatus.Spawned;
            var notSpawned = flag.SpawnStatus == SpawnStatus.NotSpawned;

            if (ImGui.Checkbox("Spawned", ref spawned))
            {
                flag.SpawnStatus = spawned ? SpawnStatus.Spawned : SpawnStatus.Unknown;
                _config.Save();
            }
            ImGui.SameLine();
            if (ImGui.Checkbox("Didn't Spawn", ref notSpawned))
            {
                flag.SpawnStatus = notSpawned ? SpawnStatus.NotSpawned : SpawnStatus.Unknown;
                _config.Save();
            }
            ImGui.SameLine();
            if (ImGui.Button("Remove"))
            {
                toRemove = i;
            }

            ImGui.Separator();
            ImGui.PopID();
        }

        if (toRemove.HasValue)
        {
            _config.Flags.RemoveAt(toRemove.Value);
            _config.Save();
        }
    }

    private void DrawScoutTab()
    {
        ImGui.Spacing();
        if (ImGui.Button("Send Scouting Report"))
        {
            _ = SendScoutingReportAsync();
        }
        ImGui.TextDisabled("Posts Hunt Helper's current train list as a paste-able import code, plus a per-expansion up count.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextWrapped(
            "Additional scouts — credit anyone else whose scouting you folded into this report " +
            "(e.g. they sent you their Hunt Helper export code privately and you imported it)."
        );
        ImGui.Spacing();

        ImGui.PushID("scouts");
        DrawStringList(
            _config.AdditionalScouts,
            MaxAdditionalScouts,
            "+ Add scout",
            $"Maximum of {MaxAdditionalScouts} additional scouts reached.");
        ImGui.PopID();
    }

    private void DrawMarksSlainTab()
    {
        ImGui.Spacing();
        ImGui.TextWrapped("Preview of what End Train Now would post right now, in the order marks actually died.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var marks = BuildCurrentMarks();
        if (marks == null)
        {
            ImGui.TextDisabled("Hunt Helper not detected.");
            return;
        }

        if (marks.Count == 0)
        {
            ImGui.TextDisabled("Nothing tracked yet — start a train with Tracking this train enabled.");
            return;
        }

        var entries = TrainReport.BuildEntries(marks);
        string? lastExpansion = null;

        foreach (var entry in entries)
        {
            if (entry.Expansion != lastExpansion)
            {
                if (lastExpansion != null) ImGui.Spacing();
                ImGui.TextWrapped(entry.Expansion.ToUpperInvariant());
                lastExpansion = entry.Expansion;
            }

            var localTime = entry.KillTimeUtc.ToLocalTime().ToString("g");

            if (entry.Location == null || entry.MinHours == null || entry.MaxHours == null)
            {
                ImGui.TextWrapped($"{localTime} — {entry.Name} — no fixed respawn timer");
                continue;
            }

            var openLocal = entry.KillTimeUtc.AddHours(entry.MinHours.Value).ToLocalTime().ToString("t");
            var capLocal = entry.KillTimeUtc.AddHours(entry.MaxHours.Value).ToLocalTime().ToString("t");
            var instanceGlyph = ExpansionData.InstanceGlyph(entry.Instance);
            ImGui.TextWrapped($"{localTime} — {entry.Location} — {entry.Name}{instanceGlyph} — window {openLocal} → {capLocal}");
        }

        var sniped = TrainReport.BuildSniped(marks);
        if (sniped.Count > 0)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.TextWrapped("Assumed Sniped (not seen this train)");
            foreach (var (expansion, names) in sniped)
            {
                ImGui.TextWrapped($"{expansion}: {string.Join(", ", names)}");
            }
        }
    }

    private void DrawSettingsTab()
    {
        ImGui.Spacing();
        if (ImGui.Button("Send test message"))
        {
            _ = SendTestAsync();
        }
        ImGui.TextDisabled("Posts to every ENABLED webhook below.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextWrapped(
            "Webhooks — one per Discord server (or channel) to post to. Untick Enabled to keep a " +
            "testing channel around without deleting it. Create a webhook in Discord via Channel " +
            "Settings > Integrations > Webhooks > New Webhook > Copy Webhook URL."
        );
        ImGui.Spacing();

        DrawWebhookList();

        ImGui.Spacing();
        ImGui.Separator();

        var pollInterval = _config.PollIntervalSeconds;
        if (ImGui.InputInt("Check interval (seconds)", ref pollInterval))
        {
            _config.PollIntervalSeconds = Math.Clamp(pollInterval, 1, 30);
            _config.Save();
        }
    }

    private void DrawWebhookList()
    {
        int? toRemove = null;

        for (var i = 0; i < _config.Webhooks.Count; i++)
        {
            ImGui.PushID(i);
            var hook = _config.Webhooks[i];

            var enabled = hook.Enabled;
            if (ImGui.Checkbox("##enabled", ref enabled))
            {
                hook.Enabled = enabled;
                _config.Save();
            }

            ImGui.SameLine();
            var label = hook.Label;
            ImGui.SetNextItemWidth(120);
            if (ImGui.InputTextWithHint("##label", "Label (optional)", ref label, 128))
            {
                hook.Label = label;
            }
            if (ImGui.IsItemDeactivatedAfterEdit()) _config.Save();

            ImGui.SameLine();
            var url = hook.Url;
            ImGui.SetNextItemWidth(220);
            if (ImGui.InputTextWithHint("##url", "Webhook URL", ref url, 512))
            {
                hook.Url = url;
            }
            if (ImGui.IsItemDeactivatedAfterEdit()) _config.Save();

            if (_config.Webhooks.Count > 1)
            {
                ImGui.SameLine();
                if (ImGui.Button("Remove"))
                {
                    toRemove = i;
                }
            }

            ImGui.PopID();
        }

        if (toRemove.HasValue)
        {
            _config.Webhooks.RemoveAt(toRemove.Value);
            if (_config.Webhooks.Count == 0) _config.Webhooks.Add(new WebhookEntry());
            _config.Save();
        }

        if (_config.Webhooks.Count < MaxWebhooks)
        {
            if (ImGui.Button("+ Add webhook"))
            {
                _config.Webhooks.Add(new WebhookEntry());
                _config.Save();
            }
        }
        else
        {
            ImGui.TextDisabled($"Maximum of {MaxWebhooks} webhooks reached.");
        }
    }

    /// <summary>
    /// Reusable add/remove list editor for simple string lists (currently just
    /// additional scouts).
    /// </summary>
    private void DrawStringList(List<string> list, int maxCount, string addLabel, string maxReachedLabel)
    {
        int? toRemove = null;

        for (var i = 0; i < list.Count; i++)
        {
            ImGui.PushID(i);

            var value = list[i];
            ImGui.SetNextItemWidth(320);
            if (ImGui.InputText("##listItem", ref value, 512))
            {
                list[i] = value;
            }
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                _config.Save();
            }

            if (list.Count > 1)
            {
                ImGui.SameLine();
                if (ImGui.Button("Remove"))
                {
                    toRemove = i;
                }
            }

            ImGui.PopID();
        }

        if (toRemove.HasValue)
        {
            list.RemoveAt(toRemove.Value);
            if (list.Count == 0) list.Add(string.Empty);
            _config.Save();
        }

        if (list.Count < maxCount)
        {
            if (ImGui.Button(addLabel))
            {
                list.Add(string.Empty);
                _config.Save();
            }
        }
        else
        {
            ImGui.TextDisabled(maxReachedLabel);
        }
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _huntTally.Dispose();
        _pluginInterface.UiBuilder.Draw -= DrawUI;
        _pluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfigUi;
        _commandManager.RemoveHandler(ConfigCommand);
    }
}
