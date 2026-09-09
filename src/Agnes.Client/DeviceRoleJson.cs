using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agnes.Client;

/// <summary>
/// Read options for the device endpoints, tolerant of an enum arriving as either a number or its name.
///
/// Roles reach clients from hosts of several vintages, and whether a host serializes
/// <c>DeviceRole</c> as <c>1</c> or <c>"Owner"</c> is a property of that host's JSON configuration
/// rather than of the contract. A client that understood only one of the two would report an owner as
/// a member — the exact confusion roles exist to end — so it reads both. Writes stay on the defaults,
/// which every host has always accepted.
/// </summary>
internal static class DeviceRoleJson
{
    internal static readonly JsonSerializerOptions Read = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}
