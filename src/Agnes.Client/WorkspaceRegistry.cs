using Agnes.Abstractions;
using Agnes.Protocol;

namespace Agnes.Client;

/// <summary>
/// One logical <see cref="Workspace"/> together with every <see cref="Checkout"/> of it found across the
/// connected hosts — the answer to "which machines have this project checked out, and on what branch."
/// </summary>
public sealed record WorkspaceCheckouts(Workspace Workspace, IReadOnlyList<Checkout> Checkouts);

/// <summary>
/// The client side of the multi-machine workspace model (<c>connectivity/05</c>): unions each connected host's
/// checkouts (via the multi-host pool) and groups them into <see cref="Workspace"/>s by normalized repository
/// URL, so a repo checked out on a laptop and a cloud box surfaces as one workspace with two checkouts (each
/// tagged with its host id + branch). Read-mostly and additive: a single-host / single-checkout setup collapses
/// to one workspace with one checkout, unchanged. Reuses <see cref="WorkspaceIdentity"/> so the grouping key
/// matches the host's own workspace tagging exactly.
/// </summary>
public sealed class WorkspaceRegistry
{
    private volatile IReadOnlyList<WorkspaceCheckouts> _workspaces = [];

    /// <summary>The current grouped view, ordered by workspace id. Rebuilt by <see cref="RefreshAsync"/>.</summary>
    public IReadOnlyList<WorkspaceCheckouts> Workspaces => _workspaces;

    /// <summary>
    /// Rebuilds the workspace view by querying every host's checkouts and grouping by normalized repository URL.
    /// A host that can't be reached (or doesn't support checkouts) is skipped, not fatal — the other hosts'
    /// checkouts still aggregate.
    /// </summary>
    public async Task RefreshAsync(IReadOnlyCollection<IAgnesHost> hosts, CancellationToken cancellationToken = default)
    {
        var pairs = new List<(IAgnesHost Host, CheckoutDto Dto)>();
        foreach (var host in hosts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<CheckoutDto> checkouts;
            try
            {
                checkouts = await host.ListCheckoutsAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A host that's mid-reconnect or predates the feature contributes nothing this pass.
                continue;
            }

            foreach (var dto in checkouts)
            {
                pairs.Add((host, dto));
            }
        }

        _workspaces = pairs
            .GroupBy(p => WorkspaceIdentity.Normalize(p.Dto.RepositoryUrl), StringComparer.Ordinal)
            .Select(group =>
            {
                var repositoryUrl = group.First().Dto.RepositoryUrl;
                var workspace = new Workspace(group.Key, WorkspaceIdentity.DisplayName(repositoryUrl), repositoryUrl);
                var checkouts = group
                    .Select(p => new Checkout(p.Dto.Id, group.Key, p.Host.HostId, p.Dto.Path, p.Dto.Branch))
                    .ToArray();
                return new WorkspaceCheckouts(workspace, checkouts);
            })
            .OrderBy(w => w.Workspace.Id, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>The workspace with the given id (and its checkouts), or null if none is currently known.</summary>
    public WorkspaceCheckouts? Find(string workspaceId)
        => _workspaces.FirstOrDefault(w => string.Equals(w.Workspace.Id, workspaceId, StringComparison.Ordinal));
}
