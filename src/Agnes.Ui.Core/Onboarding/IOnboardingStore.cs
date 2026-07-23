namespace Agnes.Ui.Core.Onboarding;

/// <summary>Loads and persists client-local <see cref="OnboardingState"/> (shown-once flags, resumable wizard
/// progress). Injected into the onboarding view models so they are testable without touching a real disk.</summary>
public interface IOnboardingStore
{
    OnboardingState Load();
    void Save(OnboardingState state);
}

/// <summary>In-memory store (default for tests / non-persistent contexts). Keeps the last saved state so
/// shown-once and resume semantics can be exercised without a file.</summary>
public sealed class InMemoryOnboardingStore : IOnboardingStore
{
    private OnboardingState _state;

    public InMemoryOnboardingStore(OnboardingState? initial = null) => _state = initial ?? new OnboardingState();

    public OnboardingState Load() => _state;
    public void Save(OnboardingState state) => _state = state;
}
