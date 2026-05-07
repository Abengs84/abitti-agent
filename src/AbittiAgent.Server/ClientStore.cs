using System.Collections.Concurrent;
using AbittiAgent.Shared;

namespace AbittiAgent.Server;

public sealed class ClientStore
{
    private readonly ConcurrentDictionary<string, StoredClient> _byId = new();

    public void Upsert(ClientHeartbeat heartbeat, string sourceIp)
    {
        var seen = DateTimeOffset.UtcNow;

        // If the same machine reports with a new client ID (e.g. after reinstall/update),
        // keep only the latest identity row in admin UI.
        var duplicates = _byId
            .Where(kvp =>
                kvp.Key != heartbeat.ClientId &&
                string.Equals(kvp.Value.Heartbeat.Hostname, heartbeat.Hostname, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(kvp.Value.SourceIp, sourceIp, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var duplicateId in duplicates)
            _byId.TryRemove(duplicateId, out _);

        _byId[heartbeat.ClientId] = new StoredClient(heartbeat, sourceIp, seen);
    }

    public int PurgeOffline(TimeSpan olderThan)
    {
        var now = DateTimeOffset.UtcNow;
        var removed = 0;
        foreach (var kvp in _byId)
        {
            if (now - kvp.Value.LastSeenUtc <= olderThan)
                continue;
            if (_byId.TryRemove(kvp.Key, out _))
                removed++;
        }

        return removed;
    }

    public IReadOnlyList<ClientSummary> ListOrderedByLastSeen()
    {
        return _byId.Values
            .OrderByDescending(x => x.LastSeenUtc)
            .Select(x => new ClientSummary(
                x.Heartbeat.ClientId,
                x.Heartbeat.Hostname,
                x.Heartbeat.OsVersion,
                x.Heartbeat.AgentVersion,
                x.Heartbeat.AbittiVersionInstalled,
                x.SourceIp,
                x.LastSeenUtc,
                x.Heartbeat.LastCheckUtc,
                x.Heartbeat.LastInstallUtc,
                x.Heartbeat.LastInstallResult,
                x.Heartbeat.LastError,
                x.Heartbeat.PendingReboot))
            .ToList();
    }

    private sealed record StoredClient(ClientHeartbeat Heartbeat, string SourceIp, DateTimeOffset LastSeenUtc);
}
