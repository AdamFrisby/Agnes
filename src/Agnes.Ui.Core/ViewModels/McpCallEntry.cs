namespace Agnes.Ui.Core.ViewModels;

/// <summary>One entry in a session's audit trail of forwarded MCP tool calls.</summary>
public sealed class McpCallEntry
{
    public McpCallEntry(string server, string tool, DateTimeOffset when)
    {
        Server = server;
        Tool = tool;
        When = when;
    }

    public string Server { get; }
    public string Tool { get; }
    public DateTimeOffset When { get; }

    public string Label => $"{Server} · {Tool}";
    public string TimeText => When.ToLocalTime().ToString("HH:mm:ss");
}
