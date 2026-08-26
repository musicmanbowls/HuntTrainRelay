using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace HuntTrainRelay;

public static class DiscordRelay
{
    private static readonly HttpClient Http = new();

    // Discord embed side-bar colour (a calm green). Decimal form of hex 2ECC71.
    private const int EmbedColor = 3066993;

    public static Task<(bool Success, string Message)> PostTestAsync(List<WebhookEntry> webhooks)
    {
        var payload = new
        {
            embeds = new object[]
            {
                new
                {
                    title = "🚂 Hunt Train Relay — test message",
                    description = $"If you can see this, your webhook is working.\nPosted <t:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:F>",
                    color = EmbedColor,
                },
            },
        };

        return SendToAllAsync(webhooks, payload);
    }

    public static Task<(bool Success, string Message)> PostScoutingReportAsync(List<WebhookEntry> webhooks, List<HuntHelperMobRecord> marks, List<string> scoutNames)
    {
        if (marks.Count == 0)
            return Task.FromResult((false, "Nothing to report — Hunt Helper's train list is empty."));

        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var exportCode = ScoutingReport.BuildExportCode(marks);
        var summary = ScoutingReport.BuildSummary(marks);

        var names = (scoutNames ?? new List<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        var scoutedBy = names.Count > 0 ? $"\n\nScouted by {string.Join(", ", names)}" : "";

        string description;
        if (exportCode.Length > 3800)
        {
            // Discord embed descriptions cap at 4096 characters. Rather than ever
            // post a code block that's been cut off mid-string (unusable to import),
            // drop the code and say so plainly if a scout is genuinely too big.
            description =
                $"Scouting done at <t:{nowUnix}:F>\n\n{summary}\n\n" +
                "(Export code omitted — this scout covers too many marks to fit in one " +
                $"Discord message. Try sending separate reports per zone instead.){scoutedBy}";
        }
        else
        {
            description = $"```\n{exportCode}\n```\nScouting done at <t:{nowUnix}:F>\n\n{summary}{scoutedBy}";
        }

        var payload = new
        {
            embeds = new object[]
            {
                new
                {
                    title = "🔭 Scouting Report",
                    description,
                    color = EmbedColor,
                },
            },
        };

        return SendToAllAsync(webhooks, payload);
    }

    public static Task<(bool Success, string Message)> PostTrainCompleteAsync(List<WebhookEntry> webhooks, List<TrackedMark> marks, string? endedBy, List<FlagEntry>? flags = null)
    {
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var endedByLine = string.IsNullOrWhiteSpace(endedBy) ? "" : $"\nEnded by {endedBy}";
        var body = BuildChronologicalBody(marks);
        var flagFooter = BuildFlagFooter(flags);

        var chunks = ChunkByLength(body + flagFooter, 3800);
        var embeds = new List<object>();
        for (var i = 0; i < chunks.Count; i++)
        {
            embeds.Add(new
            {
                title = i == 0 ? "🚂 Train Complete" : "🚂 Train Complete (continued)",
                description = i == 0
                    ? $"Finished <t:{nowUnix}:F> — {marks.Count} marks{endedByLine}\n\n{chunks[i]}"
                    : chunks[i],
                color = EmbedColor,
            });
        }

        var payload = new { embeds };
        return SendToAllAsync(webhooks, payload);
    }

    /// <summary>
    /// S-rank watch results for the train (Spawned / Didn't Spawn / never checked).
    /// </summary>
    private static string BuildFlagFooter(List<FlagEntry>? flags)
    {
        var watches = flags ?? new List<FlagEntry>();
        if (watches.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.Append("\n**S-Rank Checks**\n");
        foreach (var f in watches)
        {
            var status = f.SpawnStatus switch
            {
                SpawnStatus.Spawned => "Spawned",
                SpawnStatus.NotSpawned => "Did not spawn",
                _ => "Not checked",
            };
            sb.Append($"{f.Label} — {status}\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// One continuous list sorted by the exact moment each mark was observed
    /// dead (not by expansion or zone) — reflecting the real order the train
    /// actually killed things in. A bold expansion header is inserted wherever
    /// the expansion changes between consecutive kills, purely as a readability
    /// aid — it's a side effect of the chronological sort, not a grouping key.
    /// Finishes with an "Assumed Sniped" section. Both the entries and the
    /// sniped groups come from TrainReport, the same module the in-game
    /// "Marks Slain" tab reads from.
    /// </summary>
    private static string BuildChronologicalBody(List<TrackedMark> marks)
    {
        var entries = TrainReport.BuildEntries(marks);
        var sb = new StringBuilder();
        string? lastExpansion = null;

        foreach (var entry in entries)
        {
            if (entry.Expansion != lastExpansion)
            {
                if (lastExpansion != null) sb.Append('\n');
                sb.Append($"**{entry.Expansion}**\n");
                lastExpansion = entry.Expansion;
            }

            var killUnix = new DateTimeOffset(entry.KillTimeUtc).ToUnixTimeSeconds();

            if (entry.Location == null || entry.MinHours == null || entry.MaxHours == null)
            {
                sb.Append($"<t:{killUnix}:t> — {entry.Name} — no fixed respawn timer\n");
                continue;
            }

            var openUnix = new DateTimeOffset(entry.KillTimeUtc.AddHours(entry.MinHours.Value)).ToUnixTimeSeconds();
            var capUnix = new DateTimeOffset(entry.KillTimeUtc.AddHours(entry.MaxHours.Value)).ToUnixTimeSeconds();
            var instanceGlyph = ExpansionData.InstanceGlyph(entry.Instance);
            sb.Append($"<t:{killUnix}:t> — {entry.Location} — {entry.Name}{instanceGlyph} — window <t:{openUnix}:t> → <t:{capUnix}:t>\n");
        }

        var sniped = TrainReport.BuildSniped(marks);
        if (sniped.Count > 0)
        {
            sb.Append("\n**Assumed Sniped** (not seen this train)\n");
            sb.Append(string.Join("\n", sniped.Select(s => $"**{s.Expansion}**: {string.Join(", ", s.Marks)}")));
            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Splits text into chunks under the character limit, breaking only at line
    /// boundaries. Used as a safety net for very large trains where the full
    /// chronological list would exceed a single embed description's 4096-char
    /// cap — extra chunks become additional embeds in the same message.
    /// </summary>
    private static List<string> ChunkByLength(string body, int limit)
    {
        var lines = body.Split('\n');
        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var line in lines)
        {
            if (current.Length > 0 && current.Length + line.Length + 1 > limit)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }
            current.Append(line).Append('\n');
        }

        if (current.Length > 0) chunks.Add(current.ToString());
        if (chunks.Count == 0) chunks.Add(string.Empty);

        return chunks;
    }

    /// <summary>
    /// Posts the same payload to every enabled, non-empty webhook (e.g. one per
    /// Discord server) — disabled entries (like a testing channel toggled off)
    /// are skipped entirely. Reports full success only if every enabled target
    /// succeeded; otherwise names which ones failed and why.
    /// </summary>
    private static async Task<(bool Success, string Message)> SendToAllAsync(List<WebhookEntry>? webhooks, object payload)
    {
        var targets = (webhooks ?? new List<WebhookEntry>())
            .Where(w => w.Enabled && !string.IsNullOrWhiteSpace(w.Url))
            .Select(w => w.Url)
            .Distinct()
            .ToList();

        if (targets.Count == 0)
            return (false, "No enabled webhook configured.");

        var json = JsonConvert.SerializeObject(payload);
        var successCount = 0;
        var failures = new List<string>();

        foreach (var url in targets)
        {
            var (success, message) = await SendRawAsync(url, json);
            if (success) successCount++;
            else failures.Add(message);
        }

        if (failures.Count == 0)
            return (true, $"Posted to {successCount} webhook{(successCount == 1 ? "" : "s")} at {DateTime.Now:T}.");

        return (false, $"Posted to {successCount}/{targets.Count} webhooks. {string.Join(" | ", failures)}");
    }

    private static async Task<(bool Success, string Message)> SendRawAsync(string webhookUrl, string json)
    {
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await Http.PostAsync(webhookUrl, content);

            if (response.IsSuccessStatusCode)
                return (true, "OK");

            var body = await response.Content.ReadAsStringAsync();
            return (false, $"Discord returned {(int)response.StatusCode}: {body}");
        }
        catch (Exception ex)
        {
            return (false, $"Request failed: {ex.Message}");
        }
    }
}
