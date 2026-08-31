using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace HuntTrainRelay;

public readonly record struct AetheryteData(uint AetheryteId, byte SubIndex, uint TerritoryId, float X, float Y, string Name)
{
    public Vector2 Position => new(X, Y);
}

/// <summary>
/// Teleports to the aetheryte nearest a mark, via the Teleporter plugin's
/// "Teleport" IPC gate — signature (uint aetheryteId, byte subIndex) -> bool.
///
/// Note this is the Teleporter plugin (pohky), which is what Hunt Helper uses.
/// Lifestream is a different plugin with a different gate; if the group ends up
/// on that instead, this needs a separate subscriber.
///
/// The aetheryte table below is adapted from Hunt Helper's TeleportManager
/// (img02/HuntHelper, MIT licensed) — positions are in-game map coordinates.
/// </summary>
public sealed class TeleportHelper
{
    private static readonly List<AetheryteData> Aetherytes = new()
    {
        new(8, 0, 129, 0f, 0f, "Limsa Lominsa Lower Decks"),
        new(52, 0, 134, 26f, 16.3f, "Summerford Farms"),
        new(10, 0, 135, 24.6f, 34.9f, "Moraby Drydocks"),
        new(11, 0, 137, 31.2f, 30.8f, "Costa del Sol"),
        new(12, 0, 137, 21.1f, 21.5f, "Wineport"),
        new(13, 0, 138, 34.5f, 31.7f, "Swiftperch"),
        new(14, 0, 138, 26.6f, 25.8f, "Aleport"),
        new(15, 0, 139, 30.2f, 23.3f, "Camp Bronze Lake"),
        new(16, 0, 180, 19.1f, 17.2f, "Camp Overlook"),
        new(2, 0, 132, 0f, 0f, "New Gridania"),
        new(3, 0, 148, 21.7f, 22.2f, "Bentbranch Meadows"),
        new(4, 0, 152, 17.7f, 27.4f, "The Hawthorne Hut"),
        new(5, 0, 153, 25.0f, 20.1f, "Quarrymill"),
        new(6, 0, 153, 16.8f, 28.6f, "Camp Tranquil"),
        new(7, 0, 154, 20.6f, 26.0f, "Fallgourd Float"),
        new(9, 0, 130, 0f, 0f, "Ul'dah - Steps of Nald"),
        new(17, 0, 140, 22.8f, 16.9f, "Horizon"),
        new(53, 0, 141, 21f, 18.1f, "Black Brush Station"),
        new(18, 0, 145, 13.7f, 24.3f, "Camp Drybone"),
        new(19, 0, 146, 18.3f, 13.1f, "Little Ala Mhigo"),
        new(20, 0, 146, 14.9f, 29.6f, "Forgotten Springs"),
        new(21, 0, 147, 21.9f, 30.5f, "Camp Bluefog"),
        new(22, 0, 147, 20.9f, 20.9f, "Ceruleum Processing Plant"),
        new(24, 0, 156, 22.2f, 8.1f, "Revenant's Toll"),
        new(23, 0, 155, 25.9f, 16.8f, "Camp Dragonhead"),
        new(71, 0, 397, 32f, 36.7f, "Falcon's Nest"),
        new(72, 0, 401, 10.3f, 33.6f, "Camp Cloudtop"),
        new(73, 0, 401, 10.4f, 14.2f, "Ok' Zundu"),
        new(74, 0, 402, 8.1f, 10.6f, "Helix"),
        new(75, 0, 478, 0f, 0f, "Idyllshire"),
        new(76, 0, 398, 33.2f, 23.1f, "Tailfeather"),
        new(77, 0, 398, 16.4f, 23.2f, "Anyx Trine"),
        new(78, 0, 400, 27.9f, 34.2f, "Moghome"),
        new(79, 0, 400, 10.8f, 28.8f, "Zenith"),
        new(104, 0, 635, 0f, 0f, "Rhalgr's Reach"),
        new(98, 0, 612, 8.90f, 11.3f, "Castrum Oriens"),
        new(99, 0, 612, 29.8f, 26.4f, "The Peering Stones"),
        new(100, 0, 620, 23.7f, 6.5f, "Ala Gannha"),
        new(101, 0, 620, 16.0f, 36.4f, "Ala Ghiri"),
        new(102, 0, 621, 8.40f, 21.1f, "Porta Praetoria"),
        new(103, 0, 621, 33.8f, 34.5f, "The Ala Mhigan Quarter"),
        new(111, 0, 628, 0f, 0f, "Kugane"),
        new(105, 0, 613, 28.6f, 16.2f, "Tamamizu"),
        new(106, 0, 613, 23.2f, 9.8f, "Onokoro"),
        new(107, 0, 614, 30.1f, 19.6f, "Namai"),
        new(108, 0, 614, 26.3f, 13.4f, "The House of the Fierce"),
        new(109, 0, 622, 32.5f, 28.3f, "Reunion"),
        new(110, 0, 622, 23.0f, 22.1f, "The Dawn Throne"),
        new(128, 0, 622, 6.30f, 23.8f, "Dhoro Iloh"),
        new(132, 0, 813, 36.5f, 20.9f, "Fort Jobb"),
        new(136, 0, 813, 6.8f, 16.9f, "The Ostall Imperative"),
        new(137, 0, 814, 34.8f, 27.2f, "Stilltide"),
        new(138, 0, 814, 16.6f, 29.2f, "Wright"),
        new(139, 0, 814, 12.9f, 9f, "Tomra"),
        new(140, 0, 815, 26.4f, 17f, "Mord Souq"),
        new(161, 0, 815, 29.4f, 27.6f, "The Inn at Journey's Head"),
        new(141, 0, 815, 11.2f, 17.2f, "Twine"),
        new(144, 0, 816, 14.6f, 31.7f, "Lydha Lran"),
        new(145, 0, 816, 20.0f, 4.3f, "Pla Enni"),
        new(146, 0, 816, 29.1f, 7.7f, "Wolekdorf"),
        new(142, 0, 817, 19.4f, 27.4f, "Slitherbough"),
        new(143, 0, 817, 29.1f, 17.5f, "Fanow"),
        new(147, 0, 818, 32.7f, 17.5f, "The Ondo Cups"),
        new(148, 0, 818, 0f, 0f, "The Macarenses Angle - super far away, don't tele here stupid"),
        new(169, 0, 957, 25.4f, 34f, "Yedlihmad"),
        new(170, 0, 957, 10.9f, 22.2f, "The Great Work"),
        new(171, 0, 957, 29.5f, 16.5f, "Palaka's Stand"),
        new(172, 0, 958, 13.3f, 31f, "Camp Broken Glass"),
        new(173, 0, 958, 31.8f, 17.9f, "Tertium"),
        new(166, 0, 956, 30.3f, 11.9f, "The Archeion"),
        new(167, 0, 956, 21.6f, 20.5f, "Sharlayan Hamlet"),
        new(168, 0, 956, 6.9f, 27.5f, "Aporia"),
        new(174, 0, 959, 10.1f, 34.5f, "Sinus Lacrimarum"),
        new(175, 0, 959, 0f, 0f, "Bestways Burrow"),
        new(179, 0, 960, 10.5f, 26.8f, "Reah Tahra"),
        new(180, 0, 960, 22.6f, 8.3f, "Abode of the Ea"),
        new(181, 0, 960, 31.2f, 28.1f, "Base Omicron"),
        new(176, 0, 961, 24.6f, 24f, "Anagnorisis"),
        new(177, 0, 961, 8.7f, 32.3f, "The Twelve Wonders"),
        new(178, 0, 961, 10.8f, 17f, "Poieten Oikos"),
        new(200, 0, 1187, 28.1f, 13.1f, "Wachunpelo"),
        new(201, 0, 1187, 30.8f, 34.2f, "Worlar's Echo"),
        new(202, 0, 1188, 18.0f, 11.9f, "Ok'hanu"),
        new(203, 0, 1188, 32.2f, 25.6f, "Many Fires"),
        new(204, 0, 1188, 11.9f, 27.7f, "Earthenshire"),
        new(237, 0, 1188, 36.5f, 17.2f, "Dock Poga"),
        new(205, 0, 1189, 13.5f, 12.8f, "Iq Br'aax"),
        new(206, 0, 1189, 35.8f, 32.0f, "Mamook"),
        new(207, 0, 1190, 29.0f, 30.8f, "Hhusatahwi"),
        new(208, 0, 1190, 15.6f, 19.2f, "Sheshenewezi Springs"),
        new(209, 0, 1190, 27.6f, 10.1f, "Mehwahhetsoan"),
        new(210, 0, 1191, 31.7f, 25.6f, "Yyasulani Station"),
        new(211, 0, 1191, 17.0f, 9.8f, "The Outskirts"),
        new(212, 0, 1191, 17.1f, 23.9f, "Electrope Strike"),
        new(213, 0, 1192, 21.4f, 37.4f, "Leynode Mnemo"),
        new(214, 0, 1192, 34.6f, 15.8f, "Leynode Pyro"),
        new(215, 0, 1192, 16.3f, 13.5f, "Leynode Aero"),
    };

