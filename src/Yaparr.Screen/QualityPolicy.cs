namespace Yaparr.Screen;

/// <summary>
/// What the viewers of a share say, as far as it has reached the sharer (see docs/adaptive-quality.md): how many viewers reported, how many of them are
/// in trouble, the worst picture rate among them, and how many lose packets that resending does not get back. Viewers whose reports did not arrive (an
/// older version, or a relay that does not pass them up) are not counted at all: no report is no information, never "fine".
/// </summary>
public readonly record struct RoomQuality(int Reported, int Troubled, double WorstFps = 0, int Lossy = 0)
{
    public static RoomQuality None => default;
}

/// <summary>
/// What the sharer sees of its own link over the last few seconds: the share of what it sent that it had to send again, packets it dropped, and how many
/// viewers it sends to directly. <paramref name="SenderBehind"/>: this computer is not sending the pictures a second it was set to, without the encoder being
/// too slow for them (the capture or the clock is late), so what the viewers lose is not for the quality to fix.
/// </summary>
public readonly record struct OwnLink(double ResentFraction, long Dropped, int DirectViewers, bool SenderBehind = false);

/// <summary>What the policy decided this second: the step (0 is the quality asked for), whether it changed, and why, in words for a log or a panel.</summary>
public readonly record struct QualityDecision(int Step, bool Changed, string? Why, bool AtFloor = false, bool SenderBehind = false);

/// <summary>
/// When to lower the quality of a share for the viewers and when to raise it again. A pure function of what it is told and the time it is told it at,
/// so it can be run with scripted rooms: one weak viewer among ten changes nothing, half the room lowers it after ten seconds of trouble, a quiet
/// half minute raises it one step, and a link that cannot take the higher step is tried less and less often (docs/adaptive-quality.md).
/// </summary>
public sealed class QualityPolicy
{
    /// <summary>The numbers of the design, in one place.</summary>
    public sealed record Settings
    {
        public int TroubleSeconds { get; init; } = 10;
        public int QuietSeconds { get; init; } = 30;
        public int MinSecondsBetweenChanges { get; init; } = 20;
        /// <summary>A step up followed by trouble within this long doubles the wait before the next.</summary>
        public int FlapSeconds { get; init; } = 60;
        public int BackoffMaxSeconds { get; init; } = 900;
        /// <summary>After this long without trouble the wait for a step up falls back to <see cref="QuietSeconds"/>.</summary>
        public int BackoffResetSeconds { get; init; } = 1800;
        /// <summary>
        /// When no report arrives with viewers there (a report late over a link that loses much of what it carries), the count of trouble or quiet is held for
        /// this long, so that a gap does not wipe out seconds that were counted; after it, no report is no information and the count starts again.
        /// </summary>
        public int CarrySeconds { get; init; } = 15;
        /// <summary>The share of what was sent that is sent again, above which the sharer's own link is taken to be the cause.</summary>
        public double ResentLimit { get; init; } = 0.05;
        public int MaxStep { get; init; } = QualityLadder.Steps - 1;
    }

    /// <summary>How often <see cref="Tick"/> is called, in milliseconds.</summary>
    public const long TickMs = 1000;

    private readonly Settings _s;
    private long _troubleSince = -1, _quietSince = -1, _lastChange = long.MinValue / 2, _lastUp = long.MinValue / 2, _lastTrouble = long.MinValue / 2, _lastReportAt = long.MinValue / 2;
    private int _upWaitSeconds;

    public QualityPolicy(Settings? settings = null)
    {
        _s = settings ?? new Settings();
        _upWaitSeconds = _s.QuietSeconds;
    }

    /// <summary>The step now: 0 is what was asked for, each one above it is lower (see <see cref="QualityLadder"/>).</summary>
    public int Step { get; private set; }

    /// <summary>How long the room has to be quiet before the next step up, which grows when steps up are followed by trouble.</summary>
    public int UpWaitSeconds => _upWaitSeconds;

