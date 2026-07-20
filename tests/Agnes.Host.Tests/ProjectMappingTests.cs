using Agnes.Host.Projects;
using Agnes.Protocol;
using Agnes.Sandbox;

namespace Agnes.Host.Tests;

public class ProjectMappingTests
{
    [Fact]
    public void Project_round_trips_through_the_wire_dto()
    {
        var project = new Project
        {
            Id = "p1",
            Name = "Work Rust",
            RepoKey = "github.com/work-org/svc",
            Sandbox = new SandboxImageManifest { Node = false, AptPackages = ["git", "cargo"], Agents = [new SandboxImageAgent("claude-code-native", "copy:claude")] },
            McpServers = [new McpServerInfo("m", "rust", "sandbox", true, "stdio", "ra", [], new Dictionary<string, string>(), null, null)],
            CredentialAccount = "work-org",
            Defaults = new ProjectDefaults(SkipPermissions: true, GitCredentialMode: "Trust", McpApproval: "Trust"),
        };

        var back = ProjectMapping.ToProject(ProjectMapping.ToDto(project));

        Assert.Equal("p1", back.Id);
        Assert.Equal("Work Rust", back.Name);
        Assert.Equal("github.com/work-org/svc", back.RepoKey);
        Assert.False(back.Sandbox.Node);
        Assert.Contains("cargo", back.Sandbox.AptPackages);
        Assert.Equal("copy:claude", back.Sandbox.Agents.Single().Source);
        Assert.Equal("rust", back.McpServers.Single().Name);
        Assert.Equal("work-org", back.CredentialAccount);
        Assert.True(back.Defaults.SkipPermissions);
        Assert.Equal("Trust", back.Defaults.GitCredentialMode);
    }
}
