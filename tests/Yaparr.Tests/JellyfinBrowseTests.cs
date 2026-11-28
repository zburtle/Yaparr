using Yaparr.AppCore;

namespace Yaparr.Tests;

public class JellyfinBrowseTests
{
    private sealed class FakeLibrary : IJellyfinLibrary
    {
        public static JellyfinItem Movie(string id, string name, string overview = "") =>
            new(id, name, JellyfinKind.Movie, 1999, 60_000_000_000, overview, "tag-" + id, "", null, null, null, 0);

        public static JellyfinItem Episode(string id, string name) =>
            new(id, name, JellyfinKind.Episode, null, 20_000_000_000, "", null, "A show", "series-1", 1, 2, 42.5);

        public bool Fail;
        public (string? Library, string? Search, int Start, int Limit)? LastItems;
        public int? LastPosterWidth;

        public void Dispose() { }

        private void Check() { if (Fail) throw new JellyfinException("The server answered 500.", System.Net.HttpStatusCode.InternalServerError); }

        public Task<IReadOnlyList<JellyfinLibrary>> GetLibrariesAsync(CancellationToken ct = default)
        {
            Check();
            return Task.FromResult<IReadOnlyList<JellyfinLibrary>>(new[] { new JellyfinLibrary("lib-1", "Films", "movies"), new JellyfinLibrary("lib-2", "Shows", "tvshows") });
        }

        public Task<JellyfinPage> GetItemsAsync(string? libraryId = null, string? search = null, int start = 0, int limit = 60, CancellationToken ct = default)
        {
            Check();
            LastItems = (libraryId, search, start, limit);
            return Task.FromResult(new JellyfinPage(new[] { Movie("m1", "Alien", new string('x', 1000)), Movie("m2", "Aliens") }, 130));
        }

        public Task<JellyfinPage> GetResumeAsync(int limit = 30, CancellationToken ct = default) => Task.FromResult(new JellyfinPage(new[] { Episode("e9", "Halfway") }, 1));

        public Task<JellyfinPage> GetSeasonsAsync(string seriesId, CancellationToken ct = default) =>
            Task.FromResult(new JellyfinPage(new[] { new JellyfinItem("season-1", "Season 1", JellyfinKind.Season, null, 0, "", null, "A show", seriesId, 1, 1, 0) }, 1));

        public Task<JellyfinPage> GetEpisodesAsync(string seriesId, string seasonId, CancellationToken ct = default) =>
            Task.FromResult(new JellyfinPage(new[] { Episode("e1", "Pilot"), Episode("e2", "Second") }, 2));

        public Task<byte[]?> GetPosterAsync(string itemId, string? tag, int maxWidth, CancellationToken ct = default)
        {
            LastPosterWidth = maxWidth;
            return Task.FromResult<byte[]?>(itemId == "big" ? new byte[JellyfinBrowseServer.MaxPoster + 1] : new byte[] { 1, 2, 3, 4 });
        }
    }

    private static RemoteJellyfinLibrary Remote(FakeLibrary fake) => new((question, ct) => JellyfinBrowseServer.AnswerAsync(fake, question, ct));

    [Fact]
    public async Task The_libraries_and_pages_come_across_as_they_are_and_long_overviews_are_cut()
    {
        var fake = new FakeLibrary();
        using var remote = Remote(fake);
        var libraries = await remote.GetLibrariesAsync();
        Assert.Equal(new[] { "Films", "Shows" }, libraries.Select(l => l.Name));
        Assert.Equal("tvshows", libraries[1].CollectionType);

        var page = await remote.GetItemsAsync("lib-1", "alien", 60, 60);
        Assert.Equal(130, page.Total);
        Assert.Equal(new[] { "Alien", "Aliens" }, page.Items.Select(i => i.Name));
        Assert.Equal(("lib-1", "alien", 60, 60), fake.LastItems);
        Assert.Equal(JellyfinBrowseServer.MaxOverview, page.Items[0].Overview.Length);
        Assert.Equal("tag-m1", page.Items[0].ImageTag);
        Assert.Equal(JellyfinKind.Movie, page.Items[0].Kind);
        Assert.Equal(60_000_000_000, page.Items[0].RuntimeTicks);
    }

