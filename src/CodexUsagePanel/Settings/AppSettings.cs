using System.IO;
using System.Text.Json;

namespace CodexUsagePanel.Settings;

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public int DoubleCtrlWindowMs { get; set; } = 400;

    public int AutoHideSeconds { get; set; } = 10;

    public int RefreshThrottleSeconds { get; set; } = 15;

    public string PlanLabel { get; set; } = "PLUS";

    public bool FixedPositionEnabled { get; set; }

    public int FixedPositionX { get; set; }

    public int FixedPositionY { get; set; }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CodexUsagePanel",
        "settings.json");

    public static AppSettings Load(string path)
    {
        AppSettings settings;

        try
        {
            if (!File.Exists(path)) return new AppSettings();
            var json = File.ReadAllText(path);
            settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (JsonException)
        {
            TryBackup(path);
            return new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }

        settings.Normalize();
        return settings;
    }

    /// <summary>Writes the current values to <paramref name="path"/>.</summary>
    /// <remarks>A failed write is deliberately ignored rather than crashing the tray app.</remarks>
    public void Save(string path)
    {
        Normalize();

        var json = JsonSerializer.Serialize(this, JsonOptions);
        var tmpPath = path + ".tmp";

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            try
            {
                if (File.Exists(tmpPath)) File.Delete(tmpPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private void Normalize()
    {
        DoubleCtrlWindowMs = Math.Clamp(DoubleCtrlWindowMs, 150, 1000);
        AutoHideSeconds = Math.Clamp(AutoHideSeconds, 1, 120);
        RefreshThrottleSeconds = Math.Clamp(RefreshThrottleSeconds, 5, 300);
        PlanLabel = string.IsNullOrWhiteSpace(PlanLabel) ? "PLUS" : PlanLabel.Trim();
    }

    private static void TryBackup(string path)
    {
        try
        {
            File.Copy(path, path + ".bak", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
