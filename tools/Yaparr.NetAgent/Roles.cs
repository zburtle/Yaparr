using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using Yaparr.Chat;
using Yaparr.Voice;

internal static class Roles
{
    // The frames carry what the viewers need to judge the stream: a number, when they were sent and whether they are a key.
    private const int HeaderBytes = 21;
    private const long WarmUpMs = 3000;
    private static readonly byte[] Magic = "YNT1"u8.ToArray();

    private static byte[] MakeFrame(long index, bool key, int size)
    {
        var frame = new byte[Math.Max(size, 64)];
        new Random((int)(index * 2654435761L)).NextBytes(frame);
        Magic.CopyTo(frame, 0);
        BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(4), index);
        BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(12), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        frame[20] = (byte)(key ? 1 : 0);
        return frame;
    }

    private static bool ReadFrame(ReadOnlySpan<byte> data, out long index, out long sentAt, out bool key)
    {
        index = sentAt = 0; key = false;
        if (data.Length < HeaderBytes || !data[..4].SequenceEqual(Magic)) return false;
        index = BinaryPrimitives.ReadInt64BigEndian(data[4..]);
        sentAt = BinaryPrimitives.ReadInt64BigEndian(data[12..]);
        key = data[20] != 0;
        return true;
    }

    // ---- sharing a stream ---------------------------------------------------------------------

    public static async Task ShareAsync(ChatNode node, RoomKey key, AgentArgs a, CancellationToken outer)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var ct = limit.Token;
        void cts_Limit(double seconds) => limit.CancelAfter(TimeSpan.FromSeconds(seconds));
        var room = await node.JoinRoomAsync(key, a.Str("name"), ct);
        int kbps = a.Int("kbps", 1500), fps = Math.Max(1, a.Int("fps", 15));
        double keyEvery = Math.Max(0.5, a.Dbl("key-every", 4));
        bool signed = !a.Flag("unsigned");

        // The key frame is several times the average; the rest are sized so the whole comes to the bitrate asked for.
        double average = kbps * 125.0 / fps;
        int perKey = Math.Max(2, (int)(keyEvery * fps));
        int keySize = (int)Math.Min(3_000_000, average * 6);
        int deltaSize = (int)Math.Max(200, (average * perKey - keySize) / (perKey - 1));

        // Wait for the others to be found before sharing, so that how long finding them takes is measured on its own.
        int waitPeers = a.Int("wait-peers", 0);
        var waitLimit = TimeSpan.FromSeconds(a.Dbl("wait-peers-timeout", 90));
        var waitedFor = Stopwatch.StartNew();
        while (waitPeers > 0 && node.ConnectedPeers.Count < waitPeers && waitedFor.Elapsed < waitLimit && !ct.IsCancellationRequested)
            await Task.Delay(250, ct);
        Out.Write("ready", ("peers", node.ConnectedPeers.Count), ("wanted", waitPeers), ("waitedMs", waitedFor.ElapsedMilliseconds));
        double streamSeconds = a.Dbl("stream-seconds", 0);
        if (streamSeconds > 0) cts_Limit(streamSeconds);

        using var source = node.StartMedia(room, "screen", a.Str("name"), signed, upstream: false, fanout: null, kbps);
        Out.Write("sharing", ("group", source.Info.Id), ("kbps", kbps), ("fps", fps), ("keyBytes", keySize), ("deltaBytes", deltaSize), ("signed", signed));

        long frames = 0, bytes = 0, keys = 0, keyRequests = 0;
        bool keyNow = true;
        source.KeyFrameRequested += () => { Interlocked.Increment(ref keyRequests); keyNow = true; };

        var stats = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    var st = source.Status;
                    Out.Write("stat", ("frames", Volatile.Read(ref frames)), ("bytes", Volatile.Read(ref bytes)), ("keys", Volatile.Read(ref keys)),
                        ("keyRequests", Volatile.Read(ref keyRequests)), ("children", st.Children.Count), ("forwarded", st.Forwarded), ("retransmitted", st.Retransmitted));
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);

        var clock = Stopwatch.StartNew();
        long next = 0, periodMs = 1000 / fps, sinceKey = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                bool isKey = keyNow || sinceKey >= perKey;
                keyNow = false;
                sinceKey = isKey ? 1 : sinceKey + 1;
                var frame = MakeFrame(frames + 1, isKey, isKey ? keySize : deltaSize);
                await source.SendFrameAsync(frame, isKey, ct);
                frames++;
                bytes += frame.Length;
                if (isKey) keys++;

                next += periodMs;
                long wait = next - clock.ElapsedMilliseconds;
                if (wait > 0) await Task.Delay((int)wait, ct);
                else if (wait < -1000) next = clock.ElapsedMilliseconds;   // far behind: do not try to catch up
            }
        }
        catch (OperationCanceledException) { }

        var final = source.Status;
        Out.Write("summary", ("role", "share"), ("frames", frames), ("bytes", bytes), ("keys", keys), ("keyRequests", keyRequests),
            ("firstPeerAtMs", Found.FirstPeerAtMs), ("peersMost", Found.PeersMost), ("readyAfterMs", waitedFor.ElapsedMilliseconds),
            ("children", final.Children.Count), ("forwarded", final.Forwarded), ("retransmitted", final.Retransmitted),
            ("relayCapable", node.IsRelayCapable), ("nat", node.NetworkStatus.Nat.Type.ToString()));
        await stats;
    }

    // ---- watching a stream --------------------------------------------------------------------

    public static async Task WatchAsync(ChatNode node, RoomKey key, AgentArgs a, CancellationToken ct)
    {
        var room = await node.JoinRoomAsync(key, a.Str("name"), ct);
        var waitFor = TimeSpan.FromSeconds(a.Dbl("wait-group", 60));
        MediaGroupInfo? group = null;
        var waited = Stopwatch.StartNew();
        try
        {
            while (group == null && waited.Elapsed < waitFor && !ct.IsCancellationRequested)
            {
                group = room.MediaGroups.FirstOrDefault(g => g.Kind == "screen");
                if (group == null) await Task.Delay(200, ct);
            }
        }
        catch (OperationCanceledException) { }
        if (group == null)
        {
            Out.Write("summary", ("role", "watch"), ("sawGroup", false), ("waitedMs", waited.ElapsedMilliseconds), ("firstPeerAtMs", Found.FirstPeerAtMs), ("peersMost", Found.PeersMost),
                ("relayCapable", node.IsRelayCapable), ("nat", node.NetworkStatus.Nat.Type.ToString()));
            return;
        }
        long groupSeenAt = Out.Ms;
        Out.Write("group", ("group", group.Id), ("root", group.Root.Value), ("kbps", group.Kbps));

        using var view = node.WatchMedia(room, group.Id);
        long received = 0, keyFrames = 0, minIndex = long.MaxValue, maxIndex = -1, firstFrameAt = -1, attachedAt = -1, lastFrameAt = -1, maxGapMs = 0, parentChanges = 0;
        string? parent = null, lastParent = null;
        var distinct = new HashSet<long>();
        var latencies = new List<double>();
        var gate = new object();

        view.FrameReceived += f =>
        {
            if (!ReadFrame(f.Data.Span, out var index, out var sentAt, out var isKey)) return;
            long now = Out.Ms;
            lock (gate)
            {
                received++;
                if (isKey) keyFrames++;
                if (distinct.Add(index)) { minIndex = Math.Min(minIndex, index); maxIndex = Math.Max(maxIndex, index); }
                if (firstFrameAt < 0) firstFrameAt = now;
                else maxGapMs = Math.Max(maxGapMs, now - lastFrameAt);
                lastFrameAt = now;
                // The first seconds include the catch-up a newcomer is given, which is old by design.
                if (now - firstFrameAt > WarmUpMs && latencies.Count < 200_000) latencies.Add(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - sentAt);
            }
        };

        long lastStat = 0, goneSince = -1;
        while (!ct.IsCancellationRequested)
        {
            // The sharer stopped: give the last packets a moment, then report.
            if (room.MediaGroups.Any(g => g.Id == group.Id)) goneSince = -1;
            else if (goneSince < 0) goneSince = Out.Ms;
            else if (Out.Ms - goneSince > 2500) break;
            var st = view.Status;
            long up = Out.Ms;
            lock (gate)
            {
                if (st.Attached && attachedAt < 0) attachedAt = up;
                var now = st.Parent?.Value;
                if (now != parent) { if (parent != null || now != null) parentChanges++; parent = now; }
                if (now != null) lastParent = now;
            }
            if (up - lastStat >= 1000)
            {
                lastStat = up;
                long r, d, lost;
                lock (gate) { r = received; d = distinct.Count; }
                lost = view.FramesLost;
                Out.Write("stat", ("attached", st.Attached), ("parent", st.Parent?.Value), ("frames", r), ("distinct", d), ("lost", lost),
                    ("children", st.Children.Count), ("retransmitted", st.Retransmitted), ("recovered", st.Recovered), ("overServer", st.OverServer));
            }
            try { await Task.Delay(100, ct); } catch (OperationCanceledException) { break; }
        }

        var final = view.Status;
        double p50, p95, max;
        long continuityDen, distinctCount;
        lock (gate)
        {
            p50 = Maths.Percentile(latencies, 0.5); p95 = Maths.Percentile(latencies, 0.95);
            max = latencies.Count == 0 ? 0 : latencies.Max();
            distinctCount = distinct.Count;
            continuityDen = maxIndex < 0 ? 0 : maxIndex - minIndex + 1;
        }
        Out.Write("summary", ("role", "watch"), ("sawGroup", true), ("groupSeenAtMs", groupSeenAt), ("attachedAtMs", attachedAt), ("firstFrameAtMs", firstFrameAt),
            ("firstPeerAtMs", Found.FirstPeerAtMs), ("peersMost", Found.PeersMost),
            ("frames", received), ("distinct", distinctCount), ("span", continuityDen), ("keyFrames", keyFrames), ("framesLost", view.FramesLost),
            ("maxFrameGapMs", maxGapMs), ("latencyP50Ms", p50), ("latencyP95Ms", p95), ("latencyMaxMs", max),
            ("parentChanges", parentChanges), ("parent", final.Parent?.Value), ("lastParent", lastParent), ("attached", final.Attached), ("overServer", final.OverServer),
            ("retransmitted", final.Retransmitted), ("recovered", final.Recovered), ("duplicates", final.Duplicates), ("dropped", final.Dropped),
            ("children", final.Children.Count), ("relayCapable", node.IsRelayCapable), ("nat", node.NetworkStatus.Nat.Type.ToString()));
    }

    // ---- a voice channel ----------------------------------------------------------------------

    public static async Task VoiceAsync(ChatNode node, RoomKey key, AgentArgs a, CancellationToken ct)
    {
        var room = await node.JoinRoomAsync(key, a.Str("name"), ct);
        using var session = room.JoinVoice();
        session.Activation = VoiceActivation.Open;
        bool speak = a.Flag("speak");
        double hz = a.Dbl("tone", 440);

        var buffer = new float[480];
        var tone = new float[480];
        long offset = 0, heard = 0, ticks = 0, lastStat = 0, treeAttachedAt = -1;
        var clock = Stopwatch.StartNew();
        long next = 0;
        var started = Out.Ms;

        while (!ct.IsCancellationRequested)
        {
            if (speak)
            {
                for (int i = 0; i < tone.Length; i++) tone[i] = (float)(0.4 * Math.Sin(2 * Math.PI * hz * (i + offset) / 48000));
                offset += tone.Length;
                session.PushCapture(tone);
            }
            session.PullPlayback(buffer);
            ticks++;
            if (buffer.Max(Math.Abs) > 0.05f) heard++;

            var tree = room.VoiceTreeStatus;
            if (tree is { Attached: true } && treeAttachedAt < 0) treeAttachedAt = Out.Ms - started;

            if (Out.Ms - lastStat >= 1000)
            {
                lastStat = Out.Ms;
                var speakers = new Dictionary<string, object?>();
                foreach (var p in room.VoiceParticipants.Where(p => !p.IsSelf))
                {
                    var (received, lost, underruns) = session.Stats(p.Peer.Id);
                    speakers[p.Peer.DisplayName] = $"{received}/{lost}/{underruns}";
                }
                Out.Write("stat", ("participants", room.VoiceParticipants.Count), ("heardFrames", heard), ("ticks", ticks),
                    ("tree", tree != null), ("isRoot", tree?.IsRoot ?? false), ("parent", tree?.Parent?.Value), ("children", tree?.Children.Count ?? 0),
                    ("forwarded", tree?.Forwarded ?? 0), ("speakers", speakers));
            }

            next += 10;
            long wait = next - clock.ElapsedMilliseconds;
            if (wait > 0) { try { await Task.Delay((int)wait, ct); } catch (OperationCanceledException) { break; } }
            else if (wait < -500) next = clock.ElapsedMilliseconds;
        }

        var final = room.VoiceTreeStatus;
        var perSpeaker = new Dictionary<string, object?>();
        foreach (var p in room.VoiceParticipants.Where(p => !p.IsSelf))
        {
            var (received, lost, underruns) = session.Stats(p.Peer.Id);
            perSpeaker[p.Peer.DisplayName] = $"{received}/{lost}/{underruns}";
        }
        Out.Write("summary", ("role", "voice"), ("speaker", speak), ("participants", room.VoiceParticipants.Count), ("heardFrames", heard), ("ticks", ticks),
            ("firstPeerAtMs", Found.FirstPeerAtMs), ("peersMost", Found.PeersMost),
            ("treeAttachedAtMs", treeAttachedAt), ("tree", final != null), ("isRoot", final?.IsRoot ?? false), ("parent", final?.Parent?.Value),
            ("children", final?.Children.Count ?? 0), ("forwarded", final?.Forwarded ?? 0), ("speakers", perSpeaker),
            ("relayCapable", node.IsRelayCapable), ("nat", node.NetworkStatus.Nat.Type.ToString()));
    }
}
