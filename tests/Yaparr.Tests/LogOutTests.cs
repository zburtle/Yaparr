using Yaparr.AppCore;
using Yaparr.Identity;

namespace Yaparr.Tests;

public class LogOutTests
{
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yaparr-logout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Logging_out_removes_the_profile_at_the_next_start_and_leaves_the_programs()
    {
        var dir = NewDir();
        try
        {
            var data = new AppData(dir);
            using (var identity = PeerIdentity.Generate()) data.SaveIdentity(identity);
            data.SaveSettings(new AppSettings { DisplayName = "Zed" });
            Directory.CreateDirectory(Path.Combine(dir, "history"));
            File.WriteAllText(Path.Combine(dir, "history", "room.log"), "hello");
            Directory.CreateDirectory(Path.Combine(dir, "avatars"));
            File.WriteAllText(Path.Combine(dir, "account.json"), "{}");
            File.WriteAllText(Path.Combine(dir, "own-devices.json"), "[]");
            Directory.CreateDirectory(Path.Combine(dir, "ffmpeg"));
            File.WriteAllText(Path.Combine(dir, "ffmpeg", "ffmpeg.exe"), "program");
            Directory.CreateDirectory(Path.Combine(dir, "logs"));

            Assert.False(data.CompleteLogOut());                 // nothing was asked for: nothing is touched
            Assert.True(data.HasIdentity);

            data.RequestLogOut();
            Assert.True(data.LogOutPending);
            Assert.True(data.HasIdentity);                       // asking removes nothing yet: the running app still has its files open

            var restarted = new AppData(dir);
            Assert.True(restarted.CompleteLogOut());
            Assert.False(restarted.HasIdentity);
            Assert.False(restarted.LogOutPending);
            Assert.False(Directory.Exists(Path.Combine(dir, "history")));
            Assert.False(Directory.Exists(Path.Combine(dir, "avatars")));
            Assert.False(File.Exists(Path.Combine(dir, "account.json")));
            Assert.False(File.Exists(Path.Combine(dir, "own-devices.json")));
            Assert.Null(restarted.LoadSettings().DisplayName);   // settings are the defaults again
            Assert.True(File.Exists(Path.Combine(dir, "ffmpeg", "ffmpeg.exe")));
            Assert.True(Directory.Exists(Path.Combine(dir, "logs")));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
