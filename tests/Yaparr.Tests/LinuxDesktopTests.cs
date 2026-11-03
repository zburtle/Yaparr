using Yaparr.Desktop;

namespace Yaparr.Tests;

public class LinuxDesktopTests
{
    [Fact]
    public void Off_Linux_the_desktop_calls_do_nothing_and_do_not_fail()
    {
        if (LinuxDesktop.Available) return;    // on Linux they would really show a pop-up
        LinuxDesktop.Notify("title", "body");
        LinuxDesktop.SetUnread(3);
        LinuxDesktop.DemandAttention(new IntPtr(1), on: true);
    }

    [Fact]
    public void The_record_handed_to_the_windows_tray_has_the_size_windows_expects()
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8) return;
        Assert.Equal(976, WindowsToast.RecordSize);
    }
}
