using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using Dalamud.Plugin;
using KamiToolKit.MapOverlay;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Numerics;
using MapMarkerInfo = KamiToolKit.Classes.MapMarkerInfo;

namespace HuntTrainRelay;

/// <summary>
/// Draws A-rank spawn points onto the real in-game map, rather than in a
/// separate radar window.
///
/// PROOF OF CONCEPT — Urqopacha only. The approach is adapted from
/// EurekaTrackerAutoPopper (Infiziert90, MIT licensed), which places markers
/// through KamiToolKit's MapOverlayController and re-places them whenever the
/// AreaMap addon refreshes.
///
/// Two things worth knowing before this is expanded:
///   * KamiToolKit is our first third-party dependency. Everything else here
///     rides on Dalamud's own services.
///   * MapMarkerInfo.Position wants WORLD coordinates, but Hunt Helper's spawn
///     data is in map coordinates, so it has to be converted back — see
///     MapCoordinates.ToWorld.
/// </summary>
public sealed unsafe class HuntMapOverlay : IDisposable
{
    private readonly IFramework _framework;
    private readonly IClientState _clientState;
    private readonly IDataManager _dataManager;
    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IPluginLog _log;
    private readonly Configuration _config;
    private readonly MarkDetector _detector;

    private readonly IDalamudPluginInterface _pluginInterface;
    private Dictionary<string, string>? _dotPaths;

    private MapOverlayController? _overlay;
    private bool _enabled;
    private bool _needsRefresh = true;
    private uint _lastTerritory;

    // A failure here repeats every frame, so log it once and stop rather than
    // filling /xllog with thousands of identical lines.
    private bool _faulted;
    private long _lastMarkSignature = -1;

    public string Status { get; private set; } = "Not started.";

    public HuntMapOverlay(
        IFramework framework,
        IClientState clientState,
        IDataManager dataManager,
        IAddonLifecycle addonLifecycle,
        IPluginLog log,
        Configuration config,
        MarkDetector detector,
        IDalamudPluginInterface pluginInterface)
    {
        _framework = framework;
        _clientState = clientState;
        _dataManager = dataManager;
        _addonLifecycle = addonLifecycle;
        _log = log;
        _config = config;
        _detector = detector;
        _pluginInterface = pluginInterface;

        try
        {
            _addonLifecycle.RegisterListener(AddonEvent.PostRefresh, "AreaMap", OnMapRefresh);
            _framework.Update += OnUpdate;
            Status = "Waiting to start.";
        }
        catch (Exception ex)
        {
            // A failure here must not take the rest of the plugin down.
            Status = "Map overlay unavailable — see /xllog.";
            _log.Error(ex, "Could not start the map overlay.");
        }
    }

    /// <summary>
    /// The controller is created on the framework thread rather than in the
    /// plugin constructor. Constructing it off-thread left its internals
    /// unready and produced a null reference on the first update.
    /// </summary>
    private bool EnsureOverlay()
    {
        if (_overlay != null) return true;

        try
        {
            _overlay = new MapOverlayController();
            return true;
        }
        catch (Exception ex)
        {
            Status = $"Could not create the map overlay: {ex.GetType().Name}.";
            _log.Error(ex, "MapOverlayController could not be created.");
            return false;
        }
    }

    /// <summary>
    /// Writes the embedded dot images into the plugin's config folder once, and
    /// returns their paths. KamiToolKit takes a file path, so they need to
    /// exist on disk — but shipping them as loose files in the release risks
    /// them going missing, so they travel inside the DLL instead.
    /// </summary>
    private Dictionary<string, string>? EnsureDotFiles()
    {
        if (_dotPaths != null) return _dotPaths;

        try
        {
            var dir = Path.Combine(_pluginInterface.GetPluginConfigDirectory(), "dots");
            Directory.CreateDirectory(dir);

            var sources = new Dictionary<string, string>
            {
                ["grey"] = DotTextures.Grey,
                ["blue"] = DotTextures.Blue,
                ["red"] = DotTextures.Red,
                ["green"] = DotTextures.Green,
            };

            var paths = new Dictionary<string, string>();
            foreach (var (name, base64) in sources)
            {
                var path = Path.Combine(dir, $"{name}.png");
                if (!File.Exists(path))
                    File.WriteAllBytes(path, Convert.FromBase64String(base64));
                paths[name] = path;
            }

            _dotPaths = paths;
            return _dotPaths;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Could not write the spawn point dot images.");
            return null;
        }
    }

