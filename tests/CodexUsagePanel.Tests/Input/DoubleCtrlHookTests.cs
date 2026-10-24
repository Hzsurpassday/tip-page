using CodexUsagePanel.Input;
using Xunit;

namespace CodexUsagePanel.Tests.Input;

public class DoubleCtrlHookTests
{
    [Fact]
    public void ReArmReinstallsTheHookAfterItWasStopped()
    {
        using var hook = new DoubleCtrlHook(400, isPanelVisible: () => false, onToggle: () => { }, onEscape: () => { });

        Assert.True(hook.Start());
        hook.Stop();

        // 暂停后恢复走 ReArm：即使 Start 的短路面被旧句柄挡住，也必须能重新装上
        Assert.True(hook.ReArm());
        hook.Stop();
    }
}
