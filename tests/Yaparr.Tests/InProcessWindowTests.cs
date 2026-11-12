using System.Runtime.InteropServices;
using Yaparr.Chat;
using Yaparr.Screen;

namespace Yaparr.Tests;

/// <summary>
/// A window shared with the capture and the encoder in this program, while it is moved, resized, minimised, restored and closed: the things a real
/// window does that a monitor does not. It needs Windows, FFmpeg's libraries with the Graphics Capture fixes of October 2026, an NVIDIA card and a
/// desktop to open a console window on, so it does nothing on the build server.
/// </summary>
public class InProcessWindowTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    private const int SW_MINIMIZE = 6, SW_RESTORE = 9;

    /// <summary>A plain window on a thread of its own, repainted in changing colours so that the capture has something new to hand over.</summary>
    private sealed class TestWindow : IDisposable
    {
        private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WndClass
        {
            public uint Size, Style; public IntPtr Proc; public int ClassExtra, WindowExtra; public IntPtr Instance, Icon, Cursor, Background;
            public string? Menu, ClassName; public IntPtr IconSmall;
        }

        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct Msg { public IntPtr Hwnd; public uint Message; public IntPtr W, L; public uint Time; public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct Paint { public IntPtr Dc; public bool Erase; public Rect Area; public bool Restore, Update; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WndClass c);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(int ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern int GetMessage(out Msg m, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg m);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Msg m);
        [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint ms, IntPtr proc);
        [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
        [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out Paint p);
        [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref Paint p);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect r);
        [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref Rect r, IntPtr brush);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint colour);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);

        private const uint WM_DESTROY = 2, WM_PAINT = 0xF, WM_CLOSE = 0x10, WM_TIMER = 0x113, WS_OVERLAPPEDWINDOW = 0xCF0000, WS_VISIBLE = 0x10000000;
        private readonly WndProc _proc;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        public IntPtr Handle { get; private set; }
        public bool Closed { get; private set; }

        public TestWindow(int x, int y, int width, int height)
        {
            _proc = Handle_;
            _thread = new Thread(() => Run(x, y, width, height)) { IsBackground = true };
            _thread.Start();
            _ready.Wait(10_000);
        }

        private int _tick;

        private IntPtr Handle_(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            switch (message)
            {
                case WM_TIMER: InvalidateRect(hwnd, IntPtr.Zero, false); return IntPtr.Zero;
                case WM_PAINT:
                {
                    var dc = BeginPaint(hwnd, out var paint);
                    GetClientRect(hwnd, out var area);
                    uint colour = (uint)(((_tick * 7) & 255) | (((_tick * 3) & 255) << 8) | (((_tick * 11) & 255) << 16));
                    _tick++;
                    var brush = CreateSolidBrush(colour);
                    FillRect(dc, ref area, brush);
                    DeleteObject(brush);
                    EndPaint(hwnd, ref paint);
                    return IntPtr.Zero;
                }
                case WM_DESTROY: Closed = true; PostQuitMessage(0); return IntPtr.Zero;
            }
            return DefWindowProc(hwnd, message, wParam, lParam);
        }

        private void Run(int x, int y, int width, int height)
        {
            var cls = new WndClass { Size = (uint)Marshal.SizeOf<WndClass>(), Proc = Marshal.GetFunctionPointerForDelegate(_proc), ClassName = "YaparrTestWindow" + Environment.TickCount64, Background = IntPtr.Zero };
            RegisterClassEx(ref cls);
            Handle = CreateWindowEx(0, cls.ClassName!, "yaparr-window-test", WS_OVERLAPPEDWINDOW | WS_VISIBLE, x, y, width, height, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            SetTimer(Handle, (IntPtr)1, 16, IntPtr.Zero);
            _ready.Set();
            while (GetMessage(out var m, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref m); DispatchMessage(ref m); }
        }

        public void Close() => PostMessage(Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

        public void Dispose() { if (!Closed) Close(); _thread.Join(3000); }
    }

    [Theory]
    [InlineData(true)]    // pictures kept on the graphics card
    [InlineData(false)]   // copied to memory
    public async Task A_window_that_is_moved_resized_minimised_restored_and_closed_is_shared_all_the_way_and_the_end_is_said_plainly(bool onCard)
    {
        if (!OperatingSystem.IsWindows() || !FfmpegLibraries.IsAvailable) return;
        if (FfmpegLocator.Find() is not { } ffmpeg) return;
        using var window = new TestWindow(100, 100, 800, 500);
        if (window.Handle == IntPtr.Zero) { output.WriteLine("no window to share"); return; }
        try
        {
            long handle = window.Handle.ToInt64();
            MoveWindow((IntPtr)handle, 100, 100, 800, 500, true);
            await Task.Delay(500);
            using var cluster = await MediaTreeTests.Cluster.StartAsync(2, fanout: 2, rootFanout: 2);
            var options = new ScreenOptions
            {
                WindowHandle = handle, X11Display = null, Fps = 30, Kbps = 2000, KeyFrameSeconds = 1, MaxWidth = 1920, MaxHeight = 1080,
                Codec = ScreenCodec.H264, Encoder = HardwareEncoder.Nvenc, InProcessEncoder = true, InProcessCapture = true, CaptureOnCard = onCard, InProcessAsked = true,
            };
            using var sharer = ScreenSharer.Start(cluster.Root, cluster.Rooms[0], "Test window", options, ffmpeg);
            Exception? ended = null;
            sharer.Ended += ex => ended = ex;
            try { await TestUtil.WaitFor(() => sharer.Frames >= 45, 30_000); }
            catch (TimeoutException) { output.WriteLine(sharer.Describe()); throw; }
            if (!sharer.InProcess) return;   // no NVIDIA encoder here
            if (!sharer.CaptureInProcess) { Assert.Contains("too old", sharer.FallbackReason ?? ""); return; }
            Assert.Equal(onCard, sharer.CaptureOnCard);
            var size = sharer.PictureSize;
            // The same picture with or without the card: the size the capture itself makes of the window (its client area, 784x461 here, made even).
            Assert.Equal((784, 460), size);
            output.WriteLine($"sharing a window of 800x500: picture {size}, {sharer.Frames} frames");

            async Task FramesKeepComing(string what)
            {
                long before = sharer.Frames;
                await Task.Delay(1500);
                long after = sharer.Frames;
                output.WriteLine($"{what}: {after - before} frames in 1.5 s");
                Assert.True(after - before >= 20, $"{what}: only {after - before} frames in 1.5 s");
                Assert.Null(ended);
            }

            MoveWindow((IntPtr)handle, 300, 200, 1200, 700, true);   // larger, another shape
            await FramesKeepComing("after making it larger");
            MoveWindow((IntPtr)handle, 300, 200, 500, 400, true);    // smaller
            await FramesKeepComing("after making it smaller");
            Assert.Equal(size, sharer.PictureSize);                  // the picture keeps the size it started with

            ShowWindow((IntPtr)handle, SW_MINIMIZE);
            await FramesKeepComing("while minimised");               // the capture has nothing new; the share's clock goes on
            ShowWindow((IntPtr)handle, SW_RESTORE);
            await FramesKeepComing("after restoring it");

            // Closing it: the share ends, and says why in plain words.
            window.Close();
            try { await TestUtil.WaitFor(() => ended != null, 15_000); }
            catch (TimeoutException) { output.WriteLine(sharer.Describe()); throw; }
            output.WriteLine("ended: " + ended!.Message);
            Assert.Contains("window you were sharing was closed", ended.Message);
        }
        finally { window.Dispose(); }
    }

    [Fact]
    public async Task A_share_after_a_share_in_this_program_works_the_same()
    {
        if (!OperatingSystem.IsWindows() || !FfmpegLibraries.IsAvailable) return;
        if (FfmpegLocator.Find() is not { } ffmpeg) return;
        if (Displays.List().FirstOrDefault() is not { } display) return;
        using var cluster = await MediaTreeTests.Cluster.StartAsync(2, fanout: 2, rootFanout: 2);
        for (int round = 1; round <= 3; round++)
        {
            var options = new ScreenOptions
            {
                Display = display, X11Display = null, Fps = 30, Kbps = 1500, KeyFrameSeconds = 1, MaxWidth = 960, MaxHeight = 540,
                Codec = ScreenCodec.H264, Encoder = HardwareEncoder.Nvenc, InProcessEncoder = true, InProcessCapture = true, CaptureOnCard = true, InProcessAsked = true,
            };
            using var sharer = ScreenSharer.Start(cluster.Root, cluster.Rooms[0], "Test screen " + round, options, ffmpeg);
            try { await TestUtil.WaitFor(() => sharer.Frames >= 30, 30_000); }
            catch (TimeoutException) { output.WriteLine(sharer.Describe()); throw; }
            if (!sharer.CaptureOnCard) return;   // not here
            output.WriteLine($"round {round}: {sharer.Frames} frames, picture {sharer.PictureSize}");
        }
    }
}