    [Fact]
    public async Task A_show_and_what_was_started_come_across_with_their_place_in_the_show()
    {
        using var remote = Remote(new FakeLibrary());
        var resume = await remote.GetResumeAsync();
        Assert.Equal("Halfway", resume.Items.Single().Name);
        Assert.Equal(42.5, resume.Items.Single().PlayedPercent);

        var seasons = await remote.GetSeasonsAsync("series-1");
        Assert.Equal("Season 1", seasons.Items.Single().Name);
        var episodes = await remote.GetEpisodesAsync("series-1", "season-1");
        Assert.Equal(new[] { "Pilot", "Second" }, episodes.Items.Select(i => i.Name));
        Assert.Equal("series-1", episodes.Items[0].SeriesId);
        Assert.Equal(1, episodes.Items[0].SeasonNumber);
        Assert.Equal(2, episodes.Items[0].Number);
        Assert.Equal("S1E2 · A show", episodes.Items[0].Subtitle);
    }

    [Fact]
    public async Task A_poster_comes_across_scaled_down_and_one_that_is_too_big_does_not()
    {
        var fake = new FakeLibrary();
        using var remote = Remote(fake);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await remote.GetPosterAsync("m1", "tag", 260));
        Assert.Equal(260, fake.LastPosterWidth);
        await remote.GetPosterAsync("m1", "tag", 5000);
        Assert.Equal(JellyfinBrowseServer.MaxPosterWidth, fake.LastPosterWidth);   // a viewer cannot ask for a huge picture
        Assert.Null(await remote.GetPosterAsync("big", "tag", 260));
        Assert.Null(await remote.GetPosterAsync("m1", null, 260));
    }

    [Fact]
    public async Task A_page_cannot_be_asked_larger_than_a_list_needs()
    {
        var fake = new FakeLibrary();
        using var remote = Remote(fake);
        await remote.GetItemsAsync(null, null, -5, 100_000);
        Assert.Equal((null, null, 0, JellyfinBrowseServer.MaxPage), fake.LastItems);
    }

    [Fact]
    public async Task What_the_server_refuses_comes_back_as_a_message_and_a_question_that_makes_no_sense_is_not_answered()
    {
        var fake = new FakeLibrary { Fail = true };
        using var remote = Remote(fake);
        var ex = await Assert.ThrowsAsync<JellyfinException>(() => remote.GetLibrariesAsync());
        Assert.Equal("The server answered 500.", ex.Message);

        Assert.Contains("cannot be asked", await JellyfinBrowseServer.AnswerAsync(fake, "not json"));
        Assert.Contains("cannot be asked", await JellyfinBrowseServer.AnswerAsync(fake, "{\"op\":\"delete everything\"}"));
        Assert.Contains("cannot be asked", await JellyfinBrowseServer.AnswerAsync(fake, "{\"op\":\"seasons\"}"));   // no show named
    }

    [Fact]
    public async Task A_streamer_who_does_not_answer_is_said_so_and_a_cancelled_question_ends()
    {
        using var silent = new RemoteJellyfinLibrary(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return ""; });
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => silent.GetLibrariesAsync(cancel.Token));

        using var gone = new RemoteJellyfinLibrary((_, _) => throw new OperationCanceledException());
        var ex = await Assert.ThrowsAsync<JellyfinException>(() => gone.GetLibrariesAsync());
        Assert.Contains("did not answer", ex.Message);

        using var garbled = new RemoteJellyfinLibrary((_, _) => Task.FromResult("<html>"));
        Assert.Contains("could not be read", (await Assert.ThrowsAsync<JellyfinException>(() => garbled.GetLibrariesAsync())).Message);
    }
}
