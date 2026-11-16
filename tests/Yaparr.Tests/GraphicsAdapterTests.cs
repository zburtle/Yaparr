using Yaparr.Screen;

namespace Yaparr.Tests;

/// <summary>Auto leaves out the graphics card encoders whose maker has no adapter in the computer (issue #55).</summary>
public class GraphicsAdapterTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly Display Monitor = new(0, "x", 0, 0, 1920, 1080, true, Handle: 0x1A2B);

    [Theory]
    [InlineData(@"PCI\VEN_10DE&DEV_2684&SUBSYS_89F410DE&REV_A1", 0x10DEu)]
    [InlineData(@"pci\ven_8086&dev_a780&subsys_0a081028&rev_04", 0x8086u)]
    [InlineData(@"PCI\VEN_1002&DEV_73BF", 0x1002u)]
    public void The_maker_is_read_from_the_windows_device_id_in_any_case(string id, uint expected) => Assert.Equal(expected, GraphicsAdapters.ParseWindowsDeviceId(id));

    [Theory]
    [InlineData(@"root\basicdisplay")]      // the basic adapter has no maker
    [InlineData(@"ROOT\RdpIdd_IndirectDisplay")]
    [InlineData("")]
    [InlineData(null)]
    public void A_device_with_no_vendor_id_is_not_counted(string? id) => Assert.Null(GraphicsAdapters.ParseWindowsDeviceId(id));

    [Theory]
    [InlineData("0x10de\n", 0x10DEu)]
    [InlineData("0x8086", 0x8086u)]
    [InlineData("0x1002\r\n", 0x1002u)]
    [InlineData("1002", 0x1002u)]
    public void The_maker_is_read_from_the_linux_vendor_file(string text, uint expected) => Assert.Equal(expected, GraphicsAdapters.ParseSysfsVendor(text));

    [Theory]
    [InlineData("")]
    [InlineData("not a number")]
    [InlineData(null)]
    public void An_unreadable_vendor_file_gives_nothing(string? text) => Assert.Null(GraphicsAdapters.ParseSysfsVendor(text));

    [Theory]
    [InlineData(0x10DEu, GpuVendor.Nvidia)]
    [InlineData(0x1002u, GpuVendor.Amd)]
    [InlineData(0x8086u, GpuVendor.Intel)]
    [InlineData(0x13B5u, GpuVendor.Other)]   // Arm's Mali
    public void Makers_are_told_by_their_pci_vendor_id(uint id, GpuVendor expected) => Assert.Equal(expected, GraphicsAdapters.VendorOf(id));

    [Fact]
    public void An_encoder_needs_an_adapter_of_its_maker_and_nothing_is_left_out_when_the_adapters_are_not_known()
    {
        var intelOnly = new[] { GpuVendor.Intel };
        Assert.False(GraphicsAdapters.CanHave(HardwareEncoder.Nvenc, intelOnly));
        Assert.False(GraphicsAdapters.CanHave(HardwareEncoder.Amf, intelOnly));
        Assert.True(GraphicsAdapters.CanHave(HardwareEncoder.Qsv, intelOnly));
        Assert.True(GraphicsAdapters.CanHave(HardwareEncoder.Vaapi, intelOnly));

        var nvidiaOnly = new[] { GpuVendor.Nvidia };
        Assert.True(GraphicsAdapters.CanHave(HardwareEncoder.Nvenc, nvidiaOnly));
        Assert.False(GraphicsAdapters.CanHave(HardwareEncoder.Vaapi, nvidiaOnly));   // VA-API drives Intel and AMD

        // A laptop with Intel graphics and an NVIDIA chip has both.
        var hybrid = new[] { GpuVendor.Intel, GpuVendor.Nvidia };
        Assert.True(GraphicsAdapters.CanHave(HardwareEncoder.Nvenc, hybrid));
        Assert.True(GraphicsAdapters.CanHave(HardwareEncoder.Qsv, hybrid));
        Assert.False(GraphicsAdapters.CanHave(HardwareEncoder.Amf, hybrid));

        // A maker VA-API may drive and we do not know keeps it.
        Assert.True(GraphicsAdapters.CanHave(HardwareEncoder.Vaapi, new[] { GpuVendor.Other }));

        // Not known: everything is tried, as before.
        foreach (var e in Enum.GetValues<HardwareEncoder>())
        {
            Assert.True(GraphicsAdapters.CanHave(e, null));
            Assert.True(GraphicsAdapters.CanHave(e, Array.Empty<GpuVendor>()));
        }
    }

    [Fact]
    public void Auto_tries_only_the_encoders_the_computer_can_have_and_an_encoder_asked_for_is_always_tried()
    {
        var intelOnly = ScreenSharer.Plan(new ScreenOptions { X11Display = null, Display = Monitor }, new[] { GpuVendor.Intel }, adaptersGiven: true);
        // Never NVIDIA's or AMD's on a computer with Intel graphics only; VP8 still last.
        Assert.DoesNotContain(intelOnly, p => p.Encoder is HardwareEncoder.Nvenc or HardwareEncoder.Amf);
        Assert.Equal((ScreenCodec.Vp8, (HardwareEncoder?)null), intelOnly[^1]);
        Assert.Contains(intelOnly, p => p.Encoder == (OperatingSystem.IsLinux() ? HardwareEncoder.Vaapi : HardwareEncoder.Qsv));

        // No adapter of a maker whose encoders there are: software only.
        var none = ScreenSharer.Plan(new ScreenOptions { X11Display = null, Display = Monitor }, new[] { GpuVendor.Other }, adaptersGiven: true);
        Assert.DoesNotContain(none, p => p.Encoder is HardwareEncoder.Nvenc or HardwareEncoder.Amf or HardwareEncoder.Qsv);

        // Not known: the whole list, as before.
        var unknown = ScreenSharer.Plan(new ScreenOptions { X11Display = null, Display = Monitor }, null, adaptersGiven: true, h265: false);
        Assert.Equal(HardwareEncoders.InOrder.Count + 1, unknown.Count);

        // Named on purpose: tried whatever the adapters are.
        var named = ScreenSharer.Plan(new ScreenOptions { X11Display = null, Display = Monitor, Encoder = HardwareEncoder.Amf }, new[] { GpuVendor.Intel }, adaptersGiven: true, h265: false);
        Assert.Equal(new (ScreenCodec, HardwareEncoder?)[] { (ScreenCodec.H264, HardwareEncoder.Amf), (ScreenCodec.Vp8, null) }, named);

        // AV1 asked for: on the same cards only, and then what Auto would have done.
        var av1 = ScreenSharer.Plan(new ScreenOptions { X11Display = null, Display = Monitor, Codec = ScreenCodec.Av1 }, new[] { GpuVendor.Nvidia }, adaptersGiven: true);
        Assert.Equal(new (ScreenCodec, HardwareEncoder?)[] { (ScreenCodec.Av1, HardwareEncoder.Nvenc), (ScreenCodec.H264, HardwareEncoder.Nvenc), (ScreenCodec.Vp8, null) }, av1);
    }

    [Fact]
    public void The_diagnostics_say_what_was_found_and_what_was_left_out()
    {
        Assert.Contains("not known", GraphicsAdapters.Describe(null));   // when the real ask fails too, it says so
        var line = GraphicsAdapters.Describe(new[] { GpuVendor.Intel });
        Assert.Contains("Intel", line);
        Assert.Contains("h264_nvenc", line);
        Assert.Contains("left out", line);
    }

    [Fact]
    public void The_registry_key_the_adapters_are_read_from_is_the_display_class()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(GraphicsAdapters.DisplayClassKey);
        Assert.NotNull(key);   // a wrong GUID here would not fail anywhere: no adapters would be found and every encoder would be tried, as before
        Assert.Equal("Display", key!.GetValue("Class"));
    }

    [Fact]
    public void The_adapters_of_this_computer_are_found_without_trouble()
    {
        // Whatever this computer has, asking must not throw, and what comes back is a list of makers or nothing.
        var vendors = GraphicsAdapters.Vendors;
        output.WriteLine("this computer: " + GraphicsAdapters.Describe());
        if (vendors != null) Assert.NotEmpty(vendors);
        Assert.NotEmpty(GraphicsAdapters.Installed(vendors).Concat(HardwareEncoders.InOrder));
    }
}
