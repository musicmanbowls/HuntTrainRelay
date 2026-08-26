using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HuntTrainRelay;

public class TrackedMark
{
    public string Name = string.Empty;
    public uint ModelId;
    public uint Instance;
    public bool Dead;

    /// <summary>
    /// The moment we personally observed this mark flip to dead while polling.
    /// Falls back to Hunt Helper's LastSeenUTC if the mark was already dead
    /// the first time we saw it (e.g. plugin was (re)loaded mid-train).
    /// </summary>
    public DateTime? DeathObservedAtUtc;
    public DateTime LastSeenUtc;
}

/// <summary>
/// Continuously watches Hunt Helper's train list and records the exact moment
/// each mark flips to dead — a running "ingested kills" log, independent of
/// Hunt Helper's own list contents. Never posts or fires anything itself;
/// reporting is entirely manual via Plugin's "End Train Now", which reads this
/// history. This is deliberate: automatically firing on "everything currently
/// tracked is dead" doesn't work for multi-expansion trains (DT -> ShB -> EW),
/// since DT alone looks "fully cleared" the moment the conductor starts
/// travelling to ShB, well before the real end of the run.
/// </summary>
public class TrainWatcher : IDisposable
{
    private readonly IFramework _framework;
    private readonly HuntHelperIpc _ipc;
    private readonly Configuration _config;

    private readonly Dictionary<(uint ModelId, uint Instance), TrackedMark> _tracked = new();
    private double _secondsSinceLastPoll;

    public string LastStatus { get; private set; } = "Idle.";

    /// <summary>
    /// A defensive-copy snapshot of everything currently tracked, keyed the same
    /// way as internal state — including marks that vanished from Hunt Helper's
    /// live list while already dead (see the Remove Dead handling in Poll). Used
    /// by "End Train Now" so it also benefits from this retained history instead
    /// of only seeing Hunt Helper's current live list.
    /// </summary>
    public Dictionary<(uint ModelId, uint Instance), TrackedMark> GetTrackedSnapshot() =>
        new(_tracked);

    public TrainWatcher(IFramework framework, HuntHelperIpc ipc, Configuration config)
    {
        _framework = framework;
        _ipc = ipc;
        _config = config;
        _framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
    }

    private void OnUpdate(IFramework framework)
    {
        if (!_config.TrackingEnabled) return;

        _secondsSinceLastPoll += framework.UpdateDelta.TotalSeconds;
        var interval = Math.Max(1, _config.PollIntervalSeconds);
        if (_secondsSinceLastPoll < interval) return;
        _secondsSinceLastPoll = 0;

        Poll();
    }

    /// <summary>
    /// Immediately clears all tracked marks, so the next mob Hunt Helper reports
    /// is treated as the start of a fresh train. Called manually, or automatically
    /// right after "End Train Now" posts a report.
    /// </summary>
    public void ResetNow()
    {
        _tracked.Clear();
        LastStatus = "Train tracking reset — ready for a new train.";
    }

    private void Poll()
    {
        var list = _ipc.TryGetTrainList();
        if (list == null)
        {
            LastStatus = "Waiting for Hunt Helper (not loaded, or no version match)...";
            return;
        }

        if (list.Count == 0)
        {
            _tracked.Clear();
            LastStatus = "No active train recorded in Hunt Helper.";
            return;
        }

        var currentKeys = new HashSet<(uint, uint)>();

        foreach (var mob in list)
        {
            var key = (mob.MobID, mob.Instance);
            currentKeys.Add(key);

            if (!_tracked.TryGetValue(key, out var tracked))
            {
                tracked = new TrackedMark
                {
                    Name = mob.Name,
                    ModelId = mob.MobID,
                    Instance = mob.Instance,
                    Dead = mob.Dead,
                    LastSeenUtc = mob.LastSeenUTC,
                    DeathObservedAtUtc = mob.Dead ? mob.LastSeenUTC : null,
                };
                _tracked[key] = tracked;
            }
            else
            {
                tracked.LastSeenUtc = mob.LastSeenUTC;
                if (mob.Dead && !tracked.Dead)
                {
                    tracked.Dead = true;
                    tracked.DeathObservedAtUtc = DateTime.UtcNow;
                }
            }
        }

        // Only drop marks that vanished from Hunt Helper's list while still alive
        // (unusual - safe to forget). Marks that vanished while already dead are
        // kept: that's exactly what happens when a conductor uses Hunt Helper's
        // own "Remove Dead" to tidy up mid-train, and a tidied-up train shouldn't
        // under-report marks that genuinely died as part of it.
        foreach (var key in _tracked.Keys.Where(k => !currentKeys.Contains(k)).ToList())
        {
            if (!_tracked[key].Dead)
                _tracked.Remove(key);
        }

        var deadCount = _tracked.Values.Count(m => m.Dead);
        LastStatus = $"Tracking {_tracked.Count} marks, {deadCount} dead.";
    }
}
