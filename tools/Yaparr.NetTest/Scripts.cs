using System.Text;

/// <summary>The shell scripts the tool runs on the machines it creates.</summary>
internal static class Scripts
{
    /// <summary>Runs once when a machine first starts: the tools for shaping the network and, for the TURN machine, the server.</summary>
    /// <param name="shaping">Whether the scenario degrades the network (tc netem), which needs a large kernel module package.
    /// Installing that on many small machines at once can make them too slow to answer, so it is left out when not needed.</param>
    /// <param name="baked">The machine starts from the snapshot made by 'bake', so nothing needs installing.</param>
    public static string CloudInit(bool turn, string turnPassword, bool shaping = true, bool baked = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#cloud-config");
        if (!baked && (turn || shaping)) sb.AppendLine("package_update: true");
        if (turn)
        {
            sb.AppendLine("write_files:");
            sb.AppendLine("  - path: /etc/turnserver.conf");
            sb.AppendLine("    defer: true");   // written after the package is installed, so it is not overwritten
            sb.AppendLine("    content: |");
            foreach (var line in new[]
            {
                "listening-port=3478", "fingerprint", "lt-cred-mech", $"user=nettest:{turnPassword}", "realm=yaparr-nettest",
                "no-tls", "no-dtls", "no-cli", "min-port=49152", "max-port=65535", "simple-log",
            })
                sb.AppendLine("      " + line);
        }
        if (baked)
        {
            // An empty runcmd list is an error for cloud-init, so the key is only written when there is something to run.
            if (shaping || turn) sb.AppendLine("runcmd:");
            if (shaping) sb.AppendLine("  - [ sh, -c, \"modprobe sch_netem || true\" ]");
            if (turn) sb.AppendLine("  - [ sh, -c, \"systemctl enable coturn && systemctl restart coturn\" ]");
            return sb.ToString().Replace("\r\n", "\n");
        }
        sb.AppendLine("runcmd:");
        if (shaping)
        {
            sb.AppendLine("  - [ sh, -c, \"DEBIAN_FRONTEND=noninteractive apt-get install -y -qq iproute2 iptables linux-modules-extra-$(uname -r) || true\" ]");
            sb.AppendLine("  - [ sh, -c, \"modprobe sch_netem || true\" ]");
        }
        else
            sb.AppendLine("  - [ sh, -c, \"command -v iptables >/dev/null && command -v ip >/dev/null || (apt-get update -qq; DEBIAN_FRONTEND=noninteractive apt-get install -y -qq iproute2 iptables) || true\" ]");
        if (turn)
        {
            sb.AppendLine("  - [ sh, -c, \"DEBIAN_FRONTEND=noninteractive apt-get install -y -qq coturn\" ]");
            sb.AppendLine("  - [ sh, -c, \"sed -i 's/^#TURNSERVER_ENABLED=1/TURNSERVER_ENABLED=1/' /etc/default/coturn || true\" ]");
            sb.AppendLine("  - [ sh, -c, \"systemctl enable coturn && systemctl restart coturn\" ]");
        }
        return sb.ToString().Replace("\r\n", "\n");
    }

    /// <summary>First-start script for the machine that becomes the snapshot: every tool a scenario can need, nothing running.</summary>
    public static string BakeInit() => string.Join("\n", new[]
    {
        "#cloud-config",
        "package_update: true",
        "runcmd:",
        "  - [ sh, -c, \"DEBIAN_FRONTEND=noninteractive apt-get install -y -qq iproute2 iptables coturn\" ]",
        "  - [ sh, -c, \"DEBIAN_FRONTEND=noninteractive apt-get install -y -qq linux-modules-extra-$(uname -r) || true\" ]",
        "  - [ sh, -c, \"systemctl disable --now coturn || true\" ]",
        "",
    });

