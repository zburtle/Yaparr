using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Yaparr.Screen;

/// <summary>One playing sound stream of the sound server, from <c>pactl list sink-inputs</c>.</summary>
public sealed record SinkInput(int Index, int ProcessId, string Binary, string Application, bool Corked);

/// <summary>
/// Records sound on Linux, through the PulseAudio interface that both PulseAudio and PipeWire offer: either one program's (a shared
/// window's) or everything except Yaparr's own (a shared monitor, or anything on Wayland, where the portal does not say which program a
/// window belongs to), so that the voice chat is not sent back to the people in it; or one program picked by name. The programs'
/// streams are found with <c>pactl</c> and recorded with <c>parec --monitor-stream</c>; a program may start playing after sharing
/// began, so the streams are looked at once a second. Every stream that matches is recorded and they are mixed. The sound arrives as
/// stereo, 48 kHz, floating point, like <see cref="ProcessLoopback"/>.
/// </summary>
public sealed class PulseAppCapture : IDisposable
{
    public const int SampleRate = ProcessLoopback.SampleRate;
    public const int Channels = ProcessLoopback.Channels;

    private const int ChunkFrames = 960;                          // 20 ms
    private const int MaxBuffered = Channels * SampleRate / 5;    // 200 ms of one stream; older sound is dropped

    /// <summary>Whether the programs that find and record a program's sound are installed (package pulseaudio-utils).</summary>
    public static bool IsSupported => OperatingSystem.IsLinux() && PortalPipeline.Find("pactl") != null && PortalPipeline.Find("parec") != null;

    /// <summary>Reads <c>pactl list sink-inputs</c> (in the C locale).</summary>
    public static IReadOnlyList<SinkInput> ParseSinkInputs(string text)
    {
        var found = new List<SinkInput>();
        int index = -1, pid = 0;
        string binary = "", application = "";
        bool corked = false;
        void Finish()
        {
            if (index >= 0) found.Add(new SinkInput(index, pid, binary, application, corked));
            index = -1; pid = 0; binary = ""; application = ""; corked = false;
        }
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Sink Input #", StringComparison.Ordinal))
            {
                Finish();
                if (int.TryParse(line.AsSpan("Sink Input #".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) index = i;
            }
            else if (line.StartsWith("Corked:", StringComparison.Ordinal)) corked = line.EndsWith("yes", StringComparison.Ordinal);
            else if (Property(line, "application.process.id") is { } p) int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid);
            else if (Property(line, "application.process.binary") is { } b) binary = b;
            else if (Property(line, "application.name") is { } a) application = a;
        }
        Finish();
        return found;
    }

    // `key = "value"`
    private static string? Property(string line, string key)
    {
        if (!line.StartsWith(key, StringComparison.Ordinal)) return null;
        int eq = line.IndexOf('=', key.Length);
        if (eq < 0 || line[key.Length..eq].Trim().Length != 0) return null;
        var value = line[(eq + 1)..].Trim();
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
    }

    /// <summary>
    /// The streams to record for a program: those that are not paused and belong to it or to a program it started, or are named like it
    /// (its file name, or the window's class names, which for a browser or a game are what the sound server knows it by).
    /// </summary>
    public static IReadOnlyList<SinkInput> ChooseOf(IReadOnlyList<SinkInput> inputs, ISet<int> processIds, string program, IEnumerable<string>? windowClasses = null)
    {
        var names = new List<string> { program };
        if (windowClasses != null) names.AddRange(windowClasses);
        return inputs.Where(i => !i.Corked && ((i.ProcessId > 0 && processIds.Contains(i.ProcessId)) || names.Any(n => Named(i, n)))).ToList();
    }

