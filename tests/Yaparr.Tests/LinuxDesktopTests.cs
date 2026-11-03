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

    [Fact]
    public void The_bug_report_link_names_the_form_and_escapes_what_it_fills_in()
    {
        var link = Updater.IssueFormUrl("bug_report.yml", "1.2.3", "Windows 11 (x64) & more");
        Assert.StartsWith("https://github.com/", link);
        Assert.Contains("/issues/new?template=bug_report.yml&version=1.2.3&system=Windows%2011%20%28x64%29%20%26%20more", link);
    }
}
