using DeadlockServerPicker.Core;

namespace DeadlockServerPicker.Tests;

public class AppInfoTests
{
    [Fact]
    public void VersionIsStamped() => Assert.False(string.IsNullOrWhiteSpace(AppInfo.Version));
}
