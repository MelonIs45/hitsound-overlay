// M0 feasibility probe: logs tosu's editor time samples with high-resolution receive timestamps.
//   TosuProbe [record] [out.csv]   connect to tosu and record until Ctrl+C
//   TosuProbe analyze <in.csv>     summarize a recording
using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

var inv = CultureInfo.InvariantCulture;

if (args.Length >= 2 && args[0] == "analyze")
{
    Analyzer.Run(args[1]);
    return;
}

string outPath = args.Length >= 2 ? args[1] : $"tosu-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
var sw = Stopwatch.StartNew(); // QPC-backed
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

using var csv = new StreamWriter(outPath, false, new UTF8Encoding(false)) { AutoFlush = true };
csv.WriteLine("wall_ms,src,state,live_ms,paused,focused,mods_rate,beatmap_file");
var csvLock = new object();

Console.WriteLine($"Recording to {Path.GetFullPath(outPath)}  (Ctrl+C to stop)");
Console.WriteLine("Do this in the editor: play at 100%, pause, seek while paused (click/scroll/arrow), seek while playing,");
Console.WriteLine("then repeat play at 75%, 50%, 25%. Also leave the editor once (song select) and come back.\n");

// Live stats for the status line
long msgs = 0, changes = 0;
double lastLive = double.NaN;
int lastState = -1;
string lastFile = "";
double lastPrintMs = 0;
var recent = new Queue<(double wall, double live)>();

var v2 = RunSocket("ws://127.0.0.1:24050/websocket/v2", "v2", msg =>
{
    double wall = sw.Elapsed.TotalMilliseconds;
    using var doc = JsonDocument.Parse(msg);
    var r = doc.RootElement;
    int state = r.GetProperty("state").GetProperty("number").GetInt32();
    string stateName = r.GetProperty("state").GetProperty("name").GetString() ?? "";
    double live = r.GetProperty("beatmap").GetProperty("time").GetProperty("live").GetDouble();
    bool paused = r.GetProperty("game").GetProperty("paused").GetBoolean();
    bool focused = r.GetProperty("game").GetProperty("focused").GetBoolean();
    double modsRate = r.GetProperty("play").GetProperty("mods").GetProperty("rate").GetDouble();
    string file = r.GetProperty("directPath").GetProperty("beatmapFile").GetString() ?? "";

    lock (csvLock)
    {
        csv.WriteLine(string.Join(",",
            wall.ToString("F3", inv), "v2", state, live.ToString(inv), paused ? 1 : 0, focused ? 1 : 0,
            modsRate.ToString(inv), file == lastFile ? "" : "\"" + file.Replace("\"", "'") + "\""));
    }

    msgs++;
    if (live != lastLive) changes++;
    if (state != lastState) Console.WriteLine($"[{wall / 1000,8:F2}s] state -> {state} ({stateName})");
    if (file != lastFile) Console.WriteLine($"[{wall / 1000,8:F2}s] beatmap -> {file}");
    lastLive = live; lastState = state; lastFile = file;

    recent.Enqueue((wall, live));
    while (recent.Count > 0 && wall - recent.Peek().wall > 1000) recent.Dequeue();

    if (wall - lastPrintMs >= 1000)
    {
        double rate = double.NaN;
        if (recent.Count >= 2)
        {
            var a = recent.Peek(); var b = (wall, live);
            rate = (b.live - a.live) / (b.wall - a.wall);
        }
        Console.WriteLine($"[{wall / 1000,8:F2}s] msgs/s={msgs,4}  time-changes/s={changes,4}  live={live,9}ms  paused={paused,-5}  ~rate={rate,6:F3}");
        msgs = 0; changes = 0; lastPrintMs = wall;
    }
}, cts.Token);

// Precise socket: log its shape whenever the set of keys changes (to see if it ever carries a time).
string lastShape = "";
var precise = RunSocket("ws://127.0.0.1:24050/websocket/v2/precise", "precise", msg =>
{
    double wall = sw.Elapsed.TotalMilliseconds;
    using var doc = JsonDocument.Parse(msg);
    string shape = string.Join("|", doc.RootElement.EnumerateObject().Select(p => p.Name));
    if (doc.RootElement.TryGetProperty("currentTime", out var ct))
    {
        lock (csvLock) csv.WriteLine($"{wall.ToString("F3", inv)},precise,,{ct.GetDouble().ToString(inv)},,,,");
    }
    if (shape != lastShape)
    {
        Console.WriteLine($"[{wall / 1000,8:F2}s] precise message keys: {shape}");
        lastShape = shape;
    }
}, cts.Token);

await Task.WhenAll(v2, precise);
lock (csvLock) csv.Flush();
Console.WriteLine($"\nSaved {Path.GetFullPath(outPath)}");
Console.WriteLine($"Analyze with: dotnet run -- analyze \"{Path.GetFullPath(outPath)}\"");

async Task RunSocket(string url, string name, Action<string> onMessage, CancellationToken ct)
{
    var buf = new byte[1 << 16];
    while (!ct.IsCancellationRequested)
    {
        using var ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(new Uri(url), ct);
            Console.WriteLine($"[{name}] connected");
            var sb = new MemoryStream();
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var res = await ws.ReceiveAsync(buf, ct);
                if (res.MessageType == WebSocketMessageType.Close) break;
                sb.Write(buf, 0, res.Count);
                if (!res.EndOfMessage) continue;
                var text = Encoding.UTF8.GetString(sb.GetBuffer(), 0, (int)sb.Length);
                sb.SetLength(0);
                try { onMessage(text); }
                catch (Exception e) { Console.WriteLine($"[{name}] parse error: {e.Message}"); }
            }
        }
        catch (OperationCanceledException) { break; }
        catch (Exception e) { Console.WriteLine($"[{name}] {e.Message}; retrying in 1s"); }
        try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
    }
}

