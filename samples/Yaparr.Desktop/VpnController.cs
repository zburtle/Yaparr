using System.IO;
using Yaparr.Chat;
using Yaparr.Identity;
using Yaparr.Vpn;

namespace Yaparr.Desktop;

public sealed class VpnController
{
    private readonly AppData _data;
    private readonly AppSettings _settings;
    private readonly Func<ChatNode?> _node;

    public VpnController(AppData data, AppSettings settings, Func<ChatNode?> node)
    {
        _data = data;
        _settings = settings;
        _node = node;
    }

    public ChatNode? Node => _node();

    public bool HasConfig => File.Exists(_data.VpnConfigPath);

    public WireGuardConfig? LoadConfig()
    {
        try { return HasConfig ? WireGuardConfig.Parse(SecretStore.ReadAllText(_data.VpnConfigPath)) : null; }
        catch (Exception ex) when (ex is FormatException or IOException or System.Security.Cryptography.CryptographicException) { return null; }
    }

    public WireGuardConfig? LoadConfigIfEnabled() => _settings.VpnEnabled ? LoadConfig() : null;

    public void Import(string path)
    {
        var text = File.ReadAllText(path);
        WireGuardConfig.Parse(text);
        SecretStore.WriteAllText(_data.VpnConfigPath, text);
    }

    public async Task<bool> ConnectAsync()
    {
        var config = LoadConfig() ?? throw new InvalidOperationException("Import a WireGuard configuration first.");
        var node = Node ?? throw new InvalidOperationException("Yaparr is not running.");
        _settings.VpnEnabled = true;
        _data.SaveSettings(_settings);
        return await node.ConnectVpnAsync(config);
    }

    public async Task DisconnectAsync()
    {
        _settings.VpnEnabled = false;
        _data.SaveSettings(_settings);
        if (Node is { } node) await node.DisconnectVpnAsync();
    }

    public async Task RemoveAsync()
    {
        await DisconnectAsync();
        try { File.Delete(_data.VpnConfigPath); } catch (IOException) { }
    }
}
