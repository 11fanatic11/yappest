using DiscordRPC;
using Yappest.Core;

namespace Yappest.Services;

/// <summary>
/// Rich Presence like Medal's: game icon large, Yappest icon small in the corner,
/// the game as the title and "Clipping voice in {game} with Yappest" underneath.
/// Needs a Discord application ID (free, made once at discord.com/developers) with an art asset named "yappest".
/// </summary>
public sealed class DiscordService : IDisposable
{
    public static DiscordService Instance { get; } = new();

    DiscordRpcClient? _client;
    string? _clientId;
    DateTime _since = DateTime.UtcNow;
    string _status = "Off";

    public string Status => _status;
    public event Action? StatusChanged;

    public void Init()
    {
        GameDetector.Instance.GameChanged += _ => System.Windows.Application.Current.Dispatcher.BeginInvoke(Refresh);
        Settings.Changed += () => System.Windows.Application.Current.Dispatcher.BeginInvoke(Refresh);
        Refresh();
    }

    void SetStatus(string s) { if (_status != s) { _status = s; StatusChanged?.Invoke(); } }

    public void Refresh()
    {
        var s = Settings.Current;
        try
        {
            if (!s.DiscordRpc) { Shutdown(); SetStatus("Off"); return; }
            if (string.IsNullOrWhiteSpace(s.DiscordClientId)) { Shutdown(); SetStatus("Needs a Discord application ID"); return; }

            if (_client == null || _clientId != s.DiscordClientId)
            {
                Shutdown();
                _clientId = s.DiscordClientId.Trim();
                _client = new DiscordRpcClient(_clientId);
                _client.OnReady += (_, _) => SetStatus("Connected to Discord");
                _client.OnConnectionFailed += (_, _) => SetStatus("Discord isn't running");
                _client.Initialize();
                SetStatus("Connecting…");
            }

            var game = GameDetector.Instance.Current;
            if (game == null)
            {
                _client.ClearPresence();
                if (_status.StartsWith("Connected")) SetStatus("Connected · waiting for a game");
                return;
            }

            _client.SetPresence(new RichPresence
            {
                Details = game.Name,
                State = $"Clipping voice in {game.Name} with Yappest",
                Timestamps = new Timestamps(_since),
                Assets = new Assets
                {
                    LargeImageKey = game.IconUrl ?? "yappest",
                    LargeImageText = game.Name,
                    SmallImageKey = "yappest",
                    SmallImageText = "Yappest",
                },
            });
            SetStatus($"Showing {game.Name}");
        }
        catch (Exception ex)
        {
            Log.Error("discord rpc", ex);
            SetStatus("Error: " + ex.Message);
        }
    }

    void Shutdown()
    {
        try { _client?.ClearPresence(); _client?.Dispose(); } catch { }
        _client = null; _clientId = null;
    }

    public void Dispose() => Shutdown();
}
