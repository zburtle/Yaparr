// Measures what the encoder settings do: synthetic clips of a desktop, a video and a game go through the real encoder in this program (NVIDIA's, through FFmpeg's libraries,
// set YAPARR_FFMPEG_LIBS), once for each setting asked for, and for each run it says how big the key frames and the biggest frames are, how much the encoder puts out in the
// worst tenth of a second (a burst is what a weak link loses packets in), the bitrate it really made, and the quality (PSNR of the luma against the clip).
//
//   Yaparr.EncoderTuning [--clips desktop,video,game] [--buffer 0.25,0.5,1,1.5] [--key 1,2,4] [--maxrate 1.5] [--seconds 12] [--csv file]
//
// The clips are made here, the same every time, so that runs can be compared (see docs/encoder-tuning.md for what each one stands for and what came out).
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Yaparr.Screen;

var cli = Cli.Parse(args);
if (!FfmpegLibraries.TryLoad(out var why)) { Console.Error.WriteLine("FFmpeg's libraries are needed (set YAPARR_FFMPEG_LIBS): " + why); return 1; }

var rows = new List<Row>();
Console.WriteLine("clip | size@fps, kbit/s | key s | buffer s | max x | made kbit/s | key avg KB | key max KB | frame max KB | peak 100 ms Mbit/s | PSNR dB | encode s");
foreach (var scene in cli.Scenes)
    foreach (var key in cli.Keys)
        foreach (var buffer in cli.Buffers)
            foreach (var maxRate in cli.MaxRates)
            {
                var row = Run(scene, key, buffer, maxRate, cli.Seconds);
                rows.Add(row);
                Console.WriteLine(row.ToString());
            }
if (cli.Csv != null) File.WriteAllLines(cli.Csv, new[] { Row.Header }.Concat(rows.Select(r => r.ToCsv())));
return 0;

static Row Run(Scene scene, double keySeconds, double bufferSeconds, double maxRate, int seconds)
{
    var options = new ScreenOptions
    {
        Fps = scene.Fps, Kbps = scene.Kbps, MaxWidth = scene.Width, MaxHeight = scene.Height, KeyFrameSeconds = keySeconds, BufferSeconds = bufferSeconds, MaxRateMultiple = maxRate,
        X11Display = null,
    };
    int frames = seconds * scene.Fps;
    var sizes = new List<(int Bytes, bool Key)>();
    var packets = new List<byte[]>();
    var clock = Stopwatch.StartNew();
    using (var encoder = new InProcessEncoder(scene.Width, scene.Height, options))
    {
        var generator = scene.Make();
        var picture = new byte[encoder.PictureBytes];
        for (int n = 0; n < frames; n++)
        {
            generator.Fill(n, picture);
            encoder.Encode(picture, false, (packet, key) => { sizes.Add((packet.Length, key)); packets.Add(packet); });
        }
    }
    double encodeSeconds = clock.Elapsed.TotalSeconds;
    double psnr = Psnr(scene, packets, frames);
    return Row.Of(scene, keySeconds, bufferSeconds, maxRate, seconds, sizes, psnr, encodeSeconds);
}

// The luma of what comes out of the encoder, decoded by the viewer's decoder in this program, against the luma that went in (made again, the clips are deterministic).
static double Psnr(Scene scene, List<byte[]> packets, int frames)
{
    using var decoder = new InProcessDecoder(ScreenCodec.H264);
    var generator = scene.Make();
    var source = new byte[scene.Width * scene.Height * 3 / 2];
    int lumaBytes = scene.Width * scene.Height;
    double sum = 0; int counted = 0;
    foreach (var packet in packets)
    {
        decoder.Decode(packet, 0, 0, (decoded, w, h) =>
        {
            if (counted >= frames || w != scene.Width || h != scene.Height) return;
            generator.Fill(counted, source);
            long squares = 0;
            for (int i = 0; i < lumaBytes; i++) { int d = source[i] - decoded[i]; squares += d * d; }
            double mse = squares / (double)lumaBytes;
            sum += mse == 0 ? 60 : Math.Min(60, 10 * Math.Log10(255.0 * 255.0 / mse));
            counted++;
        });
    }
    return counted == 0 ? double.NaN : sum / counted;
}

