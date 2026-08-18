using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using AutoSwitcher.Core;
using AutoSwitcher.Atem;
using BMDSwitcherAPI;

namespace AutoSwitcher.App;

public record FoundAtem(string Name, string Ip);

class Snapshot
{
    public int Interval; public string Transition = "cut"; public double Fade = 1.0;
    public List<long> Available = new(); public bool Auto = true;
}

public class SwitcherService
{
    public readonly object Gate = new();
    private AtemDriver? _driver;
    private SwitcherBrain? _brain;
    private string? _lastIp;
    private Snapshot? _snap;
    private DateTime _lastTry = DateTime.MinValue;

    public bool AutoReconnect { get; set; } = true;
    public bool Reconnecting { get; private set; }
    public bool Connected { get { lock (Gate) return _brain != null; } }
    public AtemDriver? Driver => _driver;
    public SwitcherBrain? Brain => _brain;
    public string? CurrentIp => _lastIp;

    public void Connect(string ip)
    {
        var d = new AtemDriver();
        d.Connect(ip);
        lock (Gate)
        {
            _driver = d;
            _brain = new SwitcherBrain(d);
            if (_snap != null) { Apply(_brain, _snap); _snap = null; }
            _lastIp = ip;
            Reconnecting = false;
        }
    }

    public void Disconnect()
    {
        lock (Gate) { _brain = null; _driver = null; _snap = null; Reconnecting = false; }
    }

    // El loop llama esto: si el ATEM se cae, lo detecta y marca reconexion.
    public void Tick()
    {
        lock (Gate)
        {
            if (_brain == null) return;
            try { _brain.Tick(); }
            catch
            {
                _snap = Snap(_brain);
                _brain = null; _driver = null;
                Reconnecting = AutoReconnect && _lastIp != null;
            }
        }
    }

    // Intenta reconectar a la ultima IP (cada 3s), conservando la configuracion.
    public void MaybeReconnect()
    {
        if (!Reconnecting || _lastIp == null) return;
        if ((DateTime.Now - _lastTry).TotalSeconds < 3) return;
        _lastTry = DateTime.Now;
        try { Connect(_lastIp); } catch { }
    }

    private static Snapshot Snap(SwitcherBrain b) => new()
    {
        Interval = b.IntervalSeconds, Transition = b.TransitionMode,
        Fade = b.FadeDurationSeconds, Available = b.AvailableCameras.ToList(), Auto = b.AutoMode
    };

    private static void Apply(SwitcherBrain b, Snapshot s)
    {
        b.IntervalSeconds = s.Interval; b.TransitionMode = s.Transition;
        b.FadeDurationSeconds = s.Fade; b.AvailableCameras = s.Available.ToList();
        if (!s.Auto) b.Pause();
    }

    // --- Auto-descubrir ATEMs en la red local ---
    public List<FoundAtem> Discover()
    {
        var found = new List<FoundAtem>();
        string? local = GetLocalIPv4();
        if (local == null) return found;
        string prefix = local[..(local.LastIndexOf('.') + 1)];

        var live = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(1, 255, new ParallelOptions { MaxDegreeOfParallelism = 64 }, i =>
        {
            try { using var p = new Ping(); if (p.Send(prefix + i, 300).Status == IPStatus.Success) live.Add(prefix + i); }
            catch { }
        });

        foreach (var ip in live)
        {
            var name = ProbeAtem(ip, 1500);
            if (name != null) found.Add(new FoundAtem(name, ip));
        }
        return found;
    }

    private static string? ProbeAtem(string ip, int timeoutMs)
    {
        string? result = null;
        var t = new Thread(() =>
        {
            try
            {
                var disc = new CBMDSwitcherDiscovery();
                disc.ConnectTo(ip, out IBMDSwitcher sw, out _);
                sw.GetProductName(out string name);
                result = name;
                Marshal.ReleaseComObject(sw);
            }
            catch { }
        }) { IsBackground = true };
        t.Start();
        t.Join(timeoutMs);
        return result;
    }

    private static string? GetLocalIPv4()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(a))?.ToString();
        }
        catch { return null; }
    }
}
