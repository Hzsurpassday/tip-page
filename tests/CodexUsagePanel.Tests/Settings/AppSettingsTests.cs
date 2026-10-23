using System.IO;
using CodexUsagePanel.Settings;
using Xunit;

namespace CodexUsagePanel.Tests.Settings;

public class AppSettingsTests
{
    [Fact]
    public void MissingFile_YieldsDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        var settings = AppSettings.Load(path);

        Assert.Equal(400, settings.DoubleCtrlWindowMs);
        Assert.Equal(10, settings.AutoHideSeconds);
        Assert.Equal(15, settings.RefreshThrottleSeconds);
        Assert.Equal("PLUS", settings.PlanLabel);
        Assert.False(settings.FixedPositionEnabled);
    }

    [Fact]
    public void RoundTripsThroughDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "settings.json");

        var settings = AppSettings.Load(path);
        settings.AutoHideSeconds = 3;
        settings.PlanLabel = "PRO";
        settings.FixedPositionEnabled = true;
        settings.FixedPositionX = 100;
        settings.FixedPositionY = 200;
        settings.Save(path);

        var reloaded = AppSettings.Load(path);
        Assert.Equal(3, reloaded.AutoHideSeconds);
        Assert.Equal("PRO", reloaded.PlanLabel);
        Assert.True(reloaded.FixedPositionEnabled);
        Assert.Equal(100, reloaded.FixedPositionX);
        Assert.Equal(200, reloaded.FixedPositionY);
    }

    [Fact]
    public void CorruptFile_IsBackedUpAndDefaultsRestored()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{ this is not json");

        var settings = AppSettings.Load(path);

        Assert.Equal(400, settings.DoubleCtrlWindowMs);
        Assert.True(File.Exists(path + ".bak"));
    }

    [Fact]
    public void OutOfRangeValues_AreClamped()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, """{"DoubleCtrlWindowMs":5,"AutoHideSeconds":9999}""");

        var settings = AppSettings.Load(path);

        Assert.Equal(150, settings.DoubleCtrlWindowMs);
        Assert.Equal(120, settings.AutoHideSeconds);
    }
}
