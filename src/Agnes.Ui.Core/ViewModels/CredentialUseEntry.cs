namespace Agnes.Ui.Core.ViewModels;

/// <summary>One entry in a session's audit trail of brokered git-credential grants.</summary>
public sealed class CredentialUseEntry
{
    public CredentialUseEntry(string host, string? repo, bool allowed, DateTimeOffset when)
    {
        Host = host;
        Repo = repo;
        Allowed = allowed;
        When = when;
    }

    public string Host { get; }
    public string? Repo { get; }
    public bool Allowed { get; }
    public DateTimeOffset When { get; }

    public string Label => string.IsNullOrEmpty(Repo) ? Host : Repo!;
    public string StatusText => Allowed ? "granted" : "denied";
    public string TimeText => When.ToLocalTime().ToString("HH:mm:ss");
}
