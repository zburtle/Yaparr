// Records your microphone and shows how Yaparr's voice processing and speech leveller treat it, so the leveller's settings can be
// chosen from real voices instead of guesses.
//
//   Yaparr.VoiceProbe list                                   the microphones
//   Yaparr.VoiceProbe record [--seconds 40] [--device NAME] [--out voice-probe.wav]
//   Yaparr.VoiceProbe analyze voice-probe.wav [--noise off|low|moderate|high]
//
// record: talk the way you do in a call (a few sentences, with pauses, then a few seconds of silence, and if you like some
// louder and some quieter talking). Nothing is sent anywhere; the recording is a file on this computer.
// analyze: runs the recording through the noise suppression the app uses and through the leveller with several settings.
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Devices;
using SoundFlow.Enums;
using SoundFlow.Structs;
using Yaparr.Voice;
using Yaparr.Voice.WebRtc;

const int Rate = 48000, Frame = 960;

string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
double Db(double rms) => 20 * Math.Log10(Math.Max(rms, 1e-9));

switch (args.FirstOrDefault())
{
    case "list":
        using (var engine = new MiniAudioEngine())
        {
            engine.UpdateAudioDevicesInfo();
            foreach (var d in engine.CaptureDevices) Console.WriteLine(d.Name);
        }
        return 0;

    case "record":
        return Record(int.TryParse(Opt("--seconds"), out int s) ? s : 40, Opt("--device"), Opt("--out") ?? "voice-probe.wav");

    case "analyze":
        if (args.Length < 2 || !File.Exists(args[1])) { Console.Error.WriteLine("Give the recording to analyze."); return 2; }
        return Analyze(args[1], Enum.TryParse<NoiseSuppression>(Opt("--noise"), true, out var noise) ? noise : NoiseSuppression.Moderate);

    default:
        Console.Error.WriteLine("Yaparr.VoiceProbe list | record [--seconds N] [--device NAME] [--out FILE] | analyze FILE [--noise LEVEL]");
        return 2;
}

int Record(int seconds, string? device, string path)
{
    using var engine = new MiniAudioEngine();
    engine.UpdateAudioDevicesInfo();
    DeviceInfo? info = engine.CaptureDevices.Where(d => d.Name == device).Select(d => (DeviceInfo?)d).FirstOrDefault();
    var format = new AudioFormat { Format = SampleFormat.F32, Channels = 1, SampleRate = Rate, Layout = AudioFormat.GetLayoutFromChannels(1) };
    var samples = new List<float>(Rate * seconds);
    var gate = new object();
    using var capture = engine.InitializeCaptureDevice(info, format, new MiniAudioDeviceConfig());
    capture.OnAudioProcessed += (block, _) => { lock (gate) { foreach (var v in block) samples.Add(v); } };
    capture.Start();
    Console.WriteLine($"Recording {seconds} seconds from {info?.Name ?? "the default microphone"}. The line below tells you what to do and when:");
    Console.WriteLine("talk normally, pause, talk quietly, talk louder, then stay silent. Starting in 5 seconds.");
    Thread.Sleep(5000);
    Console.WriteLine("GO");
    for (int i = seconds; i > 0; i--)
    {
        int n; double rms = 0;
        lock (gate) { n = samples.Count; int from = Math.Max(0, n - Rate / 4); double e = 0; for (int k = from; k < n; k++) e += samples[k] * (double)samples[k]; rms = Math.Sqrt(e / Math.Max(1, n - from)); }
        double done = (seconds - i) / (double)seconds;
        string cue = done < 0.25 ? "TALK normally, as in a call" : done < 0.4 ? "PAUSE: say nothing" : done < 0.62 ? "TALK QUIETLY, like a late-night call" : done < 0.82 ? "TALK LOUDER, excited or laughing" : "SILENCE: say nothing";
        Console.Write($"\r{i,3} s  {Db(rms),6:0.0} dBFS {new string('#', (int)Math.Clamp((Db(rms) + 70) / 2, 0, 35)),-35} {cue}".PadRight(110));
        Thread.Sleep(1000);
    }
    capture.Stop();
    Console.WriteLine();
    float[] all;
    lock (gate) all = samples.ToArray();
    WriteWav(path, all);
    Console.WriteLine($"Saved {all.Length / (double)Rate:0.0} s to {Path.GetFullPath(path)}");
    return Analyze(path, NoiseSuppression.Moderate);
}

