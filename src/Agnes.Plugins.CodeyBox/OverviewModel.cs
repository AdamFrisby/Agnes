namespace Agnes.Plugins.CodeyBox;

/// <summary>
/// The pure model behind the overview: <see cref="OverviewInputs"/> in, <see cref="Overview"/> out. No
/// I/O, no time source of its own (<see cref="OverviewInputs.Now"/> is injected), so every derivation is
/// a function the tests can pin. See <c>OverviewContract.cs</c> for what each output means.
/// </summary>
public static partial class OverviewModel
{
    /// <summary>Builds the whole overview in one pass.</summary>
    public static Overview Build(OverviewInputs inputs) => throw new NotImplementedException("OverviewModel.Build is being implemented.");
}
