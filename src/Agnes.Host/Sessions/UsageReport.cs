namespace Agnes.Host.Sessions;

/// <summary>Per-owner resource usage — the attribution a sysadmin needs to see who is consuming the host.</summary>
public sealed record OwnerUsage(string Owner, int ActiveSessions, int ActiveSandboxes);

/// <summary>A snapshot of live resource usage across the host, broken down by session owner.</summary>
public sealed record UsageReport(int TotalSessions, int TotalSandboxes, IReadOnlyList<OwnerUsage> ByOwner);