sealed class Cli
{
    public List<Scene> Scenes { get; } = new();
    public double[] Keys { get; private set; } = { 1, 2, 4 };
    public double[] Buffers { get; private set; } = { 0.25, 0.5, 1, 1.5 };
    public double[] MaxRates { get; private set; } = { 1.5 };
    public int Seconds { get; private set; } = 12;
    public string? Csv { get; private set; }
    public int? Fps { get; private set; }
    public int? Kbps { get; private set; }
    public (int Width, int Height)? Size { get; private set; }

    public static Cli Parse(string[] args)
    {
        var cli = new Cli();
        string clips = "desktop,video,game";
        string? Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        static double[] Numbers(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        if (Arg("--clips") is { } c) clips = c;
        if (Arg("--key") is { } k) cli.Keys = Numbers(k);
        if (Arg("--buffer") is { } b) cli.Buffers = Numbers(b);
        if (Arg("--maxrate") is { } m) cli.MaxRates = Numbers(m);
        if (Arg("--seconds") is { } s) cli.Seconds = int.Parse(s, CultureInfo.InvariantCulture);
        cli.Csv = Arg("--csv");
        if (Arg("--fps") is { } f) cli.Fps = int.Parse(f, CultureInfo.InvariantCulture);
        if (Arg("--kbps") is { } r) cli.Kbps = int.Parse(r, CultureInfo.InvariantCulture);
        if (Arg("--size") is { } z) { var parts = z.Split('x'); cli.Size = (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture)); }
        foreach (var name in clips.Split(',', StringSplitOptions.RemoveEmptyEntries))
            cli.Scenes.AddRange(Scene.Named(name, cli.Size, cli.Fps, cli.Kbps));
        return cli;
    }
}

/// <summary>One clip at one size, rate and bitrate.</summary>
sealed record Scene(string Name, int Width, int Height, int Fps, int Kbps, Func<IClip> Make)
{
    // With a size, a rate or a bitrate asked for, one scene of the clip at that (the others are left at what the clip has).
    public static IEnumerable<Scene> Named(string name, (int Width, int Height)? size, int? fps, int? kbps)
    {
        var scenes = Named(name).ToList();
        if (size == null && fps == null && kbps == null) return scenes;
        var first = scenes[0];
        int w = size?.Width ?? first.Width, h = size?.Height ?? first.Height, f = fps ?? first.Fps;
        return new[] { first with { Width = w, Height = h, Fps = f, Kbps = kbps ?? first.Kbps, Make = MakeClip(name, w, h, f) } };
    }

    private static Func<IClip> MakeClip(string name, int w, int h, int fps) => name switch
    {
        "desktop" => () => new DesktopClip(w, h, fps),
        "video" => () => new VideoClip(w, h, fps),
        "game" => () => new GameClip(w, h, fps),
        _ => throw new ArgumentException("Unknown clip " + name),
    };

    public static IEnumerable<Scene> Named(string name) => name switch
    {
        // Text and windows: still most of the time, a page scrolled now and then, a cursor, typing. What a desktop share is.
        "desktop" => new[] { new Scene("desktop", 1920, 1080, 30, 4000, () => new DesktopClip(1920, 1080, 30)), new Scene("desktop", 1280, 720, 30, 2000, () => new DesktopClip(1280, 720, 30)) },
        // Smooth, soft and grainy motion over the whole picture, with moving shapes. What a film or a video call is.
        "video" => new[] { new Scene("video", 1920, 1080, 30, 4000, () => new VideoClip(1920, 1080, 30)), new Scene("video", 1280, 720, 30, 2000, () => new VideoClip(1280, 720, 30)) },
        // Sharp detail panning fast, parallax, sprites, a fixed overlay and a cut every few seconds. What a game is.
        "game" => new[] { new Scene("game", 1920, 1080, 60, 6000, () => new GameClip(1920, 1080, 60)) },
        _ => throw new ArgumentException("Unknown clip " + name + " (desktop, video, game)."),
    };
}

