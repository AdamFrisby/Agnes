namespace Agnes.Ui.Core.Onboarding;

/// <summary>
/// The default onboarding showcase content: a data list of the features that have actually shipped, feeding
/// <see cref="Agnes.Ui.Core.ViewModels.ShowcaseViewModel"/>. Adding or removing a highlighted feature is an
/// edit to <em>this</em> list only — no render-code change — and the same list can later seed a "what's new"
/// surface. Screenshot keys are left null here; a head can attach bundled assets without touching the renderer.
/// </summary>
public static class OnboardingCards
{
    /// <summary>The shipped-feature cards shown on first run (in order).</summary>
    public static readonly IReadOnlyList<FeatureCard> Default =
    [
        new FeatureCard(
            "Welcome to Agnes",
            "Run your coding agents on a host and reach them from any client — desktop, web, or phone. Sessions are event-sourced, so scrollback is unlimited and every client stays in sync."),
        new FeatureCard(
            "Bring your own agent CLI",
            "Claude Code, OpenCode, and Codex work out of the box over ACP, and custom backends plug in as packages — no core changes needed to add a new agent."),
        new FeatureCard(
            "Global inbox",
            "Every session's activity and background runs collect in one place, so nothing you kicked off gets lost across hosts and tabs."),
        new FeatureCard(
            "Memory search",
            "Search across your past sessions to find what an agent did, when, and why — the whole event log is queryable."),
        new FeatureCard(
            "Automations",
            "Schedule agents to run on their own — recurring tasks and triggers that pause, resume, and report back to the inbox."),
        new FeatureCard(
            "MCP management",
            "Add, toggle, and preview Model Context Protocol servers per host or project, and install curated presets without hand-editing config."),
        new FeatureCard(
            "Prompts library",
            "Save reusable prompts and slash-token templates that expand inline, shared across every session on a host."),
        new FeatureCard(
            "Deep git",
            "Stash, branch, pull, push, and check out a pull request straight from the session — the git surface an agent workflow actually needs."),
        new FeatureCard(
            "Model & engine selection",
            "Pick the model and engine per session and mark favourites, so switching between fast and deep models is one click."),
        new FeatureCard(
            "Scriptable agent CLI",
            "Drive Agnes from scripts with agnes-agent for headless and automated workflows — everything the UI does is available programmatically."),
    ];
}
