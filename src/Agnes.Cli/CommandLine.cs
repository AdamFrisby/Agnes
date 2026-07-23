namespace Agnes.Cli;

/// <summary>
/// A deliberately tiny hand-rolled argument model — no <c>System.CommandLine</c> or any other parser
/// dependency, keeping this credential-bearing binary's audit surface minimal (per the spec). Tokens are
/// split into positionals, boolean flags (<c>--wait</c>) and valued options (<c>--timeout 30</c> or
/// <c>--timeout=30</c>); the caller declares which names are boolean so a flag never swallows the following
/// positional as its value.
/// </summary>
public sealed class CommandLine
{
    private readonly Dictionary<string, string> _options;
    private readonly HashSet<string> _flags;

    private CommandLine(IReadOnlyList<string> positionals, Dictionary<string, string> options, HashSet<string> flags)
    {
        Positionals = positionals;
        _options = options;
        _flags = flags;
    }

    public IReadOnlyList<string> Positionals { get; }

    public bool Flag(string name) => _flags.Contains(name);

    public string? Option(string name) => _options.TryGetValue(name, out var v) ? v : null;

    public static CommandLine Parse(IReadOnlyList<string> args, IReadOnlySet<string> booleanFlags)
    {
        var positionals = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(token);
                continue;
            }

            var name = token[2..];
            var eq = name.IndexOf('=', StringComparison.Ordinal);
            if (eq >= 0)
            {
                options[name[..eq]] = name[(eq + 1)..];
                continue;
            }

            if (booleanFlags.Contains(name))
            {
                flags.Add(name);
                continue;
            }

            // A valued option consumes the next token; a trailing option with nothing after it is recorded empty.
            options[name] = i + 1 < args.Count ? args[++i] : string.Empty;
        }

        return new CommandLine(positionals, options, flags);
    }
}