    /// <summary>
    /// What a machine started from the snapshot still needs before it is ready: the traffic shaping module, and for the
    /// TURN machine the server once its settings file has been written.
    /// </summary>
    public static string BakedReady(bool turn, bool shaping)
    {
        var sb = new StringBuilder();
        if (shaping) sb.AppendLine("modprobe sch_netem || true");
        if (turn)
        {
            sb.AppendLine("for i in $(seq 1 60); do grep -q '^user=nettest:' /etc/turnserver.conf 2>/dev/null && break; sleep 2; done");
            sb.AppendLine("grep -q '^user=nettest:' /etc/turnserver.conf");
            sb.AppendLine("systemctl enable coturn >/dev/null 2>&1; systemctl restart coturn");
        }
        sb.AppendLine("true");
        return sb.ToString().Replace("\r\n", "\n");
    }

    /// <summary>Checks on the machine that everything the scenarios use is there.</summary>
    public const string BakeCheck = "command -v iptables && command -v ip && command -v tc && command -v turnserver && (modprobe sch_netem && echo netem-ok)";

    /// <summary>The network interface that reaches the internet.</summary>
    public const string FindInterface = "ip -o -4 route show to default | awk '{print $5}' | head -n 1";

    /// <summary>
    /// Puts the agent behind a router of its own. The machine becomes the router and the agent lives in a private
    /// network behind it, so everything it sends is translated the way a home router would.
    /// </summary>
    public static string Nat(string type, int udpPort) => $$"""
        IF=$({{FindInterface}})
        ip netns del behind 2>/dev/null || true
        ip netns add behind
        ip link add veth-out type veth peer name veth-in
        ip link set veth-in netns behind
        ip addr add 10.77.0.1/24 dev veth-out
        ip link set veth-out up
        ip netns exec behind ip addr add 10.77.0.2/24 dev veth-in
        ip netns exec behind ip link set veth-in up
        ip netns exec behind ip link set lo up
        ip netns exec behind ip route add default via 10.77.0.1
        mkdir -p /etc/netns/behind
        echo "nameserver 1.1.1.1" > /etc/netns/behind/resolv.conf
        sysctl -qw net.ipv4.ip_forward=1
        iptables -I FORWARD -s 10.77.0.0/24 -j ACCEPT
        iptables -I FORWARD -d 10.77.0.0/24 -j ACCEPT
        {{NatRule(type, udpPort)}}
        """;

    private static string NatRule(string type, int udpPort) => type switch
    {
        // The source port is kept when it can be, and replies are only let in from the address they were sent to.
        "cone" => "iptables -t nat -A POSTROUTING -s 10.77.0.0/24 -o $IF -j MASQUERADE",
        // The same, with the agent's port also open to anyone, as with a port forward.
        "fullcone" => $"iptables -t nat -A POSTROUTING -s 10.77.0.0/24 -o $IF -j MASQUERADE\niptables -t nat -A PREROUTING -i $IF -p udp --dport {udpPort} -j DNAT --to-destination 10.77.0.2:{udpPort}",
        // A different outside port for every destination.
        "symmetric" => "iptables -t nat -A POSTROUTING -s 10.77.0.0/24 -o $IF -p udp -j MASQUERADE --random\niptables -t nat -A POSTROUTING -s 10.77.0.0/24 -o $IF -j MASQUERADE",
        _ => throw new ArgumentException($"Unknown NAT type {type}."),
    };

    public static string NatCleanup() => """
        ip netns del behind 2>/dev/null || true
        iptables -t nat -F 2>/dev/null || true
        """;

    public static string NetemOn(string args) => $"tc qdisc replace dev $({FindInterface}) root netem {args}";
    public static string NetemOff() => $"tc qdisc del dev $({FindInterface}) root 2>/dev/null || true";

    public static string Block(string ip, bool add)
    {
        var flag = add ? "-I" : "-D";
        return $"""
            iptables {flag} INPUT -s {ip} -p udp -j DROP
            iptables {flag} OUTPUT -d {ip} -p udp -j DROP
            iptables {flag} FORWARD -s {ip} -p udp -j DROP
            iptables {flag} FORWARD -d {ip} -p udp -j DROP
            """;
    }
}
