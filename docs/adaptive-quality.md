# Adapting a share to the viewers

Design for stage 3 of [#225](https://github.com/zburtle/Yaparr/issues/225). Nothing here is built yet except the first piece (the encoder takes a new bitrate while it encodes, see below). It is written to be argued with before any live behaviour changes.

## The problem

A share is one stream: the sharer encodes once and the tree passes the same packets to every viewer. When a viewer loses frames today, the stream stays as it is: the sharer sends 6 Mbit/s to a link that carries 3. Lowering the stream helps that viewer, and costs everyone else, so the rule has to be about the room, not about one viewer.

Goals:
- Lower the stream when the room needs it (most viewers, or the sharer's own link, in trouble for long enough), and raise it again when the trouble is gone.
- Never let one viewer, a weak link or a person with bad intentions, lower it for everyone else.
- Say what was done and why, in the sharer's panel and in the log.
- Tell a viewer when the trouble is their own connection, and only when we are sure.

Not goals: a stream per viewer (stage 7 of #225, a second, lower stream, at the end of this page), changing Android's sending, or choosing quality from the content.

## What can be seen today

At the sharer:
- its own sending: frames and bytes, packets sent again and dropped (`MediaStatus.Retransmitted`, `Dropped`), the longest wait for a frame, how long encoding takes (`ShareFigures`);
- key frame requests that reached it (`KeyFramePacer`: requested, forced, held back);
- who sits where in the tree (`MediaGroup.Map`, from the `link` and `here` reports);
- its direct viewers' `nack`s (what they are missing).

Not seen: how a viewer further down is doing. A relay repairs its children's losses from its own cache, so the sharer sees nothing of them unless a key frame request travels all the way up.

At each viewer (since #228): packets that came, were missing, turned up late, were repaired after asking, never came; frames lost, skipped waiting for a key frame, too late to show, dropped by the decoder; pictures drawn per second (`WatchFigures`). None of it leaves the viewer.

So the missing piece is a report going up.

## The quality report

A new control operation, `quality`, sent by a viewer to its parent every 2 s while it watches, and passed up by each relay, which folds its children's reports into its own so that what leaves a relay is one small message about the whole subtree:

| field | meaning |
|---|---|
| `Viewers` | how many viewers the report covers (this one and everyone below) |
| `Troubled` | how many of them had trouble in the last 10 s (see below) |
| `Worst` | the worst picture rate of those (pictures drawn per second, so a sharer can see "nobody gets more than 20") |
| `Lossy` | how many lose packets they never get back (loss that resending does not repair) |

No names: a relay says how many, not who. (The sharer's panel can still say "3 of 4".) Reports are small (tens of bytes) and a relay sends at most one every 2 s, so a room of any size adds a trickle at the root. A report is good for 10 s at the root and each relay: the first real share showed a viewer on a link that lost 12 to 22% of the packets, whose reports were late often enough that the room looked silent in the middle of its trouble, so the policy also holds its count where it is for up to 15 s when no report arrives with viewers there (and starts again after that).

A viewer **has trouble** when, over the last 10 s, it showed fewer than 90% of the pictures that were sent, or lost frames it asked for and did not get back (the "never came" count of #228), or its decoder dropped frames. The viewer measures this against what the stream says it sends (frames per second are in the stream's own numbering), so it needs no clock agreement with anyone.

Old versions ignore an operation they do not know, and do not pass it up, so a root sometimes sees only part of the room (the newer viewers whose relays are also new). **Missing reports mean "no information", never "fine"** (and a gap of a few seconds holds the count where it is): the policy below only counts the viewers that reported, and does nothing when it has none.

## The policy

One decision a second, on the sharer, from two things: the room (the reports) and the sharer's own link (resends and drops of its direct viewers, the encoder keeping up).

**Step down** when, for 10 s in a row, either
- at least half of the viewers that reported are in trouble, and at least two of them (with one viewer in the room, that viewer is the room: its trouble counts), or
- the sharer's own link is the cause: more than 5% of what it sent was sent again, or anything was dropped, to more than one direct viewer.

**Never** step down because of one viewer among three or more, however bad. That viewer gets the message below.

**Step up** after 30 s with nobody in trouble (and at least 20 s since the last change), one step at a time. If a step up is followed by trouble within 60 s, the wait before the next try doubles (30, 60, 120 s, at most 5 minutes), so a link that cannot take the higher quality is not tried every half minute. The wait falls back to 30 s after 10 minutes without trouble. The gap between stepping down (10 s) and up (30 s and more) is the "wide gap" that stops it flapping.

**The ladder** goes down the presets of the share dialog, one at a time (`QualityLadder`), from what the person asked for (the ceiling):

| asked for | step 1 | step 2 |
|---|---|---|
| 1080p60 at 6 Mbit/s | 1080p30 at 4 Mbit/s | 720p30 at 2 Mbit/s |
| 1080p30 at 4 Mbit/s | 720p30 at 2 Mbit/s | 720p30 at 1.4 Mbit/s (70%) |
| 720p30 at 2 Mbit/s | 720p30 at 1.4 Mbit/s (70%) | 720p30 at 0.9 Mbit/s (45%) |

A share asked for below every preset keeps its own size and frame rate and goes down in bitrate (70%, then 45%). No step asks for more than the share did, and nothing goes below 500 kbit/s. A step that changes only the bitrate costs nothing: `SetBitrate`, no key frame. A step that changes the frame rate or the size builds the capture and the encoder again (the share's run ends and the next is made for the new level, about a second without a picture, then a key frame); viewers restart their decoder on a new size, which they already do for any size change. Going back up uses the same table the other way.

Below the last step the stream stays where it is and the log says the room cannot be served at a lower quality. The ceiling is whatever the settings say, and adapting never goes above it.

## What a person sees

- The sharer's panel gets one line while the stream is lowered: `Quality lowered to 720p30 at 2.7 Mbit/s: 3 of 4 viewers lost frames for 10 s` (and `back to full quality` when it is). Each change goes to the log with the same words and the numbers behind it.
- A setting, on by default: *Lower the quality when viewers cannot keep up*, with the floor.
- A viewer who is the only one in trouble, while the sharer's last report says most of the room is fine (and at least two others reported), sees: *The picture is struggling on your connection. The others are fine.* It is said once the trouble has lasted 15 s, and never when the room is small enough to be uncertain (fewer than two others), or when the sharer's own link had trouble in that time. This is the message asked for in the thread that led here: only when we are certain.

To let a viewer say that, the root sends a tiny `room` state down the tree with the stream, every 5 s: how many reported, how many are in trouble, whether its own link is fine. It is a few bytes, and a viewer on an old version ignores it.

## Trust

A report is a claim by a member of the room. A person in the room who wants to ruin a share could report trouble. So:
- the policy needs at least two viewers' trouble (one viewer's report moves nothing unless that viewer is alone in the room), so one liar cannot do anything while others watch;
- reports are counts, capped at the number of viewers the sender can have under it;
- the sharer's own link is measured by the sharer, which needs no one's word;
- the setting turns adapting off, and the log says every time a report moved the stream, so a pattern is visible.

Two liars in a room of three could still lower the stream. That is the price of a design in which nobody holds a quality number but the people watching; the sharer's panel shows the decision, and the setting is there.

## Rolling it out

1. **Shadow mode first.** The report and the policy run, the decisions go to the log (`would lower to ...`), and nothing changes. One release of that shows how often the policy would act in real rooms, and whether it flaps, before it acts.
2. The ladder (the bitrate first, then the presets), behind a setting in the share dialog.
3. The message to a lone struggler.

The policy is a pure function: reports and the sharer's own figures in, a step (or no change) out, with the clock passed in. It is tested with scripted rooms: one weak viewer among ten (no change), half the room (step down after 10 s, not after 9), the room recovering (step up after 30 s, not after 29), a link that cannot take step 0 (the wait doubles), no reports at all (no change), a single viewer (that viewer decides), two liars in three. The cluster tests that already drop packets between nodes (`OutboundFilter`) exercise the whole path on a simulated bad link.

## Where it stands

Built (the steps are acted on when the encoder runs in this program; otherwise the policy runs in shadow mode and says what it would do, and nothing is changed):
- `InProcessEncoder.SetBitrate`: the bitrate changes while encoding, no restart and no lost frame; measured with pictures that are expensive to code, 6000, 1500 and 6000 kbit/s asked for gave about 7000, 1500 and 6900 kbit/s (steps 1 and 2 of the ladder).
- `QualityPolicy` and `QualityLadder` (`QualityPolicyTests`): the rules above as a pure function, with scripted rooms.
- The report: `ViewerQualityMeter` (a viewer judging its own picture over ten seconds), the `quality` control operation, folded by each relay on the way up (`MediaSource.Room` at the root; `QualityReportTests` run it through a chain of relays).
- A viewer's own log carries its verdict: the `call quality: watching` line (and "Copy sharing diagnostics") ends its figures with `judged in trouble, 22 pictures a second over the last ten seconds, and told the sharer so` (or `judged fine`, or `not judged yet`), so one viewer's log shows what that viewer reported, beside the figures it is made from.
- `ScreenSharer` runs the policy every second from `Room` and its own link, writes `adaptive quality (shadow mode, nothing is changed): would lower to 1080p30 at 4.0 Mbit/s (step 1 of 2): 2 of 3 viewers lost frames for 10 s` to the log, and says where it is in the sharing line and the report (`ShadowQualityTests`).

- Acting on the ladder: when the encoder runs in this program (`ScreenOptions.InProcessEncoder`) and the setting is on, a decision to go to a step takes effect in one of two ways. A level with the same frame rate and size as the running capture only gets a pending bitrate, which the thread that encodes gives to `InProcessEncoder.SetBitrate` before its next picture. A level with another frame rate or size sets a restart: the capture loop ends, `PumpAsync` runs again, and `RunInProcessCaptureCoreAsync` builds the capture and the encoder from `RunOptions()` (the options asked for, at the level's box, frame rate and bitrate), so the share's clock, the encoder's time base and the key frame spacing all follow the new frame rate. Either way the stream is told (`MediaSource.EffectiveKbps`), so repairs and the pace of each key frame follow the bitrate. Where the capture is a separate FFmpeg the restart is not possible and only the bitrate follows. A share whose encoder is a separate FFmpeg, or whose setting is off, runs the policy in shadow mode as before. The log says `adaptive quality: lowered to 1080p30 at 4.0 Mbit/s (step 1 of 2): ...` when it acted and `adaptive quality (shadow mode, nothing is changed): would ...` when it did not (`ActingQualityTests`; `InProcessCaptureTests` runs a real share through 1080p60, 1080p30, 720p30 and back).
- The setting: "Lower the quality when viewers cannot keep up" in the share dialog, on by default, remembered (`AppData.ScreenShareAdaptQuality`); the environment variable `YAPARR_ADAPT=0` turns it off for a run. The sharer panel shows `Quality lowered to 720p30 at 2.0 Mbit/s: ...` while the share is below what was asked for.
- A cap on repairs: what a sender may resend is a quarter of the stream (`ChatNode.RepairShare`, a token bucket per stream, at least 32 kB/s), because repairs paced at the stream's own rate doubled the load on a link that was already full. What is held back is counted (`MediaStatus.RepairsRefused`, `N repairs held back` in the sharing line); `RepairCapTests` run it over a link that loses 35% and one that loses 3%.

Still to do: the `room` state sent down the tree and the message to a viewer who is the only one in trouble.

## Questions that are open

- Is 10 s the right time to wait? Too short and every Wi-Fi hiccup lowers the room; too long and a viewer watches a bad picture. Shadow mode has the numbers.
- 5% of the sharer's traffic sent again as "its own link is the cause": the figure comes from one Wi-Fi viewer (about 7% missing, #228); it needs real rooms.
- Should a step that restarts the capture (another frame rate or size) wait longer than one that only changes the bitrate (say 30 s of trouble)? It costs a second without a picture.
- A viewer far down the tree behind a relay that has trouble of its own: the relay's children all report trouble; the policy sees "3 of 6" and might lower the stream for a fault in one relay. Reports could carry how many of the troubled sit under one parent, and the policy could discount a subtree that is troubled all together. Not designed yet.

## Later: a second, lower stream

Stage 3 lowers the one stream, so it acts only when most of the room struggles. A viewer alone on a bad link is not helped by it. For that, the sharer would send a second, smaller stream (for example 540p at 1.5 Mbit/s) as its own stream with its own tree, and a viewer who is losing frames moves to it at a key frame, and back after a quiet stretch. Nobody else is touched.

What it would take, as far as it is understood:
- a second encoder (NVIDIA lets a consumer card run several encode sessions; with the pictures kept on the card the processor cost is small), but the smaller picture needs its own scaling, and the capture cannot hand out two sizes: a second capture of the same window, or a scaling step on the card that FFmpeg does not have for Direct3D 11 pictures that work (`scale_d3d11` does not on the card tried);
- a second media group (the tree code keys everything on the group), announced with the first so that viewers know it is there;
- the viewer's choice, made by the viewer from the same figures it already has (start low when it is losing frames, try the main stream again after a quiet stretch), and the "it is your connection" message as the moment to offer it;
- the report above would then count viewers per stream, so that the room's decision is about the viewers on the main one.

Discord is reported (by users, not by Discord) to do the opposite: one weak viewer pulled the stream down for the room. A second stream is what avoids that.
