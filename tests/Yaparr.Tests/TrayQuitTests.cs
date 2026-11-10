using Avalonia.Controls;
using Yaparr.Desktop;

namespace Yaparr.Tests;

// Closing with X hides Yaparr in the notification area, but a close request that arrives while it is already out of the way is the desktop's Quit, and quits.
public class TrayQuitTests
{
    [Fact]
    public void A_click_on_X_on_a_visible_window_goes_to_the_tray()
    {
        Assert.True(MainWindow.ShouldKeepRunning(WindowCloseReason.WindowClosing, quitting: false, closeToTray: true, hasTray: true, alreadyOutOfTheWay: false));
    }

    [Fact]
    public void A_close_request_while_the_window_is_hidden_or_minimized_really_quits()
    {
        Assert.False(MainWindow.ShouldKeepRunning(WindowCloseReason.WindowClosing, quitting: false, closeToTray: true, hasTray: true, alreadyOutOfTheWay: true));
    }

    [Theory]
    [InlineData(true, true, true)]     // Exit was chosen
    [InlineData(false, false, true)]   // close to the tray is off
    [InlineData(false, true, false)]   // there is no tray icon
    public void Nothing_else_goes_to_the_tray(bool quitting, bool closeToTray, bool hasTray)
    {
        Assert.False(MainWindow.ShouldKeepRunning(WindowCloseReason.WindowClosing, quitting, closeToTray, hasTray, alreadyOutOfTheWay: false));
        Assert.False(MainWindow.ShouldKeepRunning(WindowCloseReason.ApplicationShutdown, false, true, true, false));
        Assert.False(MainWindow.ShouldKeepRunning(WindowCloseReason.OSShutdown, false, true, true, false));
    }
}
