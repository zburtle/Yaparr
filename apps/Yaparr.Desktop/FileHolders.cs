using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Yaparr.Desktop;

/// <summary>Asks Windows which other programs have a file open, through the Restart Manager that installers use for the same question.</summary>
[SupportedOSPlatform("windows")]
internal static class FileHolders
{
    private const int ErrorMoreData = 234;

    /// <summary>The ids of the processes other than this one that have the file open. Empty if none, or if Windows cannot say.</summary>
    public static IReadOnlyList<int> Others(string path)
    {
        var key = new StringBuilder(33);
        if (RmStartSession(out var session, 0, key) != 0) return Array.Empty<int>();
        try
        {
            if (RmRegisterResources(session, 1, [path], 0, null, 0, null) != 0) return Array.Empty<int>();
            uint count = 0;
            // The first call only says how many entries there are; ERROR_MORE_DATA is the normal answer when there are some.
            var result = RmGetList(session, out var needed, ref count, null, out _);
            if (result != ErrorMoreData || needed == 0) return Array.Empty<int>();
            var info = new RmProcessInfo[needed];
            count = needed;
            if (RmGetList(session, out _, ref count, info, out _) != 0) return Array.Empty<int>();
            return info.Take((int)count).Select(i => (int)i.Process.ProcessId).Where(id => id != Environment.ProcessId).ToList();
        }
        finally { RmEndSession(session); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public uint ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TsSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, StringBuilder key);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint files, string[]? fileNames, uint apps, RmUniqueProcess[]? processes, uint services, string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RmProcessInfo[]? info, out uint reasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);
}
