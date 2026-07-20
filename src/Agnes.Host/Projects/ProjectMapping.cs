using Agnes.Host.Sessions;
using Agnes.Protocol;

namespace Agnes.Host.Projects;

/// <summary>Maps between the host <see cref="Project"/> and the wire <see cref="ProjectDto"/>.</summary>
public static class ProjectMapping
{
    public static ProjectDto ToDto(Project project) => new(
        project.Id,
        project.Name,
        project.RepoKey,
        SandboxImageMapping.ToDto(project.Sandbox),
        project.McpServers,
        project.CredentialAccount,
        new ProjectDefaultsDto(project.Defaults.SkipPermissions, project.Defaults.GitCredentialMode, project.Defaults.McpApproval));

    public static Project ToProject(ProjectDto dto) => new()
    {
        Id = dto.Id,
        Name = dto.Name,
        RepoKey = dto.RepoKey,
        Sandbox = SandboxImageMapping.ToManifest(dto.Sandbox),
        McpServers = dto.McpServers,
        CredentialAccount = dto.CredentialAccount,
        Defaults = new ProjectDefaults(dto.Defaults.SkipPermissions, dto.Defaults.GitCredentialMode, dto.Defaults.McpApproval),
    };
}
