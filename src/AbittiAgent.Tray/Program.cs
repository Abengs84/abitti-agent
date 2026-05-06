using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using AbittiAgent.Shared;
using Microsoft.Extensions.Configuration;

namespace AbittiAgent.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var singleInstanceMutex = new Mutex(true, @"Global\AbittiAgent.Tray", out var isPrimaryInstance);
        if (!isPrimaryInstance)
            return;

        ApplicationConfiguration.Initialize();
        using var context = new TrayApplicationContext();
        Application.Run(context);
    }
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string DiscoveryRequest = "ABITTI_DISCOVER_REQUEST_V1";
    private const string DiscoveryResponsePrefix = "ABITTI_DISCOVER_RESPONSE_V1|";
    private const string LocalServiceBaseUrlDefault = "http://127.0.0.1:38181";

    private readonly NotifyIcon _notifyIcon;
    private readonly CancellationTokenSource _cts = new();
    private readonly PeriodicTimer _loopTimer;
    private readonly SynchronizationContext? _syncContext;
    private readonly Task _mainLoop;
    private readonly string _configuredServerUrl;
    private readonly int _discoveryPort;
    private readonly TimeSpan _discoveryTimeout;
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _offlineRetryInterval;
    private readonly TimeSpan _commandPollInterval;
    private readonly string _localServiceBaseUrl;
    private readonly string _agentVersion;
    private DateTimeOffset _lastHeartbeatSentUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastCommandPollUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastInstallUtc = DateTimeOffset.MinValue;
    private string _lastInstallResult = string.Empty;
    private string _lastError = string.Empty;
    private bool _hasObservedInstallState;
    private bool _lastInstallRunning;
    private string? _serverUrl;

    internal TrayApplicationContext()
    {
        _syncContext = SynchronizationContext.Current;

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables(prefix: "ABITTIAGENT_")
            .Build();

        _configuredServerUrl = configuration["AbittiAgent:ServerBaseUrl"] ?? "auto";
        var heartbeatMinutes = Math.Max(1, configuration.GetValue("AbittiAgent:HeartbeatMinutes", 10));
        var heartbeatHoursFallback = Math.Max(1, configuration.GetValue("AbittiAgent:HeartbeatHours", 6));
        if (configuration["AbittiAgent:HeartbeatMinutes"] is null)
            heartbeatMinutes = heartbeatHoursFallback * 60;
        var offlineRetrySeconds = Math.Max(5, configuration.GetValue("AbittiAgent:OfflineRetrySeconds", 30));
        var commandPollSeconds = Math.Max(10, configuration.GetValue("AbittiAgent:CommandPollSeconds", 30));
        _discoveryPort = Math.Max(1, configuration.GetValue("AbittiAgent:DiscoveryPort", 51880));
        _discoveryTimeout = TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue("AbittiAgent:DiscoveryTimeoutSeconds", 3)));
        _heartbeatInterval = TimeSpan.FromMinutes(heartbeatMinutes);
        _offlineRetryInterval = TimeSpan.FromSeconds(offlineRetrySeconds);
        _commandPollInterval = TimeSpan.FromSeconds(commandPollSeconds);
        _localServiceBaseUrl = configuration["AbittiAgent:LocalServiceBaseUrl"] ?? LocalServiceBaseUrlDefault;
        _agentVersion = GetAgentVersion();
        _loopTimer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        var menu = new ContextMenuStrip();
        menu.Items.Add("Update agent", null, async (_, _) =>
        {
            var accepted = await RequestServiceSelfUpdateAsync(CancellationToken.None).ConfigureAwait(false);
            if (accepted)
            {
                ShowNotification("Abitti Agent", "Updating… tray will restart shortly", ToolTipIcon.Info);
                TryScheduleSelfRestart(TimeSpan.FromSeconds(90));
                ExitThread();
            }
            else
                ShowNotification("Abitti Agent", "Agent self-update could not be started", ToolTipIcon.Warning);
        });
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            Icon = CreateBrandIcon(),
            Text = "Abitti Agent",
            ContextMenuStrip = menu
        };

        _mainLoop = Task.Run(() => RunMainLoopAsync(_cts.Token), _cts.Token);
    }

    protected override void ExitThreadCore()
    {
        UpdateTrayStatus("Shutting down...");
        _cts.Cancel();
        _loopTimer.Dispose();
        try
        {
            _mainLoop.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // ignore
        }

        _notifyIcon.Visible = false;
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.Dispose();

        _cts.Dispose();
        base.ExitThreadCore();
    }

    private async Task RunMainLoopAsync(CancellationToken token)
    {
        try
        {
            while (await _loopTimer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (_serverUrl is null)
                {
                    UpdateTrayStatus("Discovering server...");
                    _serverUrl = await ResolveServerUrlAsync(_configuredServerUrl, _discoveryPort, _discoveryTimeout, token).ConfigureAwait(false);
                    if (_serverUrl is null)
                    {
                        UpdateTrayStatus("No server found");
                        continue;
                    }

                    _lastHeartbeatSentUtc = DateTimeOffset.MinValue;
                    _lastCommandPollUtc = DateTimeOffset.MinValue;
                }

                var now = DateTimeOffset.UtcNow;
                var dueHeartbeat = now - _lastHeartbeatSentUtc >= _heartbeatInterval;
                var dueCommandPoll = now - _lastCommandPollUtc >= _commandPollInterval;
                if (!dueHeartbeat && !dueCommandPoll)
                    continue;

                var heartbeatOk = await SendHeartbeatAsync(_serverUrl, token).ConfigureAwait(false);
                if (!heartbeatOk)
                {
                    UpdateTrayStatus("Disconnected");
                    _serverUrl = null;
                    await Task.Delay(_offlineRetryInterval, token).ConfigureAwait(false);
                    continue;
                }

                _lastHeartbeatSentUtc = now;
                UpdateTrayStatus("Connected: " + ShortServer(_serverUrl));
                await RefreshInstallStateFromServiceAsync(token).ConfigureAwait(false);

                if (!dueCommandPoll)
                    continue;

                _lastCommandPollUtc = now;
                var commands = await FetchCommandsAsync(_serverUrl, token).ConfigureAwait(false);
                foreach (var command in commands)
                    await ExecuteCommandAsync(command, _serverUrl, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private async Task<IReadOnlyList<AgentCommand>> FetchCommandsAsync(string serverUrl, CancellationToken ct)
    {
        var baseUri = serverUrl.TrimEnd('/') + "/";
        using var http = new HttpClient { BaseAddress = new Uri(baseUri), Timeout = TimeSpan.FromSeconds(15) };
        var clientId = ClientIdentity.GetOrCreateClientId();
        try
        {
            var result = await http.GetFromJsonAsync<CommandResponse>($"api/commands/{clientId}", ct).ConfigureAwait(false);
            return result?.Commands ?? new List<AgentCommand>();
        }
        catch
        {
            return Array.Empty<AgentCommand>();
        }
    }

    private async Task ExecuteCommandAsync(AgentCommand command, string serverUrl, CancellationToken ct)
    {
        switch (command.Type.Trim().ToLowerInvariant())
        {
            case "check_now":
                _lastHeartbeatSentUtc = DateTimeOffset.MinValue;
                await SendHeartbeatAsync(serverUrl, ct).ConfigureAwait(false);
                break;
            case "install_now":
                ShowNotification("Abitti Agent", "Queueing background installation", ToolTipIcon.Info);
                _lastInstallUtc = DateTimeOffset.UtcNow;
                _lastInstallResult = "Running";
                _lastError = string.Empty;
                _lastHeartbeatSentUtc = DateTimeOffset.MinValue;
                await SendHeartbeatAsync(serverUrl, ct).ConfigureAwait(false);
                var queued = await RequestServiceInstallAsync(ct).ConfigureAwait(false);
                if (!queued)
                {
                    _lastInstallUtc = DateTimeOffset.UtcNow;
                    _lastInstallResult = "Failed";
                    if (string.IsNullOrWhiteSpace(_lastError))
                        _lastError = "Local service unavailable";
                    ShowNotification("Abitti Agent", "Service unavailable for background install", ToolTipIcon.Error);
                }
                _lastHeartbeatSentUtc = DateTimeOffset.MinValue;
                await SendHeartbeatAsync(serverUrl, ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task<bool> RequestServiceInstallAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient
            {
                BaseAddress = new Uri(_localServiceBaseUrl.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(10)
            };
            using var response = await http.PostAsync("install", null, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<InstallRequestResult>(cancellationToken: ct).ConfigureAwait(false);
            var accepted = result?.Accepted ?? false;
            if (!accepted)
            {
                _lastError = "Install already running";
                return false;
            }

            _lastError = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            return false;
        }
    }

    private async Task<bool> RequestServiceSelfUpdateAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient
            {
                BaseAddress = new Uri(_localServiceBaseUrl.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(10)
            };
            using var response = await http.PostAsync("self-update", null, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<InstallRequestResult>(cancellationToken: ct).ConfigureAwait(false);
            return result?.Accepted ?? false;
        }
        catch
        {
            return false;
        }
    }

    private static void TryScheduleSelfRestart(TimeSpan delay)
    {
        try
        {
            var exePath = Application.ExecutablePath;
            var seconds = Math.Max(10, (int)delay.TotalSeconds);

            // Use a detached cmd process that waits and relaunches the tray.
            var cmd = $"ping 127.0.0.1 -n {seconds} >nul & start \"\" \"{exePath}\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{cmd}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch
        {
            // If restart scheduling fails, user can start tray manually.
        }
    }

    private async Task RefreshInstallStateFromServiceAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient
            {
                BaseAddress = new Uri(_localServiceBaseUrl.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(5)
            };
            var status = await http.GetFromJsonAsync<ServiceInstallStatus>("status", ct).ConfigureAwait(false);
            if (status is null)
                return;

            _lastInstallUtc = status.LastInstallUtc;
            _lastInstallResult = status.LastInstallResult ?? string.Empty;
            _lastError = status.LastError ?? string.Empty;

            if (!_hasObservedInstallState)
            {
                _lastInstallRunning = status.InstallRunning;
                _hasObservedInstallState = true;
                return;
            }

            if (status.InstallRunning && !_lastInstallRunning)
            {
                ShowNotification("Abitti Agent", "Abitti2 installation started", ToolTipIcon.Info);
            }
            else if (!status.InstallRunning && _lastInstallRunning)
            {
                var successful = string.Equals(_lastInstallResult, "success", StringComparison.OrdinalIgnoreCase);
                if (successful)
                    ShowNotification("Abitti Agent", "Abitti2 installation completed", ToolTipIcon.Info);
                else
                    ShowNotification("Abitti Agent", "Abitti2 installation failed", ToolTipIcon.Error);
            }

            _lastInstallRunning = status.InstallRunning;
        }
        catch
        {
            // local service may not be available
        }
    }

    private void UpdateTrayStatus(string status)
    {
        var text = $"Abitti Agent {_agentVersion} - {status}";
        if (text.Length > 63)
            text = text[..63];

        void SetText()
        {
            try { _notifyIcon.Text = text; } catch { }
        }

        if (_syncContext is null)
            SetText();
        else
            _syncContext.Post(_ => SetText(), null);
    }

    private static bool IsAutoDiscovery(string? configuredServerUrl) =>
        string.IsNullOrWhiteSpace(configuredServerUrl) ||
        string.Equals(configuredServerUrl, "auto", StringComparison.OrdinalIgnoreCase);

    private static async Task<string?> ResolveServerUrlAsync(string? configuredServerUrl, int discoveryPort, TimeSpan timeout, CancellationToken ct)
    {
        if (!IsAutoDiscovery(configuredServerUrl))
            return configuredServerUrl!.TrimEnd('/');

        try
        {
            return await DiscoverServerUrlAsync(discoveryPort, timeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Discovery failed: " + ex.Message);
            return null;
        }
    }

    private static async Task<string?> DiscoverServerUrlAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        using var udp = new UdpClient();
        udp.EnableBroadcast = true;
        var payload = Encoding.UTF8.GetBytes(DiscoveryRequest);
        await udp.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Broadcast, port)).ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        while (!timeoutCts.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await udp.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var msg = Encoding.UTF8.GetString(received.Buffer);
            if (!msg.StartsWith(DiscoveryResponsePrefix, StringComparison.Ordinal))
                continue;

            var url = msg[DiscoveryResponsePrefix.Length..].Trim().TrimEnd('/');
            if (Uri.TryCreate(url, UriKind.Absolute, out _))
                return url;
        }

        return null;
    }

    private async Task<bool> SendHeartbeatAsync(string serverBaseUrl, CancellationToken ct)
    {
        var baseUri = serverBaseUrl.TrimEnd('/') + "/";
        using var http = new HttpClient { BaseAddress = new Uri(baseUri), Timeout = TimeSpan.FromSeconds(30) };

        var heartbeat = new ClientHeartbeat(
            ClientIdentity.GetOrCreateClientId(),
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            _agentVersion,
            AbittiVersionProbe.TryReadInstalledVersion(),
            DateTimeOffset.UtcNow,
            _lastInstallUtc,
            _lastInstallResult,
            _lastError,
            IsSystemPendingReboot());

        try
        {
            using var response = await http.PostAsJsonAsync("api/heartbeat", heartbeat, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Heartbeat failed: " + ex.Message);
            return false;
        }
    }

    private void ShowNotification(string title, string message, ToolTipIcon icon)
    {
        void Show()
        {
            try
            {
                _notifyIcon.BalloonTipTitle = title;
                _notifyIcon.BalloonTipText = Truncate(message, 200);
                _notifyIcon.BalloonTipIcon = icon;
                _notifyIcon.ShowBalloonTip(4000);
            }
            catch
            {
                // ignore
            }
        }

        if (_syncContext is null)
            Show();
        else
            _syncContext.Post(_ => Show(), null);
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
            return value;
        return value[..max];
    }

    private static string ShortServer(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
            return serverUrl;

        var shortText = $"{uri.Host}:{uri.Port}";
        return shortText.Length > 32 ? shortText[..32] : shortText;
    }

    private static string GetAgentVersion()
    {
        return Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "0.0.0";
    }

    private static bool IsSystemPendingReboot()
    {
        try
        {
            using var sessionKey =
                Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            if (sessionKey is not null)
                return true;

            using var updatesKey =
                Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            return updatesKey is not null && updatesKey.SubKeyCount > 0;
        }
        catch
        {
            return false;
        }
    }

    private static Icon CreateBrandIcon()
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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private sealed class CommandResponse
    {
        public List<AgentCommand> Commands { get; set; } = new();
    }

    private sealed class InstallRequestResult
    {
        public bool Accepted { get; set; }
    }

    private sealed class ServiceInstallStatus
    {
        public bool InstallRunning { get; set; }
        public DateTimeOffset LastInstallUtc { get; set; }
        public string? LastInstallResult { get; set; }
        public string? LastError { get; set; }
    }
}
