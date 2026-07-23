using System.Reflection;
using System.Text.Json.Serialization;
using Agnes.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>Guards the one hand-maintained registry in the event model: the [JsonDerivedType] table on
/// <see cref="SessionEvent"/>. A new event kind added without its discriminator compiles fine but fails to
/// serialize at runtime (it's the sole wire contract — the snapshot ships SessionEvents directly), so this
/// asserts every concrete subtype is registered. Adding a kind now fails the build here, not in production.</summary>
public class SessionEventDiscriminatorTests
{
    [Fact]
    public void Every_SessionEvent_subtype_has_a_json_discriminator()
    {
        var registered = typeof(SessionEvent)
            .GetCustomAttributes<JsonDerivedTypeAttribute>()
            .Select(a => a.DerivedType)
            .ToHashSet();

        var concrete = typeof(SessionEvent).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false } && typeof(SessionEvent).IsAssignableFrom(t))
            .ToList();

        Assert.NotEmpty(concrete);
        var missing = concrete.Where(t => !registered.Contains(t)).Select(t => t.Name).ToList();
        Assert.True(missing.Count == 0, "SessionEvent subtypes missing a [JsonDerivedType] discriminator: " + string.Join(", ", missing));
    }

    [Fact]
    public void Discriminators_are_unique()
    {
        var discriminators = typeof(SessionEvent)
            .GetCustomAttributes<JsonDerivedTypeAttribute>()
            .Select(a => a.TypeDiscriminator?.ToString())
            .ToList();

        Assert.Equal(discriminators.Count, discriminators.Distinct().Count());
    }
}
