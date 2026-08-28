using Dalamud.Plugin.Services;
using System;
using System.Collections.Concurrent;
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
    private readonly HuntTallyIpc _huntTally;
    private readonly MarkDetector _detector;
    private readonly Configuration _config;

    private readonly Dictionary<(uint ModelId, uint Instance), TrackedMark> _tracked = new();

    // Kills arrive on Hunt Tally's thread, not the framework thread, so they're
    // queued here and applied during Poll rather than mutating _tracked directly.
    private readonly ConcurrentQueue<HuntTallyKill> _pendingKills = new();

    // Cheap insurance against double-fires, per Hunt Tally's own suggested key.
    private readonly HashSet<(uint, uint, uint, long)> _seenKills = new();

    private double _secondsSinceLastPoll;
    private double _secondsSinceConnectAttempt;

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

    /// <summary>Number of marks auto-marked dead by Hunt Tally this train.</summary>
    public int AutoMarkedCount { get; private set; }

    public TrainWatcher(IFramework framework, HuntHelperIpc ipc, HuntTallyIpc huntTally, MarkDetector detector, Configuration config)
    {
        _framework = framework;
        _ipc = ipc;
        _huntTally = huntTally;
        _detector = detector;
        _config = config;
        _huntTally.KillReceived += OnHuntTallyKill;
        _framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        _huntTally.KillReceived -= OnHuntTallyKill;
        _framework.Update -= OnUpdate;
    }

    /// <summary>
    /// Only queued while actively tracking — otherwise the queue would grow
    /// unbounded across a long session of ordinary hunting.
    /// </summary>
    private void OnHuntTallyKill(HuntTallyKill kill)
    {
        if (!_config.TrackingEnabled) return;
        if (!_config.AutoMarkDeadEnabled) return;
        _pendingKills.Enqueue(kill);
    }

    private void OnUpdate(IFramework framework)
    {
        // Retry the Hunt Tally connection regardless of tracking state. Plugin
        // load order isn't guaranteed, so if Hunt Tally came up after us, the
        // one attempt at construction would otherwise never be retried until
        // someone happened to enable tracking.
        if (!_huntTally.Connected)
        {
            _secondsSinceConnectAttempt += framework.UpdateDelta.TotalSeconds;
            if (_secondsSinceConnectAttempt >= 5)
            {
                _secondsSinceConnectAttempt = 0;
                _huntTally.EnsureConnected();
            }
        }

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
        _detector.Clear();
        _seenKills.Clear();
        AutoMarkedCount = 0;
        while (_pendingKills.TryDequeue(out _)) { }
        LastStatus = "Train tracking reset — ready for a new train.";
    }

    private void Poll()
    {
        // Always scan with our own detector, even when Hunt Helper is the active
        // source — that's what makes side-by-side comparison possible.
        try
        {
            _detector.Scan(recordNew: !_config.ScanningPaused);
        }
        catch (Exception ex)
        {
            LastStatus = $"Own detection error: {ex.Message}";
        }

        var list = _ipc.TryGetTrainList();
        if (list == null)
        {
            LastStatus = "Waiting for Hunt Helper (not loaded, or no version match)...";
            return;
        }

        if (list.Count == 0)
        {
            // Nothing currently sitting in Hunt Helper's list — but that doesn't
            // mean clear retained history. A conductor mid-train can briefly empty
            // Hunt Helper's list this way (e.g. Remove Dead clearing a finished
            // leg right before the next expansion's marks get detected), and that
            // shouldn't lose anything already tracked. Tracking only ever clears
            // via an explicit Reset or a successful End Train Now.
            var deadCountEmpty = _tracked.Values.Count(m => m.Dead);
            LastStatus = _tracked.Count > 0
                ? $"Tracking {_tracked.Count} marks, {deadCountEmpty} dead. (Hunt Helper's list is currently empty.)"
                : "No active train recorded in Hunt Helper.";
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

        ApplyPendingKills();

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
        var autoPart = AutoMarkedCount > 0 ? $" ({AutoMarkedCount} auto)" : string.Empty;
        var ownCount = _detector.Marks.Count;
        var ownDead = _detector.Marks.Values.Count(m => m.Dead);
        LastStatus = $"Hunt Helper: {_tracked.Count} marks, {deadCount} dead{autoPart}. Own: {ownCount} marks, {ownDead} dead.";
    }

    /// <summary>
    /// Applies any kills Hunt Tally reported since the last poll. Matching is a
    /// direct equality check on (nameId, instanceId): Hunt Helper records a mob
    /// using mob.NameId as what its IPC calls MobID, and Hunt Tally publishes
    /// that same BNpcName row id — so the two line up exactly, with no name
    /// matching (which would break on non-English clients) and no ID mapping.
    ///
    /// Most events won't match anything, and that's expected: Hunt Tally reports
    /// every mark you're credited with, whether or not it's part of a tracked
    /// train. Unmatched kills are simply dropped.
    ///
    /// Note this marks the mark dead in OUR records only. Hunt Helper's IPC is
    /// read-only, so the conductor's own Hunt Helper list still shows it alive
    /// until they click it there themselves.
    /// </summary>
    private void ApplyPendingKills()
    {
        while (_pendingKills.TryDequeue(out var kill))
        {
            var dedupeKey = (kill.NameId, kill.TerritoryId, kill.InstanceId, kill.UnixSeconds);
            if (!_seenKills.Add(dedupeKey)) continue;

            var killTime = DateTimeOffset.FromUnixTimeSeconds(kill.UnixSeconds).UtcDateTime;
            var matched = false;

            // Our own detected list
            if (_detector.Marks.TryGetValue((kill.NameId, kill.InstanceId), out var own) && !own.Dead)
            {
                own.Dead = true;
                own.DeathObservedAtUtc = killTime;
                matched = true;
            }

            // The Hunt Helper-derived list
            if (_tracked.TryGetValue((kill.NameId, kill.InstanceId), out var mark) && !mark.Dead)
            {
                mark.Dead = true;
                mark.DeathObservedAtUtc = killTime;
                matched = true;
            }

            if (matched) AutoMarkedCount++;
        }
    }
}
