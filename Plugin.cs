using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
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
    private const string NextAetheryteCommand = "/htra";
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
    private readonly ITextureProvider _textureProvider;

    // The game's own aetheryte crystal icon. ID confirmed from Umbra's source
    // (Umbra.Game/src/Travel/TravelDestination.cs: IconAetheryte = 60453).
    private const uint AetheryteIconId = 60453;
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
    private string _customFlagLabel = string.Empty;
    private int _blacklistExpansion;
    private int _blacklistZone;
    private int _blacklistAetheryte;

    // Drag state for the train list. Both are -1 when no drag is in progress.
    private int _dragFromIndex = -1;
    private int _dragToIndex = -1;

    // The mark the conductor is currently on. Tracked by identity rather than
    // list position, so dragging rows or removing marks can't silently change
    // what "current" points at.
    private (uint NameId, uint Instance)? _currentMark;

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
        ITextureProvider textureProvider,
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
        _textureProvider = textureProvider;
        _clientState = clientState;
        _huntTally = new HuntTallyIpc(_pluginInterface, _log);
        _detector = new MarkDetector(objectTable, clientState, dataManager);
        _teleport = new TeleportHelper(_pluginInterface, _log, dataManager);
        SyncBlacklist();
        _watcher = new TrainWatcher(framework, _ipc, _huntTally, _detector, _config);
        _zoneReminder = new SRankZoneReminder(clientState, chatGui, _log, _config, _detector);
        _counter = new HuntCounter(chatGui);
        _detector.MarkDetected += OnMarkDetected;

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

        _commandManager.AddHandler(NextAetheryteCommand, new CommandInfo(OnNextAetheryteCommand)
        {
            HelpMessage = "Name the closest aetheryte to the next mark in the train.",
        });

        _pluginInterface.UiBuilder.Draw += DrawUI;
        _pluginInterface.UiBuilder.OpenConfigUi += OnOpenConfigUi;
    }

    private void OnCommand(string command, string args) => _configWindowVisible = true;

    private void OnTrainCommand(string command, string args) => _trainPopoutVisible = true;

    private void OnMarkDetected(DetectedMark mark)
    {
        if (_config.EchoOnDetection)
            TrainChatEcho.SendDetected(_chatGui, mark);
    }

    /// <summary>Compact "how long ago was this last seen" label, e.g. 5m / 1h 12m.</summary>
    private static string FormatAge(DateTime lastSeenUtc)
    {
        var age = DateTime.UtcNow - lastSeenUtc;
        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m";
        return $"{(int)age.TotalHours}h {age.Minutes}m";
    }

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
            return _detector.Ordered().Where(d => !d.IsCustom).Select(d => new TrackedMark
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
            list = _detector.Ordered().Where(d => !d.IsCustom).Select(d => new HuntHelperMobRecord(
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
        UpdateAutoAdvance();
        DrawTrainPopout();
        DrawCounterPopout();

        if (!_configWindowVisible) return;

        ImGui.SetNextWindowSize(new Vector2(620, 560), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(420, 240), new Vector2(float.MaxValue, float.MaxValue));
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

    /// <summary>The mark the pointer is on, or null if it's been cleared/removed.</summary>
    private DetectedMark? CurrentMark()
    {
        if (_currentMark is not { } key) return null;
        return _detector.Marks.TryGetValue(key, out var mark) ? mark : null;
    }

    /// <summary>
    /// The next live (not dead) mark after the current one, in list order.
    /// Falls back to the first live mark when there's no pointer yet.
    /// </summary>
    private DetectedMark? NextLiveMark()
    {
        var ordered = _detector.Ordered();
        if (ordered.Count == 0) return null;

        var startIndex = 0;
        if (_currentMark is { } key)
        {
            var idx = ordered.FindIndex(m => m.NameId == key.NameId && m.Instance == key.Instance);
            if (idx >= 0) startIndex = idx + 1;
        }

        for (var i = startIndex; i < ordered.Count; i++)
            if (!ordered[i].Dead) return ordered[i];

        return null;
    }

    /// <summary>
    /// Moves the pointer to a mark, optionally announcing it. Announcing echoes
    /// to chat and drops the map flag, which is what makes hands-free
    /// conducting work: kill a mark, the next one is already flagged.
    /// </summary>
    private void SetCurrentMark(DetectedMark? mark, bool announce)
    {
        if (mark == null)
        {
            _currentMark = null;
            return;
        }

        _currentMark = (mark.NameId, mark.Instance);

        if (!announce) return;

        var ordered = _detector.Ordered();
        var index = ordered.FindIndex(m => m.NameId == mark.NameId && m.Instance == mark.Instance);
        TrainChatEcho.Send(_chatGui, _gameGui, mark, index < 0 ? 0 : index, ordered.Count);
    }

    /// <summary>
    /// Advances the pointer when the mark it's on has died — whether that was
    /// a manual tick or Hunt Tally doing it automatically. Runs from the draw
    /// loop so it catches both without either path needing to know about it.
    /// </summary>
    private void UpdateAutoAdvance()
    {
        if (!_config.AutoAdvance) return;

        var current = CurrentMark();
        if (current != null && !current.Dead) return;
        if (current == null && _currentMark != null)
        {
            // The pointed-at mark was removed from the list entirely.
            _currentMark = null;
        }

        var next = NextLiveMark();
        if (next == null) return;

        SetCurrentMark(next, announce: _config.EchoOnAdvance);
    }

    private void OnNextAetheryteCommand(string command, string args)
    {
        var next = NextLiveMark();
        if (next == null)
        {
            _chatGui.Print("[Hunt Train Relay] No live marks left in the train.");
            return;
        }

        var aetheryte = TeleportHelper.NearestTo(next.TerritoryId, next.MapPosition);
        if (aetheryte is not { } aeth)
        {
            _chatGui.Print($"[Hunt Train Relay] No known aetheryte near {next.Name}.");
            return;
        }

        // Deliberately no map flag here: conductors call this out ahead of time
        // while the train is still travelling, and plugins like Hunt Train
        // Assistant auto-follow flags the conductor posts — dropping one early
        // would pull people off the current mark.
        var line = $"Teleport to {aeth.Name} after this mark!";

        var sb = new Dalamud.Game.Text.SeStringHandling.SeStringBuilder();
        sb.AddUiForeground(TrainChatEcho.GoldColour);
        sb.AddText("[Hunt Train Relay] ");
        sb.AddUiForegroundOff();
        sb.AddText(line);
        _chatGui.Print(sb.BuiltString);

        ImGui.SetClipboardText(line);
    }

    /// <summary>
    /// Scanning play/pause plus the tidy-up actions. Shown on both the Train
    /// tab and the popout, so a conductor working from the popout alone still
    /// has everything they need mid-train.
    /// </summary>


    /// <summary>
    /// Surfaces a problem everywhere it might be looked for: the status line in
    /// the main window, the local chat log, and /xllog. Teleport errors were
    /// previously only written to a field rendered inside the main window, so
    /// clicking teleport from the popout failed in complete silence.
    /// </summary>
    /// <summary>Pushes the saved blacklist into the teleport helper.</summary>
    private void SyncBlacklist()
    {
        TeleportHelper.Blacklist.Clear();
        foreach (var id in _config.BlacklistedAetherytes)
            TeleportHelper.Blacklist.Add(id);
    }

    private void ReportProblem(string message)
    {
        _lastPostResult = message;
        _chatGui.PrintError($"[Hunt Train Relay] {message}");
        _log.Warning(message);
    }

    /// <summary>
    /// The actions that actually send something, or throw a train away. All
    /// three require Shift to be held: they're irreversible enough that a
    /// stray click mid-train is genuinely costly.
    ///
    /// Dimming and ignoring clicks manually rather than using ImGui's
    /// BeginDisabled — same result, and it avoids an API this project has
    /// deliberately steered clear of.
    /// </summary>
    private void DrawTrainFooter()
    {
        var armed = ImGui.GetIO().KeyShift;

        if (!armed) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.45f);

        if (ImGui.Button("Send Scouting Report") && armed)
        {
            _ = SendScoutingReportAsync();
        }

        ImGui.SameLine();
        if (ImGui.Button("End Train Now") && armed)
        {
            _ = EndTrainNowAsync();
        }

        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.35f, 0.35f, 1f));
        var resetPressed = ImGui.Button("Reset");
        ImGui.PopStyleColor();

        if (resetPressed && armed)
        {
            _watcher.ResetNow();
            _detector.Clear();
            _currentMark = null;
            _config.Flags.Clear();
            _config.Save();
            _lastPostResult = "Train reset — nothing was posted.";
        }

        if (!armed) ImGui.PopStyleVar();

        if (!armed)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(hold Shift)");
        }
    }


    private const int MaxBlacklistedAetherytes = 15;

    private static readonly (string Name, uint Min, uint Max)[] ExpansionRanges =
    {
        ("A Realm Reborn", 0, 396),
        ("Heavensward", 397, 611),
        ("Stormblood", 612, 812),
        ("Shadowbringers", 813, 955),
        ("Endwalker", 956, 1186),
        ("Dawntrail", 1187, 9999),
    };

    /// <summary>
    /// Expansion -> zone -> aetheryte picker for the blacklist. Zone names come
    /// from the game's own data rather than a hardcoded list, so they're correct
    /// and localised; expansion grouping is by territory id range.
    /// </summary>
    private void DrawBlacklistPicker()
    {
        var expansionNames = ExpansionRanges.Select(e => e.Name).ToArray();
        ImGui.SetNextItemWidth(160);
        if (ImGui.Combo("Expansion", ref _blacklistExpansion, expansionNames, expansionNames.Length))
        {
            _blacklistZone = 0;
            _blacklistAetheryte = 0;
        }

        var range = ExpansionRanges[Math.Clamp(_blacklistExpansion, 0, ExpansionRanges.Length - 1)];

        var zones = TeleportHelper.All
            .Where(a => a.TerritoryId >= range.Min && a.TerritoryId <= range.Max)
            .Select(a => a.TerritoryId)
            .Distinct()
            .OrderBy(t => t)
            .ToList();

        if (zones.Count == 0)
        {
            ImGui.TextDisabled("No aetherytes known for that expansion.");
            return;
        }

        var zoneNames = zones.Select(t => _detector.GetZoneName(t)).ToArray();
        _blacklistZone = Math.Clamp(_blacklistZone, 0, zones.Count - 1);
        ImGui.SetNextItemWidth(200);
        if (ImGui.Combo("Zone", ref _blacklistZone, zoneNames, zoneNames.Length))
        {
            _blacklistAetheryte = 0;
        }

        var inZone = TeleportHelper.All
            .Where(a => a.TerritoryId == zones[_blacklistZone])
            .OrderBy(a => a.Name)
            .ToList();

        if (inZone.Count == 0)
        {
            ImGui.TextDisabled("No aetherytes in that zone.");
            return;
        }

        var aetheryteNames = inZone.Select(a => a.Name).ToArray();
        _blacklistAetheryte = Math.Clamp(_blacklistAetheryte, 0, inZone.Count - 1);
        ImGui.SetNextItemWidth(200);
        ImGui.Combo("Aetheryte", ref _blacklistAetheryte, aetheryteNames, aetheryteNames.Length);

        ImGui.SameLine();
        if (ImGui.Button("Blacklist"))
        {
            var chosen = inZone[_blacklistAetheryte];
            if (_config.BlacklistedAetherytes.Count >= MaxBlacklistedAetherytes)
                _lastPostResult = $"Blacklist is full ({MaxBlacklistedAetherytes}).";
            else if (_config.BlacklistedAetherytes.Contains(chosen.AetheryteId))
                _lastPostResult = $"{chosen.Name} is already blacklisted.";
            else
            {
                _config.BlacklistedAetherytes.Add(chosen.AetheryteId);
                _config.Save();
                SyncBlacklist();
            }
        }

        ImGui.Spacing();

        if (_config.BlacklistedAetherytes.Count == 0)
        {
            ImGui.TextDisabled("Nothing blacklisted.");
            return;
        }

        uint? toUnblock = null;
        foreach (var id in _config.BlacklistedAetherytes)
        {
            var match = TeleportHelper.All.FirstOrDefault(a => a.AetheryteId == id);
            var label = string.IsNullOrEmpty(match.Name) ? $"Aetheryte {id}" : match.Name;
            var zoneLabel = match.TerritoryId == 0 ? "" : $" — {_detector.GetZoneName(match.TerritoryId)}";

            ImGui.PushID((int)id);
            ImGui.TextWrapped($"{label}{zoneLabel}");
            ImGui.SameLine();
            if (ImGui.SmallButton("remove")) toUnblock = id;
            ImGui.PopID();
        }

        if (toUnblock.HasValue)
        {
            _config.BlacklistedAetherytes.Remove(toUnblock.Value);
            _config.Save();
            SyncBlacklist();
        }
    }

    private void DrawTrainControls()
    {
        // Row 1: scanning state.
        if (_config.ScanningPaused)
        {
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Play))
            {
                _config.ScanningPaused = false;
                _config.Save();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Paused — click to resume picking up new marks");
        }
        else
        {
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Pause))
            {
                _config.ScanningPaused = true;
                _config.Save();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Scanning — click to stop picking up new marks");
        }

        ImGui.SameLine();
        ImGui.TextDisabled(_config.ScanningPaused ? "Paused" : "Scanning");

        // Row 2: tidy-up and navigation.
        if (ImGui.Button("Remove Dead"))
        {
            _detector.RemoveDead();
        }

        ImGui.SameLine();
        if (ImGui.Button("Next Mark"))
        {
            SetCurrentMark(NextLiveMark(), announce: true);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Move to the next live mark and flag it");

        ImGui.SameLine();
        if (ImGui.Button("Add Flag"))
        {
            var added = _detector.AddCustomFlag(_customFlagLabel);
            if (added == null)
                ReportProblem("No map flag set — place one with Ctrl+Right-Click first.");
            else
                _customFlagLabel = string.Empty;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Adds your current map flag to the train as a custom stop");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        ImGui.InputTextWithHint("##customFlagLabel", "flag name", ref _customFlagLabel, 64);

        ImGui.SameLine();
        if (ImGui.Button("Next Aetheryte"))
        {
            OnNextAetheryteCommand(NextAetheryteCommand, string.Empty);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Names the aetheryte nearest the next mark, and copies a line to your clipboard. No flag — see /htra");

        // Row 3
        var tracking = _config.TrackingEnabled;
        if (ImGui.Checkbox("Tracking this train (records exact kill times)", ref tracking))
        {
            _config.TrackingEnabled = tracking;
            _config.Save();
        }

        // Row 4
        var hideDead = _config.HideDeadMarks;
        if (ImGui.Checkbox("Hide dead", ref hideDead))
        {
            _config.HideDeadMarks = hideDead;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Only hides them from this list — they stay in the train and in reports.");
    }

    /// <summary>
    /// The train list, with drag-to-reorder.
    ///
    /// The important property here: NOTHING is reordered while the drag is in
    /// progress. Hovering a row only records where the drop would land, and the
    /// list is mutated exactly once, after the loop, when the mouse is
    /// released. Earlier versions swapped rows on every frame the cursor was
    /// off the source row, which made the dragged row race down the list and
    /// snap back — worse the slower you moved.
    ///
    /// The source index is tracked in a field rather than through an ImGui drag
    /// payload; behaviour is the same, and it keeps to API already proven to
    /// compile in this project.
    /// </summary>
    private void DrawTrainList(bool showZones = true)
    {
        var allMarks = _detector.Ordered();

        if (allMarks.Count == 0)
        {
            ImGui.TextDisabled("No marks detected yet — fly near one and it'll appear here.");
            return;
        }

        // What's shown may be a subset, but ordering maths always works against
        // the full list so hidden dead marks keep their place in the train.
        var marks = _config.HideDeadMarks
            ? allMarks.Where(m => !m.Dead).ToList()
            : allMarks;

        if (marks.Count == 0)
        {
            ImGui.TextDisabled($"All {allMarks.Count} marks are dead — untick \"Hide dead marks\" to see them.");
            return;
        }

        (uint, uint)? toRemove = null;

        var rowHeight = (float)Math.Clamp(_config.TrainRowHeight, 14, 48);
        const float leftPad = 6f;
        const float columnGap = 14f;

        // Size the columns to the widest text actually present, so long zone
        // names (Coerthas Western Highlands) and long marks (Sabotender
        // Bailarina, Yehehetoaua'pyo) can never run into the buttons.
        var zoneColWidth = 0f;
        var nameColWidth = 0f;
        foreach (var m in marks)
        {
            if (showZones)
            {
                // Must match exactly what the row draws below, or long custom
                // flag zone names overflow into the mark name column.
                var z = ExpansionData.Lookup(m.NameId)?.Location
                        ?? (m.IsCustom ? m.ZoneName : "?");
                zoneColWidth = Math.Max(zoneColWidth, ImGui.CalcTextSize($"「{z}」").X);
            }
            nameColWidth = Math.Max(nameColWidth,
                ImGui.CalcTextSize($"{m.Name}{ExpansionData.InstanceGlyph(m.Instance)}").X);
        }

        // Room for the age label, sized off a worst case so it doesn't jitter
        // as the numbers tick over.
        if (_config.ShowMarkAge)
            nameColWidth += ImGui.CalcTextSize("  (00h 00m)").X;

        // The remove button now sits at the far left, so every column shifts
        // right by its width.
        const float removeColWidth = 22f;
        var nameColumnX = leftPad + removeColWidth + zoneColWidth + columnGap;
        var buttonColumnX = nameColumnX + nameColWidth + columnGap;

        var mouseXInWindow = ImGui.GetMousePos().X - ImGui.GetWindowPos().X;
        var dragging = _dragFromIndex != -1;

        for (var i = 0; i < marks.Count; i++)
        {
            var mark = marks[i];
            ImGui.PushID($"{mark.NameId}_{mark.Instance}");

            var info = ExpansionData.Lookup(mark.NameId);
            var zone = info?.Location ?? (mark.IsCustom ? mark.ZoneName : "?");
            var glyph = ExpansionData.InstanceGlyph(mark.Instance);

            ImGui.PushStyleColor(ImGuiCol.Text,
                mark.Dead ? new Vector4(0.45f, 0.45f, 0.45f, 1f) : Vector4.One);

            ImGui.SetCursorPosX(leftPad);
            ImGui.BeginGroup();

            // Remove button on the far left, well away from teleport so it
            // can't be hit by accident.
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(3, 0));
            if (ImGui.SmallButton("x"))
            {
                toRemove = (mark.NameId, mark.Instance);
            }
            ImGui.PopStyleVar();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove this mark from the train");
            ImGui.SetItemAllowOverlap();
            ImGui.SameLine();

            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
            // Highlighted while it's the row being dragged.
            ImGui.SetCursorPosX(leftPad + removeColWidth);
            ImGui.Selectable(showZones ? $"「{zone}」" : string.Empty, _dragFromIndex == i, ImGuiSelectableFlags.None,
                new Vector2(ImGui.GetWindowWidth(), rowHeight));
            ImGui.SetItemAllowOverlap();
            ImGui.PopStyleVar();

            ImGui.SameLine();
            ImGui.SetCursorPosX(nameColumnX);
            var ageSuffix = _config.ShowMarkAge ? $"  ({FormatAge(mark.LastSeenUtc)})" : string.Empty;
            var isCurrent = _currentMark is { } cur
                            && cur.NameId == mark.NameId && cur.Instance == mark.Instance;
            if (isCurrent)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.85f, 0.4f, 1f));
                ImGui.Text($"> {mark.Name}{glyph}{ageSuffix}");
                ImGui.PopStyleColor();
            }
            else
            {
                ImGui.Text($"{mark.Name}{glyph}{ageSuffix}");
            }
            ImGui.SetItemAllowOverlap();

            ImGui.SameLine();
            ImGui.SetCursorPosX(buttonColumnX);
            // The game's own aetheryte crystal, pulled from its texture sheets
            // so it stays correct across patches and ships no assets. Falls back
            // to a text button if the icon can't be resolved.
            var iconSize = new Vector2(rowHeight - 4, rowHeight - 4);
            var teleportPressed = false;

            if (_textureProvider.TryGetFromGameIcon(new GameIconLookup(AetheryteIconId), out var iconTex)
                && iconTex.TryGetWrap(out var iconWrap, out _))
            {
                teleportPressed = ImGui.ImageButton(iconWrap.Handle, iconSize);
            }
            else
            {
                teleportPressed = ImGui.Button("tele", new Vector2(34f, rowHeight - 2));
            }

            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Teleport to the nearest aetheryte");

            if (teleportPressed)
            {
                if (!_teleport.TeleportToNearest(mark.TerritoryId, mark.MapPosition))
                    ReportProblem(_teleport.LastError);
                else if (_config.TeleportAlsoFlags)
                    MapFlagHelper.FlagMark(_gameGui, mark);
            }
            ImGui.SetItemAllowOverlap();

            ImGui.SameLine();
            ImGui.SetCursorPosX(buttonColumnX + 68);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 99);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 3);
            var dead = mark.Dead;
            ImGui.Checkbox("##dead", ref dead);
            ImGui.PopStyleVar(2);
            if (dead != mark.Dead)
            {
                mark.Dead = dead;
                mark.DeathObservedAtUtc = dead ? DateTime.UtcNow : null;
            }
            ImGui.SetItemAllowOverlap();

            ImGui.Separator();
            ImGui.EndGroup();
            ImGui.PopStyleColor();

            // Drop indicator: a bright line on the edge of the row the release
            // would land on, so it's obvious where the mark is going.
            if (dragging && _dragToIndex == i && _dragFromIndex != i)
            {
                var rowMin = ImGui.GetItemRectMin();
                var rowMax = ImGui.GetItemRectMax();
                var edgeY = _dragToIndex < _dragFromIndex ? rowMin.Y : rowMax.Y;
                ImGui.GetWindowDrawList().AddLine(
                    new Vector2(rowMin.X, edgeY),
                    new Vector2(rowMax.X, edgeY),
                    ImGui.GetColorU32(ImGuiCol.DragDropTarget),
                    2.5f);
            }

            // --- drag: only ever RECORDS intent, never mutates the list ---
            if (_dragFromIndex == -1
                && ImGui.IsItemActive()
                && ImGui.IsMouseDragging(ImGuiMouseButton.Left)
                && mouseXInWindow < buttonColumnX)
            {
                _dragFromIndex = i;
            }

            if (dragging && ImGui.IsItemHovered())
            {
                _dragToIndex = i;
            }

            // --- click: a release with no drag in progress ---
            if (!dragging
                && ImGui.IsItemFocused()
                && mouseXInWindow < buttonColumnX
                && ImGui.IsMouseReleased(ImGuiMouseButton.Left)
                && Math.Abs(ImGui.GetMouseDragDelta().Y) < 0.1f)
            {
                // Clicking a mark makes it the current one, so a conductor can
                // jump the pointer anywhere just by clicking.
                _currentMark = (mark.NameId, mark.Instance);

                if (_config.EchoOnMarkClick)
                {
                    var fullIndex = allMarks.FindIndex(m => m.NameId == mark.NameId && m.Instance == mark.Instance);
                    TrainChatEcho.Send(_chatGui, _gameGui, mark, fullIndex < 0 ? i : fullIndex, allMarks.Count);
                }
                else
                    MapFlagHelper.FlagMark(_gameGui, mark);
            }

            ImGui.PopID();
        }

        // --- floating preview under the cursor while dragging ---
        // Gemini suggested doing this inside BeginDragDropSource, but this
        // implementation tracks the drag manually rather than through an ImGui
        // payload, so a plain tooltip gives the same cursor-following preview.
        if (dragging && _dragFromIndex < marks.Count)
        {
            var source = marks[_dragFromIndex];
            var sourceZone = ExpansionData.Lookup(source.NameId)?.Location ?? "?";
            ImGui.BeginTooltip();
            ImGui.TextUnformatted($"「{sourceZone}」 {source.Name}{ExpansionData.InstanceGlyph(source.Instance)}");
            ImGui.EndTooltip();
        }

        // --- commit the move exactly once, on release, after the loop ---
        if (_dragFromIndex != -1 && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            if (_dragToIndex != -1
                && _dragToIndex != _dragFromIndex
                && _dragFromIndex < marks.Count
                && _dragToIndex < marks.Count)
            {
                // Translate the visible positions back to positions in the full
                // list, so dragging still lands correctly when dead marks are
                // hidden between the rows being moved.
                var moving = marks[_dragFromIndex];
                var target = marks[_dragToIndex];

                var fromFull = allMarks.FindIndex(m => m.NameId == moving.NameId && m.Instance == moving.Instance);
                var toFull = allMarks.FindIndex(m => m.NameId == target.NameId && m.Instance == target.Instance);

                if (fromFull >= 0 && toFull >= 0)
                {
                    allMarks.RemoveAt(fromFull);
                    allMarks.Insert(toFull, moving);
                    _detector.ApplyOrder(allMarks);
                }
            }

            _dragFromIndex = -1;
            _dragToIndex = -1;
        }

        if (toRemove.HasValue) _detector.Remove(toRemove.Value);

        ImGui.Spacing();
        ImGui.TextDisabled("Click a mark to echo + flag it. Drag a row and release where you want it.");
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
        DrawTrainControls();

        ImGui.Spacing();
        if (ImGui.Button("Open Train Popout"))
        {
            _trainPopoutVisible = true;
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
                ImGui.SetClipboardText(TrainExchange.Export(_detector.Ordered()));
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

        if (ImGui.BeginChild("trainTabScroll", new Vector2(0, 0), false))
        {
            DrawTrainList();
        }
        ImGui.EndChild();
    }

    private void DrawTrainPopout()
    {
        if (!_trainPopoutVisible) return;

        ImGui.SetNextWindowSize(new Vector2(600, 420), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(420, 200), new Vector2(float.MaxValue, float.MaxValue));
        if (ImGui.Begin("Hunt Train", ref _trainPopoutVisible))
        {
            // Controls sit outside the scrolling region so they stay put while
            // the list scrolls underneath.
            DrawTrainControls();
            ImGui.Separator();

            // Reserve room at the bottom for the footer, so the list scrolls
            // between two fixed strips rather than under them.
            var footerHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y * 2;
            if (ImGui.BeginChild("trainScroll", new Vector2(0, -footerHeight), false))
            {
                DrawTrainList(showZones: !_config.HideZonesInPopout);
            }
            ImGui.EndChild();

            // Footer: the two actions that actually send something. Deliberately
            // separated from the controls above so neither gets hit by accident
            // while reaching for play/pause mid-train.
            ImGui.Separator();
            DrawTrainFooter();
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

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextWrapped("Train list");
        ImGui.Spacing();

        var echoClick = _config.EchoOnMarkClick;
        if (ImGui.Checkbox("Echo a mark to chat when its row is clicked", ref echoClick))
        {
            _config.EchoOnMarkClick = echoClick;
            _config.Save();
        }
        ImGui.TextDisabled("Off still flags the mark on your map — it just doesn't post the chat line. S-rank reminders are unaffected.");

        var echoDetect = _config.EchoOnDetection;
        if (ImGui.Checkbox("Echo each new mark as it's detected", ref echoDetect))
        {
            _config.EchoOnDetection = echoDetect;
            _config.Save();
        }
        ImGui.TextDisabled("Useful while scouting with the window closed.");

        var teleFlags = _config.TeleportAlsoFlags;
        if (ImGui.Checkbox("Teleport also drops the map flag", ref teleFlags))
        {
            _config.TeleportAlsoFlags = teleFlags;
            _config.Save();
        }

        var showAge = _config.ShowMarkAge;
        if (ImGui.Checkbox("Show how long ago each mark was last seen", ref showAge))
        {
            _config.ShowMarkAge = showAge;
            _config.Save();
        }
        ImGui.TextDisabled("Updates in place when you fly past a mark again — it won't move in the list.");

        var hideZones = _config.HideZonesInPopout;
        if (ImGui.Checkbox("Hide zone names in the train popout", ref hideZones))
        {
            _config.HideZonesInPopout = hideZones;
            _config.Save();
        }
        ImGui.TextDisabled("Keeps the popout narrow. The Train tab always shows them.");

        var autoAdv = _config.AutoAdvance;
        if (ImGui.Checkbox("Auto-advance to the next mark when the current one dies", ref autoAdv))
        {
            _config.AutoAdvance = autoAdv;
            _config.Save();
        }

        if (_config.AutoAdvance)
        {
            var echoAdv = _config.EchoOnAdvance;
            if (ImGui.Checkbox("Echo and flag the mark it advances to", ref echoAdv))
            {
                _config.EchoOnAdvance = echoAdv;
                _config.Save();
            }
        }

        var hideDeadSetting = _config.HideDeadMarks;
        if (ImGui.Checkbox("Hide dead marks in the train list", ref hideDeadSetting))
        {
            _config.HideDeadMarks = hideDeadSetting;
            _config.Save();
        }
        ImGui.TextDisabled("Display only — dead marks stay in the train and in reports.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextWrapped("Aetheryte blacklist");
        ImGui.TextDisabled("Never route to these. Affects the teleport button and Next Aetheryte alike.");
        ImGui.Spacing();
        DrawBlacklistPicker();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var rowH = _config.TrainRowHeight;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Row height (pixels)", ref rowH))
        {
            _config.TrainRowHeight = Math.Clamp(rowH, 14, 48);
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
        _detector.MarkDetected -= OnMarkDetected;
        _pluginInterface.UiBuilder.Draw -= DrawUI;
        _pluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfigUi;
        _commandManager.RemoveHandler(ConfigCommand);
        _commandManager.RemoveHandler(TrainCommand);
        _commandManager.RemoveHandler(CounterCommand);
        _commandManager.RemoveHandler(NextAetheryteCommand);
    }
}
