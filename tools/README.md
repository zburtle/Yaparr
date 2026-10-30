# Network tests

Two programs that check Yaparr on real machines:

- **`Yaparr.NetAgent`** is a headless node that takes one role (share a stream, watch one, or join a voice channel) and prints what happens as one JSON object per line.
- **`Yaparr.NetTest`** runs on your PC. It creates DigitalOcean machines, copies the agent to them, starts the agents, applies network conditions on a timeline, collects the output, checks it against the scenario's limits, writes a report, and deletes the machines.

Nothing here is part of the app. It is for finding out how the tree, the loss recovery and the TURN rule behave outside a single process.

## One-time setup

1. **A DigitalOcean token.** In the control panel: API, Generate New Token, with read and write scope. Put it in an environment variable and never in a file:

   ```powershell
   $env:DIGITALOCEAN_TOKEN = "dop_v1_..."
   ```

2. **Run `setup`** in the same terminal. It makes an SSH key for the tool (`~/.ssh/yaparr-nettest`, with no passphrase because the tool uses it unattended; an existing key is never overwritten), adds the public half to your DigitalOcean account, and writes `nettest.json` (the old one is kept as `nettest.json.bak`). The token is only read from the environment, never stored.

   ```powershell
   dotnet run --project tools/Yaparr.NetTest -- setup
   ```

   `--key <file>` uses a key you already have, and `--name <name>` chooses the name it gets in your account. If you would rather do it by hand: add a key under Settings, Security, SSH keys, and copy `tools/Yaparr.NetTest/nettest.example.json` to `nettest.json` with `sshKeyFile` and `sshKeyName` filled in. `nettest.json` and `results/` are ignored by git.

3. **Check it works** (creates nothing):

   ```powershell
   dotnet run --project tools/Yaparr.NetTest -- check
   ```

   It shows your account, your droplet limit, whether the key is found and how many test machines exist. New accounts often have a limit of only a few droplets; the voice scenario needs ten, so ask DigitalOcean support to raise it first.

`ssh`, `scp` and `dotnet` must be on the PATH (Windows 10 and 11 include the OpenSSH client).

## Running

```powershell
dotnet run --project tools/Yaparr.NetTest -- list                      # the scenarios
dotnet run --project tools/Yaparr.NetTest -- run basic --dry-run       # show the plan, create nothing
dotnet run --project tools/Yaparr.NetTest -- run basic                 # do it
dotnet run --project tools/Yaparr.NetTest -- run local-demo --local    # no cloud: everything on this PC
dotnet run --project tools/Yaparr.NetTest -- cleanup                   # delete every machine the tool made
```

A run takes the scenario's length plus two or three minutes for creating the machines, and about the same again to delete them. Results go to `results/<time>-<scenario>/`: `report.md` and one `<node>.jsonl` per agent. The exit code is 0 only if every check passed.

**Cost and safety.** The machines are the smallest size, billed while they exist; a run costs a few cents. The tool deletes what it created when it finishes, fails or is stopped with Ctrl+C. Everything carries the tag `yaparr-nettest`, and `cleanup` removes anything left over (for example after your PC lost power mid-run). `maxDroplets` in `nettest.json` (default 10) stops a scenario from creating more than you expect. `--keep` leaves the machines running so you can log in and look; you pay until you run `cleanup`.

## Scenarios

| Scenario | Machines | What it checks |
|---|---|---|
| `basic` | 3 | Two viewers on public addresses: attach time, first frame, loss, delay. |
| `tree` | 7 | The sharer sends to only two, each viewer to two: a tree at least two deep, and everyone still gets the video. |
| `loss` | 3 | A chain; the middle machine drops 4% of packets for 30 s. Lost packets are asked for again. |
| `failover` | 4 | The first viewer disappears; the second finds a new parent, the third keeps its video. |
| `nat` | 4 | Viewers behind an ordinary router, a router with a forwarded port, and a symmetric one. |
| `turn-big` | 3 | Both ends behind symmetric routers, so only the TURN server connects them. A 1500 kbit/s stream must be refused clearly. |
| `turn-small` | 3 | The same, with a 64 kbit/s stream, which must get through. |
| `voice` | 10 | Ten people in one voice channel: it must switch to a tree and both speakers must be heard. |
| `local-demo` | 0 | For `--local` only: tries the tool on this PC. |

