using Yaparr.AppCore;
using Yaparr.Desktop;

namespace Yaparr.Tests;

/// <summary>The ten number keys of a soundboard, which each person arranges for themselves (issue #48).</summary>
public class SoundSlotsTests
{
    private static readonly string[] Twelve = Enumerable.Range(1, 12).Select(i => "s" + i).ToArray();

    [Theory]
    [InlineData(0, "1")]
    [InlineData(8, "9")]
    [InlineData(9, "0")]
    [InlineData(10, "")]
    [InlineData(-1, "")]
    public void The_ten_keys_are_one_to_nine_and_then_zero(int slot, string label) => Assert.Equal(label, SoundSlots.KeyLabel(slot));

    [Fact]
    public void The_number_keys_on_the_top_row_and_the_number_pad_are_the_keys()
    {
        Assert.Equal(0, SoundboardView.SlotForKey(Avalonia.Input.Key.D1));
        Assert.Equal(8, SoundboardView.SlotForKey(Avalonia.Input.Key.D9));
        Assert.Equal(9, SoundboardView.SlotForKey(Avalonia.Input.Key.D0));
        Assert.Equal(0, SoundboardView.SlotForKey(Avalonia.Input.Key.NumPad1));
        Assert.Equal(9, SoundboardView.SlotForKey(Avalonia.Input.Key.NumPad0));
        Assert.Equal(-1, SoundboardView.SlotForKey(Avalonia.Input.Key.A));
        Assert.Equal(-1, SoundboardView.SlotForKey(Avalonia.Input.Key.Space));
    }

    [Fact]
    public void A_board_nobody_arranged_has_the_first_ten_sounds_in_the_order_they_are_listed()
    {
        var slots = SoundSlots.Resolve(null, Twelve);
        Assert.Equal(Twelve.Take(10), slots);
        // With fewer sounds the rest of the keys are empty.
        Assert.Equal(new[] { "s1", "s2", "", "", "", "", "", "", "", "" }, SoundSlots.Resolve(null, Twelve.Take(2).ToList()));
        Assert.Equal(Enumerable.Repeat("", 10), SoundSlots.Resolve(null, Array.Empty<string>()));
    }

    [Fact]
    public void An_arrangement_is_kept_as_it_was_left_and_a_sound_that_is_gone_leaves_its_key_empty()
    {
        var mine = SoundSlots.Put(SoundSlots.Resolve(null, Twelve), "s12", 0);
        // New sounds arriving, or the order changing, do not move what is on the keys.
        var later = SoundSlots.Resolve(mine, new[] { "a", "s12", "s1", "s2", "s3", "s4", "s5", "s6", "s7", "s8", "s9", "s10", "s11" });
        Assert.Equal(mine, later);
        Assert.Equal("s12", later[0]);
        // A removed sound leaves an empty key; nothing else takes its place.
        var gone = SoundSlots.Resolve(mine, Twelve.Where(s => s != "s3").ToList());
        Assert.Equal("", gone[Array.IndexOf(mine, "s3")]);
        Assert.Equal(mine.Count(s => s.Length > 0) - 1, gone.Count(s => s.Length > 0));
    }

    [Fact]
    public void A_sound_dragged_from_the_list_takes_the_key_and_the_one_that_was_there_goes_off_the_keys()
    {
        var slots = SoundSlots.Resolve(null, Twelve);
        var next = SoundSlots.Put(slots, "s12", 2);
        Assert.Equal("s12", next[2]);
        Assert.DoesNotContain("s3", next);
        Assert.Equal(10, next.Length);
        // The rest did not move.
        Assert.Equal("s1", next[0]);
        Assert.Equal("s10", next[9]);
    }

    [Fact]
    public void A_sound_is_on_one_key_only_so_dragging_a_key_onto_another_swaps_them()
    {
        var slots = SoundSlots.Resolve(null, Twelve);
        var next = SoundSlots.Put(slots, "s1", 4);
        Assert.Equal("s1", next[4]);
        Assert.Equal("s5", next[0]);
        Assert.Single(next, s => s == "s1");
        // Onto its own key changes nothing.
        Assert.Equal(slots, SoundSlots.Put(slots, "s1", 0));
    }

    [Fact]
    public void A_sound_can_go_onto_an_empty_key_and_a_key_can_be_emptied()
    {
        var few = SoundSlots.Resolve(null, new[] { "a", "b" });
        var next = SoundSlots.Put(few, "b", 7);
        Assert.Equal("b", next[7]);
        Assert.Equal("", next[1]);   // it left its old key, which was swapped with an empty one
        var cleared = SoundSlots.Clear(next, 7);
        Assert.DoesNotContain("b", cleared);
        Assert.Equal("a", cleared[0]);
    }

    [Fact]
    public void Bad_input_does_nothing_and_a_sound_listed_twice_in_what_was_stored_counts_once()
    {
        var slots = SoundSlots.Resolve(null, Twelve);
        Assert.Equal(slots, SoundSlots.Put(slots, "s1", 10));
        Assert.Equal(slots, SoundSlots.Put(slots, "s1", -1));
        Assert.Equal(slots, SoundSlots.Put(slots, "", 3));
        Assert.Equal(slots, SoundSlots.Clear(slots, 12));
        var twice = SoundSlots.Resolve(new[] { "s1", "s1", "x", "s2" }, Twelve);   // x is not a sound of the room
        Assert.Equal(new[] { "s1", "", "", "s2", "", "", "", "", "", "" }, twice);
        // More than ten stored entries: only the first ten are keys.
        Assert.Equal(10, SoundSlots.Resolve(Twelve, Twelve).Length);
    }

    [Fact]
    public void The_arrangement_is_in_your_own_settings_by_room_and_survives_a_restart()
    {
        var settings = new AppSettings();
        settings.SoundboardSlots["room-a"] = SoundSlots.Put(SoundSlots.Resolve(null, Twelve), "s12", 0).ToList();
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal("s12", SoundSlots.Resolve(back.SoundboardSlots["room-a"], Twelve)[0]);
        // Another room is not touched.
        Assert.False(back.SoundboardSlots.ContainsKey("room-b"));
        Assert.Equal(Twelve.Take(10), SoundSlots.Resolve(back.SoundboardSlots.GetValueOrDefault("room-b"), Twelve));
    }
}