interface IClip { void Fill(int frame, byte[] nv12); }

sealed class Rng(ulong seed)
{
    private ulong _s = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
    public uint Next() { _s ^= _s << 13; _s ^= _s >> 7; _s ^= _s << 17; return (uint)(_s >> 16); }
    public int Next(int n) => (int)(Next() % (uint)n);
}

sealed class DesktopClip : IClip
{
    private readonly int _w, _h, _fps;
    private readonly byte[] _doc;       // a tall page of text, luma only
    private readonly int _docHeight;
    private const int Cell = 9, Line = 18;

    public DesktopClip(int w, int h, int fps)
    {
        (_w, _h, _fps) = (w, h, fps);
        _docHeight = h * 4;
        _doc = new byte[w * _docHeight];
        Array.Fill(_doc, (byte)238);
        var rng = new Rng(7);
        var glyphs = new bool[40][];
        for (int g = 0; g < glyphs.Length; g++) { glyphs[g] = new bool[5 * 9]; for (int i = 0; i < glyphs[g].Length; i++) glyphs[g][i] = rng.Next(100) < 42; }
        for (int row = 0; row + Line < _docHeight; row += Line)
        {
            int x = 40 + rng.Next(3) * 24;
            int words = 3 + rng.Next(9);
            for (int word = 0; word < words && x + 60 < w - 40; word++)
            {
                int letters = 2 + rng.Next(8);
                for (int l = 0; l < letters && x + Cell < w - 40; l++, x += Cell) Stamp(glyphs[rng.Next(glyphs.Length)], x, row + 4);
                x += Cell;
            }
        }
    }

    private void Stamp(bool[] glyph, int x, int y)
    {
        for (int gy = 0; gy < 9; gy++)
            for (int gx = 0; gx < 5; gx++)
                if (glyph[gy * 5 + gx]) _doc[(y + gy) * _w + x + gx] = 36;
    }

    public void Fill(int n, byte[] nv12)
    {
        // The page is scrolled three lines every two seconds (a jump, as a mouse wheel does), and in between it does not move.
        int jumps = n / (_fps * 2);
        int offset = (jumps * 3 * Line) % (_docHeight - _h);
        int titleBar = 56;
        for (int y = 0; y < _h; y++)
        {
            if (y < titleBar) { Array.Fill(nv12, (byte)90, y * _w, _w); continue; }
            Buffer.BlockCopy(_doc, (y - titleBar + offset) * _w, nv12, y * _w, _w);
        }
        // Typing: a few marks a second at a place that moves along; a cursor that blinks twice a second.
        int typed = n * 6 / _fps;
        for (int i = 0; i < typed % 120; i++)
        {
            int x = 60 + i * 6, y = titleBar + 300 + (i / 20) * Line;
            for (int gy = 0; gy < 9 && y + gy < _h; gy++) for (int gx = 0; gx < 4 && x + gx < _w; gx++) nv12[(y + gy) * _w + x + gx] = (byte)(((i * 7 + gx * 3 + gy) % 3 == 0) ? 40 : 236);
        }
        if ((n * 2 / _fps) % 2 == 0)
        {
            int cx = 60 + (typed % 120) * 6, cy = titleBar + 300 + ((typed % 120) / 20) * Line;
            for (int gy = 0; gy < 14 && cy + gy < _h; gy++) for (int gx = 0; gx < 2 && cx + gx < _w; gx++) nv12[(cy + gy) * _w + cx + gx] = 20;
        }
        // Colour: a blue title bar, grey elsewhere.
        int uv = _w * _h;
        for (int y = 0; y < _h / 2; y++)
            for (int x = 0; x < _w; x += 2)
            {
                bool bar = y * 2 < titleBar;
                nv12[uv + y * _w + x] = (byte)(bar ? 165 : 128);
                nv12[uv + y * _w + x + 1] = (byte)(bar ? 95 : 128);
            }
    }
}

