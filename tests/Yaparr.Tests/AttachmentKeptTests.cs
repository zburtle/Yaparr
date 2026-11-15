using Yaparr.Chat;
using Yaparr.Discovery;

namespace Yaparr.Tests;

// Pictures are kept for as long as the messages that show them are.
public class AttachmentKeptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-kept-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void A_picture_is_still_there_after_a_restart_however_old_it_is()
    {
        var png = new byte[40];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0, 20, 0, 0, 0, 10 }.CopyTo(png, 0);
        string hash;
        using (var first = TestUtil.NewNode(new InMemoryDiscoveryHub(), "me", attachmentDirectory: _dir))
        {
            hash = first.PrepareAttachment(png, "old.png", null).Hash;
        }
        var file = Path.Combine(_dir, hash);
        Assert.True(File.Exists(file));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-400));

        using (var second = TestUtil.NewNode(new InMemoryDiscoveryHub(), "me", attachmentDirectory: _dir))
        {
            second.Start();
            // The start-up work that used to clear out old pictures runs in the background: give it time to have done so.
            Thread.Sleep(1500);
        }
        Assert.True(File.Exists(file));
    }
}
