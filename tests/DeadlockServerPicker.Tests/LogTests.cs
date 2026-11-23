using DeadlockServerPicker.Core;

namespace DeadlockServerPicker.Tests;

[CollectionDefinition(nameof(LogTests), DisableParallelization = true)]
public sealed class LogTestsCollection;

[Collection(nameof(LogTests))] // Log is process-global: run alone, not alongside tests that log
public class LogTests
{
    [Fact]
    public void WritesHeaderMessagesAndStackTraces()
    {
        var path = Path.Combine(Fixtures.TempDir(), "sub", "dsp.log");
        Log.Init(path, "test");
        Log.Info("hello");
        Log.Error("boom", new InvalidOperationException("inner detail"));

        var text = File.ReadAllText(path);
        Assert.Contains($"test {AppInfo.Version}", text);
        Assert.Contains("INFO  [", text);
        Assert.Contains("hello", text);
        Assert.Contains("ERROR", text);
        Assert.Contains("System.InvalidOperationException: inner detail", text);
    }

    [Fact]
    public void RotatesOnceAtTheSizeCap()
    {
        var path = Path.Combine(Fixtures.TempDir(), "dsp.log");
        File.WriteAllText(path, new string('x', (int)Log.MaxBytes + 1));
        Log.Init(path, "test");
        Log.Info("after rotation");

        Assert.True(new FileInfo(path + ".1").Length > Log.MaxBytes);
        Assert.Contains("after rotation", File.ReadAllText(path));
        Assert.True(new FileInfo(path).Length < Log.MaxBytes);
    }
}
