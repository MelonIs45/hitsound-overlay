// Probe: finds osu! stable's song time and game state in its memory (read-only) with the signatures community tools
// use, and compares them with tosu. Usage: MemProbe [seconds]
using System.Diagnostics;
using System.Text.Json;
using Overlay.Sources.Memory;

var osu = Process.GetProcessesByName("osu!").FirstOrDefault() ?? throw new Exception("osu! isn't running");
using var mem = new ProcessMemory(osu.Id);
var patterns = new Dictionary<string, BytePattern>
{
    ["playTime"] = new("5E 5F 5D C3 A1 ?? ?? ?? ?? 89 ?? 04"),
    ["base"] = new("F8 01 74 04 83 65"),
    ["status"] = new("48 83 F8 04 73 1E"),
};
var sw = Stopwatch.StartNew();
var found = mem.FindAll(patterns.Values.ToList());
Console.WriteLine($"scan took {sw.ElapsedMilliseconds} ms");
var at = patterns.Keys.Zip(found).ToDictionary(p => p.First, p => p.Second);
foreach (var (k, v) in at) Console.WriteLine($"{k}: {(v is { } a ? $"0x{a:X8}" : "not found")}");

uint? timeAddr = null;
if (at["playTime"] is { } pt && mem.TryReadUInt32(pt + 5, out uint ta)) timeAddr = ta;
uint? statusAddr = null;
if (at["status"] is { } st && mem.TryReadUInt32(st - 4, out uint sa)) statusAddr = sa;
uint? baseStatusAddr = null;
if (at["base"] is { } b && mem.TryReadUInt32(b - 0x3C, out uint bsa)) baseStatusAddr = bsa;
Console.WriteLine($"time at 0x{timeAddr:X8}, status at 0x{statusAddr:X8} / 0x{baseStatusAddr:X8}");

using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:24050/") };
double seconds = args.Length > 0 ? double.Parse(args[0]) : 10;
var clock = Stopwatch.StartNew();
int lastTime = int.MinValue, changes = 0, reads = 0;
double lastChange = 0, maxGap = 0;
var gaps = new List<double>();
double nextTosu = 0;
while (clock.Elapsed.TotalSeconds < seconds)
{
    double now = clock.Elapsed.TotalMilliseconds;
    if (timeAddr is { } t && mem.TryReadInt32(t, out int time))
    {
        reads++;
        if (time != lastTime)
        {
            if (lastTime != int.MinValue) { changes++; gaps.Add(now - lastChange); }
            lastTime = time; lastChange = now;
        }
    }
    if (now >= nextTosu)
    {
        nextTosu = now + 1000;
        int status = statusAddr is { } s && mem.TryReadInt32(s, out int sv) ? sv : -1;
        int status2 = baseStatusAddr is { } s2 && mem.TryReadInt32(s2, out int sv2) ? sv2 : -1;
        string tosu;
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync("json/v2"));
            var r = doc.RootElement;
            tosu = $"tosu live {r.GetProperty("beatmap").GetProperty("time").GetProperty("live").GetDouble()} state {r.GetProperty("state").GetProperty("number").GetInt32()} paused {r.GetProperty("game").GetProperty("paused").GetBoolean()}";
        }
        catch (Exception e) { tosu = "tosu: " + e.Message; }
        int memNow = timeAddr is { } t2 && mem.TryReadInt32(t2, out int tv) ? tv : -1;
        Console.WriteLine($"{now / 1000,5:F1}s mem time {memNow} status {status}/{status2} | {tosu}");
    }
    Thread.SpinWait(200);
}
gaps.Sort();
Console.WriteLine($"{reads} reads, {changes} changes ({changes / seconds:F0}/s)" +
    (gaps.Count > 0 ? $", gap between changes median {gaps[gaps.Count / 2]:F2} ms, p95 {gaps[(int)(gaps.Count * 0.95)]:F2} ms" : ""));
