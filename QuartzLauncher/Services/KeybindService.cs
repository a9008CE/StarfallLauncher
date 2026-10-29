using System.IO;
using System.Text;

namespace QuartzLauncher.Services;

/// <summary>一个可配置的原版键位动作。</summary>
public sealed record KeybindAction(string Id, string Display, string Default);

/// <summary>一处按键冲突：同一个键被多个动作占用。</summary>
public sealed record KeybindConflict(string Value, List<string> Actions);

/// <summary>键位读写结果。</summary>
public sealed record KeybindApplyResult(int Applied, int Skipped, IReadOnlyList<string> Warnings, string BackupPath);

/// <summary>
/// 读写 Minecraft 的 <c>&lt;游戏目录&gt;/options.txt</c> 里的键盘/鼠标绑定。
///
/// 文件格式（1.13+，实测）：
/// <code>
/// key_key.attack:key.mouse.left
/// key_key.forward:key.keyboard.w
/// key_key.sneak:key.keyboard.left.shift
/// </code>
/// 1.12.2 及更早用的是 LWJGL 数字码：<c>key_key.forward:17</c>。
///
/// 重要约束（写之前必须知道）：
/// 1. **游戏运行时不要写** —— MC 退出时会把整份 options.txt 重写，改动直接丢；
/// 2. 我们只负责原版键位，Mod 自己的绑定在 <c>config/</c> 里，不在这个文件；
/// 3. 写之前先备份一次（<c>options.txt.starfall-bak</c>），便于一键还原。
/// </summary>
public static class KeybindService
{
    public const string BackupSuffix = ".starfall-bak";

    /// <summary>常用原版键位（现代命名；值与游戏内一致）。</summary>
    public static readonly IReadOnlyList<KeybindAction> Actions = new List<KeybindAction>
    {
        new("key.attack", "攻击 / 破坏", "key.mouse.left"),
        new("key.use", "使用 / 放置", "key.mouse.right"),
        new("key.forward", "前进", "key.keyboard.w"),
        new("key.left", "左移", "key.keyboard.a"),
        new("key.back", "后退", "key.keyboard.s"),
        new("key.right", "右移", "key.keyboard.d"),
        new("key.jump", "跳跃", "key.keyboard.space"),
        new("key.sneak", "潜行", "key.keyboard.left.shift"),
        new("key.sprint", "疾跑", "key.keyboard.left.control"),
        new("key.inventory", "背包", "key.keyboard.e"),
        new("key.drop", "丢弃物品", "key.keyboard.q"),
        new("key.swapOffhand", "交换副手", "key.keyboard.f"),
        new("key.pickItem", "选取方块", "key.mouse.middle"),
        new("key.chat", "打开聊天", "key.keyboard.t"),
        new("key.playerlist", "玩家列表", "key.keyboard.tab"),
        new("key.command", "输入命令", "key.keyboard.slash"),
        new("key.socialInteractions", "社交屏幕", "key.keyboard.p"),
        new("key.advancements", "进度", "key.keyboard.l"),
        new("key.hotbar.1", "快捷栏 1", "key.keyboard.1"),
        new("key.hotbar.2", "快捷栏 2", "key.keyboard.2"),
        new("key.hotbar.3", "快捷栏 3", "key.keyboard.3"),
        new("key.hotbar.4", "快捷栏 4", "key.keyboard.4"),
        new("key.hotbar.5", "快捷栏 5", "key.keyboard.5"),
        new("key.hotbar.6", "快捷栏 6", "key.keyboard.6"),
        new("key.hotbar.7", "快捷栏 7", "key.keyboard.7"),
        new("key.hotbar.8", "快捷栏 8", "key.keyboard.8"),
        new("key.hotbar.9", "快捷栏 9", "key.keyboard.9"),
        new("key.togglePerspective", "切换视角", "key.keyboard.f5"),
        new("key.screenshot", "截图", "key.keyboard.f2"),
        new("key.fullscreen", "全屏", "key.keyboard.f11"),
        new("key.saveToolbarActivator", "保存快捷栏", "key.keyboard.c"),
        new("key.loadToolbarActivator", "加载快捷栏", "key.keyboard.x"),
    };

