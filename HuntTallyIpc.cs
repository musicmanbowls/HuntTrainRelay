using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using System;

namespace HuntTrainRelay;

/// <summary>
/// One counted kill as reported by Hunt Tally. All primitives — Hunt Tally
/// deliberately publishes no custom types, since each plugin loads into its own
/// assembly context and a richer payload would fail to resolve on our side.
/// </summary>
public readonly record struct HuntTallyKill(
    string Name,
    uint NameId,
    int Rank,
    uint TerritoryId,
    uint InstanceId,
    long UnixSeconds);

/// <summary>
/// Subscribes to Hunt Tally's kill gate (kihtli/HuntTally). Hunt Tally fires
/// this only once the game has confirmed the player was credited with the kill,
/// so it means "you were credited with this mark", not "a mark near you died".
///
/// Two things this class is deliberately careful about, both flagged by Hunt
/// Tally's author:
///   1. It unsubscribes on Dispose. Failing to do that on a plugin reload gives
///      two callbacks per kill — the one genuine duplicate-fire risk, and it
///      sits on the consumer side, not theirs.
///   2. It never re-reads "what instance am I in now" when handling an event.
///      A kill can sit pending several seconds waiting on reward confirmation,
///      so only the instance id carried on the payload is meaningful.
/// </summary>
public sealed class HuntTallyIpc : IDisposable
{
    private const int SupportedApiVersion = 1;
    private const string ApiVersionGate = "HuntTally.ApiVersion";
    private const string OnKillGate = "HuntTally.OnKill";

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IPluginLog _log;

    private ICallGateSubscriber<string, uint, int, uint, uint, long, object>? _onKill;
    private Action<string, uint, int, uint, uint, long>? _handler;

    public bool Connected { get; private set; }
    public string Status { get; private set; } = "Hunt Tally not detected.";

    public event Action<HuntTallyKill>? KillReceived;

    public HuntTallyIpc(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        _pluginInterface = pluginInterface;
        _log = log;
        EnsureConnected();
    }

    /// <summary>
    /// Attempts to connect if not already connected. Safe and cheap to call
    /// repeatedly — Hunt Tally may well load after we do, so this gets retried
    /// from the poll loop rather than only once at startup.
    /// </summary>
    public void EnsureConnected()
    {
        if (Connected) return;

        int version;
        try
        {
            version = _pluginInterface.GetIpcSubscriber<int>(ApiVersionGate).InvokeFunc();
        }
        catch
        {
            Status = "Hunt Tally not detected (auto-marking off).";
            return;
        }

        if (version != SupportedApiVersion)
        {
            Status = $"Hunt Tally API v{version} not supported (this expects v{SupportedApiVersion}).";
            _log.Warning($"Hunt Tally IPC version mismatch: got {version}, expected {SupportedApiVersion}. Not subscribing.");
            return;
        }

        try
        {
            _onKill = _pluginInterface.GetIpcSubscriber<string, uint, int, uint, uint, long, object>(OnKillGate);
            _handler = OnKillReceived;
            _onKill.Subscribe(_handler);

            Connected = true;
            Status = $"Connected to Hunt Tally (API v{version}).";
            _log.Information($"Hunt Train Relay subscribed to {OnKillGate} (API v{version}).");
        }
        catch (Exception ex)
        {
            _onKill = null;
            _handler = null;
            Status = "Hunt Tally found but could not subscribe — see /xllog.";
            _log.Error(ex, "Could not subscribe to Hunt Tally's kill gate.");
        }
    }

    private void OnKillReceived(
        string name, uint nameId, int rank, uint territoryId, uint instanceId, long unixSeconds)
    {
        try
        {
            KillReceived?.Invoke(new HuntTallyKill(name, nameId, rank, territoryId, instanceId, unixSeconds));
        }
        catch (Exception ex)
        {
            // Never let our own handling throw back across the IPC boundary into
            // Hunt Tally's tracker.
            _log.Error(ex, "Hunt Train Relay threw while handling a Hunt Tally kill.");
        }
    }

    public void Dispose()
    {
        try
        {
            if (_onKill is not null && _handler is not null)
                _onKill.Unsubscribe(_handler);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not cleanly unsubscribe from Hunt Tally's kill gate.");
        }

        _handler = null;
        _onKill = null;
        Connected = false;
    }
}
