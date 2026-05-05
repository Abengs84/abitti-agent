using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using AbittiAgent.Server;
using AbittiAgent.Shared;
using Forms = System.Windows.Forms;

const string DiscoveryRequest = "ABITTI_DISCOVER_REQUEST_V1";
const string DiscoveryResponsePrefix = "ABITTI_DISCOVER_RESPONSE_V1|";

var builder = WebApplication.CreateBuilder(args);

var urls = builder.Configuration["Urls"] ?? "http://127.0.0.1:5188";
builder.WebHost.UseUrls(urls);

builder.Services.AddSingleton<ClientStore>();
builder.Services.AddSingleton<CommandStore>();

var app = builder.Build();
var adminUrl = BuildLocalAdminUrl(urls);
var advertisedUrl = builder.Configuration["Discovery:AdvertisedUrl"] ?? BuildAdvertisedServerUrl(urls);
var discoveryEnabled = builder.Configuration.GetValue("Discovery:Enabled", true);
var discoveryPort = builder.Configuration.GetValue("Discovery:Port", 51880);
using var trayStopCts = new CancellationTokenSource();
var trayThread = new Thread(() => RunServerTray(adminUrl, app.Lifetime, trayStopCts.Token))
{
    IsBackground = true,
    Name = "AbittiAgentServerTray"
};
trayThread.SetApartmentState(ApartmentState.STA);
trayThread.Start();

app.Lifetime.ApplicationStopping.Register(() => trayStopCts.Cancel());

using var discoveryStopCts = new CancellationTokenSource();
if (discoveryEnabled)
{
    _ = Task.Run(() => RunDiscoveryResponderAsync(discoveryPort, advertisedUrl, discoveryStopCts.Token));
    app.Lifetime.ApplicationStopping.Register(() => discoveryStopCts.Cancel());
}

