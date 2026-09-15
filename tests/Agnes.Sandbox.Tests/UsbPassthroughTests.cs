using Agnes.Sandbox;
using Agnes.Sandbox.Incus;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Sandbox.Tests;

/// <summary>
/// USB passthrough: a project's devices become Incus <c>usb</c> devices on the VM before it starts, a clone
/// sheds the ones it inherited (one device, one VM), and the host's device list is read out of the
/// resources API into typed records. The argv is what a real Incus accepts — the live check on the tablet
/// is in <c>docs/sandbox-live-testing.md</c>.
/// </summary>
public class UsbPassthroughTests
{
    private static readonly IncusOptions Options = new();

    /// <summary>Records every invocation, answers success, and scripts stdout for the reads a test names.</summary>
    private sealed class ScriptedRunner : IIncusCliRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public Dictionary<string, string> Stdout { get; } = new(StringComparer.Ordinal);

        public Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
            IReadOnlyList<string> argv, string? stdin = null,
            Action<string>? stdoutChunk = null, Action<string>? stderrChunk = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(argv);
            var key = Stdout.Keys.FirstOrDefault(k => argv.Contains(k));
            return Task.FromResult((0, key is null ? string.Empty : Stdout[key], string.Empty));
        }

        public Task RunCheckedAsync(string what, IReadOnlyList<string> argv, string? stdin = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(argv);
            return Task.CompletedTask;
        }

        public int IndexOf(params string[] contains) => Calls.FindIndex(c => contains.All(c.Contains));
    }

    private static IncusSandboxProvider Provider(ScriptedRunner runner)
        => new(Options, NullLoggerFactory.Instance, runner); // the runner answers 0 to the ready probe, so no wait

    [Fact]
    public void Usb_add_is_incus_own_device_type_with_vetted_ids()
    {
        var argv = IncusCommandBuilder.BuildUsbAdd(Options, "agnes-abc", 0, new UsbDeviceSelector("0e8d", "201c"));
        Assert.Equal(["incus", "--project", "agnes", "config", "device", "add", "agnes-abc", "agnes-usb-0", "usb", "vendorid=0e8d", "productid=201c"], argv);

        var pinned = IncusCommandBuilder.BuildUsbAdd(Options, "agnes-abc", 1, new UsbDeviceSelector("0e8d", "201c", "HA20HAXW"));
        Assert.Contains("agnes-usb-1", pinned);
        Assert.Contains("serial=HA20HAXW", pinned);

        // An empty serial is no serial: the selector matches any unit.
        Assert.DoesNotContain(IncusCommandBuilder.BuildUsbAdd(Options, "agnes-abc", 0, new UsbDeviceSelector("0e8d", "201c", "")), a => a.StartsWith("serial=", StringComparison.Ordinal));

        // The label is for people; it never reaches argv.
        var labelled = IncusCommandBuilder.BuildUsbAdd(Options, "agnes-abc", 0, new UsbDeviceSelector("0e8d", "201c", Label: "Lenovo Tab M9 --help"));
        Assert.DoesNotContain(labelled, a => a.Contains("Lenovo", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0E8D", "201c")]      // uppercase: Incus matches lowercase, and the mapper lowercases before this
    [InlineData("0e8d", "201")]       // three digits
    [InlineData("0e8d", "201cz")]     // five
    [InlineData("--vm", "201c")]      // an option
    [InlineData("0e8d", "20 1c")]     // whitespace
    public void Usb_ids_that_are_not_four_hex_digits_are_refused(string vendor, string product)
        => Assert.Throws<ArgumentException>(() => IncusCommandBuilder.BuildUsbAdd(Options, "agnes-abc", 0, new UsbDeviceSelector(vendor, product)));

    [Theory]
    [InlineData("HA20 HAXW")]
    [InlineData("a=b")]
    [InlineData("a,b")]
    public void Usb_serials_that_could_split_the_pair_are_refused(string serial)
        => Assert.Throws<ArgumentException>(() => IncusCommandBuilder.BuildUsbAdd(Options, "agnes-abc", 0, new UsbDeviceSelector("0e8d", "201c", serial)));

    [Fact]
    public void Query_takes_only_an_api_path_and_no_project_flag()
    {
        // Found live: `incus --project x query …` is refused outright, so this is the one verb without the prefix.
        Assert.Equal(["incus", "query", "/1.0/resources"], IncusCommandBuilder.BuildQuery(Options, "/1.0/resources"));
        Assert.Throws<ArgumentException>(() => IncusCommandBuilder.BuildQuery(Options, "resources"));
        Assert.Throws<ArgumentException>(() => IncusCommandBuilder.BuildQuery(Options, "-X"));
    }

    [Fact]
    public async Task Create_attaches_each_device_after_the_nic_and_before_start()
    {
        var runner = new ScriptedRunner();
        await Provider(runner).CreateAsync(new SandboxSpec
        {
            UsbDevices = [new UsbDeviceSelector("0e8d", "201c", Label: "tablet"), new UsbDeviceSelector("0403", "6001", "FT1234")],
        });

        var nic = runner.IndexOf("device", "add", "nic");
        var first = runner.IndexOf("agnes-usb-0", "usb");
        var second = runner.IndexOf("agnes-usb-1", "usb");
        var start = runner.IndexOf("start");
        Assert.True(nic >= 0 && first > nic && second > first && start > second, "nic, usb 0, usb 1, start — in that order");
        Assert.Contains("serial=FT1234", runner.Calls[second]);
    }

    [Fact]
    public async Task Create_with_no_devices_adds_no_usb_device()
    {
        var runner = new ScriptedRunner();
        await Provider(runner).CreateAsync(new SandboxSpec());
        Assert.DoesNotContain(runner.Calls, c => c.Contains("usb"));
    }

    [Fact]
    public async Task Clone_sheds_the_devices_it_inherited_and_takes_only_its_own()
    {
        var runner = new ScriptedRunner();
        // What `config device list` says the copy carries: the work mount, the nic, and two inherited USB devices.
        runner.Stdout["list"] = "agnes-net\nagnes-usb-0\nagnes-usb-1\nagnes-work\n";

        await Provider(runner).CloneAsync("agnes-src", "/tmp/fork", new SandboxSpec { HostWorkingDirectory = "/tmp/fork" });

        var copy = runner.IndexOf("copy");
        var removed0 = runner.IndexOf("device", "remove", "agnes-usb-0");
        var removed1 = runner.IndexOf("device", "remove", "agnes-usb-1");
        var start = runner.IndexOf("start");
        Assert.True(copy >= 0 && removed0 > copy && removed1 > copy && start > removed0 && start > removed1, "inherited devices go before the clone starts");
        Assert.DoesNotContain(runner.Calls, c => c.Contains("remove") && c.Contains("agnes-net")); // only the USB ones
        Assert.DoesNotContain(runner.Calls, c => c.Contains("add") && c.Contains("usb"));           // a fork's spec asks for none
    }

    [Fact]
    public async Task The_host_device_list_is_read_from_the_resources_api()
    {
        var runner = new ScriptedRunner();
        runner.Stdout["/1.0/resources"] = """
            {"cpu":{"total":16},"usb":{"devices":[
              {"bus_address":7,"device_address":6,"vendor_id":"0E8D","product_id":"201c","vendor":"MediaTek Inc.","product":"Lenovo Tab M9","serial":"",
               "interfaces":[{"class":"Vendor Specific Class","class_id":255}]},
              {"bus_address":1,"device_address":14,"vendor_id":"1b1c","product_id":"1b08","vendor":"Corsair","product":"K95W","serial":"",
               "interfaces":[{"class":"Human Interface Device"},{"class":"Human Interface Device"}]},
              {"bus_address":4,"device_address":2,"vendor_id":"0951","product_id":"1780","vendor":"","product":"","serial":"ABC123",
               "interfaces":[{"class":"Mass Storage"}]},
              {"bus_address":9,"device_address":1,"vendor_id":"bad","product_id":"1","vendor":"","product":"","serial":""}
            ]}}
            """;

        var devices = await Provider(runner).ListUsbDevicesAsync();

        Assert.Equal(["1b1c:1b08", "0951:1780", "0e8d:201c"], devices.Select(d => $"{d.VendorId}:{d.ProductId}")); // bus order, ids lowercased, junk dropped
        var tablet = devices.Single(d => d.ProductId == "201c");
        Assert.Equal("MediaTek Inc. Lenovo Tab M9", tablet.Label);
        Assert.Null(tablet.Serial);
        Assert.Equal(["Vendor Specific Class"], tablet.Classes);
        Assert.Equal(["Human Interface Device"], devices[0].Classes); // de-duplicated
        Assert.Equal("0951:1780", devices[1].Label);                  // blank descriptors fall back to the ids
        Assert.Equal("0951:1780/ABC123", devices[1].ToSelector().Id); // a serial is kept when the device reports one
        Assert.Equal(["incus", "query", "/1.0/resources"], runner.Calls.Single());
    }

    [Fact]
    public void A_resources_document_without_a_usb_section_is_an_empty_list()
    {
        Assert.Empty(IncusUsbCatalog.Parse("""{"cpu":{"total":2}}"""));
        Assert.Empty(IncusUsbCatalog.Parse(""));
        Assert.Empty(IncusUsbCatalog.Parse("not json"));
    }
}