    private readonly ICallGateSubscriber<uint, byte, bool> _teleportIpc;
    private readonly IPluginLog _log;

    public string LastError { get; private set; } = string.Empty;

    public TeleportHelper(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        _log = log;
        _teleportIpc = pluginInterface.GetIpcSubscriber<uint, byte, bool>("Teleport");
    }

    /// <summary>
    /// The closest aetheryte to a point in a zone, or null if that zone has
    /// none known. Used both for teleporting and for naming the next stop.
    /// </summary>
    public static AetheryteData? NearestTo(uint territoryId, Vector2 mapPosition)
    {
        var candidates = Aetherytes.Where(a => a.TerritoryId == territoryId).ToList();
        if (candidates.Count == 0) return null;

        return candidates
            .OrderBy(a => Vector2.DistanceSquared(a.Position, mapPosition))
            .First();
    }

    /// <summary>
    /// Finds the closest aetheryte in the same zone and teleports there.
    /// Returns false (with LastError set) if the zone has no known aetheryte or
    /// the Teleporter plugin isn't installed.
    /// </summary>
    public bool TeleportToNearest(uint territoryId, Vector2 mapPosition)
    {
        var found = NearestTo(territoryId, mapPosition);
        if (found is not { } nearest)
        {
            LastError = "No known aetheryte for that zone.";
            return false;
        }

        try
        {
            // The gate returns whether Teleporter actually accepted the request.
            // This used to be discarded, so a rejected teleport looked like a
            // success and failed in complete silence — which is why the Dock
            // Poga case gave no clue what was wrong.
            var accepted = _teleportIpc.InvokeFunc(nearest.AetheryteId, nearest.SubIndex);
            if (!accepted)
            {
                LastError = $"Teleporter refused \"{nearest.Name}\" (aetheryte {nearest.AetheryteId}" +
                            $", sub {nearest.SubIndex}) — likely not attuned, or a bad ID in our table.";
                return false;
            }

            LastError = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            LastError = "Teleport failed — the Teleporter plugin must be installed.";
            _log.Warning(ex, "Teleport IPC call failed.");
            return false;
        }
    }
}
