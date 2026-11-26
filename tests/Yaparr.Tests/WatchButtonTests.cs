using Yaparr.Desktop;

namespace Yaparr.Tests;

public class WatchButtonTests
{
    private static ConversationVm Room(bool inVoice, params VoiceMemberVm[] members)
    {
        var conv = new ConversationVm("Room", isRoom: true, _ => Task.FromResult(""));
        foreach (var m in members) conv.VoiceMembers.Add(m);
        conv.InVoice = inVoice;
        return conv;
    }

    private static string Device(char c) => new string(c, 64);

    [Fact]
    public void The_button_to_watch_a_share_is_on_the_row_of_the_person_who_shares_it()
    {
        var sam = new VoiceMemberVm(Device('a'), "Sam", isSelf: false);
        var kim = new VoiceMemberVm(Device('b'), "Kim", isSelf: false);
        var conv = Room(inVoice: true, sam, kim);
        conv.Shares.Add(new ShareVm("share1", "Sam's screen", isMine: false, sharer: Device('a')));

        MainWindow.LinkShares(conv);

        Assert.True(sam.CanWatchShare);
        Assert.Equal("Watch", sam.WatchText);
        Assert.Equal("Sam's screen", sam.WatchTip);
        Assert.False(kim.CanWatchShare);
        Assert.Empty(conv.UnlinkedShares);
    }

    [Fact]
    public void The_button_says_so_while_the_share_is_being_watched()
    {
        var sam = new VoiceMemberVm(Device('a'), "Sam", isSelf: false);
        var conv = Room(true, sam);
        var share = new ShareVm("share1", "Sam's screen", isMine: false, sharer: Device('a'));
        conv.Shares.Add(share);
        MainWindow.LinkShares(conv);
        var raised = new List<string?>();
        sam.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        share.Watching = true;

        Assert.Equal("Stop watching", sam.WatchText);
        Assert.Contains(nameof(VoiceMemberVm.WatchText), raised);
    }

    [Fact]
    public void Your_own_share_has_no_button_and_nothing_is_offered_outside_the_call()
    {
        var me = new VoiceMemberVm(Device('a'), "Me", isSelf: true);
        var sam = new VoiceMemberVm(Device('b'), "Sam", isSelf: false);
        var conv = Room(true, me, sam);
        conv.Shares.Add(new ShareVm("mine", "My screen", isMine: true, sharer: Device('a')));
        conv.Shares.Add(new ShareVm("theirs", "Sam's screen", isMine: false, sharer: Device('b')));
        MainWindow.LinkShares(conv);
        Assert.False(me.CanWatchShare);
        Assert.True(sam.CanWatchShare);

        // Out of the call, shares are not to be watched, so no row has a button.
        conv.InVoice = false;
        MainWindow.LinkShares(conv);
        Assert.False(sam.CanWatchShare);
    }

    [Fact]
    public void A_share_whose_sharer_has_no_row_stays_on_offer_beside_the_list()
    {
        var sam = new VoiceMemberVm(Device('a'), "Sam", isSelf: false);
        var conv = Room(true, sam);
        conv.Shares.Add(new ShareVm("gone", "Kim's screen", isMine: false, sharer: Device('z')));   // Kim left the call, the share goes on
        conv.Shares.Add(new ShareVm("here", "Sam's screen", isMine: false, sharer: Device('a')));

        MainWindow.LinkShares(conv);

        Assert.Equal(new[] { "gone" }, conv.UnlinkedShares.Select(s => s.Id));
        Assert.True(sam.CanWatchShare);

        // And when the share ends, the button goes.
        conv.Shares.Clear();
        MainWindow.LinkShares(conv);
        Assert.False(sam.CanWatchShare);
        Assert.Empty(conv.UnlinkedShares);
    }
}