app.MapGet("/", (ClientStore s, CommandStore commands) =>
{
    static string Esc(string? value) => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
    var clients = s.ListOrderedByLastSeen();
    var nowUtc = DateTimeOffset.UtcNow;
    var staleAfter = TimeSpan.FromSeconds(45);

    var rows = string.Join("", clients.Select(c =>
    {
        var age = nowUtc - c.LastSeenUtc;
        var isStale = age > staleAfter;
        var statusClass = isStale ? "status-yellow" : "status-green";
        var statusText = isStale ? "Missed multiple heartbeats" : "Online";
        var statusTooltip = $"{statusText} | Last seen: {c.LastSeenUtc.ToLocalTime():yyyy-MM-dd HH:mm} | Last check: {c.LastCheckUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
        var idShort = c.ClientId.Length <= 12 ? c.ClientId : c.ClientId[..12] + "…";
        var agentShort = ShortAgentVersion(c.AgentVersion);
        var installResult = string.IsNullOrWhiteSpace(c.LastInstallResult) ? "-" : c.LastInstallResult;
        var lastError = string.IsNullOrWhiteSpace(c.LastError) ? "-" : c.LastError;
        var pendingCount = commands.GetPendingCount(c.ClientId);
        var cmdStatus = pendingCount > 0
            ? $"queued ({pendingCount})"
            : installResult.StartsWith("Running", StringComparison.OrdinalIgnoreCase)
                ? "running"
                : installResult.StartsWith("Success", StringComparison.OrdinalIgnoreCase)
                    ? "success"
                    : installResult.StartsWith("Failed", StringComparison.OrdinalIgnoreCase)
                        ? "fail"
                        : "idle";
        return $"""
        <tr>
          <td><span class="status-dot {statusClass}" title="{Esc(statusTooltip)}"></span>{Esc(idShort)}</td>
          <td>{Esc(c.Hostname)}</td>
          <td>{Esc(c.AbittiVersionInstalled)}</td>
          <td title="{Esc(c.AgentVersion)}">{Esc(agentShort)}</td>
          <td>{Esc(c.SourceIp)}</td>
          <td>{Esc(installResult)}</td>
          <td>{Esc(lastError)}</td>
          <td>{Esc(cmdStatus)}</td>
          <td class="actions-cell">
            <form method="post" action="/api/admin/clients/{Esc(c.ClientId)}/commands/check_now">
              <button type="submit">Check now</button>
            </form>
            <form method="post" action="/api/admin/clients/{Esc(c.ClientId)}/commands/install_now">
              <button type="submit">Install now</button>
            </form>
          </td>
        </tr>
        """;
    }));

    var html =
        "<html>\n" +
        "<head>\n" +
        "<meta charset=\"utf-8\"/>\n" +
        "<meta http-equiv=\"refresh\" content=\"10\" />\n" +
        "<title>Abitti Agent Admin</title>\n" +
        "<style>\n" +
        "body { font-family: system-ui, sans-serif; margin: 1.5rem; }\n" +
        "table { border-collapse: collapse; width: 100%; max-width: 1400px; }\n" +
        "th, td { border: 1px solid #ccc; padding: 8px; text-align: left; white-space: nowrap; }\n" +
        "th { background: #f4f4f4; }\n" +
        ".status-dot { display: inline-block; width: 10px; height: 10px; border-radius: 50%; margin-right: 8px; vertical-align: middle; }\n" +
        ".status-green { background: #16a34a; }\n" +
        ".status-yellow { background: #ca8a04; }\n" +
        ".actions-cell { display: flex; gap: 6px; align-items: center; }\n" +
        ".actions-cell form { margin: 0; }\n" +
        ".actions-cell button { min-width: 84px; }\n" +
        "</style>\n" +
        "</head>\n" +
        "<body>\n" +
        "<h1>Abitti Agent — Clients</h1>\n" +
        $"<p>Clients: <strong>{clients.Count}</strong> | Backend/API: <code>/api/admin/clients</code> — auto-refresh every 10s.</p>\n" +
        "<table>\n" +
        "<thead><tr>\n" +
        "<th>Client</th><th>Hostname</th><th>Abitti</th><th>Tray</th><th>Source IP</th><th>Install result</th><th>Last error</th><th>Cmd status</th><th>Actions</th>\n" +
        "</tr></thead>\n" +
        "<tbody>\n" +
        rows +
        "</tbody>\n" +
        "</table>\n" +
        "</body>\n</html>";

    return Results.Content(html, "text/html; charset=utf-8");
});

app.MapPost("/api/heartbeat", (ClientHeartbeat heartbeat, ClientStore s, HttpContext ctx) =>
{
    if (string.IsNullOrWhiteSpace(heartbeat.ClientId))
        return Results.BadRequest("ClientId required.");
    var sourceIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    s.Upsert(heartbeat, sourceIp);
    return Results.Ok(new ServerPolicy(360, "16:00", "06:00"));
});

app.MapGet("/api/admin/clients", (ClientStore s) => Results.Ok(s.ListOrderedByLastSeen()));
app.MapPost("/api/admin/clients/{clientId}/commands/{type}", (string clientId, string type, CommandStore commands) =>
{
    if (string.IsNullOrWhiteSpace(clientId))
        return Results.BadRequest("clientId required");

    var normalizedType = type.Trim().ToLowerInvariant();
    if (normalizedType is not ("check_now" or "install_now"))
        return Results.BadRequest("unsupported command type");

    commands.Enqueue(clientId, normalizedType);
    return Results.Redirect("/");
});
app.MapGet("/api/debug/ping", (HttpContext ctx) => Results.Ok(new
{
    service = "AbittiAgent.Server",
    utc = DateTimeOffset.UtcNow,
    remoteIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"
}));

app.MapGet("/api/commands/{clientId}", (string clientId, CommandStore commands) =>
{
    var list = commands.DequeueAvailable(clientId);
    return Results.Ok(new { commands = list });
});

