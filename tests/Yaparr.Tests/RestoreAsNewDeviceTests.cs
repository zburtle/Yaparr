using Yaparr.AppCore;
using Yaparr.Identity;

namespace Yaparr.Tests;

public class RestoreAsNewDeviceTests
{
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yaparr-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void A_restored_backup_is_another_device_of_the_account_and_not_a_copy_of_the_original()
    {
        var source = NewDir();
        var zip = Path.Combine(Path.GetTempPath(), "yaparr-backup-" + Guid.NewGuid().ToString("N") + ".zip");
        var targetA = NewDir();
        var targetB = NewDir();
        try
        {
            var original = new AppData(source);
            using var first = PeerIdentity.Generate();
            original.SaveIdentity(first);
            original.SaveSettings(new AppSettings { DisplayName = "Zed" });
            original.ExportProfile(zip);

            // The same backup restored on two computers (the original still in use): two new devices of one account.
            var staged = new[] { new AppData(targetA).StageProfile(zip), new AppData(targetB).StageProfile(zip) };
            var devices = new List<PeerIdentity>();
            try
            {
                foreach (var staging in staged)
                    devices.Add(PeerIdentity.Import(File.ReadAllBytes(Path.Combine(staging, "identity.key"))));

                foreach (var device in devices)
                {
                    Assert.Equal(first.AccountId, device.AccountId);               // the same person
                    Assert.NotEqual(first.Id, device.Id);                          // not the same device as the original
                    Assert.NotNull(device.DeviceCertificate);
                    Assert.True(PeerIdentity.VerifyDevice(first.AccountPublicKey, device.PublicKey, device.DeviceCertificate!));
                    Assert.True(device.CanLinkDevices);                            // like any linked device, it holds the account key
                }
                Assert.NotEqual(devices[0].Id, devices[1].Id);                      // and not the same as each other
            }
            finally { foreach (var d in devices) d.Dispose(); }
        }
        finally
        {
            foreach (var path in new[] { source, targetA, targetB }) { try { Directory.Delete(path, true); } catch (IOException) { } }
            try { File.Delete(zip); } catch (IOException) { }
        }
    }
}
