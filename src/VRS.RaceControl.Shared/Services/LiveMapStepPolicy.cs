using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

/// <summary>Which plain-language state the HOST map screen is in. The screen shows one headline and one hint per state.</summary>
public enum LiveMapStep
{
    WaitingForGame,
    /// <summary>A map is on screen but LMU is not on a track yet (menu, garage, not running), so there are no cars to draw.</summary>
    MapLoadedWaiting,
    Recording,
    Unloaded,
    NoMap,
    WrongTrack,
    NeedsSave,
    Ready
}

public static class LiveMapStepPolicy
{
    /// <param name="gameOnTrack">The simulator reports a track name. In menus it sends frames with no track and no cars.</param>
    /// <param name="recording">The operator pressed RECORD and the lap is not finished.</param>
    /// <param name="unloaded">The operator pressed UNLOAD for the track the game is on.</param>
    /// <param name="map">The map currently held, if any.</param>
    /// <param name="matchesGame">The held map belongs to the track the game is on.</param>
    public static LiveMapStep Decide(bool gameOnTrack, bool recording, bool unloaded, TrackDefinition? map, bool matchesGame)
    {
        if (!gameOnTrack) return map != null ? LiveMapStep.MapLoadedWaiting : LiveMapStep.WaitingForGame;
        if (recording) return LiveMapStep.Recording;
        if (map == null) return unloaded ? LiveMapStep.Unloaded : LiveMapStep.NoMap;
        if (!matchesGame) return LiveMapStep.WrongTrack;
        return map.Verified ? LiveMapStep.Ready : LiveMapStep.NeedsSave;
    }
}

/// <summary>What the map screen does with the map already on screen when the operator picks a track and layout from the lists.</summary>
public static class MapPickPolicy
{
    /// <summary>
    /// True when the map on screen must come down because the operator picked another track or layout that has no saved map. Picking only
    /// chooses what LOAD MAP would load; leaving the old map up under the new choice made "Monza Curva Grande" show the Monza GP map.
    /// The live map stays while LMU is on a track (the operator is only browsing) and while a lap is being recorded.
    /// </summary>
    public static bool ShouldClearShownMap(bool operatorPicked, bool pickedHasSavedMap, string? shownMapTrackId, string? pickedGameName,
        bool gameOnTrack, bool recording)
    {
        if (!operatorPicked || pickedHasSavedMap || recording || gameOnTrack || string.IsNullOrWhiteSpace(shownMapTrackId)) return false;
        // A layout whose in-game name is not known yet (pickedGameName null) cannot be the shown map's layout.
        return string.IsNullOrWhiteSpace(pickedGameName)
            || LmuTrackCatalog.Normalize(shownMapTrackId) != LmuTrackCatalog.Normalize(pickedGameName);
    }
}
