using Agnes.Abstractions;

namespace Agnes.Sandbox;

/// <summary>Provisions and lists sandboxes for a backend (Incus, …). Mirrors CodeyBox's provider.</summary>
public interface ISandboxProvider
{
    /// <summary>Backend id, e.g. "incus".</summary>
    string Name { get; }

    /// <summary>Provisions a sandbox and returns a live handle (persisted until explicitly deleted).</summary>
    Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken cancellationToken = default);

    /// <summary>Sandboxes this provider owns (for reconnect / cleanup).</summary>
    Task<IReadOnlyList<SandboxInfo>> ListManagedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A live sandbox. <see cref="WrapCommand"/> makes an agent run inside it (the Agnes-interactive
/// path); <see cref="ExecAsync"/> runs a one-shot command (used to materialise credentials).
/// Disposing does NOT destroy the sandbox — Agnes persists VMs; call <see cref="DeleteAsync"/> to
/// destroy.
/// </summary>
public interface ISandbox : ISandboxCommand, IAsyncDisposable
{
    string Id { get; }

    Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken cancellationToken = default);

    /// <summary>Provisions the agent's credentials (env + files) into the sandbox securely.</summary>
    Task MaterializeCredentialAsync(Credentials.SandboxCredential credential, CancellationToken cancellationToken = default);

    /// <summary>Destroys the sandbox (irreversible).</summary>
    Task DeleteAsync(CancellationToken cancellationToken = default);

    SandboxInfo Info { get; }
}

/// <summary>A sandbox that can be paused (RAM→disk) and resumed. Optional capability.</summary>
public interface IPausableSandbox
{
    bool IsPaused { get; }
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task ResumeAsync(CancellationToken cancellationToken = default);
}

/// <summary>Status of a sandbox surfaced to clients.</summary>
public sealed record SandboxInfo(string Provider, string Id, SandboxState State);

public enum SandboxState
{
    Running,
    Paused,
    Stopped,
}
