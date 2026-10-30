using System.Buffers.Binary;
using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;
using Yaparr.Voice;

namespace Yaparr.Tests;

[Collection("real-time audio")]
public class SoundboardTests
{
    private static float[] Tone(double hz, double seconds, double amplitude = 0.4, int rate = 48000) =>
        Enumerable.Range(0, (int)(rate * seconds)).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate))).ToArray();

    private static double Rms(IReadOnlyList<float> s) => Math.Sqrt(s.Sum(x => (double)x * x) / Math.Max(1, s.Count));

    // Plays the speakers for a while and returns what came out.
    private static float[] Listen(VoiceSession session, int frames)
    {
        var all = new float[frames * 960];
        for (int f = 0; f < frames; f++) session.PullPlayback(all.AsSpan(f * 960, 960));
        return all;
    }

    private static byte[] Wav(float[] samples, int rate, int channels)
    {
        var data = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), (short)(samples[i] * 32767));
        var file = new byte[44 + data.Length];
        "RIFF"u8.CopyTo(file); BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), 36 + data.Length);
        "WAVEfmt "u8.CopyTo(file.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(22), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(24), rate);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(28), rate * channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(32), (short)(channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(34), 16);
        "data"u8.CopyTo(file.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(40), data.Length);
        data.CopyTo(file.AsSpan(44));
        return file;
    }

    // ---- the sound format -------------------------------------------------------------------

    [Fact]
    public void A_sound_is_made_equally_loud_and_reads_back()
    {
        var quiet = SoundClip.FromPcm(Tone(440, 1, 0.02));
        var loud = SoundClip.FromPcm(Tone(440, 1, 0.9));
        Assert.Equal(50, quiet.Frames);
        Assert.Equal(TimeSpan.FromSeconds(1), quiet.Duration);
        double a = Rms(quiet.ToSamples().Skip(4800).ToArray()), b = Rms(loud.ToSamples().Skip(4800).ToArray());
        Assert.InRange(a, 0.06, 0.14);
        Assert.InRange(b, 0.06, 0.14);
        Assert.True(SoundClip.TryParse(quiet.Data, out var again));
        Assert.Equal(quiet.Frames, again.Frames);
    }

    [Fact]
    public void A_sound_can_be_twenty_seconds_but_not_longer()
    {
        var longest = SoundClip.FromPcm(Tone(300, 20));
        Assert.Equal(1000, longest.Frames);
        Assert.True(longest.Data.Length <= SoundClip.MaxBytes);
        var ex = Assert.Throws<ArgumentException>(() => SoundClip.FromPcm(Tone(300, 20.1)));
        Assert.Contains("20 seconds", ex.Message);
    }

    [Fact]
    public void Empty_and_silent_sounds_are_refused()
    {
        Assert.Throws<ArgumentException>(() => SoundClip.FromPcm(Array.Empty<float>()));
        Assert.Throws<ArgumentException>(() => SoundClip.FromPcm(new float[48000]));
    }

    [Fact]
    public void A_wav_file_at_another_rate_in_stereo_becomes_a_sound_of_the_same_length()
    {
        var mono = Tone(500, 2, 0.3, 44100);
        var stereo = mono.SelectMany(s => new[] { s, s }).ToArray();
        var clip = SoundClip.FromWav(Wav(stereo, 44100, 2));
        Assert.InRange(clip.Duration.TotalSeconds, 1.98, 2.02);
        Assert.Throws<ArgumentException>(() => SoundClip.FromWav(new byte[100]));
    }

    [Fact]
    public void A_wav_file_that_is_too_long_is_refused_before_it_is_converted()
    {
        var wav = Wav(Tone(300, 21, 0.3, 8000), 8000, 1);
        Assert.Throws<ArgumentException>(() => SoundClip.FromWav(wav));
    }

    [Fact]
    public void Damaged_sounds_are_not_accepted()
    {
        var data = SoundClip.FromPcm(Tone(440, 1)).Data;
        Assert.False(SoundClip.TryParse(data[..^1], out _));
        Assert.False(SoundClip.TryParse(data.Append((byte)0).ToArray(), out _));
        Assert.False(SoundClip.TryParse(new byte[] { 1, 2, 3 }, out _));
        var wrong = (byte[])data.Clone(); wrong[0] = (byte)'X';
        Assert.False(SoundClip.TryParse(wrong, out _));
        var tooMany = (byte[])data.Clone(); BinaryPrimitives.WriteUInt16BigEndian(tooMany.AsSpan(5), 1001);
        Assert.False(SoundClip.TryParse(tooMany, out _));
    }

    [Fact]
    public void A_sound_entry_cannot_be_changed_in_transit()
    {
        var id = PeerIdentity.Generate();
        var s = SignedSound.Create(id, "room", SignedSound.NewId(), "airhorn", "alice", new string('a', 64), 1000, 1500, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), false);
        Assert.True(SignedSound.Verify(s, "room"));
        Assert.False(SignedSound.Verify(s, "other room"));
        Assert.False(SignedSound.Verify(s with { Name = "other" }, "room"));
        Assert.False(SignedSound.Verify(s with { Hash = new string('b', 64) }, "room"));
        Assert.False(SignedSound.Verify(s with { Deleted = true }, "room"));
    }

    // ---- sharing and playing ----------------------------------------------------------------

    private static async Task<(ChatNode, ChatNode, ChatRoom, ChatRoom)> Pair(InMemoryDiscoveryHub hub, TimeSpan? interval = null)
    {
        var a = TestUtil.NewNode(hub, "alice", soundInterval: interval);
        var b = TestUtil.NewNode(hub, "bob", soundInterval: interval);
        var ra = await a.JoinRoomAsync("lobby");
        var rb = await b.JoinRoomAsync("lobby");
        await TestUtil.WaitFor(() => ra.Members.Any(m => m.Id == b.Id) && rb.Members.Any(m => m.Id == a.Id), 30_000);
        return (a, b, ra, rb);
    }

    [Fact]
    public async Task A_sound_one_member_adds_reaches_the_others_including_people_who_join_later()
    {
        var hub = new InMemoryDiscoveryHub();
        var (a, b, ra, rb) = await Pair(hub);
        using var _ = a; using var __ = b;

        var added = ra.AddSound("airhorn", SoundClip.FromPcm(Tone(440, 1)));
        Assert.True(added.Available);
        await TestUtil.WaitFor(() => rb.Sounds.Any(s => s.Name == "airhorn" && s.Available), 20_000);
        var seen = rb.Sounds.Single();
        Assert.Equal(a.Id, seen.Owner);
        Assert.Equal("alice", seen.OwnerName);
        Assert.Equal(TimeSpan.FromSeconds(1), seen.Duration);
        Assert.NotNull(rb.GetSound(seen.Id));

        using var c = TestUtil.NewNode(hub, "carol");
        var rc = await c.JoinRoomAsync("lobby");
        await TestUtil.WaitFor(() => rc.Sounds.Any(s => s.Name == "airhorn" && s.Available), 30_000);
    }

    [Fact]
    public async Task Only_the_person_who_added_a_sound_can_rename_or_remove_it()
    {
        var hub = new InMemoryDiscoveryHub();
        var (a, b, ra, rb) = await Pair(hub);
        using var _ = a; using var __ = b;

        var s = ra.AddSound("airhorn", SoundClip.FromPcm(Tone(440, 1)));
        await TestUtil.WaitFor(() => rb.Sounds.Count == 1);
        Assert.Throws<InvalidOperationException>(() => rb.RemoveSound(s.Id));
        Assert.Throws<InvalidOperationException>(() => rb.RenameSound(s.Id, "mine now"));

        ra.RenameSound(s.Id, "Air horn");
        await TestUtil.WaitFor(() => rb.Sounds.Any(x => x.Name == "Air horn"));
        ra.RemoveSound(s.Id);
        await TestUtil.WaitFor(() => rb.Sounds.Count == 0 && ra.Sounds.Count == 0);

        // Someone joining afterwards is told about the removal and must not bring the sound back.
        using var c = TestUtil.NewNode(hub, "carol");
        var rc = await c.JoinRoomAsync("lobby");
        await TestUtil.WaitFor(() => rc.Members.Count >= 2, 30_000);
        await Task.Delay(1500);
        Assert.Empty(rc.Sounds);
        Assert.Empty(rb.Sounds);
    }

    [Fact]
    public async Task A_played_sound_is_heard_by_people_in_voice_and_a_soundboard_mute_silences_only_that()
    {
        var hub = new InMemoryDiscoveryHub();
        var (a, b, ra, rb) = await Pair(hub, interval: TimeSpan.Zero);
        using var _ = a; using var __ = b;

        var s = ra.AddSound("tone", SoundClip.FromPcm(Tone(440, 1)));
        await TestUtil.WaitFor(() => rb.Sounds.Any(x => x.Available), 20_000);

        Assert.False(ra.PlaySound(s.Id)); // not in voice yet
        using var va = ra.JoinVoice();
        using var vb = rb.JoinVoice();
        await TestUtil.WaitFor(() => ra.VoiceParticipants.Count == 2 && rb.VoiceParticipants.Count == 2);

        var heard = new TaskCompletionSource<SoundInfo>();
        rb.SoundPlayed += (by, info) => { if (by == a.Id) heard.TrySetResult(info); };
        Assert.True(ra.PlaySound(s.Id));
        Assert.Equal("tone", (await heard.Task.WaitAsync(TimeSpan.FromSeconds(10))).Name);
        var theirs = Listen(vb, 60);
        Assert.True(Rms(theirs) > 0.03, $"rms {Rms(theirs)}");
        var yours = Listen(va, 60);
        Assert.True(Rms(yours) > 0.03, "the player hears their own sound");

        // Muting one person's soundboard drops their next sound for you, and nothing else changes.
        vb.SetSoundboardMute(a.Id, true);
        Assert.True(vb.IsSoundboardMuted(a.Id));
        Assert.True(ra.PlaySound(s.Id));
        await Task.Delay(800);
        Assert.True(Rms(Listen(vb, 60)) < 0.001);

        vb.SetSoundboardMute(a.Id, false);
        Assert.True(ra.PlaySound(s.Id));
        await Task.Delay(800);
        Assert.True(Rms(Listen(vb, 60)) > 0.03);

        vb.SoundboardsMuted = true;
        Assert.True(ra.PlaySound(s.Id));
        await Task.Delay(800);
        Assert.True(Rms(Listen(vb, 60)) < 0.001);
    }

    [Fact]
    public async Task Someone_not_in_voice_hears_nothing_and_a_deafened_listener_hears_nothing()
    {
        var hub = new InMemoryDiscoveryHub();
        var (a, b, ra, rb) = await Pair(hub, interval: TimeSpan.Zero);
        using var _ = a; using var __ = b;

        var s = ra.AddSound("tone", SoundClip.FromPcm(Tone(440, 1)));
        await TestUtil.WaitFor(() => rb.Sounds.Any(x => x.Available), 20_000);
        using var va = ra.JoinVoice();
        await TestUtil.WaitFor(() => ra.VoiceParticipants.Count == 1);

        int plays = 0;
        rb.SoundPlayed += (_, _) => Interlocked.Increment(ref plays);
        Assert.True(ra.PlaySound(s.Id));
        await Task.Delay(1000);
        Assert.Equal(0, plays);

        using var vb = rb.JoinVoice();
        await TestUtil.WaitFor(() => rb.VoiceParticipants.Count == 2 && ra.VoiceParticipants.Count == 2);
        vb.Deafened = true;
        Assert.True(ra.PlaySound(s.Id));
        await Task.Delay(800);
        Assert.True(Rms(Listen(vb, 60)) < 0.001);
        Assert.False(rb.PlaySound(rb.Sounds[0].Id));
    }

    [Fact]
    public async Task Sounds_from_one_person_in_quick_succession_are_dropped()
    {
        var hub = new InMemoryDiscoveryHub();
        var (a, b, ra, rb) = await Pair(hub, interval: TimeSpan.FromSeconds(30));
        using var _ = a; using var __ = b;

        var s = ra.AddSound("tone", SoundClip.FromPcm(Tone(440, 1)));
        await TestUtil.WaitFor(() => rb.Sounds.Any(x => x.Available), 20_000);
        using var va = ra.JoinVoice();
        using var vb = rb.JoinVoice();
        await TestUtil.WaitFor(() => ra.VoiceParticipants.Count == 2 && rb.VoiceParticipants.Count == 2);

        int plays = 0;
        rb.SoundPlayed += (_, _) => Interlocked.Increment(ref plays);
        for (int i = 0; i < 5; i++) { Assert.True(ra.PlaySound(s.Id)); await Task.Delay(100); }
        await Task.Delay(1000);
        Assert.Equal(1, plays);
    }

    [Fact]
    public async Task A_blocked_persons_sounds_are_hidden()
    {
        var hub = new InMemoryDiscoveryHub();
        var (a, b, ra, rb) = await Pair(hub);
        using var _ = a; using var __ = b;

        ra.AddSound("tone", SoundClip.FromPcm(Tone(440, 1)));
        await TestUtil.WaitFor(() => rb.Sounds.Count == 1);
        b.Block(a.Id);
        Assert.Empty(rb.Sounds);
        b.Unblock(a.Id);
        Assert.Single(rb.Sounds);
    }

    [Fact]
    public async Task The_soundboard_is_still_there_after_a_restart()
    {
        var folder = Path.Combine(Path.GetTempPath(), "yaparr-sb-" + Guid.NewGuid().ToString("N"));
        try
        {
            var identity = PeerIdentity.Generate();
            using (var first = TestUtil.NewNode(new InMemoryDiscoveryHub(), "alice", identity: identity, soundboardDirectory: Path.Combine(folder, "lists"), attachmentDirectory: Path.Combine(folder, "files")))
            {
                var room = await first.JoinRoomAsync("lobby");
                room.AddSound("airhorn", SoundClip.FromPcm(Tone(440, 1)));
            }
            using var second = TestUtil.NewNode(new InMemoryDiscoveryHub(), "alice", identity: identity, soundboardDirectory: Path.Combine(folder, "lists"), attachmentDirectory: Path.Combine(folder, "files"));
            var again = await second.JoinRoomAsync("lobby");
            var sound = Assert.Single(again.Sounds);
            Assert.Equal("airhorn", sound.Name);
            Assert.True(sound.Available);
            Assert.NotNull(again.GetSound(sound.Id));
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }
}
