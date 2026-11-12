using System.Text.Json;
using System.Text.Json.Serialization;

namespace CAAI;

public sealed class DebugLaunchConfiguration
{
    [JsonIgnore]
    public string ProjectPath { get; set; } = "";
    public string? AdapterPath { get; set; }
    public string? TargetFramework { get; set; }
    public string? WorkingDirectory { get; set; }
    public string[] Arguments { get; set; } = [];
    public Dictionary<string, string> Environment { get; set; } = new();
    public bool StopAtEntry { get; set; }
    public bool JustMyCode { get; set; } = true;
    public string[] ExceptionFilters { get; set; } = [];

    public static string SettingsPath(string projectPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectPath))!, ".caai", "launch.json");

    public static DebugLaunchConfiguration Load(string projectPath)
    {
        projectPath = Path.GetFullPath(projectPath);
        var path = SettingsPath(projectPath);
        var config = File.Exists(path)
            ? JsonSerializer.Deserialize<DebugLaunchConfiguration>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true
            }) ?? throw new InvalidDataException("The debug launch settings are empty.")
            : new DebugLaunchConfiguration();
        config.ProjectPath = projectPath;
        config.Arguments ??= [];
        config.Environment ??= new();
        config.ExceptionFilters ??= [];
        return config;
    }

    public static void CreateSettings(string projectPath)
    {
        var path = SettingsPath(projectPath);
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new DebugLaunchConfiguration(), new JsonSerializerOptions { WriteIndented = true }));
    }

    public string ResolveAdapter()
    {
        var configured = AdapterPath ?? System.Environment.GetEnvironmentVariable("CAAI_DEBUG_ADAPTER");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = Path.GetFullPath(configured, Path.GetDirectoryName(ProjectPath)!);
            return File.Exists(path) ? path : throw new FileNotFoundException("The configured netcoredbg adapter does not exist.", path);
        }
        var name = OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg";
        var installed = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "CAAI", "debuggers", "netcoredbg", name);
        if (File.Exists(installed)) return installed;
        foreach (var directory in (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("netcoredbg is not installed. Run Debugging/Install-NetCoreDbg.ps1, or set CAAI_DEBUG_ADAPTER to its executable path.");
    }
}