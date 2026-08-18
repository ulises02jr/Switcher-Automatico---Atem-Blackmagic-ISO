using System.Text.Json;

namespace AutoSwitcher.App;

// Persistencia local (dispositivos y presets) en %APPDATA%\AutoSwitcher\config.json
public record Device(string Name, string Ip);

public class Preset
{
    public string Name { get; set; } = "";
    public int Interval { get; set; } = 10;
    public string Transition { get; set; } = "cut";
    public double Fade { get; set; } = 1.0;
    public List<long> Cameras { get; set; } = new();
}

public class AppConfig
{
    public string? LastIp { get; set; }
    public string Password { get; set; } = "";
    public List<Device> Devices { get; set; } = new();
    public List<Preset> Presets { get; set; } = new();
    public Dictionary<string, string> CameraNames { get; set; } = new();
}

public class Store
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoSwitcher");
    private static readonly string File_ = Path.Combine(Dir, "config.json");
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public AppConfig Config { get; private set; } = new();

    public Store() => Load();

    private void Load()
    {
        try
        {
            if (File.Exists(File_))
                Config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(File_)) ?? new();
        }
        catch { Config = new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(File_, JsonSerializer.Serialize(Config, Opts));
        }
        catch { }
    }

    public void RememberDevice(string name, string ip)
    {
        var existing = Config.Devices.FirstOrDefault(d => d.Ip == ip);
        string finalName = existing?.Name ?? name;
        Config.Devices.RemoveAll(d => d.Ip == ip);
        Config.Devices.Insert(0, new Device(finalName, ip));
        Config.LastIp = ip;
        Save();
    }
}
