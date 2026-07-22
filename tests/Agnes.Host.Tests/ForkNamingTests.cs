using Agnes.Host.Sessions;

namespace Agnes.Host.Tests;

public class ForkNamingTests
{
    private static string P(params string[] parts) => Path.Combine(parts);

    private static readonly Func<string, bool> None = _ => false;

    [Fact]
    public void Increments_a_trailing_number()
        => Assert.Equal(P("home", "adam", "Projects", "Agnes2"), ForkNaming.Propose(P("home", "adam", "Projects", "Agnes1"), None));

    [Fact]
    public void Appends_2_when_there_is_no_trailing_number()
        => Assert.Equal(P("p", "Agnes2"), ForkNaming.Propose(P("p", "Agnes"), None));

    [Theory]
    [InlineData("Agnes9", "Agnes10")]
    [InlineData("Agnes10", "Agnes11")]
    [InlineData("Agnes99", "Agnes100")]
    [InlineData("Agnes100", "Agnes101")]
    public void Handles_double_and_triple_digits(string source, string expected)
        => Assert.Equal(P("p", expected), ForkNaming.Propose(P("p", source), None));

    [Fact]
    public void Skips_existing_targets()
    {
        var taken = new HashSet<string> { P("p", "Agnes2"), P("p", "Agnes3") };
        Assert.Equal(P("p", "Agnes4"), ForkNaming.Propose(P("p", "Agnes1"), taken.Contains));
    }

    [Fact]
    public void Skips_existing_even_without_a_source_number()
    {
        var taken = new HashSet<string> { P("p", "app2") };
        Assert.Equal(P("p", "app3"), ForkNaming.Propose(P("p", "app"), taken.Contains));
    }

    [Fact]
    public void Tolerates_a_trailing_separator()
        => Assert.Equal(P("p", "Agnes2"), ForkNaming.Propose(P("p", "Agnes1") + Path.DirectorySeparatorChar, None));

    [Fact]
    public void Keeps_the_stem_when_digits_are_embedded_earlier()
        => Assert.Equal(P("p", "v2app2"), ForkNaming.Propose(P("p", "v2app"), None));
}
