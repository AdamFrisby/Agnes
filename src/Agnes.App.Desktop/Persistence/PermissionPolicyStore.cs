using System.Text.Json;
using Agnes.Abstractions;
using Agnes.Ui.Core;

namespace Agnes.App.Desktop.Persistence;

/// <summary>
/// Persists "always allow / always reject" permission decisions per host + tool kind, so the
/// client can auto-answer matching requests across sessions and relaunches. Enforced client-side;
/// every auto-decision still produces a normal (audited) response to the agent.
/// </summary>
public sealed class PermissionPolicyStore : IPermissionPolicy
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, bool> _rules;

    public PermissionPolicyStore(string? path = null)
    {
        _path = path ?? DefaultPath();
        _rules = Load();
    }

    public static string DefaultPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Agnes");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "permission-policy.json");
    }

    public bool? Decide(string hostUrl, ToolKind? toolKind)
    {
        lock (_gate)
        {
            return _rules.TryGetValue(Key(hostUrl, toolKind), out var allow) ? allow : null;
        }
    }

    public void Remember(string hostUrl, ToolKind? toolKind, bool allow)
    {
        lock (_gate)
        {
            _rules[Key(hostUrl, toolKind)] = allow;
            Flush();
        }
    }

    public void Forget(string hostUrl, ToolKind? toolKind)
    {
        lock (_gate)
        {
            if (_rules.Remove(Key(hostUrl, toolKind)))
            {
                Flush();
            }
        }
    }

    private static string Key(string hostUrl, ToolKind? toolKind)
        => $"{hostUrl}|{toolKind?.ToString() ?? "any"}";

    private Dictionary<string, bool> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(_path), Options) ?? new();
            }
        }
        catch
        {
            // start clean on a corrupt file
        }

        return new();
    }

    private void Flush()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_rules, Options));
        }
        catch
        {
            // best-effort
        }
    }
}
