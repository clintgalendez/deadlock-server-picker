using System.Net;
using DeadlockServerPicker.Core.Sdr;

namespace DeadlockServerPicker.Tests;

internal static class Fixtures
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "fixtures");
    public static string Read(string name) => File.ReadAllText(Path.Combine(Dir, name));
    public static SdrConfig Deadlock { get; } = SdrConfig.Parse(Read("sdr-1422450.json"));
    public static SdrConfig Cs2 { get; } = SdrConfig.Parse(Read("sdr-730.json"));

    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "dsp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }
}

/// <summary>HttpMessageHandler returning a canned response (or throwing) for every request.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(respond(request));
    }

    public static StubHandler Ok(string body) => new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    public static StubHandler Fail() => new(_ => throw new HttpRequestException("network down"));
}
