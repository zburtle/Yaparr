using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Yaparr.AppCore;

/// <summary>An audio or subtitle track of a film or an episode, as the server lists it.</summary>
public sealed record JellyfinTrack(int Index, string Title, string Language, string Codec, bool IsDefault, bool IsText);

/// <summary>What there is to play in an item: the file the server would use, how long it is and the tracks it has.</summary>
public sealed record JellyfinPlaybackInfo(string ItemId, string SourceId, long RunTimeTicks, string VideoRange,
    IReadOnlyList<JellyfinTrack> Audio, IReadOnlyList<JellyfinTrack> Subtitles);

/// <summary>What Yaparr asks the server to make of an item: a transport stream of H.264 (without B-frames, so each picture can go out when it comes) and AAC stereo.</summary>
public sealed record JellyfinStreamRequest(string ItemId, string SourceId, string PlaySessionId, int VideoKbps = 3000, int MaxHeight = 720,
    int? AudioIndex = null, int? SubtitleIndex = null, long StartTicks = 0);

/// <summary>A running transcode: the bytes of the transport stream, and what stops it on the server when it is disposed.</summary>
public sealed class JellyfinStream : IAsyncDisposable
{
    private readonly JellyfinClient _client;
    private readonly HttpResponseMessage _response;
    private int _disposed;

    internal JellyfinStream(JellyfinClient client, HttpResponseMessage response, Stream body, string playSessionId)
    {
        _client = client;
        _response = response;
        Body = body;
        PlaySessionId = playSessionId;
    }

    public Stream Body { get; }
    public string PlaySessionId { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await Body.DisposeAsync().ConfigureAwait(false);
        _response.Dispose();
        await _client.StopEncodingAsync(PlaySessionId).ConfigureAwait(false);
    }
}

public sealed partial class JellyfinClient
{
    /// <summary>The tracks of an item and how long it is.</summary>
    public async Task<JellyfinPlaybackInfo> GetPlaybackInfoAsync(string itemId, CancellationToken ct = default)
    {
        using var doc = await PostJsonAsync($"Items/{Uri.EscapeDataString(itemId)}/PlaybackInfo?userId={_userId}", new { }, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (!root.TryGetProperty("MediaSources", out var sources) || sources.ValueKind != JsonValueKind.Array || sources.GetArrayLength() == 0)
            throw new JellyfinException("There is nothing to play in that.");
        var source = sources[0];
        var audio = new List<JellyfinTrack>();
        var subtitles = new List<JellyfinTrack>();
        var range = "";
        if (source.TryGetProperty("MediaStreams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            foreach (var s in streams.EnumerateArray())
            {
                var type = Str(s, "Type");
                int index = Int(s, "Index") ?? -1;
                if (index < 0) continue;
                var title = Str(s, "DisplayTitle");
                var track = new JellyfinTrack(index, title.Length > 0 ? title : Str(s, "Language"), Str(s, "Language"), Str(s, "Codec"),
                    s.TryGetProperty("IsDefault", out var d) && d.ValueKind == JsonValueKind.True,
                    // Only text subtitles can be put into the picture without trouble; picture subtitles (PGS) are rendered by the server too, but slowly.
                    Str(s, "Codec") is "subrip" or "srt" or "ass" or "ssa" or "webvtt" or "mov_text");
                if (type == "Audio") audio.Add(track);
                else if (type == "Subtitle") subtitles.Add(track);
                else if (type == "Video" && range.Length == 0) range = Str(s, "VideoRangeType");
            }
        return new JellyfinPlaybackInfo(itemId, Str(source, "Id"), Long(source, "RunTimeTicks"), range, audio, subtitles);
    }

    /// <summary>The path (and query) that asks the server for the transcode. A seek and burned-in subtitles do not go together (see the remark in the code).</summary>
    public static string StreamPath(JellyfinStreamRequest r, string deviceId)
    {
        // Measured against a real server: with a start time and subtitles burned in, the sound came from the start of the film while the picture came from the start time, so the
        // two did not belong together. Either alone is fine.
        if (r.SubtitleIndex != null && r.StartTicks > 0) throw new ArgumentException("A film cannot be started part-way in with subtitles put into the picture.", nameof(r));
        var q = new StringBuilder();
        q.Append("mediaSourceId=").Append(Uri.EscapeDataString(r.SourceId));
        q.Append("&deviceId=").Append(Uri.EscapeDataString(deviceId));
        q.Append("&playSessionId=").Append(Uri.EscapeDataString(r.PlaySessionId));
        q.Append("&static=false&videoCodec=h264&audioCodec=aac&profile=high&level=41&maxRefFrames=1");
        q.Append("&videoBitRate=").Append(r.VideoKbps * 1000L);
        q.Append("&maxHeight=").Append(r.MaxHeight);
        q.Append("&audioBitRate=128000&audioChannels=2&maxAudioChannels=2&audioSampleRate=48000");
        q.Append("&allowVideoStreamCopy=false&allowAudioStreamCopy=false&breakOnNonKeyFrames=false");
        if (r.AudioIndex is { } a) q.Append("&audioStreamIndex=").Append(a);
        if (r.SubtitleIndex is { } s) q.Append("&subtitleStreamIndex=").Append(s).Append("&subtitleMethod=Encode");
        if (r.StartTicks > 0) q.Append("&startTimeTicks=").Append(r.StartTicks.ToString(CultureInfo.InvariantCulture));
        return $"Videos/{Uri.EscapeDataString(r.ItemId)}/stream.ts?" + q;
    }

    /// <summary>Starts the transcode and hands over its bytes. Dispose the result to stop it on the server.</summary>
    public async Task<JellyfinStream> OpenStreamAsync(JellyfinStreamRequest request, CancellationToken ct = default)
    {
        var response = await SendAsync(HttpMethod.Get, StreamPath(request, _deviceId), null, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new JellyfinException(status == HttpStatusCode.Unauthorized ? "The server did not accept this sign-in." : $"The server could not start the film ({(int)status}).", status);
        }
        var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return new JellyfinStream(this, response, body, request.PlaySessionId);
    }

    /// <summary>Tells the server to stop making a transcode nobody reads any more. A server that cannot be reached will stop it by itself.</summary>
    public async Task StopEncodingAsync(string playSessionId)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Delete, $"Videos/ActiveEncodings?deviceId={Uri.EscapeDataString(_deviceId)}&playSessionId={Uri.EscapeDataString(playSessionId)}", null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
    }

    /// <summary>Tells the server that this device started playing, moved on, or stopped, so that its "continue watching" and "watched" follow along.</summary>
    public async Task ReportPlaybackAsync(string kind, string itemId, string sourceId, string playSessionId, long positionTicks, CancellationToken ct = default)
    {
        var path = kind switch { "start" => "Sessions/Playing", "stop" => "Sessions/Playing/Stopped", _ => "Sessions/Playing/Progress" };
        try
        {
            using var response = await SendAsync(HttpMethod.Post, path, new { ItemId = itemId, MediaSourceId = sourceId, PlaySessionId = playSessionId, PositionTicks = positionTicks, CanSeek = false, PlayMethod = "Transcode" }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
    }
}
