using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using QuartzLauncher.Models;

namespace QuartzLauncher.Services;

public class ContentService
{
    public List<ModInfo> Mods(string instanceRoot)
    {
        var result = new List<ModInfo>();
        var modsDir = Path.Combine(instanceRoot, "mods");
        if (!Directory.Exists(modsDir)) return result;

        foreach (var path in Directory.GetFiles(modsDir).OrderBy(p => p))
        {
            var ext = Path.GetExtension(path).ToLower();
            if (ext != ".jar" && ext != ".disabled" && ext != ".old") continue;
            if (ext == ".disabled" || ext == ".old")
            {
                var baseName = Path.GetFileNameWithoutExtension(path);
                var baseExt = Path.GetExtension(baseName).ToLower();
                if (baseExt != ".jar" && baseExt != ".zip" && baseExt != ".litemod") continue;
            }

            var isEnabled = ext is ".jar" or ".zip" or ".litemod";
            var fi = new FileInfo(path);
            var name = Path.GetFileNameWithoutExtension(path);
            if (!isEnabled)
            {
                var innerName = Path.GetFileNameWithoutExtension(name);
                if (!string.IsNullOrEmpty(innerName)) name = innerName;
            }
            var version = "";
            var loader = "";
            var description = "";

            try
            {
                using var archive = System.IO.Compression.ZipFile.OpenRead(path);
                var names = archive.Entries.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (names.Contains("fabric.mod.json"))
                {
                    var entry = archive.GetEntry("fabric.mod.json")!;
                    using var reader = new StreamReader(entry.Open());
                    var data = JsonConvert.DeserializeObject<Dictionary<string, object>>(reader.ReadToEnd()) ?? new();
                    name = data.ContainsKey("name") ? data["name"]?.ToString() ?? name : name;
                    version = data.ContainsKey("version") ? data["version"]?.ToString() ?? "" : "";
                    description = data.ContainsKey("description") ? data["description"]?.ToString() ?? "" : "";
                    loader = "Fabric";
                }
                else if (names.Contains("META-INF/mods.toml"))
                {
                    var entry = archive.GetEntry("META-INF/mods.toml")!;
                    using var reader = new StreamReader(entry.Open());
                    var toml = reader.ReadToEnd();
                    var modLoaderMatch = System.Text.RegularExpressions.Regex.Match(toml, @"modLoader\s*=\s*""(.+?)""");
                    var versionMatch = System.Text.RegularExpressions.Regex.Match(toml, @"version\s*=\s*""(.+?)""");
                    var descMatch = System.Text.RegularExpressions.Regex.Match(toml, @"description\s*=\s*""(.+?)""");
                    if (versionMatch.Success) version = versionMatch.Groups[1].Value;
                    if (descMatch.Success) description = descMatch.Groups[1].Value;
                    loader = "Forge";
                }
                else if (names.Contains("quilt.mod.json"))
                {
                    var entry = archive.GetEntry("quilt.mod.json")!;
                    using var reader = new StreamReader(entry.Open());
                    var data = JsonConvert.DeserializeObject<Dictionary<string, object>>(reader.ReadToEnd()) ?? new();
                    if (data.ContainsKey("quilt_loader"))
                    {
                        var quiltLoader = data["quilt_loader"] as Dictionary<string, object>;
                        if (quiltLoader != null)
                        {
                            if (quiltLoader.ContainsKey("version")) version = quiltLoader["version"]?.ToString() ?? "";
                            if (quiltLoader.ContainsKey("description")) description = quiltLoader["description"]?.ToString() ?? "";
                        }
                    }
                    loader = "Quilt";
                }
                else if (names.Contains("mcmod.info"))
                {
                    var entry = archive.GetEntry("mcmod.info")!;
                    using var reader = new StreamReader(entry.Open());
                    var data = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(reader.ReadToEnd());
                    if (data != null && data.Count > 0)
                    {
                        name = data[0].ContainsKey("name") ? data[0]["name"]?.ToString() ?? name : name;
                        version = data[0].ContainsKey("version") ? data[0]["version"]?.ToString() ?? "" : "";
                        description = data[0].ContainsKey("description") ? data[0]["description"]?.ToString() ?? "" : "";
                    }
                    loader = "Forge";
                }
                else if (names.Contains("pack.mcmeta"))
                {
                    loader = "ResourcePack";
                }
            }
            catch { }

            if (loader == "ResourcePack") continue;

            result.Add(new ModInfo(
                Path: path,
                Name: name,
                Version: version,
                Loader: loader,
                Enabled: isEnabled,
                Description: description,
                FileName: Path.GetFileName(path),
                FileSize: fi.Length));
        }
        return result;
    }

