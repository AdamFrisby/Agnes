using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Agnes.Abstractions;
using Agnes.Host.Hosting;
using Agnes.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

public class McpForwardTests
{
    private static McpServerInfo Echo(string name = "echo") => new(
        "id-" + name, name, "host", true, "stdio", "cat", [], new Dictionary<string, string>(), null, null);

    [Fact]
    public void Registry_grants_resolve_and_revoke()
    {
        var reg = new McpForwardRegistry();
        var token = reg.Register([Echo("files")]);

        Assert.NotNull(reg.Resolve(token, "files"));
        Assert.Null(reg.Resolve(token, "other"));   // not granted
        Assert.Null(reg.Resolve("bad-token", "files"));

        reg.Unregister(token);
        Assert.Null(reg.Resolve(token, "files"));
    }

    [Fact]
    public async Task Listener_tunnels_bytes_to_the_backend_for_a_valid_grant()
    {
        if (!AgentCommand.IsOnPath("cat"))
        {
            return; // no echo backend available
        }

        var reg = new McpForwardRegistry();
        var token = reg.Register([Echo()]);
        await using var listener = new McpForwardListener(reg, IPAddress.Loopback, 0, "127.0.0.1", NullLogger<McpForwardListener>.Instance);
        listener.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listener.Port);
        var stream = client.GetStream();

        await SendAsync(stream, JsonSerializer.Serialize(new { token, server = "echo" }) + "\n");
        await SendAsync(stream, "{\"jsonrpc\":\"2.0\"}\n");

        var echoed = await ReadSomeAsync(stream);
        Assert.Contains("jsonrpc", echoed);
    }

    [Fact]
    public async Task Listener_reports_tools_call_for_audit()
    {
        if (!AgentCommand.IsOnPath("cat"))
        {
            return;
        }

        var reg = new McpForwardRegistry();
        var token = reg.Register([Echo()], "session-1");
        var seen = new TaskCompletionSource<(string Server, string Tool)>();
        await using var listener = new McpForwardListener(reg, IPAddress.Loopback, 0, "127.0.0.1", NullLogger<McpForwardListener>.Instance)
        {
            OnToolCall = (_, server, tool) => seen.TrySetResult((server, tool)),
        };
        listener.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listener.Port);
        var stream = client.GetStream();

        await SendAsync(stream, JsonSerializer.Serialize(new { token, server = "echo" }) + "\n");
        await SendAsync(stream, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"read_file\"}}\n");

        var (srv, tool) = await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("echo", srv);
        Assert.Equal("read_file", tool);
    }

    [Fact]
    public async Task Listener_rejects_an_unknown_token_and_spawns_nothing()
    {
        var reg = new McpForwardRegistry();
        reg.Register([Echo()]);
        await using var listener = new McpForwardListener(reg, IPAddress.Loopback, 0, "127.0.0.1", NullLogger<McpForwardListener>.Instance);
        listener.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listener.Port);
        var stream = client.GetStream();

        await SendAsync(stream, "{\"token\":\"wrong\",\"server\":\"echo\"}\n");
        await SendAsync(stream, "should-not-be-echoed\n");

        // The listener closes the connection without a backend, so the read returns 0 (EOF).
        var n = await stream.ReadAsync(new byte[64]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, n);
    }

    [Fact]
    public async Task Real_shim_tunnels_stdio_end_to_end()
    {
        if (!AgentCommand.IsOnPath("python3") || !AgentCommand.IsOnPath("cat"))
        {
            return; // needs python3 (the shim) + cat (the backend)
        }

        var reg = new McpForwardRegistry();
        var token = reg.Register([Echo()]);
        await using var listener = new McpForwardListener(reg, IPAddress.Loopback, 0, "127.0.0.1", NullLogger<McpForwardListener>.Instance);
        listener.Start();

        var shimPath = Path.Combine(Path.GetTempPath(), $"mcp-forward-{Guid.NewGuid():n}.py");
        await File.WriteAllTextAsync(shimPath, McpForward.ShimScript);
        try
        {
            var psi = new ProcessStartInfo("python3")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(shimPath);
            psi.ArgumentList.Add("echo");
            psi.Environment["AGNES_MCP_HOST"] = "127.0.0.1";
            psi.Environment["AGNES_MCP_PORT"] = listener.Port.ToString();
            psi.Environment["AGNES_MCP_TOKEN"] = token;
            using var shim = Process.Start(psi)!;

            // The agent would write JSON-RPC to the shim's stdin; it must come back via the backend.
            await shim.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}");
            await shim.StandardInput.FlushAsync();

            var reply = await shim.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotNull(reply);
            Assert.Contains("ping", reply);

            shim.Kill(entireProcessTree: true);
        }
        finally
        {
            File.Delete(shimPath);
        }
    }

    private static Task SendAsync(NetworkStream stream, string text)
        => stream.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();

    private static async Task<string> ReadSomeAsync(NetworkStream stream)
    {
        var buffer = new byte[256];
        var n = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        return Encoding.UTF8.GetString(buffer, 0, n);
    }
}
