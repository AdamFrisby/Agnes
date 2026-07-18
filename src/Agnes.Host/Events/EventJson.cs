using System.Text.Json;
using Agnes.Abstractions;

namespace Agnes.Host.Events;

/// <summary>Serialization for persisted session events (polymorphism via attributes on <see cref="SessionEvent"/>).</summary>
internal static class EventJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(SessionEvent @event) => JsonSerializer.Serialize(@event, Options);

    public static SessionEvent Deserialize(string json)
        => JsonSerializer.Deserialize<SessionEvent>(json, Options)
           ?? throw new InvalidOperationException("Failed to deserialize session event.");
}
