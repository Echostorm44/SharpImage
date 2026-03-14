using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpImage.Editor.Services;

[JsonSerializable(typeof(EditorSettings))]
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class EditorSettingsContext : JsonSerializerContext;

/// <summary>
/// Persists user preferences and workspace state to a JSON file
/// in the user's application data directory.
/// </summary>
public sealed class SettingsService
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SharpImageEditor");

    private static readonly string SettingsPath =
        Path.Combine(SettingsDir, "settings.json");

    public EditorSettings Settings{ get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize(json, EditorSettingsContext.Default.EditorSettings);
                if (loaded is not null)
                    Settings = loaded;
            }
        }
        catch
        {
            Settings = new EditorSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(Settings, EditorSettingsContext.Default.EditorSettings);
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Settings save error: {ex.Message}");
        }
    }

    /// <summary>Apply workspace state from settings to WorkspaceService.</summary>
    public void ApplyTo(WorkspaceService workspace)
    {
        workspace.ShowNavigator = Settings.ShowNavigator;
        workspace.ShowColor = Settings.ShowColor;
        workspace.ShowLayers = Settings.ShowLayers;
        workspace.ShowHistory = Settings.ShowHistory;
        workspace.ShowProperties = Settings.ShowProperties;
        workspace.ShowChannels = Settings.ShowChannels;
        workspace.ShowBrushes = Settings.ShowBrushes;
        workspace.ShowAdjustments = Settings.ShowAdjustments;
    }

    /// <summary>Capture workspace state from WorkspaceService into settings.</summary>
    public void CaptureFrom(WorkspaceService workspace)
    {
        Settings.ShowNavigator = workspace.ShowNavigator;
        Settings.ShowColor = workspace.ShowColor;
        Settings.ShowLayers = workspace.ShowLayers;
        Settings.ShowHistory = workspace.ShowHistory;
        Settings.ShowProperties = workspace.ShowProperties;
        Settings.ShowChannels = workspace.ShowChannels;
        Settings.ShowBrushes = workspace.ShowBrushes;
        Settings.ShowAdjustments = workspace.ShowAdjustments;
    }
}

/// <summary>
/// Serializable settings data for the editor.
/// </summary>
public sealed class EditorSettings
{
    // General
    public int UndoLevels { get; set; } = 50;
    public int RecentFilesCount { get; set; } = 10;

    // Display
    public string CheckerboardSize { get; set; } = "Medium";
    public string DefaultZoom { get; set; } = "Fit in Window";

    // Performance
    public int MaxMemoryMb { get; set; } = 2048;

    // Theme
    public string Theme { get; set; } = "System";

    // Panel visibility
    public bool ShowNavigator { get; set; } = true;
    public bool ShowColor { get; set; } = true;
    public bool ShowLayers { get; set; } = true;
    public bool ShowHistory { get; set; } = true;
    public bool ShowProperties { get; set; }
    public bool ShowChannels { get; set; }
    public bool ShowBrushes { get; set; }
    public bool ShowAdjustments { get; set; }

    // Window state
    public double WindowX { get; set; } = double.NaN;
    public double WindowY { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1400;
    public double WindowHeight { get; set; } = 900;
    public bool IsMaximized { get; set; }

    // Recent files
    public List<string> RecentFiles { get; set; } = [];

    // Last open file
    public string? LastOpenFile { get; set; }
}