    private static bool Named(SinkInput input, string name) =>
        name.Length > 0 && (string.Equals(input.Binary, name, StringComparison.OrdinalIgnoreCase) || string.Equals(input.Application, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The programs with a stream playing that could be picked to share the sound of, other than the excluded one, by name.</summary>
    public static IReadOnlyList<string> PlayingPrograms(IReadOnlyList<SinkInput> inputs, ISet<int> excludedProcessIds, string excludedProgram) =>
        ChooseAllExcept(inputs, excludedProcessIds, excludedProgram)
            .Select(i => i.Application.Length > 0 ? i.Application : i.Binary).Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The programs playing sound now, other than Yaparr (to offer as the source of a share's sound). Empty if the sound server cannot be reached.</summary>
    public static IReadOnlyList<string> ListPlayingPrograms()
    {
        if (!IsSupported || Run("pactl", "list sink-inputs") is not { } text) return Array.Empty<string>();
        return PlayingPrograms(ParseSinkInputs(text), ProcessTree(Environment.ProcessId), ProgramName(Environment.ProcessId));
    }

    /// <summary>Every stream that is not paused and does not belong to the excluded program (or a program it started, or one with its name).</summary>
    public static IReadOnlyList<SinkInput> ChooseAllExcept(IReadOnlyList<SinkInput> inputs, ISet<int> excludedProcessIds, string excludedProgram) =>
        inputs.Where(i => !i.Corked && !excludedProcessIds.Contains(i.ProcessId)
                && !(excludedProgram.Length > 0 && string.Equals(i.Binary, excludedProgram, StringComparison.Ordinal))).ToList();

    /// <summary>The program and the programs it started (children, and theirs), from /proc.</summary>
    public static HashSet<int> ProcessTree(int root)
    {
        var parents = new Dictionary<int, int>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)) continue;
                try
                {
                    // "pid (name) state ppid ...": the name may hold spaces and brackets, so the end of it is the last ')'.
                    var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                    var rest = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                    if (int.TryParse(rest[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parent)) parents[pid] = parent;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException or IndexOutOfRangeException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var tree = new HashSet<int> { root };
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var (pid, parent) in parents)
                if (!tree.Contains(pid) && tree.Contains(parent)) { tree.Add(pid); grew = true; }
        }
        return tree;
    }

    /// <summary>
    /// Adds sound streams that arrive at their own pace into one: <see cref="Add"/> on a stream's own thread, <see cref="Take"/> on the
    /// mixer's. Sound older than 200 ms is dropped, so a stream that is read slowly cannot make the others late.
    /// </summary>
    public sealed class Track
    {
        private readonly object _lock = new();
        private readonly float[] _buffer = new float[MaxBuffered];
        private int _count;

        public void Add(ReadOnlySpan<float> samples)
        {
            lock (_lock)
            {
                if (samples.Length >= _buffer.Length) { samples[^_buffer.Length..].CopyTo(_buffer); _count = _buffer.Length; return; }
                int over = _count + samples.Length - _buffer.Length;
                if (over > 0)
                {
                    over += over % Channels;
                    Array.Copy(_buffer, over, _buffer, 0, _count - over);
                    _count -= over;
                }
                samples.CopyTo(_buffer.AsSpan(_count));
                _count += samples.Length;
            }
        }

        /// <summary>Moves up to <paramref name="destination"/>'s length of whole frames out; returns how many values.</summary>
        public int Take(Span<float> destination)
        {
            lock (_lock)
            {
                int n = Math.Min(_count, destination.Length);
                n -= n % Channels;
                if (n == 0) return 0;
                _buffer.AsSpan(0, n).CopyTo(destination);
                Array.Copy(_buffer, n, _buffer, 0, _count - n);
                _count -= n;
                return n;
            }
        }
    }

    /// <summary>Adds the streams' sound together (sample by sample, clipped to -1..1) into <paramref name="mix"/>; returns how many values it holds.</summary>
    public static int Mix(IReadOnlyList<Track> tracks, float[] mix, float[] part)
    {
        Array.Clear(mix);
        int count = 0;
        foreach (var track in tracks)
        {
            int n = track.Take(part.AsSpan(0, Math.Min(part.Length, mix.Length)));
            if (n == 0) continue;
            for (int i = 0; i < n; i++) mix[i] += part[i];
            if (n > count) count = n;
        }
        for (int i = 0; i < count; i++) mix[i] = Math.Clamp(mix[i], -1f, 1f);
        return count;
    }

    private readonly Func<IReadOnlyList<SinkInput>, IEnumerable<SinkInput>> _select;
    private readonly Action<float[], int> _onSamples;
    private readonly Action<Exception>? _onFailed;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Dictionary<int, (Process Process, Track Track)> _recorders = new();
    private Task? _watch, _mix;

    private PulseAppCapture(Func<IReadOnlyList<SinkInput>, IEnumerable<SinkInput>> select, Action<float[], int> onSamples, Action<Exception>? onFailed)
    {
        _select = select;
        _onSamples = onSamples;
        _onFailed = onFailed;
    }

    /// <summary>
    /// Starts watching for the program's sound and recording it. <paramref name="onSamples"/> gets interleaved stereo samples and how
    /// many values are valid, on a mixing thread. <paramref name="windowClasses"/> are the shared window's class names, another way to
    /// find its program. Throws if the sound server cannot be reached.
    /// </summary>
    public static PulseAppCapture Start(int processId, Action<float[], int> onSamples, Action<Exception>? onFailed = null, IReadOnlyList<string>? windowClasses = null)
    {
        string program = ProgramName(processId);
        return Begin(inputs => ChooseOf(inputs, ProcessTree(processId), program, windowClasses), onSamples, onFailed);
    }

    /// <summary>Like <see cref="Start"/>, for a program picked by name (its file name, or the name it gives the sound server).</summary>
    public static PulseAppCapture StartProgram(string name, Action<float[], int> onSamples, Action<Exception>? onFailed = null) =>
        Begin(inputs => ChooseOf(inputs, new HashSet<int>(), name), onSamples, onFailed);

    /// <summary>Like <see cref="Start"/>, for the sound of every program except Yaparr itself (and the programs it started).</summary>
    public static PulseAppCapture StartAllExceptThisProgram(Action<float[], int> onSamples, Action<Exception>? onFailed = null)
    {
        string program = ProgramName(Environment.ProcessId);
        return Begin(inputs => ChooseAllExcept(inputs, ProcessTree(Environment.ProcessId), program), onSamples, onFailed);
    }

    private static PulseAppCapture Begin(Func<IReadOnlyList<SinkInput>, IEnumerable<SinkInput>> select, Action<float[], int> onSamples, Action<Exception>? onFailed)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("Sharing sound needs the pactl and parec programs (the pulseaudio-utils package).");
        if (Run("pactl", "info") == null) throw new InvalidOperationException("The sound server (PulseAudio or PipeWire) is not reachable.");
        var capture = new PulseAppCapture(select, onSamples, onFailed);
        capture._watch = Task.Run(() => capture.WatchAsync(capture._cts.Token));
        capture._mix = Task.Run(() => capture.MixAsync(capture._cts.Token));
        return capture;
    }

