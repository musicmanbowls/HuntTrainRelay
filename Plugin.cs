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
    private const string TrainCommand = "/htrt";
    private const string CounterCommand = "/htrc";
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
    private readonly SRankZoneReminder _zoneReminder;
    private readonly MarkDetector _detector;
    private readonly TeleportHelper _teleport;
    private readonly IGameGui _gameGui;
    private readonly HuntCounter _counter;
    private readonly IClientState _clientState;

    private uint _clientTerritory => _clientState.TerritoryType;

    private readonly Configuration _config;
    private readonly HuntHelperIpc _ipc;
    private readonly HuntTallyIpc _huntTally;
    private readonly TrainWatcher _watcher;

    private bool _configWindowVisible;
    private bool _trainPopoutVisible;
    private bool _counterPopoutVisible;
    private string _importCode = string.Empty;

    // Measured on the previous frame. The drag threshold has to match the real
    // on-screen row pitch (selectable + buttons + separator), not just a line of
    // text — using a smaller value makes each swap jump further than the cursor
    // moved, so the row visibly outruns the mouse.
    private string _lastPostResult = string.Empty;
    private int _selectedNarrowRiftSpawn;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        ICommandManager commandManager,
        IChatGui chatGui,
        IObjectTable objectTable,
        IClientState clientState,
        IDataManager dataManager,
        IGameGui gameGui,
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
        _gameGui = gameGui;
        _clientState = clientState;
        _huntTally = new HuntTallyIpc(_pluginInterface, _log);
        _detector = new MarkDetector(objectTable, clientState, dataManager);
        _teleport = new TeleportHelper(_pluginInterface, _log);
        _watcher = new TrainWatcher(framework, _ipc, _huntTally, _detector, _config);
        _zoneReminder = new SRankZoneReminder(clientState, chatGui, _log, _config, _detector);
        _counter = new HuntCounter(chatGui);

        _commandManager.AddHandler(ConfigCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Hunt Train Relay settings.",
        });

        _commandManager.AddHandler(TrainCommand, new CommandInfo(OnTrainCommand)
        {
            HelpMessage = "Open the Hunt Train Relay train list popout.",
        });

        _commandManager.AddHandler(CounterCommand, new CommandInfo(OnCounterCommand)
        {
            HelpMessage = "Open the Hunt Train Relay mob counter popout.",
        });

        _pluginInterface.UiBuilder.Draw += DrawUI;
        _pluginInterface.UiBuilder.OpenConfigUi += OnOpenConfigUi;
    }

    private void OnCommand(string command, string args) => _configWindowVisible = true;

    private void OnTrainCommand(string command, string args) => _trainPopoutVisible = true;

    private void OnCounterCommand(string command, string args) => _counterPopoutVisible = true;

    private void OnOpenConfigUi() => _configWindowVisible = true;

    /// <summary>
    /// Builds the current merged mark set — Hunt Helper's live list plus anything
    /// the background tracker already recorded that's no longer in that live list
    /// (e.g. cleared away mid-train with Remove Dead). Returns null if Hunt Helper
    /// isn't detected at all.
    /// </summary>
    private List<TrackedMark>? BuildCurrentMarks()
    {
        if (_config.UseOwnTrainList)
        {
            return _detector.Marks.Values.Select(d => new TrackedMark
            {
                Name = d.Name,
                ModelId = d.NameId,
                Instance = d.Instance,
                Dead = d.Dead,
                LastSeenUtc = d.LastSeenUtc,
                DeathObservedAtUtc = d.DeathObservedAtUtc,
            }).ToList();
        }

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
        List<HuntHelperMobRecord>? list;

        if (_config.UseOwnTrainList)
        {
            list = _detector.Marks.Values.Select(d => new HuntHelperMobRecord(
                d.Name, d.NameId, d.TerritoryId, d.MapId, d.Instance,
                d.MapPosition, d.Dead, d.LastSeenUtc)).ToList();
        }
        else
        {
            list = _ipc.TryGetTrainList();
        }

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
        DrawTrainPopout();
        DrawCounterPopout();

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

                if (ImGui.BeginTabItem("Train"))
                {
                    DrawTrainTab();
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


    /// <summary>
    /// Our own detected train list, with per-row teleport and map-flag actions.
    /// Drawn in both the Train tab and the standalone popout.
    /// </summary>
    /// <summary>
    /// The train list, in scouted order (or whatever order it's been dragged
    /// into). Click a row to echo it to chat and flag it; click and drag to
    /// move it.
    ///
    /// The click/drag handling is a direct port of Hunt Helper's own
    /// (HuntTrainUI.cs, img02/HuntHelper, MIT licensed), including its exact
    /// drag-delta thresholds. The important part is that everything is gated on
    /// ImGui.IsItemFocused(): only one row can hold focus, so at most one swap
    /// can happen per frame. Earlier attempts here tracked the dragged row in a
    /// field and reassigned it mid-loop, which let a single frame cascade swaps
    /// all the way down the list.
    /// </summary>
    private void DrawTrainList()
    {
        var marks = _detector.Ordered();

        if (marks.Count == 0)
        {
            ImGui.TextDisabled("No marks detected yet — fly near one and it'll appear here.");
            return;
        }

        (uint, uint)? toRemove = null;

        ImGui.Separator();

        for (var i = 0; i < marks.Count; i++)
        {
            var mark = marks[i];
            ImGui.PushID($"{mark.NameId}_{mark.Instance}");

            var info = ExpansionData.Lookup(mark.NameId);
            var zone = info?.Location ?? "?";
            var glyph = ExpansionData.InstanceGlyph(mark.Instance);
            var label = $"「{zone}」  {mark.Name}{glyph}";

            if (mark.Dead) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.45f, 0.45f, 0.45f, 1f));
            ImGui.Selectable(label, false, ImGuiSelectableFlags.None, new Vector2(230, 0));
            if (mark.Dead) ImGui.PopStyleColor();

            if (ImGui.IsItemFocused())
            {
                // A release with essentially no movement is a click, not a drag.
                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left)
                    && Math.Abs(ImGui.GetMouseDragDelta().Y) < 0.1f)
                {
                    TrainChatEcho.Send(_chatGui, _gameGui, mark, i, marks.Count);
                }

                // Swap only once the cursor has left this row, one step at a time.
                if (!ImGui.IsItemHovered())
                {
                    var next = i;
                    var dragY = ImGui.GetMouseDragDelta(ImGuiMouseButton.Left).Y;
                    if (dragY < 0.3f) next -= 1;
                    else if (dragY > 0f) next += 1;

                    if (next >= 0 && next < marks.Count)
                    {
                        (marks[i], marks[next]) = (marks[next], marks[i]);
                        _detector.ApplyOrder(marks);
                        ImGui.ResetMouseDragDelta();
                    }
                }
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("tele"))
            {
                if (!_teleport.TeleportToNearest(mark.TerritoryId, mark.MapPosition))
                    _lastPostResult = _teleport.LastError;
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("x"))
            {
                toRemove = (mark.NameId, mark.Instance);
            }

            // Dead indicator — normally set automatically by Hunt Tally, but
            // clickable as a manual fallback.
            ImGui.SameLine();
            if (ImGui.RadioButton("##dead", mark.Dead))
            {
                mark.Dead = !mark.Dead;
                mark.DeathObservedAtUtc = mark.Dead ? DateTime.UtcNow : null;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(mark.Dead ? "Dead — click to undo" : "Alive — set automatically when killed");

            ImGui.Separator();
            ImGui.PopID();
        }

        if (toRemove.HasValue) _detector.Remove(toRemove.Value);

        ImGui.Spacing();
        ImGui.TextDisabled("Click a mark to echo + flag it. Click and drag to move it in the list.");
    }

    private void DrawTrainTab()
    {
        ImGui.Spacing();

        var useOwn = _config.UseOwnTrainList;
        if (ImGui.Checkbox("Use this list for reports (instead of Hunt Helper's)", ref useOwn))
        {
            _config.UseOwnTrainList = useOwn;
            _config.Save();
        }
        ImGui.TextDisabled("Both lists always populate, so you can compare them before switching over.");

        ImGui.Spacing();
        if (ImGui.Button("Open Train Popout"))
        {
            _trainPopoutVisible = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("Remove Dead"))
        {
            _detector.RemoveDead();
        }
        ImGui.SameLine();
        if (ImGui.Button("Clear All"))
        {
            _detector.Clear();
        }

        ImGui.Spacing();
        if (ImGui.Button("Copy Export Code"))
        {
            if (_detector.Marks.Count == 0)
            {
                _lastPostResult = "Nothing to export — no marks detected yet.";
            }
            else
            {
                ImGui.SetClipboardText(TrainExchange.Export(_detector.Marks.Values));
                _lastPostResult = $"Exported {_detector.Marks.Count} marks to clipboard.";
            }
        }
        ImGui.TextDisabled("Uses Hunt Helper's own format — the code pastes into Hunt Helper too.");

        ImGui.Spacing();
        ImGui.SetNextItemWidth(260);
        ImGui.InputTextWithHint("##importCode", "Paste an import code here", ref _importCode, 65536);
        ImGui.SameLine();
        if (ImGui.Button("Import"))
        {
            var imported = TrainExchange.Import(_importCode);
            if (imported == null)
            {
                _lastPostResult = "That import code couldn't be read.";
            }
            else
            {
                var added = _detector.Merge(imported);
                _importCode = string.Empty;
                _lastPostResult = $"Imported {imported.Count} marks ({added} new).";
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawTrainList();
    }

    private void DrawTrainPopout()
    {
        if (!_trainPopoutVisible) return;

        ImGui.SetNextWindowSize(new Vector2(360, 320), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Hunt Train", ref _trainPopoutVisible))
        {
            DrawTrainList();
        }
        ImGui.End();
    }


    private void DrawCounterList(bool currentZoneOnly = false)
    {
        var defs = HuntCounter.Definitions.AsEnumerable();
        if (currentZoneOnly)
        {
            var here = _clientTerritory;
            defs = defs.Where(d => d.TerritoryId == here);
        }

        if (currentZoneOnly && !defs.Any())
        {
            ImGui.TextDisabled("No counted S-rank in this zone.");
            return;
        }

        foreach (var def in defs)
        {
            ImGui.PushID(def.MarkName);
            ImGui.TextWrapped($"{def.MarkName} — {def.Zone}");

            foreach (var mob in def.MobNames)
            {
                var count = _counter.Tallies.TryGetValue(mob, out var c) ? c : 0;
                ImGui.TextDisabled($"    {mob}: {count}");
            }

            if (ImGui.SmallButton("Reset"))
            {
                _counter.ResetFor(def);
            }

            ImGui.Separator();
            ImGui.PopID();
        }
    }

    private void DrawCounterPopout()
    {
        if (!_counterPopoutVisible) return;

        ImGui.SetNextWindowSize(new Vector2(300, 400), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Hunt Counter", ref _counterPopoutVisible))
        {
            DrawCounterList(currentZoneOnly: true);
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

        var reminderOn = _config.SRankZoneReminderEnabled;
        if (ImGui.Checkbox("Remind me on entering an S-rank zone", ref reminderOn))
        {
            _config.SRankZoneReminderEnabled = reminderOn;
            _config.Save();
        }

        if (_config.SRankZoneReminderEnabled)
        {
            ImGui.SameLine();
            var reminderSound = _config.SRankZoneReminderSound;
            if (ImGui.Checkbox("with sound", ref reminderSound))
            {
                _config.SRankZoneReminderSound = reminderSound;
                _config.Save();
            }
        }
        ImGui.TextDisabled("Lakeland (Tyger), Ultima Thule (Narrow-rift), Elpis (Ophioneus). Only you see it.");

        ImGui.Spacing();

        foreach (var name in SimpleSRanks)
        {
            if (ImGui.Button($"Watch {name}"))
            {
                _config.Flags.Add(new FlagEntry
                {
                    Label = name,
                    TerritoryId = name == "Tyger" ? 813u : 961u, // Lakeland / Elpis
                });
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
                TerritoryId = 960, // Ultima Thule
                HasLocation = true,
                X = spot.X,
                Y = spot.Y,
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

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (ImGui.Button("Open Counter Popout"))
        {
            _counterPopoutVisible = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("Reset All Counts"))
        {
            _counter.Reset();
        }
        ImGui.TextDisabled("Counts trigger-mob kills for S-ranks that need them (also /htrc).");
        ImGui.Spacing();
        DrawCounterList();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
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
        _zoneReminder.Dispose();
        _counter.Dispose();
        _pluginInterface.UiBuilder.Draw -= DrawUI;
        _pluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfigUi;
        _commandManager.RemoveHandler(ConfigCommand);
        _commandManager.RemoveHandler(TrainCommand);
        _commandManager.RemoveHandler(CounterCommand);
    }
}
