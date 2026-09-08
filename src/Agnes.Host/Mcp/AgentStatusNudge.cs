using Agnes.Host.Sessions;

namespace Agnes.Host.Mcp;

/// <summary>
/// The one sentence that tells a model the <c>report_status</c> tool is worth calling.
/// <para>
/// A tool description alone is read as "here is a thing you may use"; a standing instruction is read as
/// "here is something you are expected to do". The status line only works if it arrives without being asked
/// for, so the nudge exists — and it exists <b>once</b>, here, because it reaches a model by two different
/// routes (the MCP server's <c>ServerInstructions</c>, which every MCP client puts in context, and the
/// system-prompt append for adapters whose CLI takes one) and two copies would drift into two different
/// promises about the same tool.
/// </para>
/// </summary>
public static class AgentStatusNudge
{
    /// <summary>
    /// The nudge. States the cadence, the shape, and the limit — the limit especially, because a model that
    /// knows the budget writes to it, and one that doesn't gets clipped and told off afterwards.
    /// </summary>
    public const string Text =
        "Keep the person informed without their having to read you: every few minutes of work, or when your "
        + "plan changes or you hit a problem, call the agnes report_status tool with one or two sentences "
        + "under " + StatusOptions.DefaultMaxCharsText + " characters — what you found, what you are doing "
        + "now, and how it fits the plan.";
}
