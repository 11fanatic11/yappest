using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yappest.Audio;
using Yappest.Core;

namespace Yappest.Services;

/// <summary>
/// Local WebSocket the Yappest Stream Deck plugin connects to (127.0.0.1 only, same idea as Voicemod's local control API).
/// </summary>
public sealed class StreamDeckServer : IDisposable
{
    public static StreamDeckServer Instance { get; } = new();
    public static readonly int[] Ports = { 59712, 59713, 59714, 59715, 59716 };

    HttpListener? _http;
    CancellationTokenSource? _cts;
    readonly ConcurrentDictionary<WebSocket, SemaphoreSlim> _clients = new();
    Timer? _debounce;

    public int Port { get; private set; }
    public int ClientCount => _clients.Count;
    public event Action? ClientsChanged;

    public void Start()
    {
        if (_http != null) return;
        foreach (var port in Ports)
        {
            try
            {
                var l = new HttpListener();
                l.Prefixes.Add($"http://127.0.0.1:{port}/");
                l.Start();
                _http = l; Port = port;
                break;
            }
            catch (Exception ex) { Log.Warn($"streamdeck port {port}: {ex.Message}"); }
        }
        if (_http == null) { Log.Error("no free port for Stream Deck server"); return; }

        _cts = new CancellationTokenSource();
        _ = AcceptLoop(_cts.Token);

        Settings.Changed += OnSettingsChanged;
        SoundLibrary.Instance.Changed += () => Broadcast(SoundsMessage());
        AudioEngine.Instance.SoundStateChanged += (id, playing) =>
            Broadcast(new JsonObject { ["event"] = "soundPlaying", ["id"] = id, ["playing"] = playing });
        Controller.ClipCreated += c =>
            Broadcast(new JsonObject { ["event"] = "clip", ["seconds"] = c.DurationSeconds, ["game"] = c.Game });
        Log.Info("Stream Deck server on port " + Port);
    }

    void OnSettingsChanged()
    {
        _debounce?.Dispose();
        _debounce = new Timer(_ => Broadcast(StateMessage()), null, 60, Timeout.Infinite);
    }

    async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _http is { IsListening: true })
        {
            try
            {
                var ctx = await _http.GetContextAsync();
                if (!ctx.Request.IsWebSocketRequest || !IsLocal(ctx.Request))
                {
                    ctx.Response.StatusCode = ctx.Request.IsWebSocketRequest ? 403 : 200;
                    if (!ctx.Request.IsWebSocketRequest)
                    {
                        var b = Encoding.UTF8.GetBytes("Yappest control socket");
                        ctx.Response.OutputStream.Write(b);
                    }
                    ctx.Response.Close();
                    continue;
                }
                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                _ = HandleClient(wsCtx.WebSocket, ct);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { break; }
            catch (Exception ex) { Log.Warn("sd accept: " + ex.Message); }
        }
    }

    static bool IsLocal(HttpListenerRequest r)
    {
        if (r.RemoteEndPoint == null || !IPAddress.IsLoopback(r.RemoteEndPoint.Address)) return false;
        // refuse web pages (browsers always send an http(s) Origin); the Stream Deck plugin sends none or file://
        var origin = r.Headers["Origin"];
        return string.IsNullOrEmpty(origin) || !origin.StartsWith("http", StringComparison.OrdinalIgnoreCase);
    }

    async Task HandleClient(WebSocket ws, CancellationToken ct)
    {
        _clients[ws] = new SemaphoreSlim(1, 1);
        ClientsChanged?.Invoke();
        var buf = new byte[16 * 1024];
        try
        {
            await Send(ws, StateMessage());
            await Send(ws, SoundsMessage());
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf, ct);
                    if (r.MessageType == WebSocketMessageType.Close) goto done;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                } while (!r.EndOfMessage);
                try { Handle(ws, JsonNode.Parse(sb.ToString())?.AsObject()); }
                catch (Exception ex) { Log.Warn("sd message: " + ex.Message); }
            }
        done:;
        }
        catch (Exception ex) { Log.Info("sd client left: " + ex.Message); }
        finally
        {
            _clients.TryRemove(ws, out _);
            try { ws.Dispose(); } catch { }
            ClientsChanged?.Invoke();
        }
    }

    void Handle(WebSocket ws, JsonObject? msg)
    {
        if (msg == null) return;
        var action = msg["action"]?.GetValue<string>();
        switch (action)
        {
            case "getState": _ = Send(ws, StateMessage()); break;
            case "getSounds": _ = Send(ws, SoundsMessage()); break;
            case "playSound": Controller.PlaySound(msg["id"]?.GetValue<string>() ?? ""); break;
            case "stopSounds": Controller.StopSounds(); break;
            case "clip": Controller.Clip(); break;
            case "toggleMute": Controller.ToggleMute(); break;
            case "setMute": Controller.SetMute(msg["value"]?.GetValue<bool>() ?? false); break;
            case "toggleVoiceChanger": Controller.ToggleVoiceChanger(); break;
            case "selectEffect": Controller.SelectEffect(msg["id"]?.GetValue<string>() ?? ""); break;
            case "toggleHearMyself": Controller.ToggleHearMyself(); break;
        }
    }

    static JsonObject StateMessage()
    {
        var s = Settings.Current;
        return new JsonObject
        {
            ["event"] = "state",
            ["muted"] = s.MicMuted,
            ["voiceChanger"] = s.VoiceChangerEnabled,
            ["effect"] = s.ActiveEffect,
            ["effects"] = new JsonArray(EffectCatalog.All.Select(e => (JsonNode)new JsonObject { ["id"] = e.Id, ["name"] = e.Name }).ToArray()),
            ["hearMyself"] = s.HearMyself,
            ["clipSeconds"] = s.ClipSeconds,
            ["accent"] = s.AccentColor,
        };
    }

    static JsonObject SoundsMessage()
    {
        var boards = new JsonArray();
        foreach (var b in SoundLibrary.Instance.Boards.ToList())
        {
            var sounds = new JsonArray();
            foreach (var snd in b.Sounds.ToList())
                sounds.Add(new JsonObject { ["id"] = snd.Id, ["name"] = snd.Name, ["icon"] = ImageUtil.ToDataUrl(snd.Icon) });
            boards.Add(new JsonObject { ["id"] = b.Id, ["name"] = b.Name, ["sounds"] = sounds });
        }
        return new JsonObject { ["event"] = "sounds", ["boards"] = boards };
    }

    void Broadcast(JsonObject msg)
    {
        foreach (var ws in _clients.Keys.ToList()) _ = Send(ws, msg);
    }

    async Task Send(WebSocket ws, JsonObject msg)
    {
        if (!_clients.TryGetValue(ws, out var gate) || ws.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await gate.WaitAsync();
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
        catch { }
        finally { gate.Release(); }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _http?.Stop(); _http?.Close(); } catch { }
    }
}
