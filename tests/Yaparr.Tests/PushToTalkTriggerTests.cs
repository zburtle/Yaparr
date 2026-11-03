using Avalonia.Input;
using Yaparr.Desktop;

namespace Yaparr.Tests;

public class PushToTalkTriggerTests
{
    [Fact]
    public void A_key_or_one_of_three_mouse_buttons_can_be_chosen_and_comes_back_from_the_settings()
    {
        Assert.Equal(new PushToTalkTrigger(Key.F13, 0), PushToTalkTrigger.Parse("F13"));
        Assert.Equal(new PushToTalkTrigger(Key.None, 4), PushToTalkTrigger.Parse("Mouse4"));
        Assert.Equal("Mouse5", PushToTalkTrigger.Parse("Mouse5")!.Value.Setting);
        Assert.Equal("F13", PushToTalkTrigger.Parse("F13")!.Value.Setting);
        Assert.Equal("mouse button 4 (back)", PushToTalkTrigger.Parse("Mouse4")!.Value.Description);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("None")]
    [InlineData("Mouse1")]       // the left and right buttons are not offered
    [InlineData("Mouse2")]
    [InlineData("Mouse9")]
    [InlineData("NotAKey")]
    public void Nothing_else_is_a_trigger(string? text) => Assert.Null(PushToTalkTrigger.Parse(text));
}
