using System.Runtime.CompilerServices;
using Yaparr.Screen;

namespace Yaparr.Tests;

/// <summary>Viewers made by the tests mark a guard file while they decode; that file is a temporary one, not the one of the person running the tests.</summary>
internal static class DecodeGuardForTests
{
    [ModuleInitializer]
    internal static void Run() => InProcessDecodeGuard.UseFile(Path.Combine(Path.GetTempPath(), "yaparr-tests-decode-guard-" + Environment.ProcessId, "mark.txt"));
}