int Analyze(string path, NoiseSuppression noise)
{
    var raw = ReadWav(path);
    Console.WriteLine($"{Path.GetFileName(path)}: {raw.Length / (double)Rate:0.0} s");
    if (raw.Length < Rate * 5) { Console.Error.WriteLine("Too short to say anything."); return 2; }

    float[] processed = (float[])raw.Clone();
    {
        if (WebRtcVoiceProcessor.TryCreate(new VoiceProcessing(EchoCancellation: false, Noise: noise), out var p, out var err))
        {
            using (p!) for (int at = 0; at + Frame <= processed.Length; at += Frame) p!.ProcessCapture(processed.AsSpan(at, Frame));
        }
        else Console.WriteLine("(noise suppression is not available here: " + err + ")");
    }

    Console.WriteLine();
    Report("As recorded", raw);
    Report($"After noise suppression ({noise})", processed);

    Console.WriteLine();
    Console.WriteLine("The leveller on the cleaned recording (talking = frames it takes for speech; levels are RMS in dB below full scale):");
    Console.WriteLine($"{"target",7} {"boost",6} {"rise",5} {"fall",5} | {"speech out",10} {"gain min/max",13} {"gain steady",11} {"pause noise",11} {"limited",8} {"peak",6}");
    foreach (var (target, boost, rise, fall) in new[] { (-24f, 15f, 12f, 40f), (-22f, 15f, 12f, 40f), (-26f, 15f, 12f, 40f), (-24f, 12f, 12f, 40f), (-24f, 20f, 12f, 40f), (-24f, 15f, 6f, 40f), (-24f, 15f, 20f, 40f), (-24f, 15f, 12f, 20f) })
        Variant(processed, new SpeechLeveler(target, boost, 15f, rise, fall), $"{target,7} {boost,6} {rise,5} {fall,5}");

    Console.WriteLine();
    Console.WriteLine("The same recording as if the microphone were quieter or louder (default settings): a good leveller ends at the same level.");
    foreach (var change in new[] { -18f, -12f, -6f, 0f, 6f })
    {
        var scaled = processed.Select(v => v * MathF.Pow(10, change / 20f)).ToArray();
        Variant(scaled, new SpeechLeveler(), $"{change:+0;-0;0} dB microphone".PadRight(26));
    }
    return 0;
}

void Report(string title, float[] x)
{
    var frames = new List<double>();
    float peak = 0;
    for (int at = 0; at + Frame <= x.Length; at += Frame)
    {
        double e = 0;
        for (int k = 0; k < Frame; k++) { e += x[at + k] * (double)x[at + k]; peak = Math.Max(peak, Math.Abs(x[at + k])); }
        frames.Add(10 * Math.Log10(e / Frame + 1e-12));
    }
    var sorted = frames.OrderBy(v => v).ToList();
    double P(double q) => sorted[(int)Math.Min(sorted.Count - 1, q * sorted.Count)];
    var leveler = new SpeechLeveler();
    var copy = (float[])x.Clone();
    double speech = 0; int speechFrames = 0, total = 0;
    for (int at = 0; at + Frame <= copy.Length; at += Frame)
    {
        double e = 0;
        for (int k = 0; k < Frame; k++) e += x[at + k] * (double)x[at + k];
        leveler.Process(copy.AsSpan(at, Frame));
        total++;
        if (leveler.Talking) { speech += e / Frame; speechFrames++; }
    }
    Console.WriteLine($"{title}: frame loudness 5%/50%/95%/max = {P(0.05):0.0} / {P(0.5):0.0} / {P(0.95):0.0} / {sorted[^1]:0.0} dBFS, peak {Db(peak):0.0} dBFS");
    Console.WriteLine($"    quiet floor ~{P(0.1):0.0} dBFS; speech {speechFrames * 100 / Math.Max(1, total)}% of frames at {10 * Math.Log10(speech / Math.Max(1, speechFrames) + 1e-12):0.0} dBFS RMS");
}