static class Analyzer
{
    record Row(double Wall, int State, double Live, bool Paused);

    public static void Run(string path)
    {
        var inv = CultureInfo.InvariantCulture;
        var rows = File.ReadLines(path).Skip(1)
            .Select(l => l.Split(','))
            .Where(p => p[1] == "v2")
            .Select(p => new Row(double.Parse(p[0], inv), int.Parse(p[2]), double.Parse(p[3], inv), p[4] == "1"))
            .ToList();
        var precise = File.ReadLines(path).Skip(1).Count(l => l.Split(',')[1] == "precise");

        Console.WriteLine($"v2 messages: {rows.Count}, precise time messages: {precise}");
        if (rows.Count < 3) return;
        double dur = (rows[^1].Wall - rows[0].Wall) / 1000;
        Console.WriteLine($"duration: {dur:F1}s, message rate: {rows.Count / dur:F1}/s");

        var states = rows.GroupBy(r => r.State).Select(g => $"{g.Key}:{g.Count()}");
        Console.WriteLine($"messages per state number: {string.Join("  ", states)}  (editor is 1 in osu! stable)");

        var ed = rows.Where(r => r.State == 1).ToList();
        if (ed.Count < 3) { Console.WriteLine("Too few editor samples."); return; }

        var msgDt = ed.Zip(ed.Skip(1), (a, b) => b.Wall - a.Wall).Where(d => d < 1000).OrderBy(d => d).ToList();
        Console.WriteLine($"editor message interval ms: median {Pct(msgDt, 50):F1}, p95 {Pct(msgDt, 95):F1}, max {msgDt[^1]:F1}");

        // Intervals between *changes* of live time while it is advancing
        var changes = new List<Row> { ed[0] };
        foreach (var r in ed.Skip(1)) if (r.Live != changes[^1].Live) changes.Add(r);
        var chDt = changes.Zip(changes.Skip(1), (a, b) => b.Wall - a.Wall).Where(d => d < 300).OrderBy(d => d).ToList();
        if (chDt.Count > 0)
            Console.WriteLine($"interval between new time values (while moving) ms: median {Pct(chDt, 50):F1}, p95 {Pct(chDt, 95):F1}");

        // Segment into continuous playback runs: consecutive changed samples whose implied rate is sane.
        Console.WriteLine("\nPlayback segments (>= 1s, linear fit live = rate*wall + c):");
        Console.WriteLine("  start_s  dur_s   rate   resid_rms_ms  resid_max_ms  n");
        var seg = new List<Row>();
        int seeks = 0, pausedSeeks = 0;
        void Flush()
        {
            if (seg.Count >= 5 && seg[^1].Wall - seg[0].Wall >= 1000)
            {
                var (rate, c) = Fit(seg);
                var res = seg.Select(r => r.Live - (rate * r.Wall + c)).ToList();
                double rms = Math.Sqrt(res.Average(x => x * x));
                Console.WriteLine($"  {seg[0].Wall / 1000,7:F1}  {(seg[^1].Wall - seg[0].Wall) / 1000,5:F1}  {rate,6:F3}  {rms,12:F2}  {res.Max(Math.Abs),12:F2}  {seg.Count}");
            }
            seg.Clear();
        }
        for (int i = 1; i < ed.Count; i++)
        {
            var a = ed[i - 1]; var b = ed[i];
            double dWall = b.Wall - a.Wall, dLive = b.Live - a.Live;
            if (dLive == 0)
            {
                // Stalled sample: end the segment if time has been still for a while (pause).
                if (seg.Count > 0 && b.Wall - seg[^1].Wall > 250) Flush();
                continue;
            }
            double impl = dLive / Math.Max(dWall, 0.001);
            bool jump = dLive < 0 || Math.Abs(dLive) > dWall * 1.2 + 60;
            if (jump)
            {
                bool wasStill = i >= 2 && ed[i - 2].Live == a.Live;
                seeks++; if (wasStill) pausedSeeks++;
                Console.WriteLine($"  -- jump at {b.Wall / 1000:F2}s: {a.Live} -> {b.Live} ms ({(wasStill ? "while paused" : "while playing")})");
                Flush();
                seg.Add(b);
                continue;
            }
            if (seg.Count == 0) seg.Add(a);
            seg.Add(b);
        }
        Flush();
        Console.WriteLine($"\njumps detected: {seeks} ({pausedSeeks} while paused)");
    }

    static (double rate, double c) Fit(List<Row> s)
    {
        double mx = s.Average(r => r.Wall), my = s.Average(r => r.Live);
        double sxy = s.Sum(r => (r.Wall - mx) * (r.Live - my)), sxx = s.Sum(r => (r.Wall - mx) * (r.Wall - mx));
        double rate = sxx > 0 ? sxy / sxx : 0;
        return (rate, my - rate * mx);
    }

    static double Pct(List<double> sorted, double p) =>
        sorted[Math.Clamp((int)Math.Round(p / 100 * (sorted.Count - 1)), 0, sorted.Count - 1)];
}

