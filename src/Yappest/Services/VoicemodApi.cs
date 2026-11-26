using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yappest.Core;

namespace Yappest.Services;

public sealed record VmSound(string Id, string Name, string? ImageBase64);
public sealed record VmBoard(string Id, string Name, bool IsCustom, List<VmSound> Sounds);

/// <summary>
/// Client for Voicemod's documented local Control API (ws://localhost:&lt;port&gt;/v1, https://control-api.voicemod.net).
/// It needs the user's own free API key (Voicemod emails one on request) and a running Voicemod.
/// The API lists soundboards, sound names and images; it has no way to export audio files.
/// </summary>
public sealed class VoicemodApi : IDisposable
{
    static readonly int[] Ports = { 59129, 20000, 39273, 42152, 43782, 46667, 35679, 37170, 38501, 33952, 30546 };

    ClientWebSocket? _ws;
    int _id = 1;

    public string? Error { get; private set; }

    public async Task<bool> ConnectAsync(string clientKey, CancellationToken ct = default)
    {
        foreach (var port in Ports)
        {
            var ws = new ClientWebSocket();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(1500);
                await ws.ConnectAsync(new Uri($"ws://localhost:{port}/v1"), cts.Token);
            }
            catch { ws.Dispose(); continue; }

            _ws = ws;
            var reg = await RequestAsync("registerClient", new JsonObject { ["clientKey"] = clientKey }, "registerClient", ct);
            if (reg == null) { Error = "Voicemod didn't answer."; return false; }
            // the status arrives as {"code":401,"description":"Unauthorized"}, either nested under "code" or flat
            var code = FindCode(reg) ?? FindCode(reg["status"]) ?? "";
            if (code == "200") return true;
            Error = code == "401" ? "Voicemod rejected the API key." : code == "" ? "Voicemod gave an unexpected answer." : $"Voicemod answered {code}.";
            return false;
        }
        Error = "Voicemod isn't running (or its local API is switched off).";
        return false;
    }

    /// <summary>Dig a numeric status out of {"code":401,...} however deeply Voicemod nests it.</summary>
    static string? FindCode(JsonNode? n)
    {
        if (n is JsonObject o)
        {
            if (o["code"] is JsonNode c)
            {
                var inner = FindCode(c);
                if (inner != null) return inner;
            }
            return null;
        }
        return n is JsonValue ? n.ToString() : null;
    }

    public async Task<List<VmBoard>> GetSoundboardsAsync(CancellationToken ct = default)
    {
        var res = await RequestAsync("getAllSoundboard", new JsonObject(), "getAllSoundboard", ct);
        var list = new List<VmBoard>();
        if (res?["soundboards"] is not JsonArray boards) return list;
        foreach (var b in boards.OfType<JsonObject>())
        {
            var sounds = new List<VmSound>();
            foreach (var s in (b["sounds"] as JsonArray ?? new()).OfType<JsonObject>())
                sounds.Add(new VmSound(s["id"]?.ToString() ?? "", s["name"]?.ToString() ?? "", null));
            list.Add(new VmBoard(b["id"]?.ToString() ?? "", b["name"]?.ToString() ?? "Soundboard", b["isCustom"]?.GetValue<bool>() ?? false, sounds));
        }
        return list;
    }

    public async Task<byte[]?> GetBitmapAsync(string soundId, CancellationToken ct = default)
    {
        var res = await RequestAsync("getBitmap", new JsonObject { ["memeId"] = soundId }, "getBitmap", ct);
        var b64 = res?["result"]?["image"]?.ToString();
        if (string.IsNullOrEmpty(b64)) return null;
        try { return Convert.FromBase64String(b64); } catch { return null; }
    }

    /// <summary>Send one request, read frames until the matching response (or timeout). Returns its payload object.</summary>
    async Task<JsonObject?> RequestAsync(string action, JsonObject payload, string expectAction, CancellationToken ct)
    {
        if (_ws is not { State: WebSocketState.Open }) return null;
        var msg = new JsonObject { ["id"] = (_id++).ToString(), ["action"] = action, ["payload"] = payload };
        await _ws.SendAsync(Encoding.UTF8.GetBytes(msg.ToJsonString()), WebSocketMessageType.Text, true, ct);

        var deadline = DateTime.UtcNow.AddSeconds(8);
        var buf = new byte[1 << 16];
        while (DateTime.UtcNow < deadline)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            var sb = new StringBuilder();
            WebSocketReceiveResult r;
            try
            {
                do
                {
                    r = await _ws.ReceiveAsync(buf, cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) return null;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                } while (!r.EndOfMessage);
            }
            catch { return null; }

            JsonObject? obj;
            try { obj = JsonNode.Parse(sb.ToString())?.AsObject(); } catch { continue; }
            if (obj == null) continue;
            var type = (obj["actionType"] ?? obj["action"])?.ToString();
            if (!string.Equals(type, expectAction, StringComparison.OrdinalIgnoreCase)) continue;

            var body = obj["actionObject"] ?? obj["payload"];
            if (body is JsonValue v && v.TryGetValue<string>(out var s)) { try { body = JsonNode.Parse(s); } catch { } }
            var result = body as JsonObject ?? new JsonObject();
            // some builds put the status code next to the payload rather than inside it
            if (result["code"] == null && obj["code"] != null) result["code"] = obj["code"]!.DeepClone();
            if (result["code"] == null && obj["status"] != null) result["code"] = obj["status"]!.DeepClone();
            return result;
        }
        return null;
    }

    public void Dispose()
    {
        try { _ws?.Dispose(); } catch { }
    }
}