void Variant(float[] input, SpeechLeveler leveler, string label)
{
    var o = (float[])input.Clone();
    double speechIn = 0, speechOut = 0, pauseIn = 0, pauseOut = 0, gainSum = 0;
    int speechFrames = 0, pauseFrames = 0, limited = 0;
    float gmin = 99, gmax = -99, peak = 0;
    for (int at = 0; at + Frame <= o.Length; at += Frame)
    {
        double ein = 0;
        for (int k = 0; k < Frame; k++) ein += input[at + k] * (double)input[at + k];
        leveler.Process(o.AsSpan(at, Frame));
        double eout = 0;
        for (int k = 0; k < Frame; k++) { eout += o[at + k] * (double)o[at + k]; peak = Math.Max(peak, Math.Abs(o[at + k])); if (Math.Abs(o[at + k]) > 0.9f) limited++; }
        if (leveler.Talking) { speechIn += ein / Frame; speechOut += eout / Frame; speechFrames++; gainSum += leveler.GainDb; gmin = Math.Min(gmin, leveler.GainDb); gmax = Math.Max(gmax, leveler.GainDb); }
        else { pauseIn += ein / Frame; pauseOut += eout / Frame; pauseFrames++; }
    }
    string speechOutDb = speechFrames > 0 ? $"{10 * Math.Log10(speechOut / speechFrames + 1e-12):0.0}" : "-";
    string pause = pauseFrames > 0 ? $"{10 * Math.Log10((pauseOut + 1e-12) / (pauseIn + 1e-12)):+0.0;-0.0;0.0} dB" : "-";
    Console.WriteLine($"{label} | {speechOutDb,10} {(speechFrames > 0 ? $"{gmin:0.0}/{gmax:0.0}" : "-"),13} {(speechFrames > 0 ? $"{gainSum / speechFrames:0.0}" : "-"),11} {pause,11} {limited,8} {Db(peak),6:0.0}");
}

static void WriteWav(string path, float[] samples)
{
    using var w = new BinaryWriter(File.Create(path));
    int bytes = samples.Length * 4;
    w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
    w.Write("fmt "u8); w.Write(16); w.Write((short)3); w.Write((short)1); w.Write(Rate); w.Write(Rate * 4); w.Write((short)4); w.Write((short)32);
    w.Write("data"u8); w.Write(bytes);
    foreach (var v in samples) w.Write(v);
}

static float[] ReadWav(string path)
{
    var b = File.ReadAllBytes(path);
    if (b.Length < 44 || b[0] != 'R' || b[8] != 'W') throw new InvalidDataException("Not a WAV file.");
    int pos = 12, format = 0, channels = 1, rate = 0, bits = 0;
    while (pos + 8 <= b.Length)
    {
        string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
        int size = BitConverter.ToInt32(b, pos + 4);
        if (id == "fmt ") { format = BitConverter.ToInt16(b, pos + 8); channels = BitConverter.ToInt16(b, pos + 10); rate = BitConverter.ToInt32(b, pos + 12); bits = BitConverter.ToInt16(b, pos + 22); }
        else if (id == "data")
        {
            if (rate != Rate) throw new InvalidDataException($"The recording is {rate} Hz; 48000 Hz is needed.");
            int n = Math.Min(size, b.Length - pos - 8) / (bits / 8) / channels;
            var x = new float[n];
            for (int i = 0; i < n; i++)
            {
                int at = pos + 8 + i * channels * (bits / 8);
                x[i] = format == 3 && bits == 32 ? BitConverter.ToSingle(b, at) : bits == 16 ? BitConverter.ToInt16(b, at) / 32768f : throw new InvalidDataException("Only 16-bit and 32-bit float WAV files are read.");
            }
            return x;
        }
        pos += 8 + size + (size & 1);
    }
    throw new InvalidDataException("No audio in the file.");
}