app.Run();

static void RunServerTray(string adminUrl, IHostApplicationLifetime lifetime, CancellationToken stopToken)
{
    using var menu = new Forms.ContextMenuStrip();
    using var notifyIcon = new Forms.NotifyIcon
    {
        Visible = true,
        Icon = CreateBrandIcon(),
        Text = "Abitti Agent Server",
        ContextMenuStrip = menu
    };

    menu.Items.Add("Open Admin", null, (_, _) =>
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = adminUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            // ignore
        }
    });

    menu.Items.Add("Exit", null, (_, _) =>
    {
        lifetime.StopApplication();
        Forms.Application.ExitThread();
    });

    using var registration = stopToken.Register(Forms.Application.ExitThread);
    Forms.Application.Run();
    notifyIcon.Visible = false;
}

static Icon CreateBrandIcon()
{
    using var bitmap = new Bitmap(64, 64);
    using (var g = Graphics.FromImage(bitmap))
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.FromArgb(17, 24, 39));

        using var fontMain = new Font("Segoe UI", 42, FontStyle.Bold, GraphicsUnit.Pixel);
        using var fontSub = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.White);
        g.DrawString("A", fontMain, brush, new PointF(11f, 6f));
        g.DrawString("2", fontSub, brush, new PointF(34f, 3f));
    }

    var iconHandle = bitmap.GetHicon();
    try
    {
        using var tempIcon = Icon.FromHandle(iconHandle);
        return (Icon)tempIcon.Clone();
    }
    finally
    {
        DestroyIcon(iconHandle);
    }
}

[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
[return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
static extern bool DestroyIcon(IntPtr hIcon);

static string BuildAdvertisedServerUrl(string configuredUrls)
{
    var firstUrl = configuredUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
    if (firstUrl is null || !Uri.TryCreate(firstUrl, UriKind.Absolute, out var uri))
        return "http://127.0.0.1:5188";

    var ip = TryGetLocalIPv4() ?? "127.0.0.1";
    return $"{uri.Scheme}://{ip}:{uri.Port}";
}

static string BuildLocalAdminUrl(string configuredUrls)
{
    var firstUrl = configuredUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
    if (firstUrl is null || !Uri.TryCreate(firstUrl, UriKind.Absolute, out var uri))
        return "http://127.0.0.1:5188";

    var host = uri.Host;
    if (host == "0.0.0.0" || host == "+" || host == "*" || host == "::")
        host = "127.0.0.1";

    return $"{uri.Scheme}://{host}:{uri.Port}/";
}

static string? TryGetLocalIPv4()
{
    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            continue;

        var ip = ni.GetIPProperties().UnicastAddresses
            .FirstOrDefault(x => x.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
        if (ip is not null)
            return ip.ToString();
    }

    return null;
}

static string ShortAgentVersion(string version)
{
    if (string.IsNullOrWhiteSpace(version))
        return "-";

    var plusIdx = version.IndexOf('+');
    return plusIdx > 0 ? version[..plusIdx] : version;
}

static async Task RunDiscoveryResponderAsync(int port, string advertisedUrl, CancellationToken ct)
{
    using var udp = new UdpClient(port);
    var responsePayload = Encoding.UTF8.GetBytes(DiscoveryResponsePrefix + advertisedUrl.TrimEnd('/'));

    while (!ct.IsCancellationRequested)
    {
        UdpReceiveResult packet;
        try
        {
            packet = await udp.ReceiveAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch
        {
            continue;
        }

        var message = Encoding.UTF8.GetString(packet.Buffer);
        if (!string.Equals(message, DiscoveryRequest, StringComparison.Ordinal))
            continue;

        try
        {
            await udp.SendAsync(responsePayload, responsePayload.Length, packet.RemoteEndPoint).ConfigureAwait(false);
        }
        catch
        {
            // ignore individual failures
        }
    }
}
