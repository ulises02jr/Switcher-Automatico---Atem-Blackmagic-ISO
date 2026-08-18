using AutoSwitcher.App;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:5000");

var store = new Store();
var svc = new SwitcherService();
builder.Services.AddSingleton(store);
builder.Services.AddSingleton(svc);
builder.Services.AddHostedService(sp => new LoopService(svc));

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

string Token(string pw) => Convert.ToHexString(
    System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(pw)));

// Guardia de contraseña: protege /api/* para conexiones remotas (celulares).
// La app local (equipo) pasa sin contraseña.
app.Use(async (ctx, next) =>
{
    var pw = store.Config.Password;
    var path = ctx.Request.Path.Value ?? "";
    var ip = ctx.Connection.RemoteIpAddress;
    bool loopback = ip != null && (System.Net.IPAddress.IsLoopback(ip)
        || (ip.IsIPv4MappedToIPv6 && System.Net.IPAddress.IsLoopback(ip.MapToIPv4())));
    bool needsAuth = path.StartsWith("/api/") && path != "/api/login"
        && !string.IsNullOrEmpty(pw) && !loopback;
    if (needsAuth && ctx.Request.Cookies["auth"] != Token(pw))
    {
        ctx.Response.StatusCode = 401;
        return;
    }
    await next();
});

app.MapPost("/api/login", (HttpContext ctx, LoginReq r) =>
{
    if (r.password == store.Config.Password)
    {
        ctx.Response.Cookies.Append("auth", Token(r.password),
            new CookieOptions { SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromDays(30) });
        return Results.Ok(new { ok = true });
    }
    return Results.Json(new { ok = false }, statusCode: 401);
});

app.MapPost("/api/config/password", (PasswordReq r) =>
{
    store.Config.Password = r.password ?? "";
    store.Save();
    return Results.Ok();
});

// Auto-conectar si pasaron una IP por linea de comandos (opcional).
if (args.Length > 0)
{
    try { svc.Connect(args[0]); store.RememberDevice(svc.Driver!.ProductName, args[0]); }
    catch (Exception ex) { Console.WriteLine("Aviso: no se autoconecto: " + ex.Message); }
}

string Uptime(DateTime start)
{
    var s = (int)(DateTime.Now - start).TotalSeconds;
    int m = s / 60, h = m / 60;
    return h > 0 ? $"{h}h {m % 60}m" : $"{m}m";
}

object BuildStatus()
{
    lock (svc.Gate)
    {
        var b = svc.Brain;
        var baseInfo = new
        {
            devices = store.Config.Devices,
            presets = store.Config.Presets.Select(p => p.Name)
        };
        if (b == null || svc.Driver == null)
            return new { connected = false, reconnecting = svc.Reconnecting, hasPassword = !string.IsNullOrEmpty(store.Config.Password), baseInfo.devices, baseInfo.presets };

        return new
        {
            connected = true,
            reconnecting = false,
            hasPassword = !string.IsNullOrEmpty(store.Config.Password),
            product = svc.Driver.ProductName,
            ip = svc.CurrentIp,
            autoMode = b.AutoMode,
            interval = b.IntervalSeconds,
            transition = b.TransitionMode,
            fadeDuration = b.FadeDurationSeconds,
            current = b.CurrentCamera,
            count = b.SwitchCount,
            secondsUntilNext = b.AutoMode ? b.SecondsUntilNext : 0,
            uptime = Uptime(b.StartTime),
            available = b.AvailableCameras,
            cameras = svc.Driver.Cameras.Select(c => new {
                id = c.Id,
                name = store.Config.CameraNames.TryGetValue(c.Id.ToString(), out var cn)
                    && !string.IsNullOrWhiteSpace(cn) ? cn : c.Name
            }),
            baseInfo.devices,
            baseInfo.presets
        };
    }
}

app.MapGet("/api/status", () => Results.Json(BuildStatus()));

