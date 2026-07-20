using Agnes.Protocol;
using Agnes.Sandbox;

namespace Agnes.Host.Sessions;

/// <summary>Maps between the host <see cref="SandboxImageManifest"/> and the wire DTOs.</summary>
public static class SandboxImageMapping
{
    public static SandboxImageView View(SandboxImageManager manager)
        => new(ToDto(manager.Manifest), Status(manager.Status));

    public static SandboxImageDto ToDto(SandboxImageManifest m) => new(
        m.BaseImage, m.Alias, m.Node, m.AptPackages, m.NpmGlobals, m.PipPackages,
        m.Agents.Select(a => new SandboxImageAgentDto(a.AdapterId, a.Source)).ToArray());

    public static SandboxImageManifest ToManifest(SandboxImageDto d) => new()
    {
        BaseImage = d.BaseImage,
        Alias = d.Alias,
        Node = d.Node,
        AptPackages = d.AptPackages,
        NpmGlobals = d.NpmGlobals,
        PipPackages = d.PipPackages,
        Agents = d.Agents.Select(a => new SandboxImageAgent(a.AdapterId, a.Source)).ToArray(),
    };

    public static SandboxImageStatusDto Status(SandboxImageStatus s)
        => new(s.State.ToString().ToLowerInvariant(), s.Message, s.UpdatedAt);
}
