using System.Collections.Concurrent;
using AbittiAgent.Shared;

namespace AbittiAgent.Server;

public sealed class ClientStore
{
    private readonly ConcurrentDictionary<string, StoredClient> _byId = new();

    public void Upsert(ClientHeartbeat heartbeat, string sourceIp)
    {
        var seen = DateTimeOffset.UtcNow;
        _byId[heartbeat.ClientId] = new StoredClient(heartbeat, sourceIp, seen);
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
