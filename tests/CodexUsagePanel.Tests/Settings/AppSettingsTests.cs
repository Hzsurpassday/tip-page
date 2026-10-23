using System.IO;
using System.Linq;
using System.Text.Json;
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

    [Fact]
    public void RefreshThrottleSeconds_IsClamped()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var lowPath = Path.Combine(dir, "low.json");
        var highPath = Path.Combine(dir, "high.json");

        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(lowPath, """{"RefreshThrottleSeconds":1}""");
            File.WriteAllText(highPath, """{"RefreshThrottleSeconds":9999}""");

            Assert.Equal(5, AppSettings.Load(lowPath).RefreshThrottleSeconds);
            Assert.Equal(300, AppSettings.Load(highPath).RefreshThrottleSeconds);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DefaultPath_EndsWithExpectedSegments()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CodexUsagePanel",
            "settings.json");

        Assert.EndsWith(expected, AppSettings.DefaultPath(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingFile_DoesNotCreateAnything()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "settings.json");

        try
        {
            var settings = AppSettings.Load(path);

            Assert.Equal(15, settings.RefreshThrottleSeconds);
            Assert.False(File.Exists(path));
            Assert.False(Directory.Exists(dir));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Save_WritesExactlyTheSevenDocumentedFields()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "settings.json");

        try
        {
            new AppSettings().Save(path);

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var names = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();

            Assert.Equal(
                new[]
                {
                    "DoubleCtrlWindowMs",
                    "AutoHideSeconds",
                    "RefreshThrottleSeconds",
                    "PlanLabel",
                    "FixedPositionEnabled",
                    "FixedPositionX",
                    "FixedPositionY"
                },
                names);
            Assert.Equal(7, names.Length);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
