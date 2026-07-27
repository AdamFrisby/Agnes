namespace Agnes.Abstractions;

/// <summary>
/// A browsable, searchable source of things a user can install — skill bundles, MCP servers, and whatever
/// comes next. Registered as an <see cref="IPluginRegistry{TProvider}"/> plugin point per entry type, so
/// "where can I get these from" is answered by plugins rather than by a list baked into the host.
///
/// The two methods differ in what they promise. <see cref="ListAsync"/> is what to show someone who hasn't
/// asked for anything yet — a small local source can answer with everything it has, but a registry of
/// fourteen thousand entries must answer with a curated or trending subset, because a flat dump of that is
/// not a list anyone can use. <see cref="SearchAsync"/> is the one that has to be exhaustive; it defaults to
/// <see cref="ListAsync"/>, which is the honest answer for a source small enough that its list already
/// <em>is</em> everything, and which a network source overrides to push the query to the server where the
/// index actually lives.
/// </summary>
/// <typeparam name="TEntry">The catalogue's entry type (e.g. <see cref="RegistrySkillEntry"/>).</typeparam>
public interface ICatalogProvider<TEntry>
{
    /// <summary>Stable id for this source, e.g. <c>local-dir</c> or <c>skillshub</c>. Used on the wire.</summary>
    string Id { get; }

    /// <summary>Human-readable name for the source, shown in a picker.</summary>
    string DisplayName { get; }

    /// <summary>
    /// What to offer before the user has searched: everything, for a small source; the curated or trending
    /// front page, for a large one.
    /// </summary>
    Task<IReadOnlyList<TEntry>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Entries matching a free-text query. Defaults to <see cref="ListAsync"/> — correct for a source whose
    /// list is already its entire contents, and overridden by network sources whose list is only a front page.
    /// </summary>
    Task<IReadOnlyList<TEntry>> SearchAsync(string query, CancellationToken ct = default)
        => ListAsync(ct);

    /// <summary>Whether <see cref="SearchAsync"/> means anything more than <see cref="ListAsync"/> here, so a
    /// UI can hide a search box that would only re-show the same list.</summary>
    bool SupportsSearch => false;
}

/// <summary>
/// A catalogue source as a client sees it. <paramref name="SupportsSearch"/> is false for a source whose list
/// is already everything it has, so a UI can tell "search this" apart from "this is all of it".
/// </summary>
public sealed record CatalogSource(string Id, string DisplayName, bool SupportsSearch);

/// <summary>One catalogue entry plus the source it came from, so results from several registries can be shown
/// together and installed against the right one.</summary>
public sealed record CatalogHit<TEntry>(string CatalogId, string CatalogName, TEntry Entry);

/// <summary>
/// The outcome of asking every registered source at once: what was found, and which sources couldn't answer.
/// The failures travel with the results deliberately — a registry that is down or rate-limited must not look
/// like a registry with nothing in it.
/// </summary>
public sealed record CatalogResults<TEntry>(IReadOnlyList<CatalogHit<TEntry>> Hits, IReadOnlyList<string> Failures)
{
    public static CatalogResults<TEntry> Empty { get; } = new([], []);
}

/// <summary>How a catalogued MCP server is launched or reached.</summary>
public enum McpCatalogTransport
{
    /// <summary>A child process spoken to over stdio.</summary>
    Stdio,

    /// <summary>A remote endpoint reached over HTTP.</summary>
    Http,
}

/// <summary>
/// One environment variable a catalogued MCP server reads. Carried so the surface offering an install can say
/// what the server will need <em>before</em> it is installed and fails at first use — the difference between
/// "add this" and "add this, then go and find a GCS_BUCKET".
/// </summary>
/// <param name="IsSecret">True for tokens/keys, so a UI never echoes the value back.</param>
public sealed record McpCatalogEnvVar(
    string Name,
    string? Description = null,
    bool IsRequired = false,
    bool IsSecret = false,
    string? Default = null);

/// <summary>
/// An MCP server offered by a catalogue, before it is configured. This is the <em>domain</em> shape, not the
/// wire/config one: <c>Agnes.Protocol.McpServerInfo</c> describes a server the user has actually configured on
/// a host, and lives downstream of this assembly. Mapping one to the other is the host's job, which keeps the
/// plugin contract free of the wire contract.
/// </summary>
public sealed record McpCatalogEntry(
    string Id,
    string Name,
    string? Description = null,
    string? Publisher = null,
    string? Homepage = null,
    McpCatalogTransport Transport = McpCatalogTransport.Stdio,
    string? Command = null,
    IReadOnlyList<string>? Args = null,
    string? Url = null,
    IReadOnlyList<McpCatalogEnvVar>? EnvironmentVariables = null,
    string? Version = null)
{
    public IReadOnlyList<string> LaunchArgs => Args ?? [];

    public IReadOnlyList<McpCatalogEnvVar> Environment => EnvironmentVariables ?? [];

    /// <summary>The variables that must be set for this server to work at all.</summary>
    public IReadOnlyList<McpCatalogEnvVar> RequiredEnvironment
        => Environment.Where(v => v.IsRequired).ToArray();
}

/// <summary>
/// One source of installable MCP servers — the built-in curated handful, an organisation's internal list, or a
/// public registry. Same plugin-point shape as <see cref="IPromptRegistryProvider"/>, and deliberately in this
/// assembly rather than in the host: a plugin package can only implement what it can reference.
/// </summary>
public interface IMcpCatalogProvider : ICatalogProvider<McpCatalogEntry>
{
}
