namespace Overlay.Core.Clock;

/// <summary>
/// Turns song-time samples from a time source into the position to show on each frame.
/// Samples arrive on any thread; <see cref="GetTime"/> is called once per rendered frame with increasing times.
/// </summary>
public interface IEditorClock
{
    void AddSample(TimeSample s);

    /// <summary>Song position to display at <paramref name="wallMs"/>.</summary>
    double GetTime(double wallMs);

    /// <summary>Whether any sample has arrived yet.</summary>
    bool HasTime { get; }

    /// <summary>Added to the wall time before predicting, i.e. in real time (scales with the playback speed).</summary>
    double OffsetMs { get; set; }

    /// <summary>Number of seeks detected so far.</summary>
    int SeekCount { get; }

    ClockDebugInfo Debug { get; }

    /// <summary>Optional diagnostics: called with a line for notable events.</summary>
    Action<string>? Trace { get; set; }

    /// <summary>The play/pause key was pressed (a hint for slow sources; fast ones can ignore it).</summary>
    void PlayPauseKeyPressed(double wallMs);

    /// <summary>The editor's timeline was scrolled with the mouse wheel (a hint for slow sources).</summary>
    void ScrollWheelMoved(double wallMs);
}
