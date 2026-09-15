using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agnes.Sandbox.Incus;

/// <summary>
/// Reads the host's USB devices out of <c>incus query /1.0/resources</c>. The resources document is
/// Incus's format — a boundary — so it is deserialised into the handful of typed records below and
/// handed on as <see cref="HostUsbDevice"/>; nothing untyped leaves this file.
/// </summary>
internal static class IncusUsbCatalog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    /// <summary>Parses the resources JSON. A document with no <c>usb</c> section (a container host with the
    /// bus hidden, an older Incus) is simply an empty list, not an error.</summary>
    internal static IReadOnlyList<HostUsbDevice> Parse(string resourcesJson)
    {
        if (string.IsNullOrWhiteSpace(resourcesJson))
        {
            return [];
        }

        Resources? resources;
        try
        {
            resources = JsonSerializer.Deserialize<Resources>(resourcesJson, Json);
        }
        catch (JsonException)
        {
            return [];
        }

        var devices = resources?.Usb?.Devices;
        if (devices is null)
        {
            return [];
        }

        return devices
            .Where(d => d.VendorId is { Length: 4 } && d.ProductId is { Length: 4 })
            .Select(d => new HostUsbDevice(
                d.VendorId!.ToLowerInvariant(),
                d.ProductId!.ToLowerInvariant(),
                d.Vendor ?? string.Empty,
                d.Product ?? string.Empty,
                string.IsNullOrWhiteSpace(d.Serial) ? null : d.Serial,
                d.BusAddress,
                d.DeviceAddress,
                (d.Interfaces ?? []).Select(i => i.Class ?? string.Empty).Where(c => c.Length > 0).Distinct().ToArray()))
            .OrderBy(d => d.Bus).ThenBy(d => d.Address)
            .ToArray();
    }

    // ---- the slice of /1.0/resources this reads ----

    private sealed record Resources([property: JsonPropertyName("usb")] UsbSection? Usb);

    private sealed record UsbSection([property: JsonPropertyName("devices")] IReadOnlyList<UsbEntry>? Devices);

    private sealed record UsbEntry(
        [property: JsonPropertyName("bus_address")] int BusAddress,
        [property: JsonPropertyName("device_address")] int DeviceAddress,
        [property: JsonPropertyName("vendor_id")] string? VendorId,
        [property: JsonPropertyName("product_id")] string? ProductId,
        [property: JsonPropertyName("vendor")] string? Vendor,
        [property: JsonPropertyName("product")] string? Product,
        [property: JsonPropertyName("serial")] string? Serial,
        [property: JsonPropertyName("interfaces")] IReadOnlyList<UsbInterface>? Interfaces);

    private sealed record UsbInterface([property: JsonPropertyName("class")] string? Class);
}
