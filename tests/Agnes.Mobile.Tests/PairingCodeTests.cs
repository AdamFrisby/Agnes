using Agnes.App.Mobile.ViewModels;

namespace Agnes.Mobile.Tests;

/// <summary>
/// The host issues codes as ABCD-EFGH from an unambiguous alphabet and compares them with a fixed-time
/// byte comparison of the trimmed string — so case and the hyphen both matter. An Android keyboard
/// gives you lowercase by default, which would otherwise fail as "wrong code".
/// </summary>
public sealed class PairingCodeTests
{
    [Theory]
    [InlineData("K7QX-M3RT")]      // exactly as printed
    [InlineData("k7qx-m3rt")]      // what a soft keyboard actually produces
    [InlineData("K7QXM3RT")]       // hyphen skipped
    [InlineData("k7qx m3rt")]      // space instead of a hyphen
    [InlineData("  K7QX-M3RT  ")]  // pasted with padding
    [InlineData("K7QX–M3RT")]      // an en dash, courtesy of autocorrect
    public void Every_plausible_way_of_typing_a_code_normalizes_to_the_issued_form(string typed)
        => Assert.Equal("K7QX-M3RT", ConnectPageViewModel.NormalizeCode(typed));

    [Fact]
    public void A_pasted_bootstrap_token_is_left_alone()
    {
        // The same field accepts a pre-issued host token, which is not 8 characters and must not be
        // upper-cased or re-grouped on its way to the fallback path.
        const string token = "9f3a1c7e-b204-4d55-a1e8-6c0f2b9d7e31";

        Assert.Equal(token, ConnectPageViewModel.NormalizeCode("  " + token + " "));
    }

    [Fact]
    public void An_empty_entry_stays_empty()
        => Assert.Equal(string.Empty, ConnectPageViewModel.NormalizeCode("   "));
}
