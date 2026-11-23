using System.Text.Json;

namespace DeadlockServerPicker.Core.Sdr;

public sealed record LoadedConfig(SdrConfig Config, int AppId, DateTimeOffset FetchedAt, string? StaleReason)
{
    /// <summary>True when the config came from the on-disk cache instead of a fresh download.</summary>
    public bool Stale => StaleReason is not null;
}

public sealed class ConfigUnavailableException(string message) : Exception(message);

/// <summary>Downloads GetSDRConfig and keeps the last good copy on disk so the app works offline.</summary>
public sealed class SdrConfigSource(HttpClient http, string cacheDir)
{
    public static string Url(int appId) => $"https://api.steampowered.com/ISteamApps/GetSDRConfig/v1/?appid={appId}";

    public string CachePath(int appId) => Path.Combine(cacheDir, $"sdr-{appId}.json");

    public async Task<LoadedConfig> LoadAsync(int appId, bool offline = false, CancellationToken ct = default)
    {
        string? error = null;
        if (!offline)
        {
            try
            {
                var body = await http.GetStringAsync(Url(appId), ct);
                var fresh = SdrConfig.Parse(body); // validate before it can replace the last good copy
                WriteCache(appId, body);
                Log.Info($"SDR config app={appId} revision={fresh.Revision} pops={fresh.Pops.Count} downloaded");
                return new LoadedConfig(fresh, appId, DateTimeOffset.UtcNow, null);
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or TaskCanceledException or FormatException or JsonException)
            {
                error = e.Message;
                Log.Warn($"SDR config app={appId} download failed, using cache if present: {e}");
            }
        }

        var path = CachePath(appId);
        if (!File.Exists(path))
            throw new ConfigUnavailableException(offline
                ? "No cached SDR config yet. Run once while online."
                : $"Could not download the SDR config ({error}) and there is no cached copy. Check your internet connection.");
        var cached = SdrConfig.Parse(await File.ReadAllTextAsync(path, ct));
        var fetchedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        return new LoadedConfig(cached, appId, fetchedAt, offline ? "offline mode" : $"download failed: {error}");
    }

    private void WriteCache(int appId, string body)
    {
        Directory.CreateDirectory(cacheDir);
        var path = CachePath(appId);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, body);
        File.Move(tmp, path, overwrite: true);
    }
}
