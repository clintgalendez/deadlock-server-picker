using DeadlockServerPicker.Core.Sdr;

namespace DeadlockServerPicker.Tests;

public class SdrConfigSourceTests
{
    private static readonly string Body = Fixtures.Read("sdr-1422450.json");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FetchesTheRequestedAppAndCachesIt()
    {
        var dir = Fixtures.TempDir();
        var handler = StubHandler.Ok(Body);
        var src = new SdrConfigSource(new HttpClient(handler), dir);

        var loaded = await src.LoadAsync(730, ct: Ct);

        Assert.False(loaded.Stale);
        Assert.Equal("https://api.steampowered.com/ISteamApps/GetSDRConfig/v1/?appid=730", handler.Requests.Single().ToString());
        Assert.Equal(Body, File.ReadAllText(src.CachePath(730)));
    }

    [Fact]
    public async Task FallsBackToCacheWithStaleWarning()
    {
        var dir = Fixtures.TempDir();
        await new SdrConfigSource(new HttpClient(StubHandler.Ok(Body)), dir).LoadAsync(1422450, ct: Ct);

        var loaded = await new SdrConfigSource(new HttpClient(StubHandler.Fail()), dir).LoadAsync(1422450, ct: Ct);

        Assert.True(loaded.Stale);
        Assert.Contains("network down", loaded.StaleReason);
        Assert.Equal(1794268433, loaded.Config.Revision);
    }

    [Fact]
    public async Task OfflineModeNeverTouchesTheNetwork()
    {
        var dir = Fixtures.TempDir();
        await new SdrConfigSource(new HttpClient(StubHandler.Ok(Body)), dir).LoadAsync(1422450, ct: Ct);
        var handler = StubHandler.Ok(Body);

        var loaded = await new SdrConfigSource(new HttpClient(handler), dir).LoadAsync(1422450, offline: true, Ct);

        Assert.Empty(handler.Requests);
        Assert.Equal("offline mode", loaded.StaleReason);
    }

    [Fact]
    public async Task InvalidDownloadDoesNotReplaceTheLastGoodCopy()
    {
        var dir = Fixtures.TempDir();
        var src = new SdrConfigSource(new HttpClient(StubHandler.Ok(Body)), dir);
        await src.LoadAsync(1422450, ct: Ct);

        var loaded = await new SdrConfigSource(new HttpClient(StubHandler.Ok("""{"success": false}""")), dir).LoadAsync(1422450, ct: Ct);

        Assert.True(loaded.Stale);
        Assert.Equal(Body, File.ReadAllText(src.CachePath(1422450)));
    }

    [Fact]
    public async Task NoNetworkAndNoCacheIsAClearError()
    {
        var src = new SdrConfigSource(new HttpClient(StubHandler.Fail()), Fixtures.TempDir());
        var e = await Assert.ThrowsAsync<ConfigUnavailableException>(() => src.LoadAsync(1422450, ct: Ct));
        Assert.Contains("internet connection", e.Message);
    }
    [Fact]
    public async Task UnwritableCacheDirDoesNotBreakOnlineUse()
    {
        var blocker = Path.Combine(Fixtures.TempDir(), "not-a-dir");
        File.WriteAllText(blocker, "x"); // a file where the cache directory should be
        var loaded = await new SdrConfigSource(new HttpClient(StubHandler.Ok(Body)), blocker).LoadAsync(1422450, ct: Ct);
        Assert.False(loaded.Stale);
        Assert.Equal(37, loaded.Config.Pops.Count);
    }
}
