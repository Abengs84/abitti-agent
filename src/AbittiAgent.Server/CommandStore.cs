using System.Collections.Concurrent;
using AbittiAgent.Shared;

namespace AbittiAgent.Server;

public sealed class CommandStore
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<AgentCommand>> _queues = new();

    public void Enqueue(string clientId, string type)
    {
        var queue = _queues.GetOrAdd(clientId, _ => new ConcurrentQueue<AgentCommand>());
        queue.Enqueue(new AgentCommand(Guid.NewGuid().ToString("N"), type, DateTimeOffset.UtcNow.AddHours(4)));
    }

    public IReadOnlyList<AgentCommand> DequeueAvailable(string clientId)
    {
        if (!_queues.TryGetValue(clientId, out var queue))
            return Array.Empty<AgentCommand>();

        var now = DateTimeOffset.UtcNow;
        var list = new List<AgentCommand>();
        while (queue.TryDequeue(out var cmd))
        {
            if (cmd.ExpiresUtc > now)
                list.Add(cmd);
        }

        return list;
    }

    public int GetPendingCount(string clientId)
    {
        return _queues.TryGetValue(clientId, out var queue) ? queue.Count : 0;
    }
}