    /// <summary>老版本（≤1.12.2）常用键的 LWJGL 数字码，用于把现代名字翻译成数字。</summary>
    private static readonly Dictionary<string, string> LegacyCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["key.keyboard.w"] = "17", ["key.keyboard.a"] = "30", ["key.keyboard.s"] = "31",
        ["key.keyboard.d"] = "32", ["key.keyboard.e"] = "18", ["key.keyboard.q"] = "16",
        ["key.keyboard.f"] = "33", ["key.keyboard.c"] = "46", ["key.keyboard.x"] = "45",
        ["key.keyboard.t"] = "20", ["key.keyboard.p"] = "25", ["key.keyboard.l"] = "38",
        ["key.keyboard.space"] = "57", ["key.keyboard.left.shift"] = "42",
        ["key.keyboard.right.shift"] = "54", ["key.keyboard.left.control"] = "29",
        ["key.keyboard.right.control"] = "157", ["key.keyboard.tab"] = "15",
        ["key.keyboard.slash"] = "53", ["key.keyboard.escape"] = "1",
        ["key.keyboard.f2"] = "60", ["key.keyboard.f5"] = "63", ["key.keyboard.f11"] = "87",
        ["key.keyboard.1"] = "2", ["key.keyboard.2"] = "3", ["key.keyboard.3"] = "4",
        ["key.keyboard.4"] = "5", ["key.keyboard.5"] = "6", ["key.keyboard.6"] = "7",
        ["key.keyboard.7"] = "8", ["key.keyboard.8"] = "9", ["key.keyboard.9"] = "10",
        ["key.mouse.left"] = "-100", ["key.mouse.right"] = "-99", ["key.mouse.middle"] = "-98",
    };

    public static string OptionsPath(string gameRoot) => Path.Combine(gameRoot, "options.txt");

    /// <summary>读取实例当前键位（只取 <c>key_</c> 开头的行）。</summary>
    public static Dictionary<string, string> Read(string gameRoot)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = OptionsPath(gameRoot);
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("key_", StringComparison.Ordinal)) continue;
            var idx = trimmed.IndexOf(':');
            if (idx <= 4) continue;
            var id = trimmed[4..idx];
            var value = trimmed[(idx + 1)..];
            result[id] = value;
        }
        return result;
    }

    /// <summary>这份 options.txt 用的是现代键名还是老式数字码（没有文件时按现代处理）。</summary>
    public static bool IsModernStyle(string gameRoot)
    {
        var path = OptionsPath(gameRoot);
        if (!File.Exists(path)) return true;
        try
        {
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (!line.StartsWith("key_", StringComparison.Ordinal)) continue;
                return line.Contains("key.keyboard.", StringComparison.OrdinalIgnoreCase)
                       || line.Contains("key.mouse.", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // 读失败按现代处理
        }
        return true;
    }

    /// <summary>
    /// 把键位方案写进 options.txt（保留其它设置）。
    /// <paramref name="binds"/> 的 key 是不带 <c>key_</c> 前缀的动作 id（如 <c>key.forward</c>）。
    /// </summary>
    public static KeybindApplyResult Apply(string gameRoot, IReadOnlyDictionary<string, string> binds)
    {
        var warnings = new List<string>();
        var applied = 0;
        var skipped = 0;
        var path = OptionsPath(gameRoot);
        Directory.CreateDirectory(gameRoot);

        var modern = IsModernStyle(gameRoot);
        var lines = File.Exists(path)
            ? File.ReadAllLines(path, Encoding.UTF8).ToList()
            : new List<string>();

        var backupPath = path + BackupSuffix;
        if (File.Exists(path) && !File.Exists(backupPath))
        {
            try { File.Copy(path, backupPath, overwrite: false); }
            catch (Exception ex) { warnings.Add("备份 options.txt 失败：" + ex.Message); }
        }

        foreach (var (id, rawValue) in binds)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            var value = NormalizeValue(rawValue, modern);
            if (value == null)
            {
                skipped++;
                warnings.Add($"{DisplayName(id)}：老版本只支持数字键码，已跳过「{rawValue}」");
                continue;
            }

            var key = "key_" + id.Trim();
            var newLine = key + ":" + value;
            var index = lines.FindIndex(line =>
                line.StartsWith(key + ":", StringComparison.Ordinal));
            if (index >= 0) lines[index] = newLine;
            else lines.Add(newLine);
            applied++;
        }

        if (applied == 0) return new KeybindApplyResult(0, skipped, warnings, backupPath);

        try
        {
            File.WriteAllLines(path, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            warnings.Add("写入 options.txt 失败（游戏是否正在运行？）：" + ex.Message);
            return new KeybindApplyResult(0, skipped, warnings, backupPath);
        }
        return new KeybindApplyResult(applied, skipped, warnings, backupPath);
    }

    /// <summary>把备份还原回 options.txt（没有备份则返回 false）。</summary>
    public static bool RestoreBackup(string gameRoot)
    {
        var path = OptionsPath(gameRoot);
        var backupPath = path + BackupSuffix;
        if (!File.Exists(backupPath)) return false;
        try
        {
            File.Copy(backupPath, path, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>找出「同一个按键被多个动作占用」的冲突。</summary>
    public static List<KeybindConflict> DetectConflicts(
        IReadOnlyDictionary<string, string> binds)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, value) in binds)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (!map.TryGetValue(value.Trim(), out var list))
            {
                list = new List<string>();
                map[value.Trim()] = list;
            }
            list.Add(id);
        }
        return map.Where(pair => pair.Value.Count > 1)
            .Select(pair => new KeybindConflict(pair.Key, pair.Value))
            .OrderBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>这个动作 id 是不是原版键位（只有原版键位会被存进默认方案）。</summary>
    public static bool IsVanillaId(string id) =>
        Actions.Any(action => string.Equals(action.Id, id, StringComparison.OrdinalIgnoreCase));

    public static string DisplayName(string id)
    {
        var action = Actions.FirstOrDefault(item =>
            string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        return action?.Display ?? id;
    }

    // ══════════════════════════════════════════════════════════
    // 显示名 / 按键录入（2026-10-28）
    // ══════════════════════════════════════════════════════════

    private static readonly Dictionary<string, string> FriendlyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = "空格",
        ["tab"] = "Tab",
        ["enter"] = "回车",
        ["escape"] = "Esc",
        ["backspace"] = "退格",
        ["insert"] = "Insert",
        ["delete"] = "Delete",
        ["home"] = "Home",
        ["end"] = "End",
        ["page.up"] = "PageUp",
        ["page.down"] = "PageDown",
        ["caps.lock"] = "CapsLock",
        ["left.shift"] = "左 Shift",
        ["right.shift"] = "右 Shift",
        ["left.control"] = "左 Ctrl",
        ["right.control"] = "右 Ctrl",
        ["left.alt"] = "左 Alt",
        ["right.alt"] = "右 Alt",
        ["up"] = "↑",
        ["down"] = "↓",
        ["left"] = "←",
        ["right"] = "→",
        ["minus"] = "-",
        ["equal"] = "=",
        ["left.bracket"] = "[",
        ["right.bracket"] = "]",
        ["backslash"] = "\\",
        ["semicolon"] = ";",
        ["apostrophe"] = "'",
        ["grave.accent"] = "`",
        ["comma"] = ",",
        ["period"] = ".",
        ["slash"] = "/",
        ["print.screen"] = "PrintScreen",
        ["scroll.lock"] = "ScrollLock",
        ["pause"] = "Pause",
        ["unknown"] = "未绑定",
    };

    /// <summary>把 MC 的键值翻译成人看的文字（空格、W、鼠标左键…）。</summary>
    public static string DescribeValue(string? rawValue)
    {
        var value = (rawValue ?? "").Trim();
        if (value.Length == 0) return "未绑定";

        if (value.StartsWith("key.mouse.", StringComparison.OrdinalIgnoreCase))
        {
            return value[10..].ToLowerInvariant() switch
            {
                "left" => "鼠标左键",
                "right" => "鼠标右键",
                "middle" => "鼠标中键",
                "4" => "鼠标侧键 4",
                "5" => "鼠标侧键 5",
                var other => "鼠标 " + other,
            };
        }

        if (value.StartsWith("key.keyboard.", StringComparison.OrdinalIgnoreCase))
        {
            return DescribeKeyboardName(value[13..].ToLowerInvariant());
        }

        // 老版本的纯数字键码：反查成现代名字再显示
        if (int.TryParse(value, out _))
        {
            foreach (var pair in LegacyCodes)
            {
                if (pair.Value == value) return DescribeKeyboardName(pair.Key[13..]);
            }
            return "键码 " + value;
        }

        return value;
    }

    private static string DescribeKeyboardName(string name)
    {
        if (name.Length == 1 && name[0] >= 'a' && name[0] <= 'z')
            return name.ToUpperInvariant();
        if (name.Length == 1 && name[0] >= '0' && name[0] <= '9')
            return name;
        if (name.StartsWith("f", StringComparison.Ordinal)
            && name.Length <= 3 && int.TryParse(name[1..], out var f) && f is >= 1 and <= 24)
            return "F" + f;
        if (name.StartsWith("keypad.", StringComparison.Ordinal))
            return "小键盘 " + name[7..];
        return FriendlyNames.TryGetValue(name, out var friendly) ? friendly : name;
    }

    /// <summary>
    /// Windows 虚拟键码 → Minecraft 键名（用于「点一下再按键」的录入）。
    /// 只覆盖常用键；认不出来返回 null（交给调用方忽略）。
    /// </summary>
    public static string? FromVirtualKey(int vk)
    {
        if (vk >= 0x41 && vk <= 0x5A) return "key.keyboard." + (char)('a' + (vk - 0x41));
        if (vk >= 0x30 && vk <= 0x39) return "key.keyboard." + (char)('0' + (vk - 0x30));
        if (vk >= 0x70 && vk <= 0x7B) return "key.keyboard.f" + (vk - 0x70 + 1);
        if (vk >= 0x60 && vk <= 0x69) return "key.keyboard.keypad." + (vk - 0x60);

        return vk switch
        {
            0x20 => "key.keyboard.space",
            0x09 => "key.keyboard.tab",
            0x0D => "key.keyboard.enter",
            0x1B => "key.keyboard.escape",
            0x08 => "key.keyboard.backspace",
            0x2D => "key.keyboard.insert",
            0x2E => "key.keyboard.delete",
            0x24 => "key.keyboard.home",
            0x23 => "key.keyboard.end",
            0x21 => "key.keyboard.page.up",
            0x22 => "key.keyboard.page.down",
            0x14 => "key.keyboard.caps.lock",
            0x25 => "key.keyboard.left",
            0x26 => "key.keyboard.up",
            0x27 => "key.keyboard.right",
            0x28 => "key.keyboard.down",
            0xA0 => "key.keyboard.left.shift",
            0xA1 => "key.keyboard.right.shift",
            0xA2 => "key.keyboard.left.control",
            0xA3 => "key.keyboard.right.control",
            0xA4 => "key.keyboard.left.alt",
            0xA5 => "key.keyboard.right.alt",
            0xBD => "key.keyboard.minus",
            0xBB => "key.keyboard.equal",
            0xDB => "key.keyboard.left.bracket",
            0xDD => "key.keyboard.right.bracket",
            0xDC => "key.keyboard.backslash",
            0xBA => "key.keyboard.semicolon",
            0xDE => "key.keyboard.apostrophe",
            0xC0 => "key.keyboard.grave.accent",
            0xBC => "key.keyboard.comma",
            0xBE => "key.keyboard.period",
            0xBF => "key.keyboard.slash",
            0x6A => "key.keyboard.keypad.multiply",
            0x6B => "key.keyboard.keypad.add",
            0x6D => "key.keyboard.keypad.subtract",
            0x6E => "key.keyboard.keypad.decimal",
            0x6F => "key.keyboard.keypad.divide",
            0x2C => "key.keyboard.print.screen",
            0x91 => "key.keyboard.scroll.lock",
            0x13 => "key.keyboard.pause",
            _ => null,
        };
    }

    /// <summary>把用户输入的值规整成 MC 认识的样子；老版本无法翻译时返回 null。</summary>
    private static string? NormalizeValue(string? rawValue, bool modern)
    {
        var value = (rawValue ?? "").Trim();
        if (value.Length == 0) return "key.keyboard.unknown";
        if (modern) return value;

        // 老版本：数字直接可用；现代名字尽量翻译成数字，翻译不了就跳过
        if (int.TryParse(value, out _)) return value;
        return LegacyCodes.TryGetValue(value, out var code) ? code : null;
    }
}
