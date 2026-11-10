using System.Text.Json;
using Yaparr.AppCore;
using Yaparr.Voice;

namespace Yaparr.Tests;

public class VoiceBitrateTests
{
    [Fact]
    public void The_voice_bitrate_is_one_of_32_48_or_64_and_defaults_to_64()
    {
        Assert.Equal(new[] { 32, 48, 64 }, VoiceSession.BitrateChoices);
        Assert.Equal(64, VoiceSession.DefaultBitrateKbps);

        foreach (var kbps in VoiceSession.BitrateChoices) Assert.Equal(kbps, VoiceSession.NormalizeBitrate(kbps));
        // Anything else (a hand-edited settings file, a value from a future version) is the default.
        Assert.Equal(64, VoiceSession.NormalizeBitrate(0));
        Assert.Equal(64, VoiceSession.NormalizeBitrate(40));
        Assert.Equal(64, VoiceSession.NormalizeBitrate(500));
    }

    [Fact]
    public void Settings_from_before_the_choice_existed_get_64_and_a_chosen_value_is_kept()
    {
        Assert.Equal(64, new AppSettings().VoiceBitrateKbps);
        Assert.Equal(64, JsonSerializer.Deserialize<AppSettings>("{\"DisplayName\":\"Zed\"}")!.VoiceBitrateKbps);
        Assert.Equal(32, JsonSerializer.Deserialize<AppSettings>("{\"VoiceBitrateKbps\":32}")!.VoiceBitrateKbps);
    }
}
