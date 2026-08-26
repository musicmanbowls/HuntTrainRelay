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

    // Only the S-ranks conductors actually check for during trains, per group feedback.
    private static readonly (string Name, string Expansion)[] QuickSRanks =
    {
        ("Narrow-rift", "Endwalker"),
        ("Ophioneus", "Endwalker"),
        ("Tyger", "Shadowbringers"),
    };

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ICommandManager _commandManager;
    private readonly IChatGui _chatGui;
    private readonly IObjectTable _objectTable;
    private readonly IGameGui _gameGui;
    private readonly IPluginLog _log;

    private readonly Configuration _config;
    private readonly HuntHelperIpc _ipc;
    private readonly TrainWatcher _watcher;

    private bool _configWindowVisible;
    private bool _flagPopoutVisible;
    private string _lastPostResult = string.Empty;
    private int _selectedSavedLocationIndex;

    // New Saved Location form state (Settings tab)
    private string _newLocationName = string.Empty;
    private int _newLocationTerritory;
    private int _newLocationMap;
    private int _newLocationInstance;
    private float _newLocationX;
    private float _newLocationY;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        ICommandManager commandManager,
        IChatGui chatGui,
        IObjectTable objectTable,
        IGameGui gameGui,
        IPluginLog pluginLog)
    {
        _pluginInterface = pluginInterface;
        _commandManager = commandManager;
        _chatGui = chatGui;
        _objectTable = objectTable;
        _gameGui = gameGui;
        _log = pluginLog;

        _config = _pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        _config.Initialize(_pluginInterface);

        _ipc = new HuntHelperIpc(_pluginInterface);
        _watcher = new TrainWatcher(framework, _ipc, _config);

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
    /// died, plus any S-rank check results. Tracking and the flag list only
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
        DrawMainWindow();
        DrawFlagPopout();
    }

    private void DrawMainWindow()
    {
        if (!_configWindowVisible) return;

        ImGui.SetNextWindowSize(new Vector2(500, 520), ImGuiCond.FirstUseEver);
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

                if (ImGui.BeginTabItem("Flags"))
                {
                    DrawFlagsTab();
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
    /// A small, separate always-available window listing the current flags —
    /// meant to sit on the side of the screen while conducting, independent of
    /// whether the main settings window is even open. Only quick actions here
    /// (spawn status, ping, copy message); adding/removing flags stays on the
    /// Flags tab in the main window.
    /// </summary>
    private void DrawFlagPopout()
    {
        if (!_flagPopoutVisible) return;

        ImGui.SetNextWindowSize(new Vector2(320, 260), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Hunt Train Flags", ref _flagPopoutVisible))
        {
            if (_config.Flags.Count == 0)
            {
                ImGui.TextDisabled("No flags yet — add some on the Flags tab in the main window.");
            }

            foreach (var flag in _config.Flags)
            {
                ImGui.PushID(flag.GetHashCode());
                ImGui.TextWrapped(flag.IsSRank ? $"[S] {flag.Label}" : flag.Label);

                if (!flag.IsSRank && flag.HasLocation)
                {
                    ImGui.TextDisabled(MapLinkHelper.CoordinateSummary(flag));
                }

                if (flag.IsSRank)
                {
                    var spawned = flag.SpawnStatus == SpawnStatus.Spawned;
                    var notSpawned = flag.SpawnStatus == SpawnStatus.NotSpawned;

                    if (ImGui.Checkbox("Up", ref spawned))
                    {
                        flag.SpawnStatus = spawned ? SpawnStatus.Spawned : SpawnStatus.Unknown;
                        _config.Save();
                    }
                    ImGui.SameLine();
                    if (ImGui.Checkbox("Not up", ref notSpawned))
                    {
                        flag.SpawnStatus = notSpawned ? SpawnStatus.NotSpawned : SpawnStatus.Unknown;
                        _config.Save();
                    }
                    ImGui.SameLine();
                }
                else if (flag.HasLocation)
                {
                    if (ImGui.Button("Ping"))
                    {
                        MapLinkHelper.OpenMap(_gameGui, flag);
                    }
                    ImGui.SameLine();
                }

                if (ImGui.Button("Copy"))
                {
                    ImGui.SetClipboardText(FlagMessageHelper.BuildChatMessage(flag));
                }

                ImGui.Separator();
                ImGui.PopID();
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
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("End Train Now"))
        {
            _ = EndTrainNowAsync();
        }
        ImGui.TextDisabled("Posts the report, sorted by the order marks actually died. Only clears tracking and the flag list once the post actually succeeds.");

        ImGui.Spacing();
        if (ImGui.Button("Reset train tracking now"))
        {
            _watcher.ResetNow();
            _config.Flags.Clear();
            _config.Save();
        }
        ImGui.TextDisabled("Clears tracking and the flag list without posting anything — use if you need to abandon a train.");
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

    private void DrawFlagsTab()
    {
        ImGui.Spacing();
        ImGui.TextWrapped("S-rank watches and Rally Flags for this train. Clears when the train ends (Reset or a successful End Train Now).");
        ImGui.Spacing();

        if (ImGui.Button("Open Flag List Popup"))
        {
            _flagPopoutVisible = true;
        }
        ImGui.TextDisabled("A small separate window you can keep on-screen while conducting.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // --- Add S-rank (fixed quick list, per group feedback) ---
        ImGui.TextWrapped("Add an S-rank to watch for:");
        foreach (var (sName, sExpansion) in QuickSRanks)
        {
            if (ImGui.Button($"Add {sName}"))
            {
                _config.Flags.Add(new FlagEntry { Label = sName, IsSRank = true });
                _config.Save();
            }
            ImGui.SameLine();
        }
        ImGui.NewLine();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // --- Add Rally Flag ---
        ImGui.TextWrapped("Add a Rally Flag:");

        if (_config.SavedLocations.Count > 0)
        {
            var savedNames = _config.SavedLocations.Select(s => s.Name).ToArray();
            ImGui.SetNextItemWidth(220);
            ImGui.Combo("##savedLocation", ref _selectedSavedLocationIndex, savedNames, savedNames.Length);
            ImGui.SameLine();
            if (ImGui.Button("Add from Library"))
            {
                var loc = _config.SavedLocations[_selectedSavedLocationIndex];
                _config.Flags.Add(new FlagEntry
                {
                    Label = loc.Name,
                    IsSRank = false,
                    HasLocation = true,
                    TerritoryId = loc.TerritoryId,
                    MapId = loc.MapId,
                    Instance = loc.Instance,
                    X = loc.X,
                    Y = loc.Y,
                });
                _config.Save();
            }
        }

        ImGui.Spacing();
        if (ImGui.Button("Add Blank Rally Flag"))
        {
            _config.Flags.Add(new FlagEntry { Label = "New Rally Flag", IsSRank = false });
            _config.Save();
        }
        ImGui.TextDisabled(
            "Set your own in-game flag with Ctrl+Right-Click, or use Ping My Map below once a " +
            "location's filled in. New locations can be saved to the Library (Settings tab) for reuse."
        );

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        int? toRemove = null;
        for (var i = 0; i < _config.Flags.Count; i++)
        {
            ImGui.PushID(i);
            DrawFlagEntry(_config.Flags[i], () => toRemove = i);
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.PopID();
        }

        if (toRemove.HasValue)
        {
            _config.Flags.RemoveAt(toRemove.Value);
            _config.Save();
        }
    }

    private void DrawFlagEntry(FlagEntry flag, Action onRemove)
    {
        if (flag.IsSRank)
        {
            ImGui.TextWrapped($"[S-Rank] {flag.Label}");

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

            ImGui.Spacing();
            if (ImGui.Button("Remove"))
            {
                onRemove();
            }
            return;
        }

        var label = flag.Label;
        ImGui.SetNextItemWidth(300);
        if (ImGui.InputText("Label", ref label, 256))
        {
            flag.Label = label;
        }
        if (ImGui.IsItemDeactivatedAfterEdit()) _config.Save();

        ImGui.TextWrapped(MapLinkHelper.CoordinateSummary(flag));

        var territory = (int)flag.TerritoryId;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Territory ID", ref territory))
        {
            flag.TerritoryId = (uint)Math.Max(0, territory);
            flag.HasLocation = flag.TerritoryId > 0 && flag.MapId > 0;
            _config.Save();
        }

        var map = (int)flag.MapId;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Map ID", ref map))
        {
            flag.MapId = (uint)Math.Max(0, map);
            flag.HasLocation = flag.TerritoryId > 0 && flag.MapId > 0;
            _config.Save();
        }

        var instance = flag.Instance;
        ImGui.SetNextItemWidth(80);
        if (ImGui.InputInt("Instance (0 = none)", ref instance))
        {
            flag.Instance = Math.Clamp(instance, 0, 9);
            _config.Save();
        }

        var x = flag.X;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputFloat("X", ref x, 0.1f))
        {
            flag.X = x;
            _config.Save();
        }

        ImGui.SameLine();
        var y = flag.Y;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputFloat("Y", ref y, 0.1f))
        {
            flag.Y = y;
            _config.Save();
        }

        ImGui.Spacing();

        if (flag.HasLocation)
        {
            if (ImGui.Button("Ping My Map"))
            {
                if (!MapLinkHelper.OpenMap(_gameGui, flag))
                    _lastPostResult = "Could not open the map with that location — double-check Territory ID / Map ID.";
            }
            ImGui.SameLine();
        }

        if (ImGui.Button("Copy Message"))
        {
            ImGui.SetClipboardText(FlagMessageHelper.BuildChatMessage(flag));
        }
        ImGui.SameLine();

        if (flag.HasLocation)
        {
            if (ImGui.Button("Save to Library"))
            {
                _config.SavedLocations.Add(new SavedLocation
                {
                    Name = flag.Label,
                    TerritoryId = flag.TerritoryId,
                    MapId = flag.MapId,
                    Instance = flag.Instance,
                    X = flag.X,
                    Y = flag.Y,
                });
                _config.Save();
            }
            ImGui.SameLine();
        }

        if (ImGui.Button("Remove"))
        {
            onRemove();
        }
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
        ImGui.TextWrapped(
            "Saved Locations — reusable rally points. Set one up once with real coordinates, and " +
            "it's a one-click add on the Flags tab for every future train."
        );
        ImGui.Spacing();

        DrawSavedLocationsList();
    }

    private void DrawSavedLocationsList()
    {
        int? toRemove = null;

        for (var i = 0; i < _config.SavedLocations.Count; i++)
        {
            ImGui.PushID(i);
            var loc = _config.SavedLocations[i];
            var instancePart = loc.Instance > 0 ? $", Instance {loc.Instance}" : "";
            ImGui.TextWrapped($"{loc.Name} — Territory {loc.TerritoryId}, Map {loc.MapId}{instancePart} — ({loc.X:F1}, {loc.Y:F1})");
            if (ImGui.Button("Remove"))
            {
                toRemove = i;
            }
            ImGui.Separator();
            ImGui.PopID();
        }

        if (toRemove.HasValue)
        {
            _config.SavedLocations.RemoveAt(toRemove.Value);
            _config.Save();
        }

        ImGui.Spacing();
        ImGui.TextWrapped("Add a new saved location:");

        ImGui.SetNextItemWidth(200);
        ImGui.InputText("Name", ref _newLocationName, 128);

        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("Territory ID##new", ref _newLocationTerritory);

        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("Map ID##new", ref _newLocationMap);

        ImGui.SetNextItemWidth(80);
        ImGui.InputInt("Instance (0 = none)##new", ref _newLocationInstance);

        ImGui.SetNextItemWidth(100);
        ImGui.InputFloat("X##new", ref _newLocationX, 0.1f);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        ImGui.InputFloat("Y##new", ref _newLocationY, 0.1f);

        if (ImGui.Button("+ Add Saved Location"))
        {
            if (string.IsNullOrWhiteSpace(_newLocationName) || _newLocationTerritory <= 0 || _newLocationMap <= 0)
            {
                _lastPostResult = "Enter a name, Territory ID, and Map ID before adding a saved location.";
            }
            else
            {
                _config.SavedLocations.Add(new SavedLocation
                {
                    Name = _newLocationName,
                    TerritoryId = (uint)_newLocationTerritory,
                    MapId = (uint)_newLocationMap,
                    Instance = Math.Clamp(_newLocationInstance, 0, 9),
                    X = _newLocationX,
                    Y = _newLocationY,
                });
                _config.Save();
                _newLocationName = string.Empty;
                _newLocationTerritory = 0;
                _newLocationMap = 0;
                _newLocationInstance = 0;
                _newLocationX = 0;
                _newLocationY = 0;
            }
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
    /// additional scouts). Caller must wrap the call in ImGui.PushID with a
    /// unique key if more than one such list is ever drawn in the same window.
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
        _pluginInterface.UiBuilder.Draw -= DrawUI;
        _pluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfigUi;
        _commandManager.RemoveHandler(ConfigCommand);
    }
}
