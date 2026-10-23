namespace CodexUsagePanel.Input;

public static class ClickAwayDetector
{
    public static bool IsOutside(int x, int y, int left, int top, int right, int bottom) =>
        x < left || x > right || y < top || y > bottom;
}