    /// <summary>The room is in trouble: at least half of the viewers that reported and at least two of them, or the one viewer there is.</summary>
    public static bool RoomInTrouble(RoomQuality room)
        => room.Reported >= 1 && ((room.Reported == 1 && room.Troubled >= 1) || (room.Reported >= 2 && room.Troubled >= 2 && room.Troubled * 2 >= room.Reported));

    /// <summary>The sharer's own link is the cause: it had to send a good share of the stream again, or dropped packets, and sends to more than one viewer directly (with one it is that viewer's link, which the room rule sees).</summary>
    public bool OwnLinkInTrouble(OwnLink own) => own.DirectViewers >= 2 && (own.ResentFraction > _s.ResentLimit || own.Dropped > 0);

    /// <summary>Called about once a second. <paramref name="viewers"/> is how many viewers the sharer knows of (from the tree), reported or not.</summary>
    public QualityDecision Tick(long nowMs, RoomQuality room, OwnLink own, int viewers)
    {
        // A sharer that is not producing its own frame rate is the cause of what the viewers lose, whatever their links are: lowering the quality for them would
        // change nothing they can see, so nothing is counted either way (trouble or quiet) until it is sending what it should again.
        if (own.SenderBehind)
        {
            _troubleSince = -1;
            _quietSince = -1;
            return new QualityDecision(Step, false, null, SenderBehind: true);
        }
        bool roomTrouble = RoomInTrouble(room), ownTrouble = OwnLinkInTrouble(own);
        if (room.Reported > 0) _lastReportAt = nowMs;
        else if (viewers > 0 && !ownTrouble && nowMs - _lastReportAt <= _s.CarrySeconds * 1000L)
        {
            // Nothing arrived this second, with viewers there and a report a moment ago (late over a link that loses much of what it carries): no information
            // changes nothing, so the count of trouble or quiet is held where it is and goes on when reports come back. After the carry it starts again.
            if (_troubleSince >= 0) _troubleSince += TickMs;
            if (_quietSince >= 0) _quietSince += TickMs;
            return new QualityDecision(Step, false, null);
        }
        bool trouble = roomTrouble || ownTrouble;
        // Quiet: nobody that reported is in trouble and the sharer's link is fine, with someone to hear from; or nobody is watching at all.
        bool quiet = !trouble && ((room.Reported > 0 && room.Troubled == 0) || viewers == 0);
        if (trouble) _lastTrouble = nowMs;
        if (nowMs - _lastTrouble >= _s.BackoffResetSeconds * 1000L) _upWaitSeconds = _s.QuietSeconds;

        // The second just seen counts: ten seconds of trouble is the tenth one-second look that shows it, and a quiet half minute the thirtieth.
        if (trouble) { if (_troubleSince < 0) _troubleSince = nowMs - TickMs; _quietSince = -1; }
        else
        {
            _troubleSince = -1;
            if (quiet) { if (_quietSince < 0) _quietSince = nowMs - TickMs; }
            else _quietSince = -1;   // some viewers in trouble, not enough to act on; or no information: hold where we are
        }

        if (_troubleSince >= 0 && nowMs - _troubleSince >= _s.TroubleSeconds * 1000L)
        {
            if (Step >= _s.MaxStep)
            {
                _troubleSince = nowMs;
                return new QualityDecision(Step, false, "the room is still in trouble and the quality is as low as it goes", AtFloor: true);
            }
            string why = roomTrouble ? $"{room.Troubled} of {room.Reported} viewers lost frames for {_s.TroubleSeconds} s" : $"this computer's own link: {own.ResentFraction * 100:0}% sent again, {own.Dropped} dropped";
            // A step up that was followed by trouble within the minute: the link cannot take it, so the next try is further off.
            if (nowMs - _lastUp < _s.FlapSeconds * 1000L) _upWaitSeconds = Math.Min(_upWaitSeconds * 2, _s.BackoffMaxSeconds);
            Step++;
            _lastChange = nowMs;
            _troubleSince = nowMs;
            return new QualityDecision(Step, true, why);
        }

        if (_quietSince >= 0 && Step > 0 && nowMs - _quietSince >= _upWaitSeconds * 1000L && nowMs - _lastChange >= _s.MinSecondsBetweenChanges * 1000L)
        {
            Step--;
            _lastChange = nowMs;
            _lastUp = nowMs;
            _quietSince = nowMs;
            return new QualityDecision(Step, true, viewers == 0 ? "nobody is watching" : $"no viewer in trouble for {_upWaitSeconds} s");
        }
        return new QualityDecision(Step, false, null);
    }
}

