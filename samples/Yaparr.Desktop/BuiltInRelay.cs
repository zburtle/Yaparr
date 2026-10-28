using System.Reflection;
using Yaparr.Transport;

namespace Yaparr.Desktop;

/// <summary>The relay compiled into this build (see the TurnServer, TurnUser and TurnCredential build properties).</summary>
internal static class BuiltInRelay
{
    public static TurnOptions? Load()
    {
        string? Get(string key) => Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value;
        var server = Get("TurnServer");
        if (string.IsNullOrWhiteSpace(server)) return null;
        return new TurnOptions { Server = server, Username = Get("TurnUser") ?? "", Credential = Get("TurnCredential") ?? "", Enabled = true };
    }
}
