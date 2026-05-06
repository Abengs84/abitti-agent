using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AbittiAgent.Service;

public sealed class Worker(IConfiguration configuration, ILogger<Worker> logger) : BackgroundService
{
    private readonly object _stateLock = new();
    private volatile bool _installRunning;
    private volatile bool _agentUpdateRunning;
    private DateTimeOffset _lastInstallUtc = DateTimeOffset.MinValue;
    private string _lastInstallResult = string.Empty;
    private string _lastError = string.Empty;
    private DateTimeOffset _lastAgentUpdateUtc = DateTimeOffset.MinValue;
    private string _lastAgentUpdateResult = string.Empty;
    private string _lastAgentUpdateError = string.Empty;

    private readonly string _installerUrl =
        configuration["AbittiAgent:InstallerUrl"] ?? "https://dl.abitti.fi/AbittiCandidateInstaller.msi";
    private readonly string _localApiUrl =
        configuration["AbittiAgent:LocalApiUrl"] ?? "http://127.0.0.1:38181/";
    private readonly string _githubOwner = configuration["AbittiAgent:UpdateRepoOwner"] ?? "Abengs84";
    private readonly string _githubRepo = configuration["AbittiAgent:UpdateRepoName"] ?? "abitti-agent";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("AbittiAgent.Service started.");
        var prefix = _localApiUrl.TrimEnd('/') + "/";
        while (!stoppingToken.IsCancellationRequested)
        {
            using var listener = new HttpListener();
            listener.Prefixes.Add(prefix);

            try
            {
                listener.Start();
                logger.LogInformation("Local API listening on {Prefix}", prefix);

                while (!stoppingToken.IsCancellationRequested)
                {
                    var context = await listener.GetContextAsync().WaitAsync(stoppingToken);
                    _ = Task.Run(() => HandleRequestAsync(context, stoppingToken), stoppingToken);
                }
            }
            catch (HttpListenerException ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Failed to bind local API on {Prefix}. Retrying in 10 seconds.", prefix);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // shutdown
                break;
            }
            finally
            {
                if (listener.IsListening)
                    listener.Stop();
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken stoppingToken)
    {
        var req = context.Request;
        var res = context.Response;
        res.ContentType = "application/json";
        try
        {
            if (req.Url is null)
            {
                res.StatusCode = 400;
                await WriteJsonAsync(res, new { error = "Invalid URL" });
                return;
            }

            if (req.Url.AbsolutePath.Equals("/status", StringComparison.OrdinalIgnoreCase))
            {
                var status = GetStatus();
                await WriteJsonAsync(res, status);
                return;
            }

            if (req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                req.Url.AbsolutePath.Equals("/install", StringComparison.OrdinalIgnoreCase))
            {
                if (_installRunning)
                {
                    await WriteJsonAsync(res, new { accepted = false, reason = "already-running" });
                    return;
                }

                _ = Task.Run(() => RunInstallAsync(stoppingToken), stoppingToken);
                await WriteJsonAsync(res, new { accepted = true });
                return;
            }

            if (req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                req.Url.AbsolutePath.Equals("/self-update", StringComparison.OrdinalIgnoreCase))
            {
                if (_agentUpdateRunning)
                {
                    await WriteJsonAsync(res, new { accepted = false, reason = "already-running" });
                    return;
                }

                _ = Task.Run(() => RunAgentSelfUpdateAsync(stoppingToken), stoppingToken);
                await WriteJsonAsync(res, new { accepted = true });
                return;
            }

            if (req.Url.AbsolutePath.Equals("/ping", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(res, new { service = "AbittiAgent.Service", utc = DateTimeOffset.UtcNow });
                return;
            }

            res.StatusCode = 404;
            await WriteJsonAsync(res, new { error = "Not found" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Local API request failed");
            res.StatusCode = 500;
            await WriteJsonAsync(res, new { error = ex.Message });
        }
        finally
        {
            res.OutputStream.Close();
        }
    }

    private async Task RunInstallAsync(CancellationToken stoppingToken)
    {
        lock (_stateLock)
        {
            _installRunning = true;
            _lastInstallUtc = DateTimeOffset.UtcNow;
            _lastInstallResult = "Running";
            _lastError = string.Empty;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "AbittiAgent");
        Directory.CreateDirectory(tempDir);
        var msiPath = Path.Combine(tempDir, "AbittiCandidateInstaller.msi");
        var logPath = Path.Combine(tempDir, "abitti-msi.log");

        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            await using (var src = await http.GetStreamAsync(_installerUrl, stoppingToken).ConfigureAwait(false))
            await using (var dst = File.Create(msiPath))
                await src.CopyToAsync(dst, stoppingToken).ConfigureAwait(false);

            var psi = new ProcessStartInfo
            {
                FileName = "msiexec.exe",
                Arguments = $"/i \"{msiPath}\" /qn /norestart /L*v \"{logPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
                throw new InvalidOperationException("Failed to start msiexec.");

            await process.WaitForExitAsync(stoppingToken).ConfigureAwait(false);

            lock (_stateLock)
            {
                _lastInstallUtc = DateTimeOffset.UtcNow;
                if (process.ExitCode == 0 || process.ExitCode == 3010)
                {
                    _lastInstallResult = process.ExitCode == 3010 ? "Success (reboot required)" : "Success";
                    _lastError = string.Empty;
                }
                else
                {
                    _lastInstallResult = $"Failed ({process.ExitCode})";
                    _lastError = $"msiexec exit code {process.ExitCode}";
                }
            }
        }
        catch (Exception ex)
        {
            lock (_stateLock)
            {
                _lastInstallUtc = DateTimeOffset.UtcNow;
                _lastInstallResult = "Failed";
                _lastError = ex.Message;
            }
            logger.LogError(ex, "Background install failed");
        }
        finally
        {
            _installRunning = false;
        }
    }

    private async Task RunAgentSelfUpdateAsync(CancellationToken stoppingToken)
    {
        lock (_stateLock)
        {
            _agentUpdateRunning = true;
            _lastAgentUpdateUtc = DateTimeOffset.UtcNow;
            _lastAgentUpdateResult = "Running";
            _lastAgentUpdateError = string.Empty;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "AbittiAgent");
        Directory.CreateDirectory(tempDir);
        var msiPath = Path.Combine(tempDir, "AbittiAgent-latest.msi");
        var logPath = Path.Combine(tempDir, "abitti-agent-self-update.log");
        var taskName = "AbittiAgentSelfUpdate";
        var cmdPath = Path.Combine(tempDir, "abitti-agent-self-update.cmd");

        try
        {
            var downloadUrl = await ResolveLatestAgentMsiUrlAsync(stoppingToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(downloadUrl))
                throw new InvalidOperationException("No matching MSI asset found in latest GitHub release.");

            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            await using (var src = await http.GetStreamAsync(downloadUrl, stoppingToken).ConfigureAwait(false))
            await using (var dst = File.Create(msiPath))
                await src.CopyToAsync(dst, stoppingToken).ConfigureAwait(false);

            await File.WriteAllTextAsync(
                cmdPath,
                "@echo off\r\n" +
                $"msiexec.exe /i \"{msiPath}\" /qn /norestart /L*v \"{logPath}\"\r\n" +
                "exit /b %errorlevel%\r\n",
                stoppingToken).ConfigureAwait(false);

            await ScheduleSelfUpdateTaskAsync(taskName, cmdPath, stoppingToken).ConfigureAwait(false);
            await RunScheduledTaskAsync(taskName, stoppingToken).ConfigureAwait(false);

            lock (_stateLock)
            {
                _lastAgentUpdateUtc = DateTimeOffset.UtcNow;
                _lastAgentUpdateResult = "Scheduled";
                _lastAgentUpdateError = string.Empty;
            }
        }
        catch (Exception ex)
        {
            lock (_stateLock)
            {
                _lastAgentUpdateUtc = DateTimeOffset.UtcNow;
                _lastAgentUpdateResult = "Failed";
                _lastAgentUpdateError = ex.Message;
            }
            logger.LogError(ex, "Agent self-update failed");
        }
        finally
        {
            _agentUpdateRunning = false;
        }
    }

    private static async Task RunScheduledTaskAsync(string taskName, CancellationToken ct)
    {
        try
        {
            var (code, stdout, stderr) = await RunProcessCaptureAsync(
                "schtasks.exe",
                $"/Run /TN \"{taskName}\"",
                ct).ConfigureAwait(false);

            if (code != 0)
                throw new InvalidOperationException($"schtasks /Run failed (exit {code}). {stdout} {stderr}".Trim());
        }
        finally
        {
            // Best-effort cleanup. Avoid using /Z because it requires an EndBoundary in the underlying XML on some systems.
            try
            {
                await RunProcessCaptureAsync(
                    "schtasks.exe",
                    $"/Delete /F /TN \"{taskName}\"",
                    ct).ConfigureAwait(false);
            }
            catch
            {
                // ignore cleanup failures
            }
        }
    }

    private static async Task ScheduleSelfUpdateTaskAsync(string taskName, string cmdPath, CancellationToken ct)
    {
        // Schedule at least 2 minutes ahead to avoid "start time is earlier than current time" edge cases.
        var startLocal = DateTimeOffset.Now.AddMinutes(2);
        var st = startLocal.ToString("HH:mm", CultureInfo.InvariantCulture);
        var sd = startLocal.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);

        var tr = $"cmd.exe /c \"\\\"{cmdPath}\\\"\"";

        var (code, stdout, stderr) = await RunProcessCaptureAsync(
            "schtasks.exe",
            $"/Create /F /TN \"{taskName}\" /SC ONCE /SD {sd} /ST {st} /RL HIGHEST /RU SYSTEM /TR \"{tr}\"",
            ct).ConfigureAwait(false);

        if (code != 0)
            throw new InvalidOperationException($"schtasks /Create failed (exit {code}). {stdout} {stderr}".Trim());
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessCaptureAsync(string fileName, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var p = Process.Start(psi);
        if (p is null)
            throw new InvalidOperationException($"Failed to start {fileName}.");

        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return (p.ExitCode, stdout.Trim(), stderr.Trim());
    }

    private async Task<string?> ResolveLatestAgentMsiUrlAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AbittiAgentService", "1.0"));

        var apiUrl = $"https://api.github.com/repos/{_githubOwner}/{_githubRepo}/releases/latest";
        using var stream = await http.GetStreamAsync(apiUrl, ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
                continue;
            var name = nameEl.GetString();
            if (string.IsNullOrWhiteSpace(name))
                continue;
            if (!name.StartsWith("AbittiAgent-", StringComparison.OrdinalIgnoreCase) || !name.EndsWith("-win-x64.msi", StringComparison.OrdinalIgnoreCase))
                continue;

            if (asset.TryGetProperty("browser_download_url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
                return urlEl.GetString();
        }

        return null;
    }

    private object GetStatus()
    {
        lock (_stateLock)
        {
            return new
            {
                installRunning = _installRunning,
                lastInstallUtc = _lastInstallUtc,
                lastInstallResult = _lastInstallResult,
                lastError = _lastError,
                agentUpdateRunning = _agentUpdateRunning,
                lastAgentUpdateUtc = _lastAgentUpdateUtc,
                lastAgentUpdateResult = _lastAgentUpdateResult,
                lastAgentUpdateError = _lastAgentUpdateError
            };
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        response.StatusCode = response.StatusCode == 0 ? 200 : response.StatusCode;
        await response.OutputStream.WriteAsync(bytes);
    }
}
