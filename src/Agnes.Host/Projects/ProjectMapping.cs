using Agnes.Host.Sessions;
using Agnes.Protocol;
using Agnes.Sandbox;

namespace Agnes.Host.Projects;

/// <summary>Maps between the host <see cref="Project"/> and the wire <see cref="ProjectDto"/>.</summary>
public static class ProjectMapping
{
    private const long GiB = 1024L * 1024 * 1024;

    private static int? ToGiB(long? bytes) => bytes is { } b ? (int)(b / GiB) : null;

    public static ProjectDto ToDto(Project project) => new(
        project.Id,
        project.Name,
        project.RepoKey,
        SandboxImageMapping.ToDto(project.Sandbox),
        project.McpServers,
        project.CredentialAccount,
        new ProjectDefaultsDto(project.Defaults.SkipPermissions, project.Defaults.GitCredentialMode, project.Defaults.McpApproval),
        project.Repo,
        project.SandboxResources?.CpuCount,
        ToGiB(project.SandboxResources?.MemoryBytes),
        ToGiB(project.SandboxResources?.DiskBytes));

    public static Project ToProject(ProjectDto dto) => new()
    {
        Id = dto.Id,
        Name = dto.Name,
        RepoKey = dto.RepoKey,
        Sandbox = SandboxImageMapping.ToManifest(dto.Sandbox),
        McpServers = dto.McpServers,
        CredentialAccount = dto.CredentialAccount,
        Repo = dto.Repo,
        Defaults = new ProjectDefaults(dto.Defaults.SkipPermissions, dto.Defaults.GitCredentialMode, dto.Defaults.McpApproval),
        SandboxResources = ToOverride(dto),
    };

    private static SandboxResourceOverride? ToOverride(ProjectDto dto)
    {
        var over = new SandboxResourceOverride
        {
            CpuCount = dto.SandboxCpu is > 0 ? dto.SandboxCpu : null,
            MemoryBytes = dto.SandboxMemoryGiB is > 0 ? dto.SandboxMemoryGiB * GiB : null,
            DiskBytes = dto.SandboxDiskGiB is > 0 ? dto.SandboxDiskGiB * GiB : null,
        };
        return over.IsEmpty ? null : over;
    }
}
