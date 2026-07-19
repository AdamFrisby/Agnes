using Agnes.Abstractions;

namespace Agnes.Host.Tests;

public class AgentCommandTests
{
    [Fact]
    public void Resolves_a_command_that_is_on_path()
    {
        // dotnet must be on PATH for the test host to run at all.
        var command = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        Assert.True(AgentCommand.IsOnPath(command));
    }

    [Fact]
    public void Rejects_a_command_that_is_not_installed()
        => Assert.False(AgentCommand.IsOnPath("definitely-not-a-real-agent-cli-xyz"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rejects_empty(string? command)
        => Assert.False(AgentCommand.IsOnPath(command));

    [Fact]
    public void An_absolute_path_is_checked_directly()
    {
        Assert.False(AgentCommand.IsOnPath("/no/such/binary/here"));
        var self = Environment.ProcessPath;
        if (self is not null)
        {
            Assert.True(AgentCommand.IsOnPath(self));
        }
    }
}