sealed class VideoClip : IClip
{
    private readonly int _w, _h, _fps;
    private readonly float[] _sinX, _cosY;
    public VideoClip(int w, int h, int fps)
    {
        (_w, _h, _fps) = (w, h, fps);
        _sinX = new float[w + 4000];
        _cosY = new float[h + 4000];
        for (int i = 0; i < _sinX.Length; i++) _sinX[i] = MathF.Sin(i / 90f);
        for (int i = 0; i < _cosY.Length; i++) _cosY[i] = MathF.Cos(i / 70f);
    }

    public void Fill(int n, byte[] nv12)
    {
        var rng = new Rng(1000u + (ulong)n * 7919);
        var diagonal = new float[_w + _h];
        for (int i = 0; i < diagonal.Length; i++) diagonal[i] = MathF.Sin(i / 37f - n / 10f);
        int panX = n * 3 % 4000, panY = n * 2 % 4000;
        for (int y = 0; y < _h; y++)
        {
            float cy = _cosY[y + panY];
            int row = y * _w;
            for (int x = 0; x < _w; x++)
            {
                float v = 128 + 55 * _sinX[x + panX] * cy + 22 * diagonal[x + y];
                v += (int)(rng.Next() & 7) - 3.5f;   // film grain
                nv12[row + x] = (byte)Math.Clamp(v, 16, 235);
            }
        }
        // Two bright shapes moving across.
        for (int s = 0; s < 2; s++)
        {
            int size = 140 + s * 60, x0 = (n * (5 + s * 3) + s * 400) % (_w - size), y0 = (_h / 4 + s * _h / 3 + (int)(60 * MathF.Sin(n / 15f))) % (_h - size);
            for (int y = y0; y < y0 + size; y++) for (int x = x0; x < x0 + size; x++) nv12[y * _w + x] = (byte)(180 + ((x - x0 + y - y0) % 32));
        }
        int uv = _w * _h;
        for (int y = 0; y < _h / 2; y++)
            for (int x = 0; x < _w; x += 2)
            {
                nv12[uv + y * _w + x] = (byte)(128 + 30 * MathF.Sin(x / 200f + n / 30f));
                nv12[uv + y * _w + x + 1] = (byte)(128 + 30 * MathF.Cos(y / 120f - n / 40f));
            }
    }
}

sealed class GameClip : IClip
{
    private readonly int _w, _h, _fps;
    private readonly byte[][] _far = new byte[3][], _near = new byte[3][];   // textures per scene, twice the picture wide so they can wrap
    public GameClip(int w, int h, int fps)
    {
        (_w, _h, _fps) = (w, h, fps);
        for (int s = 0; s < 3; s++) { _far[s] = Texture(2 * w, h / 2, (ulong)(21 + s), 24); _near[s] = Texture(2 * w, h / 2, (ulong)(77 + s), 10); }
    }

    private static byte[] Texture(int w, int h, ulong seed, int block)
    {
        var rng = new Rng(seed);
        var t = new byte[w * h];
        for (int by = 0; by < h; by += block)
            for (int bx = 0; bx < w; bx += block)
            {
                int level = 40 + rng.Next(170), edge = rng.Next(4);
                for (int y = by; y < Math.Min(h, by + block); y++)
                    for (int x = bx; x < Math.Min(w, bx + block); x++)
                    {
                        bool line = edge == 0 && (x - bx) % 6 == 0 || edge == 1 && (x + y) % 11 == 0;
                        t[y * w + x] = (byte)(line ? 235 - level / 3 : level + (int)(rng.Next() & 15));
                    }
            }
        return t;
    }

