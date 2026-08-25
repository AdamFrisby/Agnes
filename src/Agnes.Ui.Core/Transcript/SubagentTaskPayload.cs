using System.Text.RegularExpressions;

namespace Agnes.Ui.Core.Transcript;

/// <summary>
/// A subagent OpenCode reported through the result of its <c>task</c> tool.
/// </summary>
/// <param name="TaskId">
/// OpenCode's own id for the subagent (<c>ses_…</c>). This — not the tool-call id — is the subagent's
/// identity: a background task is *launched* by one tool call and *reports back* through a later,
/// different one, and only this id ties the two together.
/// </param>
/// <param name="IsRunning">Whether the subagent is still working, per the payload's own state.</param>
/// <param name="Body">
/// What the subagent actually said, with the envelope and the model-facing instructions stripped. Empty
/// for a launch, which carries nothing but boilerplate.
/// </param>
public sealed partial record SubagentTaskPayload(string TaskId, bool IsRunning, string Body)
{
    /// <summary>
    /// Recognizes the XML-ish envelope OpenCode returns from its <c>task</c> tool:
    /// <c>&lt;task id="ses_…" state="running|completed"&gt;…&lt;task_result&gt;…&lt;/task_result&gt;&lt;/task&gt;</c>.
    /// </summary>
    /// <remarks>
    /// This is a boundary parse of a format that belongs to OpenCode, so it stays deliberately tolerant —
    /// a payload we only half-recognize still beats rendering the raw markup at the user, which is what
    /// this replaces. It is matched by shape rather than by adapter id on purpose: an agent that adopts
    /// the same envelope gets the same treatment without core learning its name.
    /// </remarks>
    public static SubagentTaskPayload? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var open = TaskOpen().Match(text);
        if (!open.Success)
        {
            return null;
        }

        var id = open.Groups["id"].Value;
        var running = !string.Equals(open.Groups["state"].Value, "completed", StringComparison.OrdinalIgnoreCase);

        // The launch payload's <task_result> is instructions addressed to the model ("DO NOT sleep, poll
        // for progress…"), not a report; showing it to a person is noise. A finished task's is the report.
        var body = running ? string.Empty : Result().Match(text) is { Success: true } m ? m.Groups["body"].Value.Trim() : string.Empty;

        return new SubagentTaskPayload(id, running, body);
    }

    [GeneratedRegex("""<task\s+id="(?<id>[^"]+)"\s+state="(?<state>[^"]+)"\s*>""", RegexOptions.IgnoreCase)]
    private static partial Regex TaskOpen();

    [GeneratedRegex("""<task_result>(?<body>.*?)</task_result>""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Result();
}
