namespace CodexUsagePanel.Ui;

/// <summary>
/// 纯计算：把光标物理坐标换算成窗口应放置的 DIP 坐标，并夹在显示器工作区内。
/// 不读注册表、不碰窗口句柄，方便单测覆盖多屏与高 DPI。
/// </summary>
public static class PanelPositioner
{
    public static (double Left, double Top) Resolve(
        int cursorX,
        int cursorY,
        int workLeft,
        int workTop,
        int workRight,
        int workBottom,
        double dpiScale,
        double logicalWidth,
        double logicalHeight,
        int offset = 16)
    {
        // 工作区与光标都是物理像素，窗口尺寸是 DIP：先统一换算到物理像素再夹
        double width = logicalWidth * dpiScale;
        double height = logicalHeight * dpiScale;

        double left = cursorX + offset;
        double top = cursorY + offset;

        if (left + width > workRight) left = workRight - width;
        if (top + height > workBottom) top = workBottom - height;
        if (left < workLeft) left = workLeft;
        if (top < workTop) top = workTop;

        // WPF 的 Left/Top 是 DIP，按同一 DPI 换回去物理位置才落在上面算出的点
        return (left / dpiScale, top / dpiScale);
    }

    /// <summary>把 WPF 的 DIP 矩形换算成鼠标钩子比较用的物理像素矩形。</summary>
    public static (int Left, int Top, int Right, int Bottom) ToPhysicalBounds(
        double left,
        double top,
        double width,
        double height,
        double dpiScale) => (
            (int)Math.Floor(left * dpiScale),
            (int)Math.Floor(top * dpiScale),
            (int)Math.Ceiling((left + width) * dpiScale),
            (int)Math.Ceiling((top + height) * dpiScale));
}
