using System.ComponentModel;

namespace QuartzLauncher.Models;

public enum ModSource
{
    Modrinth,
    CurseForge,
    MCmod
}

public sealed class McmodInfoItem
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
}

public class ModItem : INotifyPropertyChanged
{
    private string _name = "";
    private string _summary = "";
    private List<string> _versions = new();
    private List<string> _loaders = new();

    public string Id { get; set; } = "";
    public string Slug { get; set; } = "";

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            Raise(nameof(Name));
        }
    }

    public string OriginalName { get; set; } = "";
    public string McmodId { get; set; } = "";
    public string McmodPageUrl { get; set; } = "";

    public string Summary
    {
        get => _summary;
        set
        {
            if (_summary == value) return;
            _summary = value;
            Raise(nameof(Summary));
        }
    }

    public string Description { get; set; } = "";
    public List<string> DetailImageUrls { get; set; } = new();
    public string DetailHtml { get; set; } = "";
    public string McmodStatus { get; set; } = "";
    public string McmodSourceType { get; set; } = "";
    public List<McmodInfoItem> McmodInternalInfo { get; set; } = new();
    public long Downloads { get; set; }
    public string IconUrl { get; set; } = "";
    public string PageUrl { get; set; } = "";
    public List<string> Authors { get; set; } = new();
    public List<string> Categories { get; set; } = new();

    public List<string> Versions
    {
        get => _versions;
        set
        {
            _versions = value ?? new();
            Raise(nameof(Versions));
            Raise(nameof(SupportLabel));
        }
    }

    public List<string> Loaders
    {
        get => _loaders;
        set
        {
            _loaders = value ?? new();
            Raise(nameof(Loaders));
            Raise(nameof(SupportLabel));
        }
    }

    public string SupportLabel
    {
        get
        {
            var loaders = Loaders
                .Where(loader => !string.IsNullOrWhiteSpace(loader))
                .Select(FormatLoader)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var versions = Versions
                .Where(version => !string.IsNullOrWhiteSpace(version))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList();

            var parts = new List<string>();
            if (loaders.Count > 0) parts.Add(string.Join(" / ", loaders));
            if (versions.Count > 0) parts.Add(string.Join(" / ", versions));
            return string.Join("  ·  ", parts);
        }
    }

    public ModSource Source { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string FormatLoader(string loader) => loader.ToLowerInvariant() switch
    {
        "forge" => "Forge",
        "fabric" => "Fabric",
        "neoforge" => "NeoForge",
        "quilt" => "Quilt",
        "liteloader" => "LiteLoader",
        "rift" => "Rift",
        _ => loader
    };
}

public class ModVersionItem
{
    public string ProjectId { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string VersionNumber { get; set; } = "";
    public string GameVersion { get; set; } = "";
    public List<string> GameVersions { get; set; } = new();
    public string Loader { get; set; } = "";
    public List<string> Loaders { get; set; } = new();
    public long FileSize { get; set; }
    public string Sha1 { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string FileName { get; set; } = "";
    public long DateUploaded { get; set; }
    public ModSource Source { get; set; }
    public List<ModDependency> Dependencies { get; set; } = new();
}

public class ModDependency
{
    public string ProjectId { get; set; } = "";
    public string ProjectSlug { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string VersionId { get; set; } = "";
    public string VersionNumber { get; set; } = "";
    public string Type { get; set; } = ""; // required/optional/embedded/incompatible
    public ModSource Source { get; set; }
}