    /// <summary>
    /// 一个 Mod 文件现在**会不会被游戏加载**（2026-10-27 加）。
    /// 游戏只认 .jar/.zip/.litemod；`.disabled` / `.old` 后缀的是被我们改过名的禁用项。
    /// </summary>
    public static bool IsEnabledModFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".jar" or ".zip" or ".litemod";
    }

    /// <summary>被禁用（`.disabled` / `.old` 结尾）的 Mod 文件。</summary>
    public static bool IsDisabledModFile(string path)
    {
        var lower = path.ToLowerInvariant();
        if (!lower.EndsWith(".disabled") && !lower.EndsWith(".old")) return false;
        var inner = Path.GetExtension(lower[..^Path.GetExtension(lower).Length]).ToLowerInvariant();
        return inner is ".jar" or ".zip" or ".litemod";
    }

    /// <summary>
    /// 统计某个实例目录下「已启用 / 已禁用」的 Mod 数量。
    /// 首页卡片和启动前自检都用它，保证**口径一致**（以前首页只数 .jar、Mod 页把禁用也算进去，
    /// 玩家看到「列表里有 Mod、游戏里 0 Mod」时根本分不清）。
    /// </summary>
    public static (int Enabled, int Disabled) CountMods(string instanceRoot)
    {
        var modsDir = Path.Combine(instanceRoot, "mods");
        if (!Directory.Exists(modsDir)) return (0, 0);
        var enabled = 0;
        var disabled = 0;
        foreach (var file in Directory.EnumerateFiles(modsDir))
        {
            if (IsEnabledModFile(file)) enabled++;
            else if (IsDisabledModFile(file)) disabled++;
        }
        return (enabled, disabled);
    }

    /// <summary>
    /// 把实例里**所有**被禁用的 Mod 恢复启用（`.disabled`/`.old` 去掉后缀）。
    /// 目标文件已存在时跳过（同名冲突不覆盖）。返回成功启用的数量。
    /// </summary>
    public static int EnableAllDisabled(string instanceRoot)
    {
        var modsDir = Path.Combine(instanceRoot, "mods");
        if (!Directory.Exists(modsDir)) return 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(modsDir).ToList())
        {
            if (!IsDisabledModFile(file)) continue;
            var target = file[..^Path.GetExtension(file).Length];
            try
            {
                if (!File.Exists(target)) File.Move(file, target);
            }
            catch
            {
                // 单个文件失败（占用/权限）不能影响其它 Mod
            }
            if (File.Exists(target)) count++;
        }
        return count;
    }

    /// <summary>
    /// 把 fromRoot/mods 里的 Mod 迁到 toRoot/mods（2026-10-27）。
    /// 换布局（`versions/<id>` ↔ `instances/<id>`）时用：玩家的 Mod 在一处、游戏读另一处。
    /// 同名的目标文件已存在就跳过（不覆盖）。跨盘/占用时退回复制+删除。返回迁移成功的数量。
    /// </summary>
    public static int MigrateMods(string fromRoot, string toRoot)
    {
        var from = Path.Combine(fromRoot, "mods");
        var to = Path.Combine(toRoot, "mods");
        if (!Directory.Exists(from) || string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase))
            return 0;
        Directory.CreateDirectory(to);
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(from).ToList())
        {
            if (!IsEnabledModFile(file) && !IsDisabledModFile(file)) continue;
            var target = Path.Combine(to, Path.GetFileName(file));
            if (File.Exists(target)) continue;
            try
            {
                File.Move(file, target);
            }
            catch
            {
                try { File.Copy(file, target, false); File.Delete(file); }
                catch { continue; }
            }
            count++;
        }
        return count;
    }

    public void AddMods(string instanceRoot, List<string> files)
    {
        var target = Path.Combine(instanceRoot, "mods");
        Directory.CreateDirectory(target);
        foreach (var file in files)
        {
            if (Path.GetExtension(file).ToLower() == ".jar")
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        }
    }

    public void ToggleMod(ModInfo mod)
    {
        if (mod.Enabled)
        {
            var disablePath = File.Exists(mod.Path + ".disabled") ? mod.Path + ".old" : mod.Path + ".disabled";
            File.Move(mod.Path, disablePath);
        }
        else
        {
            var baseName = Path.GetFileNameWithoutExtension(mod.Path);
            var baseExt = Path.GetExtension(baseName).ToLower();
            if (baseExt is ".jar" or ".zip" or ".litemod")
                File.Move(mod.Path, Path.Combine(Path.GetDirectoryName(mod.Path)!, baseName));
            else
                File.Move(mod.Path, mod.Path.Replace(".disabled", "").Replace(".old", ""));
        }
    }

    public void DeleteMod(ModInfo mod)
    {
        if (File.Exists(mod.Path)) File.Delete(mod.Path);
        var dir = Path.GetDirectoryName(mod.Path)!;
        var baseName = Path.GetFileNameWithoutExtension(mod.Path);
        var disabled = Path.Combine(dir, baseName + ".disabled");
        var old = Path.Combine(dir, baseName + ".old");
        if (File.Exists(disabled)) File.Delete(disabled);
        if (File.Exists(old)) File.Delete(old);
    }

    public void DeleteMods(IEnumerable<ModInfo> mods)
    {
        foreach (var mod in mods) DeleteMod(mod);
    }

    public void ToggleMods(IEnumerable<ModInfo> mods)
    {
        foreach (var mod in mods) ToggleMod(mod);
    }

    public List<string> Saves(string instanceRoot)
    {
        var savesDir = Path.Combine(instanceRoot, "saves");
        if (!Directory.Exists(savesDir)) return new();
        return Directory.GetDirectories(savesDir)
            .OrderByDescending(d => Directory.GetCreationTime(d))
            .ToList();
    }

    public void ImportSave(string instanceRoot, string source)
    {
        var saveName = Directory.Exists(source)
            ? new DirectoryInfo(source).Name
            : Path.GetFileNameWithoutExtension(source);
        var target = Path.Combine(instanceRoot, "saves", saveName);
        if (Directory.Exists(target)) throw new Exception($"Save already exists: {Path.GetFileName(source)}");
        Directory.CreateDirectory(Path.Combine(instanceRoot, "saves"));
        if (Directory.Exists(source))
        {
            CopyDirectory(source, target);
            return;
        }

        if (!Path.GetExtension(source).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new Exception("存档必须是文件夹或 ZIP 压缩包。");

        var temporary = target + ".import-" + Guid.NewGuid().ToString("N");
        try
        {
            ZipFile.ExtractToDirectory(source, temporary);
            var directories = Directory.GetDirectories(temporary);
            var files = Directory.GetFiles(temporary);
            if (directories.Length == 1 && files.Length == 0)
                CopyDirectory(directories[0], target);
            else
                CopyDirectory(temporary, target);
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
            if (!Directory.Exists(target))
                throw new IOException("存档解压失败，未生成有效的存档目录。");
        }
    }

    public void DeleteSave(string savePath) => Directory.Delete(savePath, true);

    public List<string> ResourcePacks(string instanceRoot)
    {
        var dir = Path.Combine(instanceRoot, "resourcepacks");
        if (!Directory.Exists(dir)) return new();
        return Directory.GetFiles(dir)
            .Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".zip.disabled", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => File.GetCreationTime(f))
            .ToList();
    }

    public void ImportResourcePack(string instanceRoot, string source)
    {
        var dir = Path.Combine(instanceRoot, "resourcepacks");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, Path.GetFileName(source));
        File.Copy(source, target, true);
    }

    public void ToggleResourcePack(string path)
    {
        if (path.EndsWith(".disabled"))
            File.Move(path, path[..^".disabled".Length]);
        else
            File.Move(path, path + ".disabled");
    }

    public void DeleteResourcePack(string path) => File.Delete(path);

    public List<string> ShaderPacks(string instanceRoot)
    {
        var dir = Path.Combine(instanceRoot, "shaderpacks");
        if (!Directory.Exists(dir)) return new();
        return Directory.GetFiles(dir)
            .Where(f =>
            {
                var ext = Path.GetExtension(f).ToLower();
                return ext is ".zip" or ".jar" or ".vsh" or ".glsl" or ".fsh" or ".disabled";
            })
            .OrderByDescending(f => File.GetCreationTime(f))
            .ToList();
    }

    public void ImportShaderPack(string instanceRoot, string source)
    {
        var dir = Path.Combine(instanceRoot, "shaderpacks");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, Path.GetFileName(source));
        File.Copy(source, target, true);
    }

    public void ToggleShaderPack(string path)
    {
        if (path.EndsWith(".disabled"))
            File.Move(path, path[..^".disabled".Length]);
        else
            File.Move(path, path + ".disabled");
    }

    public void DeleteShaderPack(string path) => File.Delete(path);

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }
}