    private void OnMapRefresh(AddonEvent type, AddonArgs args) => _needsRefresh = true;

    private void OnUpdate(IFramework framework)
    {
        if (_faulted) return;

        try
        {
            // Nothing to do until the player is actually in the world.
            if (!_clientState.IsLoggedIn) return;

            unsafe
            {
                // Eureka guards this too — the map agent isn't there during
                // loading screens, and touching the overlay then throws.
                if (FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMap.Instance() == null)
                    return;
            }

            if (!EnsureOverlay() || _overlay == null) return;

            if (!_config.ShowSpawnPointsOnMap)
            {
                if (_enabled)
                {
                    _overlay.RemoveAllMarkers();
                    _overlay.Disable();
                    _enabled = false;
                    Status = "Off.";
                }
                return;
            }

            if (!_enabled)
            {
                _overlay.Enable();
                _enabled = true;
                _needsRefresh = true;
            }

            var territory = _clientState.TerritoryType;
            if (territory != _lastTerritory)
            {
                _lastTerritory = territory;
                _needsRefresh = true;
            }

            // Re-place when the detected marks change, so a dot lights up as
            // soon as something is found there.
            var markSignature = _detector.Marks.Count == 0
                ? 0
                : _detector.Marks.Values.Where(m => !m.Dead).Sum(m => (long)m.NameId);
            markSignature += _detector.OtherRanks.Values.Sum(o => (long)o.NameId);
            if (markSignature != _lastMarkSignature)
            {
                _lastMarkSignature = markSignature;
                _needsRefresh = true;
            }

            if (!_needsRefresh) return;
            _needsRefresh = false;

            _overlay.RemoveAllMarkers();

            var points = SpawnPointData.For(territory);
            if (points.Length == 0)
            {
                Status = $"No spawn point data for territory {territory}.";
                return;
            }

            var dots = EnsureDotFiles();
            if (dots == null)
            {
                Status = "Could not prepare the dot images — see /xllog.";
                return;
            }

            var mapId = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()
                .GetRowOrDefault(territory)?.Map.RowId ?? 0;
            if (mapId == 0)
            {
                Status = "Could not resolve the map id for this zone.";
                return;
            }

            // Live A-ranks come from the train list; B and S from the separate
            // sighting store, which never touches the train.
            // A-ranks come from sightings rather than the train, so they still
            // show with recording paused. Anything already killed in the train
            // is excluded so a dead mark doesn't stay lit.
            var deadNameIds = _detector.Marks.Values
                .Where(m => m.Dead)
                .Select(m => m.NameId)
                .ToHashSet();

            // Instance matters: Heritage Found 1 and 2 are different worlds as
            // far as marks are concerned, so sightings from one must not show
            // on the other's map.
            var instance = MarkDetector.GetCurrentInstance();

            var here = _detector.OtherRanks.Values
                .Where(o => o.TerritoryId == territory && o.Instance == instance)
                .ToList();

            var aMarks = here.Where(o => o.Rank == HuntRank.A && !deadNameIds.Contains(o.NameId)).ToList();
            var bSightings = here.Where(o => o.Rank == HuntRank.B).ToList();
            var sSightings = here.Where(o => o.Rank == HuntRank.S).ToList();

            var radius = Math.Max(0.5f, _config.SpawnPointMatchRadius);

            // Claim points per MARK rather than per point. Checking each point
            // for "is any mark near me" lit up every point within the radius —
            // Chernobog filled four dots at once. Each mark now takes only its
            // single closest point.
            var claimed = new Dictionary<int, OtherRankSighting>();

            void Claim(List<OtherRankSighting> sightings)
            {
                foreach (var sighting in sightings)
                {
                    var bestIndex = -1;
                    var bestDistance = float.MaxValue;

                    for (var i = 0; i < points.Length; i++)
                    {
                        var d = Vector2.Distance(new Vector2(points[i].X, points[i].Y), sighting.MapPosition);
                        if (d > radius || d >= bestDistance) continue;
                        bestDistance = d;
                        bestIndex = i;
                    }

                    // Higher ranks claim first, so don't overwrite them.
                    if (bestIndex >= 0 && !claimed.ContainsKey(bestIndex))
                        claimed[bestIndex] = sighting;
                }
            }

            Claim(sSightings);
            Claim(aMarks);
            Claim(bSightings);

            var placed = 0;
            var occupied = 0;

            for (var pointIndex = 0; pointIndex < points.Length; pointIndex++)
            {
                var point = points[pointIndex];

                // Only draw points that can host a rank the player wants shown.
                var wanted = SpawnRanks.None;
                if (_config.ShowARankPoints) wanted |= SpawnRanks.A;
                if (_config.ShowBRankPoints) wanted |= SpawnRanks.B;
                if (_config.ShowSRankPoints) wanted |= SpawnRanks.S;
                if ((point.Ranks & wanted) == SpawnRanks.None) continue;

                string dot;
                string tooltip;

                if (claimed.TryGetValue(pointIndex, out var mark))
                {
                    dot = mark.Rank switch
                    {
                        HuntRank.S => "green",
                        HuntRank.A => "red",
                        _ => "blue",
                    };
                    tooltip = $"{mark.Name}  ({mark.Rank} rank)\n{point.X:F1}, {point.Y:F1}";
                    occupied++;
                }
                else
                {
                    dot = "grey";
                    var canSpawn = new List<string>();
                    if (point.Ranks.HasFlag(SpawnRanks.B)) canSpawn.Add("B");
                    if (point.Ranks.HasFlag(SpawnRanks.A)) canSpawn.Add("A");
                    if (point.Ranks.HasFlag(SpawnRanks.S)) canSpawn.Add("S");
                    var ranks = canSpawn.Count > 0 ? string.Join("/", canSpawn) : "?";
                    tooltip = $"Spawn point ({ranks})\n{point.X:F1}, {point.Y:F1}";
                }

                var world = MapCoordinates.ToWorld(_dataManager, mapId, point.X, point.Y);

                _overlay.AddMarker(new MapMarkerInfo
                {
                    AllowAnyMap = false,
                    MapId = mapId,
                    Position = world,
                    TexturePath = dots[dot],
                    Size = new Vector2(_config.SpawnDotSize, _config.SpawnDotSize),
                    Tooltip = tooltip,
                });
                placed++;
            }

            Status = $"{placed} spawn points shown, {occupied} with a mark on them.";
        }
        catch (Exception ex)
        {
            // This runs every frame, so log once and stand down rather than
            // filling /xllog with thousands of identical lines.
            var where = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "unknown";
            Status = $"Map overlay disabled after an error: {ex.GetType().Name} — {ex.Message} @ {where}";
            _log.Error(ex, "Map overlay update failed; disabling it for this session.");

            _faulted = true;
            _needsRefresh = false;

            try
            {
                _overlay?.RemoveAllMarkers();
                _overlay?.Disable();
            }
            catch
            {
                // Already broken; nothing useful to do.
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _framework.Update -= OnUpdate;
            _addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, "AreaMap", OnMapRefresh);

            if (_overlay != null)
            {
                _overlay.RemoveAllMarkers();
                _overlay.Disable();
                _overlay.Dispose();
                _overlay = null;
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Map overlay did not shut down cleanly.");
        }
    }
}
