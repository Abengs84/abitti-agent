using Microsoft.Win32;

namespace AbittiAgent.Tray;

internal static class AbittiVersionProbe
{
    internal static string TryReadInstalledVersion()
    {
        foreach (var root in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(root, view);
                    using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstall is null)
                        continue;

                    foreach (var name in uninstall.GetSubKeyNames())
                    {
                        using var sub = uninstall.OpenSubKey(name);
                        var displayName = sub?.GetValue("DisplayName") as string;
                        if (displayName is null || displayName.IndexOf("Abitti", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        var ver = sub!.GetValue("DisplayVersion") as string;
                        if (!string.IsNullOrWhiteSpace(ver))
                            return ver.Trim();
                    }
                }
                catch
                {
                    // ignore and continue
                }
            }
        }

        return "unknown";
    }
}
