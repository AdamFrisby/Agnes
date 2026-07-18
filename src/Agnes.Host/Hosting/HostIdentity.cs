namespace Agnes.Host.Hosting;

/// <summary>Identity this host advertises to clients.</summary>
public sealed record HostIdentity(string HostId, string DisplayName, string Version);