    public void Fill(int n, byte[] nv12)
    {
        int scene = n / (_fps * 4) % 3;   // a cut every four seconds
        int farShift = n * 5 % _w, nearShift = n * 14 % _w;
        int half = _h / 2;
        for (int y = 0; y < _h; y++)
        {
            var texture = y < half ? _far[scene] : _near[scene];
            int shift = y < half ? farShift : nearShift;
            int ty = y < half ? y : y - half;
            Buffer.BlockCopy(texture, ty * 2 * _w + shift, nv12, y * _w, _w);
        }
        // Sprites, and a fixed overlay (a bar and a mini-map) as a game has.
        for (int s = 0; s < 6; s++)
        {
            int size = 48 + s * 6, x0 = (n * (6 + s * 2) + s * 300) % (_w - size), y0 = (200 + s * 110 + (int)(80 * MathF.Sin(n / 9f + s))) % (_h - size);
            for (int y = y0; y < y0 + size; y++) for (int x = x0; x < x0 + size; x++) nv12[y * _w + x] = (byte)((x + y + s * 17) % 2 == 0 ? 245 : 30);
        }
        for (int y = _h - 90; y < _h - 20; y++) for (int x = 60; x < 700; x++) nv12[y * _w + x] = (byte)(x < 60 + (n * 3 % 640) ? 220 : 50);
        for (int y = 40; y < 280; y++) for (int x = _w - 300; x < _w - 40; x++) nv12[y * _w + x] = (byte)(((x / 16) + (y / 16)) % 2 == 0 ? 90 : 160);
        int uv = _w * _h;
        for (int y = 0; y < _h / 2; y++)
            for (int x = 0; x < _w; x += 2)
            {
                nv12[uv + y * _w + x] = (byte)(110 + (x * 40 / _w));
                nv12[uv + y * _w + x + 1] = (byte)(140 - (y * 50 / (_h / 2)));
            }
    }
}

sealed record Row(string Clip, string Size, double Key, double Buffer, double MaxRate, double MadeKbps, double KeyAvgKb, double KeyMaxKb, double FrameMaxKb, double Peak100, double Psnr, double EncodeSeconds)
{
    public const string Header = "clip,size_fps_kbps,key_s,buffer_s,maxrate_x,made_kbps,key_avg_kb,key_max_kb,frame_max_kb,peak100_mbps,psnr_db,encode_s";

    public static Row Of(Scene scene, double key, double buffer, double maxRate, int seconds, List<(int Bytes, bool Key)> sizes, double psnr, double encodeSeconds)
    {
        double total = sizes.Sum(s => (double)s.Bytes);
        var keys = sizes.Where(s => s.Key).Select(s => s.Bytes).ToList();
        // The most put out in any tenth of a second: the sum of consecutive frames that cover 100 ms.
        int window = Math.Max(1, (int)Math.Round(scene.Fps / 10.0));
        double peak = 0, run = 0;
        for (int i = 0; i < sizes.Count; i++)
        {
            run += sizes[i].Bytes;
            if (i >= window) run -= sizes[i - window].Bytes;
            if (i >= window - 1) peak = Math.Max(peak, run);
        }
        double windowSeconds = window / (double)scene.Fps;
        return new Row(scene.Name, $"{scene.Width}x{scene.Height}@{scene.Fps}, {scene.Kbps}", key, buffer, maxRate, total * 8 / 1000 / seconds,
            keys.Count == 0 ? 0 : keys.Average() / 1024, keys.Count == 0 ? 0 : keys.Max() / 1024, sizes.Max(s => s.Bytes) / 1024.0, peak * 8 / 1e6 / windowSeconds, psnr, encodeSeconds);
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{Clip} | {Size} | {Key:0.##} | {Buffer:0.##} | {MaxRate:0.##} | {MadeKbps:0} | {KeyAvgKb:0} | {KeyMaxKb:0} | {FrameMaxKb:0} | {Peak100:0.0} | {Psnr:0.0} | {EncodeSeconds:0.0}");

    public string ToCsv() => string.Create(CultureInfo.InvariantCulture,
        $"{Clip},{Size.Replace(',', ';')},{Key},{Buffer},{MaxRate},{MadeKbps:0},{KeyAvgKb:0.0},{KeyMaxKb:0.0},{FrameMaxKb:0.0},{Peak100:0.00},{Psnr:0.00},{EncodeSeconds:0.0}");
}
