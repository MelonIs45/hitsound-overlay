using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Overlay.Sources.Memory;

/// <summary>
/// Read-only access to another process's memory (osu! stable, a 32-bit process): reading values and finding byte
/// patterns. Opens the process with read and query rights only; never writes.
/// </summary>
public sealed class ProcessMemory : IDisposable
{
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint MEM_COMMIT = 0x1000;
    private const uint PAGE_GUARD = 0x100, PAGE_NOACCESS = 0x01;
    /// <summary>Executable pages (JIT-compiled code, where the patterns are).</summary>
    private const uint PAGE_EXECUTE_ANY = 0x10 | 0x20 | 0x40 | 0x80;

    private readonly IntPtr handle;
    private readonly byte[] scratch = new byte[8];

    public int ProcessId { get; }

    public ProcessMemory(int processId)
    {
        ProcessId = processId;
        handle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), $"can't open process {processId}");
    }

    /// <summary>Whether the process is still running.</summary>
    public bool IsAlive => GetExitCodeProcess(handle, out uint code) && code == 259; // STILL_ACTIVE

    public unsafe bool TryRead(ulong address, Span<byte> into)
    {
        fixed (byte* p = into)
            return ReadProcessMemory(handle, (IntPtr)(long)address, (IntPtr)p, (IntPtr)into.Length, out var read)
                && (long)read == into.Length;
    }

    public bool TryReadInt32(ulong address, out int value)
    {
        value = 0;
        if (!TryRead(address, scratch.AsSpan(0, 4))) return false;
        value = BitConverter.ToInt32(scratch, 0);
        return true;
    }

    public bool TryReadUInt32(ulong address, out uint value)
    {
        value = 0;
        if (!TryRead(address, scratch.AsSpan(0, 4))) return false;
        value = BitConverter.ToUInt32(scratch, 0);
        return true;
    }

    public bool TryReadDouble(ulong address, out double value)
    {
        value = 0;
        if (!TryRead(address, scratch.AsSpan(0, 8))) return false;
        value = BitConverter.ToDouble(scratch, 0);
        return true;
    }

    /// <summary>
    /// Finds the first occurrence of <paramref name="pattern"/> in the process's executable memory (where the CLR puts
    /// JIT-compiled code), or null.
    /// </summary>
    public ulong? Find(BytePattern pattern)
    {
        var results = FindAll(new[] { pattern });
        return results[0];
    }

    /// <summary>Finds the first occurrence of each pattern in one pass over the process's executable memory.</summary>
    public ulong?[] FindAll(IReadOnlyList<BytePattern> patterns)
    {
        var found = new ulong?[patterns.Count];
        int remaining = patterns.Count;
        var buffer = Array.Empty<byte>();
        ulong address = 0;
        int infoSize = Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
        while (remaining > 0 && VirtualQueryEx(handle, (IntPtr)(long)address, out var info, (IntPtr)infoSize) != IntPtr.Zero)
        {
            ulong regionBase = (ulong)(long)info.BaseAddress;
            ulong size = (ulong)(long)info.RegionSize;
            if (size == 0) break;
            bool readable = info.State == MEM_COMMIT && (info.Protect & PAGE_EXECUTE_ANY) != 0
                && (info.Protect & (PAGE_GUARD | PAGE_NOACCESS)) == 0;
            if (readable && size < 1UL << 30)
            {
                if (buffer.Length < (int)size) buffer = new byte[size];
                var span = buffer.AsSpan(0, (int)size);
                if (TryRead(regionBase, span))
                {
                    for (int i = 0; i < patterns.Count; i++)
                    {
                        if (found[i] != null) continue;
                        int at = patterns[i].IndexIn(span);
                        if (at >= 0) { found[i] = regionBase + (ulong)at; remaining--; }
                    }
                }
            }
            address = regionBase + size;
            if (address >= 1UL << 47) break;
        }
        return found;
    }

    public void Dispose() => CloseHandle(handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, IntPtr buffer, IntPtr size, out IntPtr read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualQueryEx(IntPtr process, IntPtr address, out MEMORY_BASIC_INFORMATION info, IntPtr length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
}

/// <summary>A byte pattern with wildcards, written like "5E 5F 5D C3 A1 ?? ?? ?? ??".</summary>
public sealed class BytePattern
{
    private readonly byte[] bytes;
    private readonly bool[] wild;

    public string Text { get; }
    public int Length => bytes.Length;

    public BytePattern(string text)
    {
        Text = text;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bytes = new byte[parts.Length];
        wild = new bool[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] == "??") wild[i] = true;
            else bytes[i] = Convert.ToByte(parts[i], 16);
        }
        if (wild[0]) throw new ArgumentException("a pattern must start with a fixed byte", nameof(text));
    }

    /// <summary>Index of the first match in <paramref name="data"/>, or -1.</summary>
    public int IndexIn(ReadOnlySpan<byte> data)
    {
        byte first = bytes[0];
        int last = data.Length - bytes.Length;
        int i = 0;
        while (i <= last)
        {
            int next = data.Slice(i, last - i + 1).IndexOf(first);
            if (next < 0) return -1;
            i += next;
            int k = 1;
            while (k < bytes.Length && (wild[k] || data[i + k] == bytes[k])) k++;
            if (k == bytes.Length) return i;
            i++;
        }
        return -1;
    }

    public override string ToString() => Text;
}
