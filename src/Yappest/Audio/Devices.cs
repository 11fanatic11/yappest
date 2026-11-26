using NAudio.CoreAudioApi;

namespace Yappest.Audio;

public sealed record DeviceInfo(string Id, string Name, bool IsDefault)
{
    public override string ToString() => IsDefault ? Name + "  (default)" : Name;
}

public static class Devices
{
    /// <summary>VB-Cable endpoints: render side is "CABLE Input", capture side "CABLE Output" (renamed to Yappest Input).</summary>
    public static bool IsVirtualCable(string name) =>
        (name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) && name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase))
        || name.Contains("Yappest Input", StringComparison.OrdinalIgnoreCase);

    /// <summary>Software devices (Voicemod, Elgato mixers, VR, cables) make poor defaults for a real mic or headphones.</summary>
    public static bool IsSoftwareDevice(string name) =>
        new[] { "Voicemod", "Virtual", "VB-Audio", "Stream Mix", "Chat Mix", "Personal Mix", "Dummy", "Yappest" }
            .Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));

    public static DeviceInfo? PickDefault(IEnumerable<DeviceInfo> items)
    {
        var list = items.ToList();
        return list.FirstOrDefault(d => d.IsDefault && !IsSoftwareDevice(d.Name))
            ?? list.FirstOrDefault(d => !IsSoftwareDevice(d.Name))
            ?? list.FirstOrDefault(d => d.IsDefault)
            ?? list.FirstOrDefault();
    }

    public static List<DeviceInfo> List(DataFlow flow, bool hideVirtualCable)
    {
        using var en = new MMDeviceEnumerator();
        string? defId = null;
        try { defId = en.GetDefaultAudioEndpoint(flow, Role.Console).ID; } catch { }
        var list = new List<DeviceInfo>();
        foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            string name;
            try { name = d.FriendlyName; } catch { continue; }
            if (hideVirtualCable && IsVirtualCable(name)) continue;
            list.Add(new DeviceInfo(d.ID, name, d.ID == defId));
            d.Dispose();
        }
        return list;
    }

    public static MMDevice? Get(DataFlow flow, string? id)
    {
        using var en = new MMDeviceEnumerator();
        try
        {
            if (!string.IsNullOrEmpty(id))
            {
                var d = en.GetDevice(id);
                if (d.State == DeviceState.Active) return d;
            }
        }
        catch { }
        try { return en.GetDefaultAudioEndpoint(flow, Role.Console); } catch { return null; }
    }

    /// <summary>The virtual cable's playback side ("CABLE Input"). We write mic + sounds into it.</summary>
    public static MMDevice? FindCableRender()
    {
        using var en = new MMDeviceEnumerator();
        MMDevice? best = null;
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            var n = d.FriendlyName;
            if (n.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) && !n.Contains("16ch", StringComparison.OrdinalIgnoreCase)
                && !n.Contains("(Elgato", StringComparison.OrdinalIgnoreCase))
            { best = d; break; }
        }
        return best;
    }

    /// <summary>The virtual cable's recording side: what apps see as "Yappest Input" once renamed.</summary>
    public static MMDevice? FindCableCapture()
    {
        using var en = new MMDeviceEnumerator();
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            var n = d.FriendlyName;
            if ((n.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase) || n.Contains("Yappest Input", StringComparison.OrdinalIgnoreCase))
                && !n.Contains("16ch", StringComparison.OrdinalIgnoreCase) && !n.Contains("(Elgato", StringComparison.OrdinalIgnoreCase))
                return d;
        }
        return null;
    }
}
