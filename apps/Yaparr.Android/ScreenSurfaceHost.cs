using Android.Graphics;
using Android.Views;
using Avalonia.Android;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Yaparr.AndroidApp;

/// <summary>
/// A piece of the screen that the phone's video decoder draws on directly: a native Android view (a <see cref="TextureView"/>) inside the Avalonia page.
/// <see cref="SurfaceReady"/> gives the surface to decode onto once Android has made it, and <see cref="SurfaceLost"/> says it is going.
/// </summary>
internal sealed class ScreenSurfaceHost : NativeControlHost
{
    private TextureView? _view;
    private Surface? _surface;

    /// <summary>The surface, while there is one.</summary>
    public Surface? CurrentSurface => _surface;

    /// <summary>The surface can be decoded onto now.</summary>
    public event Action<Surface>? SurfaceReady;

    /// <summary>The surface is gone (the page was closed or the phone left the app): stop drawing on it.</summary>
    public event Action? SurfaceLost;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var context = MainActivity.Instance ?? throw new InvalidOperationException("There is no screen to show a picture on.");
        _view = new TextureView(context) { SurfaceTextureListener = new Listener(this) };
        return new AndroidViewControlHandle(_view);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (_view != null) _view.SurfaceTextureListener = null;
        ReleaseSurface();
        base.DestroyNativeControlCore(control);
        _view = null;
    }

    private void ReleaseSurface()
    {
        var surface = _surface;
        _surface = null;
        if (surface == null) return;
        SurfaceLost?.Invoke();
        surface.Release();
    }

    private sealed class Listener : Java.Lang.Object, TextureView.ISurfaceTextureListener
    {
        private readonly ScreenSurfaceHost _host;
        public Listener(ScreenSurfaceHost host) => _host = host;

        public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height)
        {
            _host._surface = new Surface(surface);
            _host.SurfaceReady?.Invoke(_host._surface);
        }

        public bool OnSurfaceTextureDestroyed(SurfaceTexture surface)
        {
            _host.ReleaseSurface();
            return true;
        }

        public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height) { }
        public void OnSurfaceTextureUpdated(SurfaceTexture surface) { }
    }
}
