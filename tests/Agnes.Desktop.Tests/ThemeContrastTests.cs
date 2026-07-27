using System.Globalization;
using System.Text.RegularExpressions;

namespace Agnes.Desktop.Tests;

/// <summary>
/// Every theme's text must actually be readable on every surface it lands on.
///
/// This was not true: the Spacegray ports took <c>PanelAlt</c> — the background of every text box —
/// from the palette's *selection* grey, a mid-tone. Dim text on a mid-tone is mud, and an unfocused
/// input was close to unreadable in four of the six flavours. It is the kind of defect that survives
/// review because each colour looks fine on its own and only the pairing fails, so the pairing is
/// what's asserted here rather than any particular hex value.
///
/// Thresholds are WCAG 2.1 contrast ratios: 4.5 is AA for body text, 7 is AAA. Faint text is held to
/// AA rather than AAA because it is deliberately recessive — but recessive is not invisible.
/// </summary>
public class ThemeContrastTests
{
    private const double AaBody = 4.5;
    private const double AaaBody = 7.0;

    private static string ThemesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Agnes.App.Desktop")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "Agnes.App.Desktop", "Themes");
    }

    /// <summary>Every colour set in a theme file, keyed by the variant that declares it.</summary>
    private static IEnumerable<(string Variant, Dictionary<string, string> Colors)> Variants(string file)
    {
        var text = File.ReadAllText(Path.Combine(ThemesDir(), file));
        // Each variant is one <ResourceDictionary x:Key="..."> block of <Color> entries.
        var blocks = Regex.Matches(text,
            """<ResourceDictionary x:Key="(?<key>[^"]+)">(?<body>.*?)(?=<ResourceDictionary x:Key="|</ResourceDictionary\.ThemeDictionaries>)""",
            RegexOptions.Singleline);

        foreach (Match block in blocks)
        {
            var colors = Regex.Matches(block.Groups["body"].Value, """<Color x:Key="(?<k>\w+)">(?<v>#[0-9A-Fa-f]+)</Color>""")
                .ToDictionary(m => m.Groups["k"].Value, m => m.Groups["v"].Value);
            if (colors.ContainsKey("FgColor"))
            {
                // Trim the x:Static wrapper the ported flavours use, leaving a readable name.
                var name = block.Groups["key"].Value;
                yield return (name[(name.LastIndexOf('.') + 1)..].TrimEnd('}'), colors);
            }
        }
    }

    private static (double R, double G, double B) Rgb(string hex)
    {
        var h = hex.TrimStart('#');
        var o = h.Length == 8 ? 2 : 0; // themes state colours opaque; alpha, if present, is on the edges only
        return (int.Parse(h.Substring(o, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(h.Substring(o + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(h.Substring(o + 4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    private static double Relative((double R, double G, double B) c)
    {
        static double Channel(double v)
        {
            v /= 255;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    private static double Contrast(string fg, string bg)
    {
        var (a, b) = (Relative(Rgb(fg)), Relative(Rgb(bg)));
        var (hi, lo) = a > b ? (a, b) : (b, a);
        return (hi + 0.05) / (lo + 0.05);
    }

    public static TheoryData<string> ThemeFiles => new() { "Tokens.axaml", "Spacegray.axaml" };

    [Theory]
    [MemberData(nameof(ThemeFiles))]
    public void Text_is_readable_on_every_surface_of_every_theme(string file)
    {
        var checks = new (string Fg, double Min)[]
        {
            ("FgColor", AaaBody),      // body copy
            ("FgDimColor", AaBody),    // secondary labels
            ("FgFaintColor", AaBody),  // placeholders, timestamps — recessive, not invisible
        };

        var failures = new List<string>();
        foreach (var (variant, colors) in Variants(file))
        {
            foreach (var surface in new[] { "BgColor", "PanelColor", "PanelAltColor" })
            {
                if (!colors.TryGetValue(surface, out var bg))
                {
                    continue;
                }

                foreach (var (fg, min) in checks)
                {
                    var ratio = Contrast(colors[fg], bg);
                    if (ratio < min)
                    {
                        failures.Add($"{variant}: {fg} on {surface} is {ratio:F2}:1, needs {min}:1");
                    }
                }
            }
        }

        Assert.Empty(failures);
    }

    [Theory]
    [MemberData(nameof(ThemeFiles))]
    public void The_three_text_tones_stay_distinguishable_from_each_other(string file)
    {
        // Contrast alone can be satisfied by collapsing dim and faint onto the same colour, which passes
        // the check above while destroying the hierarchy the roles exist to express.
        var failures = new List<string>();
        foreach (var (variant, colors) in Variants(file))
        {
            var surface = colors["PanelAltColor"];
            var fg = Contrast(colors["FgColor"], surface);
            var dim = Contrast(colors["FgDimColor"], surface);
            var faint = Contrast(colors["FgFaintColor"], surface);

            if (!(fg > dim && dim > faint))
            {
                failures.Add($"{variant}: expected Fg > FgDim > FgFaint, got {fg:F2} / {dim:F2} / {faint:F2}");
            }
        }

        Assert.Empty(failures);
    }
}
