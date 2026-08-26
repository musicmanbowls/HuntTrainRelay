using Dalamud.Configuration;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;

namespace HuntTrainRelay;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    /// <summary>
    /// Discord "Incoming Webhook" URLs, created in Discord via Channel Settings >
    /// Integrations > Webhooks > New Webhook > Copy Webhook URL. Every report is
    /// posted to every non-empty URL in this list, so multiple Discord servers can
    /// each get their own copy. Anyone with one of these URLs can post to that
    /// channel, so treat them like passwords.
    /// </summary>
    public List<string> WebhookUrls { get; set; } = new() { string.Empty };

    /// <summary>
    /// Legacy single-webhook field from before multi-webhook support. Only kept so
    /// existing saved configs migrate into WebhookUrls automatically; not used
    /// otherwise.
    /// </summary>
    [Obsolete("Use WebhookUrls instead. Kept only for migrating old saved configs.")]
    public string? WebhookUrl { get; set; }

    /// <summary>
    /// Turns on background tracking of kill times for this train. Only the
    /// conductor actively recording in Hunt Helper needs this on. Reporting
    /// itself is always manual via "End Train Now" — this setting only affects
    /// how accurate the recorded kill times are, not whether anything gets posted.
    /// </summary>
    public bool TrackingEnabled { get; set; } = false;

    /// <summary>
    /// How often (in seconds) to check Hunt Helper's train list for changes.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 3;

    /// <summary>
    /// Extra names credited alongside the submitting character on a scouting
    /// report — e.g. a friend who scouted one expansion and sent you their
    /// Hunt Helper export code privately to fold into the combined report.
    /// Capped at 3 in the UI.
    /// </summary>
    public List<string> AdditionalScouts { get; set; } = new() { string.Empty };

    [NonSerialized]
    private IDalamudPluginInterface? _pluginInterface;

    public void Initialize(IDalamudPluginInterface pluginInterface)
    {
        _pluginInterface = pluginInterface;

#pragma warning disable CS0618 // reading the obsolete field deliberately, once, to migrate it
        if ((WebhookUrls == null || WebhookUrls.Count == 0 || WebhookUrls.TrueForAll(string.IsNullOrWhiteSpace))
            && !string.IsNullOrWhiteSpace(WebhookUrl))
        {
            WebhookUrls = new List<string> { WebhookUrl! };
            WebhookUrl = null;
            Save();
        }
#pragma warning restore CS0618

        if (WebhookUrls == null || WebhookUrls.Count == 0)
        {
            WebhookUrls = new List<string> { string.Empty };
        }
    }

    public void Save()
    {
        _pluginInterface?.SavePluginConfig(this);
    }
}
