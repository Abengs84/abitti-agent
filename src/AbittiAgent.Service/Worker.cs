using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AbittiAgent.Service;

public sealed class Worker(IConfiguration configuration, ILogger<Worker> logger) : BackgroundService
{
    private readonly object _stateLock = new();
    private volatile bool _installRunning;
    private DateTimeOffset _lastInstallUtc = DateTimeOffset.MinValue;
    private string _lastInstallResult = string.Empty;
    private string _lastError = string.Empty;

    private readonly string _installerUrl =
        configuration["AbittiAgent:InstallerUrl"] ?? "https://dl.abitti.fi/AbittiCandidateInstaller.msi";
    private readonly string _localApiUrl =
        configuration["AbittiAgent:LocalApiUrl"] ?? "http://127.0.0.1:51881/";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("AbittiAgent.Service started.");
        using var listener = new HttpListener();
        listener.Prefixes.Add(_localApiUrl.TrimEnd('/') + "/");
        listener.Start();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(stoppingToken);
                _ = Task.Run(() => HandleRequestAsync(context, stoppingToken), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
        finally
        {
            listener.Stop();
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

    private object GetStatus()
    {
        lock (_stateLock)
        {
            return new
            {
                installRunning = _installRunning,
                lastInstallUtc = _lastInstallUtc,
                lastInstallResult = _lastInstallResult,
                lastError = _lastError
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