/// <summary>What a step of the ladder means for the picture: the bitrate, the frame rate and the largest size.</summary>
public readonly record struct QualityLevel(int Kbps, int Fps, int Width, int Height)
{
    /// <summary>For a log or a panel: "720p30 at 2.7 Mbit/s".</summary>
    public string Describe() => $"{Height}p{Fps} at {(Kbps >= 1000 ? (Kbps / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " Mbit/s" : Kbps + " kbit/s")}";
}

/// <summary>
/// The steps from the quality that was asked for down through the presets of the share dialog, one preset at a time, with the same picture at 70% of its
/// bitrate before each drop in size: from 1080p at 60 frames a second the steps are 1080p30 at 4 Mbit/s, 1080p30 at 2.8 Mbit/s and 720p30 at 2 Mbit/s. The extra
/// step is there because a link often carries something between two presets, and a size drop is the step a viewer notices most. A share asked for at a lower
/// preset has fewer presets left and goes down in bitrate (70% each) for the rest. Nothing goes below 500 kbit/s, or above what was asked for.
/// </summary>
public static class QualityLadder
{
    public const int Steps = 4, FloorKbps = 500;

    /// <summary>What the share dialog offers, lowest first: the box the picture is fitted into (never scaled up), the frame rate and the bitrate in kbit/s.</summary>
    public static readonly (int Width, int Height, int Fps, int Kbps)[] Presets =
    {
        (1280, 720, 30, 2000),
        (1920, 1080, 30, 4000),
        (1920, 1080, 60, 6000),
    };

    /// <summary>
    /// The level of <paramref name="step"/> for a share asked for with <paramref name="kbps"/> and <paramref name="fps"/>, in a box of <paramref name="width"/> x
    /// <paramref name="height"/>. A step never asks for more than the share did, in any of the three.
    /// </summary>
    public static QualityLevel For(int step, int kbps, int fps, int width, int height)
        => Levels(kbps, fps, width, height)[Math.Clamp(step, 0, Steps - 1)];

    /// <summary>All the levels, from the one asked for (step 0) down; always <see cref="Steps"/> of them.</summary>
    public static IReadOnlyList<QualityLevel> Levels(int kbps, int fps, int width, int height)
    {
        var levels = new List<QualityLevel> { new(kbps, fps, width, height) };
        // The preset the share was asked for: the highest one that is not above it (none, when it is below them all).
        int asked = -1;
        for (int i = 0; i < Presets.Length; i++)
            if (Presets[i].Height <= height && Presets[i].Fps <= fps) asked = i;
        for (int i = asked - 1; i >= 0 && levels.Count < Steps; i--)
        {
            var p = Presets[i];
            var current = levels[^1];
            var next = new QualityLevel(Math.Max(Math.Min(kbps, FloorKbps), Math.Min(kbps, p.Kbps)), Math.Min(fps, p.Fps), Math.Min(width, p.Width), Math.Min(height, p.Height));
            // Before the picture gets smaller, the same picture at 70%, when that is still more than the smaller one gets.
            if (next.Height < current.Height && levels.Count < Steps - 1)
            {
                int middle = Math.Max(Math.Min(current.Kbps, FloorKbps), (int)Math.Round(current.Kbps * 0.70));
                if (middle > next.Kbps) levels.Add(current with { Kbps = middle });
            }
            levels.Add(next);
        }
        // No preset left to go to: the last level at 70% of its bitrate, again and again.
        while (levels.Count < Steps)
        {
            var last = levels[^1];
            levels.Add(last with { Kbps = Math.Max(Math.Min(last.Kbps, FloorKbps), (int)Math.Round(last.Kbps * 0.70)) });
        }
        return levels;
    }
}
