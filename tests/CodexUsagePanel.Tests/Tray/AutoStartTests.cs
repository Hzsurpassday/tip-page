using CodexUsagePanel.Tray;
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
}