    private async Task WatchAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Prune();
                if (Run("pactl", "list sink-inputs") is { } text)
                {
                    foreach (var input in _select(ParseSinkInputs(text)))
                    {
                        bool known;
                        lock (_gate) known = _recorders.ContainsKey(input.Index);
                        if (!known) StartRecorder(input.Index, ct);
                    }
                }
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _onFailed?.Invoke(ex); }
    }

    // Forgets the recorders that have ended.
    private void Prune()
    {
        lock (_gate)
        {
            foreach (var index in _recorders.Where(r => r.Value.Process.HasExited).Select(r => r.Key).ToList())
            {
                _recorders[index].Process.Dispose();
                _recorders.Remove(index);
            }
        }
    }

    internal static string ProgramName(int pid)
    {
        try { return File.ReadAllText($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/comm").Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    private void StartRecorder(int sinkInput, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("parec") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "--monitor-stream=" + sinkInput.ToString(CultureInfo.InvariantCulture), "--format=float32le", "--rate=" + SampleRate, "--channels=" + Channels, "--latency-msec=20", "--raw" })
            psi.ArgumentList.Add(a);
        var process = Process.Start(psi);
        if (process == null) return;
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        var track = new Track();
        lock (_gate) _recorders[sinkInput] = (process, track);
        _ = Task.Run(() => ReadAsync(process, track, ct));
    }

    private async Task ReadAsync(Process process, Track track, CancellationToken ct)
    {
        try
        {
            var stream = process.StandardOutput.BaseStream;
            const int frameBytes = Channels * sizeof(float);
            var bytes = new byte[frameBytes * ChunkFrames];
            var samples = new float[Channels * ChunkFrames];
            int have = 0;
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(bytes.AsMemory(have), ct).ConfigureAwait(false);
                if (n <= 0) break;
                have += n;
                int whole = have / frameBytes * frameBytes;
                if (whole > 0)
                {
                    MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, whole)).CopyTo(samples);
                    track.Add(samples.AsSpan(0, whole / sizeof(float)));
                    have -= whole;
                    if (have > 0) Buffer.BlockCopy(bytes, whole, bytes, 0, have);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        finally
        {
            try { if (!process.HasExited) process.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
        }
    }

    // Every 20 ms: the sound the streams have collected, added together, goes on.
    private async Task MixAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            var mix = new float[Channels * ChunkFrames];
            var part = new float[mix.Length];
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                Track[] tracks;
                lock (_gate) tracks = _recorders.Values.Select(r => r.Track).ToArray();
                int count = Mix(tracks, mix, part);
                if (count > 0) _onSamples(mix, count);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _onFailed?.Invoke(ex); }
    }

    // The output of the program (in the C locale), or null if it failed.
    private static string? Run(string program, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(program, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            psi.Environment["LC_ALL"] = "C";
            using var process = Process.Start(psi);
            if (process == null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000)) { try { process.Kill(); } catch (InvalidOperationException) { } return null; }
            return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        (Process Process, Track Track)[] recorders;
        lock (_gate) { recorders = _recorders.Values.ToArray(); _recorders.Clear(); }
        foreach (var (process, _) in recorders)
        {
            try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
            process.Dispose();
        }
        try { Task.WaitAll([_watch ?? Task.CompletedTask, _mix ?? Task.CompletedTask], TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
