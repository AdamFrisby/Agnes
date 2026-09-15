using System.Text.Json.Serialization;

namespace Agnes.Sandbox;

/// <summary>
/// One USB device to hand to a sandbox, named the way the host sees it: a vendor and product id (four
/// lowercase hex digits each, as <c>lsusb</c> prints them) and, when two identical units are plugged in,
/// the serial that tells them apart. A selector, not a bus address — it follows the device across a
/// replug and a reboot, and the provider attaches whatever matches whenever it appears.
/// </summary>
/// <param name="Label">What the person who picked it saw ("Lenovo Tab M9"); shown, never sent to the provider.</param>
public sealed record UsbDeviceSelector(string VendorId, string ProductId, string? Serial = null, string? Label = null)
{
    /// <summary>"0e8d:201c", or "0e8d:201c/HA20HAXW" with a serial — the shape a person recognises.</summary>
    [JsonIgnore]
    public string Id => Serial is { Length: > 0 } s ? $"{VendorId}:{ProductId}/{s}" : $"{VendorId}:{ProductId}";

    /// <summary>The label when there is one, else the id.</summary>
    [JsonIgnore]
    public string Display => Label is { Length: > 0 } l ? l : Id;
}

/// <summary>A USB device present on the host right now, as the provider reports it — what a picker lists.</summary>
/// <param name="Classes">The device's interface classes ("Vendor Specific Class", "Mass Storage", "Human
/// Interface Device"): enough for a person to recognise their keyboard and leave it alone.</param>
public sealed record HostUsbDevice(
    string VendorId,
    string ProductId,
    string Vendor,
    string Product,
    string? Serial,
    int Bus,
    int Address,
    IReadOnlyList<string> Classes)
{
    /// <summary>The selector that would attach this device: ids plus the serial when the device reports one.</summary>
    public UsbDeviceSelector ToSelector() => new(VendorId, ProductId, string.IsNullOrEmpty(Serial) ? null : Serial, Label);

    /// <summary>"MediaTek Inc. Lenovo Tab M9", falling back to the ids when the descriptor strings are blank.</summary>
    [JsonIgnore]
    public string Label
    {
        get
        {
            var name = string.Join(' ', new[] { Vendor, Product }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return name.Length > 0 ? name : $"{VendorId}:{ProductId}";
        }
    }
}

/// <summary>
/// A provider that can say which USB devices the host has to offer. Optional capability, like
/// <see cref="ISandboxImageBuilder"/>: a host without it offers no picker and every project's device
/// list is typed by hand.
/// </summary>
public interface ISandboxUsbCatalog
{
    Task<IReadOnlyList<HostUsbDevice>> ListUsbDevicesAsync(CancellationToken cancellationToken = default);
}
