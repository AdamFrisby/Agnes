namespace Agnes.Ui.Core;

/// <summary>Persists per-session prompt drafts and prompt history (recall).</summary>
public interface IPromptStore
{
    string LoadDraft(string sessionId);
    void SaveDraft(string sessionId, string draft);
    IReadOnlyList<string> LoadHistory(string sessionId);
    void AppendHistory(string sessionId, string prompt);
}

/// <summary>No-op store (default; used in tests and non-persistent contexts).</summary>
public sealed class NullPromptStore : IPromptStore
{
    public static readonly NullPromptStore Instance = new();

    public string LoadDraft(string sessionId) => string.Empty;
    public void SaveDraft(string sessionId, string draft) { }
    public IReadOnlyList<string> LoadHistory(string sessionId) => [];
    public void AppendHistory(string sessionId, string prompt) { }
}
