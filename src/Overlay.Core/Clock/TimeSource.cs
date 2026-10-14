namespace Overlay.Core.Clock;

/// <summary>One reading of the editor's song position.</summary>
/// <param name="SongTimeMs">Song time as reported by osu!.</param>
/// <param name="WallMs">High-resolution receive time (Stopwatch/QPC based), in ms.</param>
/// <param name="Paused">The editor's pause state if the source reports it, otherwise null.</param>
public readonly record struct TimeSample(double SongTimeMs, double WallMs, bool? Paused = null);

/// <summary>The beatmap currently open in osu!.</summary>
/// <param name="BeatmapFolder">Full path of the beatmap set's folder.</param>
/// <param name="BeatmapFile">File name of the open difficulty inside that folder.</param>
public sealed record BeatmapInfo(string BeatmapFolder, string BeatmapFile);

/// <summary>Connection and game state as seen by the time source.</summary>
/// <param name="Connected">Whether the source is receiving data from osu!.</param>
/// <param name="State">osu! stable's state number (1 = editor).</param>
/// <param name="StateName">Readable state name, for the status display.</param>
public sealed record SourceStatus(bool Connected, int State, string StateName)
{
    public const int EditorState = 1;

    public bool InEditor => Connected && State == EditorState;

    public static readonly SourceStatus Disconnected = new(false, -1, "not connected");
}

public interface ITimeSource
{
    /// <summary>Raised for every reading, on a background thread.</summary>
    event Action<TimeSample>? Sample;
    /// <summary>Raised when the open beatmap changes, on a background thread.</summary>
    event Action<BeatmapInfo>? BeatmapChanged;
    /// <summary>Raised when the connection or game state changes, on a background thread.</summary>
    event Action<SourceStatus>? StatusChanged;
}
