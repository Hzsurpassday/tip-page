using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexUsagePanel.Settings;

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
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
        catch (IOException)
        {
            return new AppSettings();
        }

        settings.Normalize();
        return settings;
    }

    public void Save(string path)
    {
        Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
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
        catch (IOException)
        {
        }
    }
}
