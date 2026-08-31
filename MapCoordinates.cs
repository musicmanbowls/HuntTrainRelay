using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using System;
using System.Numerics;

namespace HuntTrainRelay;

/// <summary>
/// World position to in-game map coordinates (the 1-42ish numbers on the map).
///
/// This replaces an earlier approximation copied from Hunt Helper
/// (2048/scale + pos/50 + 1) which assumed a size factor of 100 and ignored
/// each map's offsets. That's within a fraction of a coordinate for most
/// zones, but it's wrong wherever a map has real offsets — and it was never
/// applied to conductor-placed flags at all, which is why those came out as
/// raw world values like -394.8 instead of something on the map scale.
/// </summary>
public static class MapCoordinates
{
    public static Vector2 FromWorld(IDataManager dataManager, uint mapId, float worldX, float worldZ)
    {
        try
        {
            var map = dataManager.GetExcelSheet<Map>().GetRowOrDefault(mapId);
            if (map == null) return new Vector2(worldX, worldZ);

            var scale = map.Value.SizeFactor / 100f;
            if (scale <= 0f) scale = 1f;

            return new Vector2(
                Convert(worldX, map.Value.OffsetX, scale),
                Convert(worldZ, map.Value.OffsetY, scale));
        }
        catch
        {
            return new Vector2(worldX, worldZ);
        }
    }

    private static float Convert(float world, float offset, float scale)
    {
        var value = (41f / scale) * ((world + offset) * scale + 1024f) / 2048f + 1f;
        return MathF.Round(value, 1, MidpointRounding.AwayFromZero);
    }
}
