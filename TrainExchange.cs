using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Text;

namespace HuntTrainRelay;

/// <summary>
/// Mirrors Hunt Helper's HuntTrainMob JSON shape exactly — same property names,
/// same set — so codes exported here import cleanly into Hunt Helper and vice
/// versa. MapLink is [JsonIgnore] on their side, so it's absent here too.
/// </summary>
public class ExchangeMob
{
    public string Name { get; set; } = string.Empty;
    public uint MobID { get; set; }
    public string MapName { get; set; } = string.Empty;
    public DateTime LastSeenUTC { get; set; }
    public Vector2 Position { get; set; }
    public bool Dead { get; set; }
    public uint TerritoryID { get; set; }
    public uint MapID { get; set; }
    public uint Instance { get; set; }
}

/// <summary>
/// Import/export of train lists using Hunt Helper's own encoding — gzip the
/// JSON, then base64 it. Adapted from HuntHelper/Utilities/ExportImport.cs
/// (img02/HuntHelper, MIT licensed).
/// </summary>
public static class TrainExchange
{
    public static string Export(IEnumerable<DetectedMark> marks)
    {
        var payload = marks.Select(m => new ExchangeMob
        {
            Name = m.Name,
            MobID = m.NameId,
            MapName = ExpansionData.Lookup(m.NameId)?.Location ?? string.Empty,
            LastSeenUTC = m.LastSeenUtc,
            Position = m.MapPosition,
            Dead = m.Dead,
            TerritoryID = m.TerritoryId,
            MapID = m.MapId,
            Instance = m.Instance,
        }).ToList();

        var json = JsonConvert.SerializeObject(payload);
        var bytes = Encoding.UTF8.GetBytes(json);

        using var input = new MemoryStream(bytes);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress))
        {
            input.CopyTo(gzip);
        }

        return Convert.ToBase64String(output.ToArray());
    }

    /// <summary>
    /// Decodes an import code. Returns null if it isn't a valid code, rather
    /// than throwing — pasted codes are frequently truncated or mangled.
    /// </summary>
    public static List<DetectedMark>? Import(string code)
    {
        try
        {
            var bytes = Convert.FromBase64String(code.Trim());
            using var input = new MemoryStream(bytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            var json = reader.ReadToEnd();

            var mobs = JsonConvert.DeserializeObject<List<ExchangeMob>>(json);
            if (mobs == null) return null;

            return mobs.Select(m => new DetectedMark
            {
                Name = m.Name,
                NameId = m.MobID,
                TerritoryId = m.TerritoryID,
                MapId = m.MapID,
                Instance = m.Instance,
                MapPosition = m.Position,
                Dead = m.Dead,
                FirstSeenUtc = m.LastSeenUTC,
                LastSeenUtc = m.LastSeenUTC,
                DeathObservedAtUtc = m.Dead ? m.LastSeenUTC : null,
            }).ToList();
        }
        catch
        {
            return null;
        }
    }
}
