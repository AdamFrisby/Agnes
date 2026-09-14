using Agnes.App.Mobile.Views;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Agnes.Mobile.Tests;

/// <summary>
/// The alias dictionary that lets the plugin's drawn controls find this head's colours.
/// </summary>
/// <remarks>
/// <para><c>ThemedDrawing</c> cannot use <c>DynamicResource</c> — a control that renders itself has no
/// property to bind — so it looks its colours up BY NAME, and the names it asks for are the desktop
/// head's: <c>Fg</c>, <c>FgDim</c>, <c>FgFaint</c>, <c>Line</c>, <c>Panel</c>/<c>PanelAlt</c> and the
/// <c>Status*</c> hues. This head's vocabulary is different, so <c>Themes/DrawingRoles.axaml</c> aliases
/// them rather than editing eight controls to know about two naming schemes.</para>
///
/// <para>Those aliases restate their colours, because a ResourceDictionary entry cannot be a reference to
/// another entry. This is what stops the two drifting apart: every pair below has to resolve to the same
/// colour as the role it stands for, in <em>both</em> variants — a brush resolved under Dark is simply
/// wrong under Light, which is the failure that would otherwise ship as "the sparkline is invisible in
/// light mode".</para>
/// </remarks>
[Collection(AvaloniaCollection.Name)]
public sealed class CodeyBoxRoleAliasTests
{
    private readonly AvaloniaSession _avalonia;

    public CodeyBoxRoleAliasTests(AvaloniaSession avalonia) => _avalonia = avalonia;

    /// <summary>alias → the role in this head's own vocabulary that it stands for.</summary>
    public static TheoryData<string, string> Aliases => new()
    {
        { "Fg", "Text" },
        { "FgDim", "TextDim" },
        { "FgFaint", "TextMuted" },
        { "Line", "Border" },
        { "Panel", "Surface1" },
        { "PanelAlt", "Surface2" },

        // One meaning per hue: sky in motion, amber blocked on you, mint done, pink failed, quiet for
        // present-but-not-asking.
        { "StatusWorking", "Info" },
        { "StatusAttention", "Warning" },
        { "StatusDone", "Success" },
        { "StatusError", "Danger" },
        { "StatusIdle", "TextMuted" },
    };

    [Theory]
    [MemberData(nameof(Aliases))]
    public async Task An_alias_is_the_role_it_stands_for_in_both_variants(string alias, string role)
    {
        await _avalonia.Run(() =>
        {
            foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                // The variant is asked for on the window, which is where a theme scope actually lives;
                // the probe inherits it and resolves exactly as a drawn control in that window would.
                var probe = new Border();
                var window = new Window { Content = probe, RequestedThemeVariant = variant };
                window.Show();

                Assert.True(
                    probe.TryFindResource(alias, variant, out var aliased),
                    $"{alias} is missing from the alias dictionary ({variant})");
                Assert.True(
                    probe.TryFindResource(role, variant, out var actual),
                    $"{role} is missing from Tokens.axaml ({variant})");

                Assert.Equal(
                    ((ISolidColorBrush)actual!).Color,
                    ((ISolidColorBrush)aliased!).Color);
            }
        });
    }

    [Fact]
    public async Task The_two_roles_that_already_exist_are_not_aliased_twice()
    {
        // Accent and Danger are this head's own names AND the names the plugin's controls fall back to,
        // so the alias dictionary deliberately says nothing about them. A second definition would be a
        // place for them to drift.
        await _avalonia.Run(() =>
        {
            var probe = new Border();
            new Window { Content = probe }.Show();

            foreach (var role in new[] { "Accent", "Danger" })
            {
                Assert.True(probe.TryFindResource(role, ThemeVariant.Dark, out var brush));
                Assert.IsAssignableFrom<ISolidColorBrush>(brush);
            }
        });
    }
}
