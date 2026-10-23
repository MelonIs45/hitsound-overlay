using System.Diagnostics;

namespace Overlay.Sources.Memory;

/// <summary>
/// Finds and reads values in a running osu! stable, read-only, using the byte signatures that community tools (tosu,
/// gosumemory) use: the song time and the game state are static fields whose addresses appear in JIT-compiled code.
/// </summary>
public sealed class OsuMemory : IDisposable
{
    /// <summary>Code that loads the audio position: <c>mov eax, [time]</c> at +5.</summary>
    public static readonly BytePattern PlayTime = new("5E 5F 5D C3 A1 ?? ?? ?? ?? 89 ?? 04");
    /// <summary>Code that compares the game state: the state's address is at -4.</summary>
    public static readonly BytePattern Status = new("48 83 F8 04 73 1E");

    private readonly ProcessMemory memory;
    private readonly ulong timeAddress, statusAddress;

    private OsuMemory(ProcessMemory memory, ulong timeAddress, ulong statusAddress)
    {
        this.memory = memory;
        this.timeAddress = timeAddress;
        this.statusAddress = statusAddress;
    }

    public int ProcessId => memory.ProcessId;
    public bool IsAlive => memory.IsAlive;

    /// <summary>
    /// Attaches to <paramref name="osu"/> and finds the values. Returns null with a reason when they aren't there (yet):
    /// osu! may still be starting up, as the code is only compiled once it has run.
    /// </summary>
    public static OsuMemory? TryAttach(Process osu, out string problem)
    {
        problem = "";
        ProcessMemory? memory = null;
        try
        {
            memory = new ProcessMemory(osu.Id);
            var found = memory.FindAll(new[] { PlayTime, Status });
            if (found[0] is not { } time || found[1] is not { } status)
            {
                problem = $"osu!'s {(found[0] == null ? "song time" : "game state")} wasn't found in its memory " +
                          "(still starting up, or a version this overlay doesn't know)";
                memory.Dispose();
                return null;
            }
            if (!memory.TryReadUInt32(time + 5, out uint timeAddress) || !memory.TryReadUInt32(status - 4, out uint statusAddress))
            {
                problem = "couldn't read osu!'s memory";
                memory.Dispose();
                return null;
            }
            return new OsuMemory(memory, timeAddress, statusAddress);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            memory?.Dispose();
            problem = $"can't read osu!'s memory ({e.Message}; is osu! running as administrator?)";
            return null;
        }
    }

    /// <summary>The song position in ms, as osu! last set it (once per frame).</summary>
    public bool TryReadTime(out int ms) => memory.TryReadInt32(timeAddress, out ms);

    /// <summary>The game state (1 = editor), as tosu reports it.</summary>
    public bool TryReadStatus(out int state) => memory.TryReadInt32(statusAddress, out state);

    public static string StateName(int state) => state switch
    {
        0 => "menu", 1 => "edit", 2 => "play", 3 => "exit", 4 => "selectEdit", 5 => "selectPlay", 6 => "selectDrawings",
        7 => "resultScreen", 8 => "update", 9 => "busy", 11 => "lobby", 12 => "matchSetup", 13 => "selectMulti",
        14 => "rankingVs", 15 => "onlineSelection", 16 => "optionsOffsetWizard", 17 => "rankingTagCoop", 18 => "rankingTeam",
        19 => "beatmapImport", 20 => "packageUpdater", 21 => "benchmark", 22 => "tourney", 23 => "charts",
        _ => $"state {state}",
    };

    public void Dispose() => memory.Dispose();
}
