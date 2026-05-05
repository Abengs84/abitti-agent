using System.IO;

namespace AbittiAgent.Tray;

internal static class ClientIdentity
{
    private static readonly object Gate = new();

    internal static string GetOrCreateClientId()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AbittiAgent");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "client-id.txt");

        lock (Gate)
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (!string.IsNullOrEmpty(existing))
                    return existing;
            }

            var id = Guid.NewGuid().ToString("N");
            File.WriteAllText(path, id);
            return id;
        }
    }
}