app.MapPost("/api/connect", (ConnectReq r) =>
{
    try
    {
        svc.Connect(r.ip);
        store.RememberDevice(svc.Driver!.ProductName, r.ip);
        return Results.Json(BuildStatus());
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/disconnect", () => { svc.Disconnect(); return Results.Ok(); });

app.MapGet("/api/discover", () => Results.Json(svc.Discover()));

app.MapPost("/api/cameras/rename", (RenameReq r) =>
{
    store.Config.CameraNames[r.id.ToString()] = r.name;
    store.Save();
    return Results.Ok();
});

// --- Dispositivos ---
app.MapPost("/api/devices", (Device d) =>
{
    store.Config.Devices.RemoveAll(x => x.Ip == d.Ip);
    store.Config.Devices.Insert(0, d);
    store.Save();
    return Results.Ok();
});

app.MapDelete("/api/devices/{ip}", (string ip) =>
{
    store.Config.Devices.RemoveAll(x => x.Ip == ip);
    store.Save();
    return Results.Ok();
});

// --- Presets ---
app.MapPost("/api/presets", (NameReq r) =>
{
    lock (svc.Gate)
    {
        var b = svc.Brain;
        if (b == null) return Results.BadRequest(new { error = "No conectado" });
        store.Config.Presets.RemoveAll(p => p.Name == r.name);
        store.Config.Presets.Add(new Preset
        {
            Name = r.name, Interval = b.IntervalSeconds, Transition = b.TransitionMode,
            Fade = b.FadeDurationSeconds, Cameras = b.AvailableCameras.ToList()
        });
    }
    store.Save();
    return Results.Ok();
});

app.MapPost("/api/presets/load", (NameReq r) =>
{
    var p = store.Config.Presets.FirstOrDefault(x => x.Name == r.name);
    if (p == null) return Results.NotFound();
    lock (svc.Gate)
    {
        var b = svc.Brain;
        if (b != null)
        {
            b.IntervalSeconds = p.Interval;
            b.TransitionMode = p.Transition;
            b.FadeDurationSeconds = p.Fade;
            b.AvailableCameras = p.Cameras.ToList();
        }
    }
    return Results.Ok();
});

app.MapDelete("/api/presets/{name}", (string name) =>
{
    store.Config.Presets.RemoveAll(p => p.Name == name);
    store.Save();
    return Results.Ok();
});

// --- Configuracion y control (solo si hay conexion) ---
void WithBrain(Action<AutoSwitcher.Core.SwitcherBrain> a)
{
    lock (svc.Gate) { if (svc.Brain != null) a(svc.Brain); }
}

app.MapPost("/api/config/interval", (IntervalReq r) =>
{ WithBrain(b => b.IntervalSeconds = Math.Clamp(r.interval, 3, 60)); return Results.Ok(); });

app.MapPost("/api/config/cameras", (CamerasReq r) =>
{
    WithBrain(b =>
    {
        var valid = svc.Driver!.Cameras.Select(c => c.Id).ToHashSet();
        b.AvailableCameras = r.cameras.Where(valid.Contains).OrderBy(x => x).ToList();
    });
    return Results.Ok();
});

app.MapPost("/api/config/transition", (TransitionReq r) =>
{ WithBrain(b => b.TransitionMode = r.mode == "mix" ? "mix" : "cut"); return Results.Ok(); });

app.MapPost("/api/config/fadetime", (FadeReq r) =>
{ WithBrain(b => b.FadeDurationSeconds = Math.Clamp(r.duration, 0.5, 3.0)); return Results.Ok(); });

app.MapPost("/api/pause", () => { WithBrain(b => b.Pause()); return Results.Ok(); });
app.MapPost("/api/resume", () => { WithBrain(b => b.Resume()); return Results.Ok(); });
app.MapPost("/api/force/{cam:long}", (long cam) =>
{ WithBrain(b => { if (b.AvailableCameras.Contains(cam)) b.ForceCamera(cam); }); return Results.Ok(); });

// Arranca el servidor en segundo plano (sin bloquear) y abre la ventana nativa.
await app.StartAsync();

var uiThread = new Thread(() =>
{
    Application.SetHighDpiMode(HighDpiMode.SystemAware);
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    Application.Run(new MainForm());
});
uiThread.SetApartmentState(ApartmentState.STA);
uiThread.Start();
uiThread.Join();

await app.StopAsync();

// --- Tipos de apoyo ---
record ConnectReq(string ip);
record LoginReq(string password);
record PasswordReq(string password);
record NameReq(string name);
record RenameReq(long id, string name);
record IntervalReq(int interval);
record CamerasReq(List<long> cameras);
record TransitionReq(string mode);
record FadeReq(double duration);

class LoopService : BackgroundService
{
    private readonly SwitcherService _svc;
    public LoopService(SwitcherService svc) { _svc = svc; }
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            _svc.Tick();
            _svc.MaybeReconnect();
            try { await Task.Delay(200, ct); } catch { }
        }
    }
}
