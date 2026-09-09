using System.Reflection;
using System.Text.Json;
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

    /// <summary>A sent file travels to every client as a polymorphic <see cref="SessionEvent"/> and nothing
    /// else — so if the discriminator or a field didn't survive the round trip, the file would simply never
    /// appear on the far end.</summary>
    [Fact]
    public void A_file_shared_event_round_trips_as_a_session_event()
    {
        SessionEvent original = new FileSharedEvent(
            "0f1e2d3c4b5a6978", "q3-chart.png", ".agnes/shared/0f1e2d3c4b5a6978/q3-chart.png", 2048, "image/png", "before vs after");

        var json = JsonSerializer.Serialize(original);
        Assert.Contains("\"file_shared\"", json, StringComparison.Ordinal);

        var shared = Assert.IsType<FileSharedEvent>(JsonSerializer.Deserialize<SessionEvent>(json));
        Assert.Equal("0f1e2d3c4b5a6978", shared.FileId);
        Assert.Equal("q3-chart.png", shared.FileName);
        Assert.Equal(".agnes/shared/0f1e2d3c4b5a6978/q3-chart.png", shared.RelativePath);
        Assert.Equal(2048, shared.Size);
        Assert.Equal("image/png", shared.MimeType);
        Assert.Equal("before vs after", shared.Caption);
    }

    /// <summary>A null caption/mime is the common case (no context given, unknown extension) and must survive
    /// as null rather than becoming an empty string.</summary>
    [Fact]
    public void A_file_shared_event_with_no_caption_or_mime_round_trips_as_null()
    {
        SessionEvent original = new FileSharedEvent("abcdef0123456789", "thing.qqq", ".agnes/shared/abcdef0123456789/thing.qqq", 3, null, null);

        var shared = Assert.IsType<FileSharedEvent>(
            JsonSerializer.Deserialize<SessionEvent>(JsonSerializer.Serialize(original)));

        Assert.Null(shared.MimeType);
        Assert.Null(shared.Caption);
    }
}
