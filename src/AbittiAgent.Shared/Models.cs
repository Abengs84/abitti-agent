namespace AbittiAgent.Shared;

public sealed record ClientHeartbeat(
    string ClientId,
    string Hostname,
    string OsVersion,
    string AgentVersion,
    string AbittiVersionInstalled,
    DateTimeOffset LastCheckUtc,
    DateTimeOffset LastInstallUtc,
    string LastInstallResult,
    string LastError,
    bool PendingReboot
);

/// <summary>
/// Server-side row for admin UI.
/// </summary>
public sealed record ClientSummary(
    string ClientId,
    string Hostname,
    string OsVersion,
    string AgentVersion,
    string AbittiVersionInstalled,
    string SourceIp,
    DateTimeOffset LastSeenUtc,
    DateTimeOffset LastCheckUtc,
    DateTimeOffset LastInstallUtc,
    string LastInstallResult,
    string LastError,
    bool PendingReboot);

public sealed record ServerPolicy(
    int PollIntervalMinutes,
    string MaintenanceStartLocal,
    string MaintenanceEndLocal
);

public sealed record AgentCommand(
    string CommandId,
    string Type,
    DateTimeOffset ExpiresUtc
);
