using CodexUsagePanel.Tray;
using Microsoft.Win32;
using Xunit;

namespace CodexUsagePanel.Tests.Tray;

public class AutoStartTests
{
    [Fact]
    public void BuildCommandQuotesPathContainingSpaces()
    {
        var command = AutoStart.BuildCommand(@"E:\Viding\tip page\publish\CodexUsagePanel.exe");
        Assert.Equal("\"E:\\Viding\\tip page\\publish\\CodexUsagePanel.exe\" --tray", command);
    }

    [Fact]
    public void BuildCommandKeepsShortPathIntact()
    {
        var command = AutoStart.BuildCommand(@"C:\apps\cup.exe");
        Assert.Equal("\"C:\\apps\\cup.exe\" --tray", command);
    }

    [Fact]
    public void SetAndIsEnabledRoundTripInAnIsolatedKey()
    {
        // 只用测试专用子键，绝不碰真实的 Run 键（否则会清掉用户已有的自启设置）
        var keyPath = $@"Software\CodexUsagePanel.Tests\{Guid.NewGuid():N}";

        try
        {
            Assert.False(AutoStart.IsEnabled(keyPath, "CodexUsagePanel"));

            Assert.True(AutoStart.Set(true, @"C:\apps\cup.exe", keyPath, "CodexUsagePanel"));
            Assert.True(AutoStart.IsEnabled(keyPath, "CodexUsagePanel"));

            using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
            {
                Assert.Equal("\"C:\\apps\\cup.exe\" --tray", key?.GetValue("CodexUsagePanel"));
            }

            Assert.True(AutoStart.Set(false, @"C:\apps\cup.exe", keyPath, "CodexUsagePanel"));
            Assert.False(AutoStart.IsEnabled(keyPath, "CodexUsagePanel"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }
}
