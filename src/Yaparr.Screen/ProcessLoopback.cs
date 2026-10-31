using System.Runtime.InteropServices;

namespace Yaparr.Screen;

/// <summary>
/// Records the sound one program plays (and the programs it starts), or everything except one program, without a driver or a
/// "stereo mix" device: Windows 10 version 2004 (build 19041) or newer can capture per process. Sharing a window records
/// that window's program; sharing a monitor records everything but Yaparr itself, so the voice chat is not sent back.
/// The sound arrives as stereo, 48 kHz, floating point.
/// </summary>
public sealed class ProcessLoopback : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    /// <summary>Whether this Windows can record per process.</summary>
    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    private readonly Action<float[], int> _onSamples;
    private IAudioClient? _client;
    private IAudioCaptureClient? _capture;
    private IntPtr _event;
    private Thread? _thread;
    private volatile bool _stop;

    private ProcessLoopback(Action<float[], int> onSamples) => _onSamples = onSamples;

    /// <summary>
    /// Starts recording. <paramref name="processId"/> is the program to record, or with <paramref name="exclude"/> the one to leave out.
    /// <paramref name="onSamples"/> gets interleaved stereo samples and how many values are valid; it runs on the recording thread.
    /// </summary>
    public static ProcessLoopback Start(int processId, bool exclude, Action<float[], int> onSamples)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("Sharing sound needs Windows 10 version 2004 or newer.");
        var loopback = new ProcessLoopback(onSamples);
        try { loopback.Open(processId, exclude); }
        catch { loopback.Dispose(); throw; }
        return loopback;
    }

    private void Open(int processId, bool exclude)
    {
        _client = Activate(processId, exclude);

        var format = new WaveFormat
        {
            FormatTag = 3, // IEEE float
            Channels = Channels,
            SamplesPerSec = SampleRate,
            BitsPerSample = 32,
            BlockAlign = Channels * 4,
            AvgBytesPerSec = SampleRate * Channels * 4,
        };
        const uint loopback = 0x00020000, eventCallback = 0x00040000;
        Check(_client.Initialize(0, loopback | eventCallback, 200 * 10_000, 0, ref format, IntPtr.Zero), "initialize");
        _event = CreateEvent(IntPtr.Zero, false, false, null);
        Check(_client.SetEventHandle(_event), "event");
        var captureId = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
        Check(_client.GetService(ref captureId, out var service), "starting the recording");
        _capture = (IAudioCaptureClient)service;
        Check(_client.Start(), "starting the recording");

        _thread = new Thread(Run) { IsBackground = true, Name = "screen sound" };
        _thread.Start();
    }

    // ActivateAudioInterfaceAsync with the virtual "process loopback" device: the answer comes on another thread.
    private static IAudioClient Activate(int processId, bool exclude)
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS: type 1 (process loopback), the process, and 0 = record it / 1 = record all but it.
        var parameters = Marshal.AllocHGlobal(12);
        var variant = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.WriteInt32(parameters, 0, 1);
            Marshal.WriteInt32(parameters, 4, processId);
            Marshal.WriteInt32(parameters, 8, exclude ? 1 : 0);
            // PROPVARIANT: VT_BLOB (0x41) holding those 12 bytes; the blob's size comes after the 8-byte header, then its pointer.
            for (int i = 0; i < 24; i++) Marshal.WriteByte(variant, i, 0);
            Marshal.WriteInt16(variant, 0, 0x41);
            Marshal.WriteInt32(variant, 8, 12);
            Marshal.WriteIntPtr(variant, 8 + IntPtr.Size, parameters);

            var handler = new ActivationHandler();
            var iid = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"); // IAudioClient
            Check(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, variant, handler, out var operation), "starting the recording");
            if (!handler.Done.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Windows did not answer when asked to record the sound.");
            Check(operation.GetActivateResult(out int hr, out var client), "result");
            Check(hr, "activation");
            return (IAudioClient)client;
        }
        finally
        {
            Marshal.FreeHGlobal(variant);
            Marshal.FreeHGlobal(parameters);
        }
    }

    private void Run()
    {
        var buffer = new float[SampleRate / 10 * Channels];
        try
        {
            while (!_stop)
            {
                if (WaitForSingleObject(_event, 100) != 0) continue;
                while (!_stop && _capture!.GetNextPacketSize(out uint frames) == 0 && frames > 0)
                {
                    if (_capture.GetBuffer(out var data, out uint count, out uint flags, out _, out _) != 0) break;
                    int values = (int)count * Channels;
                    if (buffer.Length < values) buffer = new float[values];
                    // Flag 2 means silence: the data is not meaningful, but the time must still pass.
                    if ((flags & 2) != 0) Array.Clear(buffer, 0, values);
                    else Marshal.Copy(data, buffer, 0, values);
                    _capture.ReleaseBuffer(count);
                    _onSamples(buffer, values);
                }
            }
        }
        catch (Exception) when (_stop) { }
        catch (COMException) { } // the device went away; the recording ends
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"Windows could not record the sound ({what}): 0x{hr:X8}.");
    }

    public void Dispose()
    {
        _stop = true;
        try { _client?.Stop(); } catch (Exception) { }
        if (_thread != null && _thread != Thread.CurrentThread) _thread.Join(1000);
        if (OperatingSystem.IsWindows())
        {
            if (_capture != null) Marshal.ReleaseComObject(_capture);
            if (_client != null) Marshal.ReleaseComObject(_client);
        }
        _capture = null; _client = null;
        if (_event != IntPtr.Zero) { CloseHandle(_event); _event = IntPtr.Zero; }
    }

    // ---- Windows -----------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSec, AvgBytesPerSec;
        public ushort BlockAlign, BitsPerSample, ExtraSize;
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, ref WaveFormat format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, ref WaveFormat format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int result, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    // Tells Windows the handler may be called from any thread.
    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new();
        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation) { Done.Set(); return 0; }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid iid, IntPtr parameters,
        IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation operation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initial, string? name);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