**Settling, then streaming.** On the real DHT a machine that has just been created can take a minute or more to find the others (a cold start: no cached DHT nodes, and each lookup waits on silent nodes). So a run has two parts. First every machine joins the room and the sharer waits (up to `settleSeconds`, default 90) until the others are connected; then it shares for `durationSeconds`. Timeline steps count from the moment sharing starts. The report shows how long each node took to find a first peer ("Found a peer s") separately from the stream figures, and `discoveryWithin` can put a limit on it. Viewers finish a couple of seconds after the stream ends. The agents also record the library's discovery log lines (`"type":"log"` in the `.jsonl` files) and each peer that connects, to see where discovery time goes; pass `--verbose` as an agent argument for everything.

A scenario is a JSON file (comments allowed). Copy one and change it:

```jsonc
{
  "name": "mine", "description": "...", "settleSeconds": 90, "durationSeconds": 60,
  "turn": false,                         // true: also start a TURN server (coturn) on its own machine
  "nodes": [
    { "name": "sharer", "region": "nyc3", "role": "share", "args": { "kbps": 1500, "fps": 15, "root-fanout": 2 } },
    { "name": "v1", "region": "sfo3", "role": "watch", "nat": "symmetric", "startAfterSeconds": 5, "args": { "fanout": 2 } }
  ],
  "timeline": [
    { "atSeconds": 30, "action": "netem", "node": "v1", "args": "loss 4% delay 40ms" },
    { "atSeconds": 50, "action": "netem-clear", "node": "v1" }
  ],
  "assertions": [ { "type": "viewersReceive", "value": 0.95 } ]
}
```

- **Roles:** `share` (at most one), `watch`, `voice` (`"speak": true` plays a tone).
- **Regions:** DigitalOcean slugs such as `nyc1 nyc3 sfo3 tor1 lon1 ams3 fra1 sgp1 blr1 syd1`. Each node gets its own machine.
- **Agent arguments** (`args`): `kbps`, `fps`, `key-every`, `root-fanout`, `fanout`, `unsigned`, `tone`, `voice-threshold`, `max-metered-kbps`, `wait-group`, and anything else the agent takes (see the top of `tools/Yaparr.NetAgent/Program.cs`).
- **Timeline actions:** `kill` (end an agent), `freeze` / `thaw` (stop and continue it without closing its connections), `netem` / `netem-clear` (loss, delay and so on on that machine's network), `block` / `unblock` (drop all UDP between the node and the node named in `other`).
- **Assertions:** `viewersReceive`, `attachWithin`, `firstFrameWithin`, `treeDepthAtLeast`, `treeDepthAtMost`, `latencyP95Below`, `maxFrameGapBelow`, `discoveryWithin`, `overServer`, `noOverServer`, `retransmitsAtLeast`, `natIs`, `relayCapable`, `parentChangesAtLeast`, `voiceHeard`, `voiceTreeUsed`. An agent that ends without its summary line fails the run. A killed node is left out of the default checks.

## What the NAT settings do

`nat` puts the agent in a private network on its machine, and the machine acts as its router with `iptables`:

- `none`: the machine's public address, as on any server.
- `cone`: an ordinary home router. The outside port is kept, replies are let in only from where the packet went.
- `fullcone`: the same with the agent's port also open to anyone, as if forwarded.
- `symmetric`: a different outside port for every destination. Direct connections from it to other hard routers fail, the library should detect it and not relay for others, and with TURN configured it falls back to the TURN server.

This reproduces the router behaviour the library's connection logic cares about. It does not reproduce carrier-grade NAT, short timeouts, Wi-Fi, or a real ISP; for those, run an agent on a laptop on your phone's hotspot as a spot check.

## Reading a report

The table has one row per node: the NAT you set against the one the library found, the node's parent in the tree and its depth, frames received against frames that were sent between its first and last ("kept"), seconds from seeing the stream to attaching and to the first frame, delay (median and 95th percentile), the longest gap between frames, and packets sent again against recovered. Delay compares the sender's clock with the receiver's, so it is only as good as the machines' clock sync (about 50 ms with the usual NTP), and it leaves out the first three seconds, which include the catch-up a newcomer is given. After a failover the delay shows a spike: the replay hands the viewer everything since the last key frame in a burst.

## Limits and what is not yet known

- The DigitalOcean calls, the machine setup, the NAT and traffic-shaping scripts and the TURN server have been written and checked for form, but the first real run is likely to need a fix or two. `--dry-run` and `check` cost nothing and show the plan and your setup.
- A machine's traffic shaping needs the `sch_netem` kernel module; the first-start script installs it and the tool says so clearly if it is missing.
- All machines are in datacentres with public addresses; see above for what that leaves out.
