using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SidebarMonitor.UI;

/// <summary>Section states: 0 hidden, 1 folded, 2 expanded.</summary>
public sealed class UiConfig
{
    public int RefreshMs { get; set; } = 1000;
    public bool Topmost { get; set; } = true;

    /// <summary>CPU graph: false = single total line, true = one faint line per core overlaid.</summary>
    public bool CpuPerCoreGraph { get; set; }

    /// <summary>Docked reserves desktop space via an AppBar. Floating can be dragged anywhere.</summary>
    public bool Docked { get; set; } = true;

    public bool EdgeLeft { get; set; }
    public int Monitor { get; set; } = 1;
    public int Width { get; set; } = 280;
    public bool Minimized { get; set; }

    public double FloatX { get; set; } = 100;
    public double FloatY { get; set; } = 100;
    public double FloatHeight { get; set; } = 800;

    public Dictionary<string, int> Sections { get; set; } = [];

    /// <summary>
    /// Set when command-line flags overrode the saved settings. Debug runs must not rewrite the
    /// user's configuration behind their back, so Save becomes a no-op.
    /// </summary>
    [JsonIgnore]
    public bool Ephemeral { get; set; }

    private static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SidebarMonitor", "ui.json");

    public static UiConfig Load()
    {
        try
        {
            // An older build wrote a bare {"cpu":2,...} map here; deserializing it into UiConfig
            // yields defaults, which is the right outcome.
            if (File.Exists(Path))
                return JsonSerializer.Deserialize(File.ReadAllText(Path), UiConfigContext.Default.UiConfig) ?? new UiConfig();
        }
        catch { /* corrupt or first run */ }
        return new UiConfig();
    }

    public void Save()
    {
        if (Ephemeral) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, UiConfigContext.Default.UiConfig));
        }
        catch { /* non-fatal */ }
    }
}

/// <summary>Source-generated so the UI never needs reflection-based serialization.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UiConfig))]
internal partial class UiConfigContext : JsonSerializerContext;
