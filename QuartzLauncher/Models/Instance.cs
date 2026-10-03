using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace QuartzLauncher.Models;

public class Instance
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string VersionId { get; set; } = "";
    public string McVersion { get; set; } = "";
    public string Loader { get; set; } = "vanilla";
    public string LoaderVersion { get; set; } = "";
    public bool AutoSetChinese { get; set; } = true;
    public bool? VersionIsolation { get; set; }
    public bool? UsesVersionDirectory { get; set; }
    public string JavaPath { get; set; } = "";
    public int MemoryMb { get; set; }
    public string JvmArguments { get; set; } = "";
    public string GameArguments { get; set; } = "";
    public string IconPath { get; set; } = "";
    public string IconKey { get; set; } = "";
    public string CustomGameDir { get; set; } = "";

    public string Label
    {
        get
        {
            var baseVer = string.IsNullOrEmpty(McVersion) ? VersionId : McVersion;
            if (Loader == "vanilla")
                return $"{Name}  \u00b7  {baseVer}";
            var loaderDisplay = char.ToUpper(Loader[0]) + Loader[1..];
            var ver = LoaderVersion ?? "";
            var tag = string.IsNullOrEmpty(ver) ? loaderDisplay : $"{loaderDisplay}[{ver}]";
            return $"{Name}  \u00b7  {baseVer} {tag}";
        }
    }
}

public class InstanceStore
{
    private readonly string _instancesDir;

    public InstanceStore(string instancesDir) => _instancesDir = instancesDir;

    public List<Instance> List()
    {
        var result = new List<Instance>();
        if (!Directory.Exists(_instancesDir)) return result;
        foreach (var dir in Directory.GetDirectories(_instancesDir).OrderBy(d => d))
        {
            var file = Path.Combine(dir, "instance.json");
            if (!File.Exists(file)) continue;
            try
            {
                var json = File.ReadAllText(file);
                var inst = JsonConvert.DeserializeObject<Instance>(json);
                if (inst != null) result.Add(inst);
            }
            catch { continue; }
        }
        return result;
    }

    public string Create(Instance instance)
    {
        var root = Path.Combine(_instancesDir, instance.Id);
        Directory.CreateDirectory(root);
        WriteMetadata(root, instance);
        return root;
    }

    /// <summary>
    /// 以临时文件 + 原子替换写入实例元数据。
    /// 整合包导入完成前会先把元数据写进临时目录，再整体移动到 instances，
    /// 避免留下“游戏文件已存在但 instance.json 没写完”的半成品实例。
    /// </summary>
    public static void WriteMetadata(string instanceRoot, Instance instance)
    {
        Directory.CreateDirectory(instanceRoot);
        var target = Path.Combine(instanceRoot, "instance.json");
        var temporary = Path.Combine(instanceRoot, $".instance-{Guid.NewGuid():N}.tmp");
        var json = JsonConvert.SerializeObject(instance, Formatting.Indented);

        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(true);
            }

            if (File.Exists(target))
            {
                try
                {
                    File.Replace(temporary, target, null);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(temporary, target, true);
                }
                catch (IOException)
                {
                    // 某些文件系统不支持 ReplaceFile，仍使用同目录覆盖移动。
                    File.Move(temporary, target, true);
                }
            }
            else
            {
                File.Move(temporary, target);
            }

            // 写入后立即回读校验，避免磁盘/杀毒软件占用导致启动器显示假成功。
            var written = JsonConvert.DeserializeObject<Instance>(File.ReadAllText(target));
            if (written == null || !string.Equals(written.Id, instance.Id, StringComparison.OrdinalIgnoreCase))
                throw new IOException("实例元数据写入校验失败。");
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch
            {
                // 下次扫描会忽略临时文件，不影响已完成的实例。
            }
        }
    }

    public void Delete(string instanceId)
    {
        var root = Path.Combine(_instancesDir, instanceId);
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}
