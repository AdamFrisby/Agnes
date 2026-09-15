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

    [Fact]
    public void Usb_devices_round_trip_normalised_and_junk_is_dropped()
    {
        var project = new Project
        {
            Id = "p2",
            UsbDevices = [new UsbDeviceSelector("0e8d", "201c", null, "Lenovo Tab M9"), new UsbDeviceSelector("0403", "6001", "FT1234")],
        };

        var dto = ProjectMapping.ToDto(project);
        Assert.NotNull(dto.UsbDevices);
        Assert.Equal(["0e8d:201c", "0403:6001"], dto.UsbDevices!.Select(d => $"{d.VendorId}:{d.ProductId}"));
        Assert.Equal("Lenovo Tab M9", dto.UsbDevices[0].Label);
        Assert.Equal("FT1234", dto.UsbDevices[1].Serial);

        var back = ProjectMapping.ToProject(dto);
        Assert.Equal(["0e8d:201c", "0403:6001/FT1234"], back.UsbDevices.Select(d => d.Id));

        // A client that typed the ids in caps, with spaces, and one that is not an id at all.
        var typed = dto with
        {
            UsbDevices = [new UsbDeviceDto(" 0E8D ", "201C", " ", ""), new UsbDeviceDto("nope", "201c"), new UsbDeviceDto("0403", "60011")],
        };
        var cleaned = ProjectMapping.ToProject(typed);
        var only = Assert.Single(cleaned.UsbDevices);
        Assert.Equal("0e8d:201c", only.Id);
        Assert.Null(only.Serial);
        Assert.Null(only.Label);
    }

    [Fact]
    public void A_client_that_predates_usb_devices_leaves_the_stored_list_alone()
    {
        var stored = new Project { Id = "p3", UsbDevices = [new UsbDeviceSelector("0e8d", "201c")] };
        var oldClient = ProjectMapping.ToDto(stored) with { UsbDevices = null };

        Assert.Single(ProjectMapping.ToProject(oldClient, existing: stored).UsbDevices);          // kept
        Assert.Empty(ProjectMapping.ToProject(oldClient with { UsbDevices = [] }, existing: stored).UsbDevices); // an explicit empty list clears
        Assert.Empty(ProjectMapping.ToProject(oldClient).UsbDevices);                              // nothing stored, nothing kept
    }
}
