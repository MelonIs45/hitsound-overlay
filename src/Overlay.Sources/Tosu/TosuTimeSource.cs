using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Overlay.Core.Clock;

namespace Overlay.Sources.Tosu;

/// <summary>
/// Reads the editor's song time, game state and open beatmap from tosu's WebSocket API (v2).
/// tosu sends a full state message about every 100 ms (its minimum poll interval). Reconnects automatically.
/// </summary>
public sealed class TosuTimeSource : ITimeSource, IDisposable
{
    public const string DefaultUrl = "ws://127.0.0.1:24050/websocket/v2";

    private readonly Uri url;
    private readonly Stopwatch wall;
    private readonly CancellationTokenSource cts = new();
    private Task? loop;
    private SourceStatus status = SourceStatus.Disconnected;
    private BeatmapInfo? beatmap;
    private string? skin;
    private bool skinKnown;

    public event Action<TimeSample>? Sample;
    public event Action<BeatmapInfo>? BeatmapChanged;
    public event Action<SourceStatus>? StatusChanged;
    /// <summary>
    /// The skin folder osu! is using, or null when the editor is set to use the default skin
    /// ("Use default skin in the editor"). Raised on a background thread when it changes.
    /// </summary>
    public event Action<string?>? SkinChanged;

    /// <param name="wall">The stopwatch the renderer uses for frame times, so sample and frame times line up.</param>
    public TosuTimeSource(Stopwatch wall, string url = DefaultUrl)
    {
        this.wall = wall;
        this.url = new Uri(url);
    }

    public SourceStatus Status => status;
    public BeatmapInfo? Beatmap => beatmap;

    public void Start() => loop ??= Task.Run(() => RunAsync(cts.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        var buffer = new byte[1 << 16];
        var message = new MemoryStream();
        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            try
            {
                await ws.ConnectAsync(url, ct).ConfigureAwait(false);
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    message.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage) continue;
                    double receivedAt = wall.Elapsed.TotalMilliseconds;
                    try { Handle(message.GetBuffer().AsSpan(0, (int)message.Length), receivedAt); }
                    catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
                    {
                        Trace.WriteLine($"tosu: unexpected message: {e.Message}");
                    }
                    message.SetLength(0);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException) { }
            catch (IOException) { }

            message.SetLength(0);
            SetStatus(SourceStatus.Disconnected);
            try { await Task.Delay(1000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Handle(ReadOnlySpan<byte> json, double receivedAt)
    {
        var reader = new Utf8JsonReader(json);
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        var state = root.GetProperty("state");
        SetStatus(new SourceStatus(true, state.GetProperty("number").GetInt32(), state.GetProperty("name").GetString() ?? ""));

        var folders = root.GetProperty("folders");
        string game = folders.GetProperty("game").GetString() ?? "";
        string skinName = folders.GetProperty("skin").GetString() ?? "";
        bool defaultInEditor = root.TryGetProperty("settings", out var settings)
            && settings.TryGetProperty("skin", out var skinSettings)
            && skinSettings.TryGetProperty("useDefaultSkinInEditor", out var useDefault)
            && useDefault.ValueKind == JsonValueKind.True;
        string? skinFolder = defaultInEditor || game.Length == 0 || skinName.Length == 0 ? null : Path.Combine(game, "Skins", skinName);
        if (!skinKnown || skinFolder != skin)
        {
            skinKnown = true;
            skin = skinFolder;
            SkinChanged?.Invoke(skinFolder);
        }

        string songs = folders.GetProperty("songs").GetString() ?? "";
        var direct = root.GetProperty("directPath");
        string folder = direct.GetProperty("beatmapFolder").GetString() ?? "";
        string file = direct.GetProperty("beatmapFile").GetString() ?? "";
        if (folder.Length > 0 && file.Length > 0)
        {
            var info = new BeatmapInfo(Path.Combine(songs, folder), Path.GetFileName(file));
            if (info != beatmap)
            {
                beatmap = info;
                BeatmapChanged?.Invoke(info);
            }
        }

        double live = root.GetProperty("beatmap").GetProperty("time").GetProperty("live").GetDouble();
        bool paused = root.GetProperty("game").GetProperty("paused").GetBoolean();
        Sample?.Invoke(new TimeSample(live, receivedAt, paused));
    }

    private void SetStatus(SourceStatus s)
    {
        if (s == status) return;
        status = s;
        StatusChanged?.Invoke(s);
    }

    public void Dispose()
    {
        cts.Cancel();
        try { loop?.Wait(2000); } catch (AggregateException) { }
    }
}
