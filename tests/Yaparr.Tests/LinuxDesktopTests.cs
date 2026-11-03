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
}
