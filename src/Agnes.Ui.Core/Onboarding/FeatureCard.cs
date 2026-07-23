namespace Agnes.Ui.Core.Onboarding;

/// <summary>
/// One slide in the onboarding showcase / "what's new" sequence. Content is pure data — a title, a short
/// description, and an optional screenshot reference — so the card list is what changes when a feature is
/// added or removed, never the renderer. <see cref="Screenshot"/> is an opaque resource key the head resolves
/// (a bundled asset path or resource name); it is optional so a card can be text-only.
/// </summary>
public sealed record FeatureCard(string Title, string Description, string? Screenshot = null);
