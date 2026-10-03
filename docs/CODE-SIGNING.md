# 代码签名与 SmartScreen 提示

> 结论先行，三条硬事实：
>
> 1. **SignPath Foundation 免费签名对本项目不适用** —— 条款明确禁止「包含任何闭源组件」的
>    项目，而完整版二进制依赖 `QuartzLauncher/Private/`（不在公开仓库里），
>    公开源码**构建不出被签名的那个二进制**。
> 2. **买了证书也不能立刻消除提示** —— 微软官方文档已明确：OV/EV/Azure 签名的新文件
>    一样会弹，要「数周 + 数百次干净安装」才攒够信誉。
> 3. **EV 证书自 2024 年起不再免检** —— 多花的钱买不到「立刻不弹」，不要为这个买 EV。

---

## 一、提示是怎么来的

```
浏览器下载 → 写入 MOTW(Zone.Identifier) → 启动时 SmartScreen 查信誉
           → 未签名 / 无信誉 → 「Windows 已保护你的电脑」
```

三个关键推论（均在 `learn.microsoft.com` 有明确表述）：

- **未签名的文件，每个新版本都从零攒信誉**，无法跨版本继承 → 每次发版用户都要再点一次「仍要运行」。
- **签名后信誉挂在证书上**，同证书的后续版本可以继承 → 这是买证书的**真正**价值，而不是「立刻不弹」。
- 本机 `dotnet build` 出来的 exe **没有 MOTW**，理论上不该弹；如果弹了，是本机
  「检查应用和文件」(`EnableWebContentEvaluation=1`) 在起作用 → 见第四节。

## 二、SignPath Foundation 为什么走不通

[SignPath 条款](https://signpath.org/terms.html)原文：

> **No proprietary code:** The project may not contain any proprietary, non open-source
> component (especially code published by a maintainer or an affiliated person/organization).

> Binary artifacts must be built from source code in a verifiable way.

本项目现状（已核对）：

| 检查 | 结果 |
|---|---|
| `git ls-files QuartzLauncher/Private/*` | **0 个文件被跟踪** |
| `.git/info/exclude` | 含 `Private/`、`relay/`、`admin/`、`PCL-source/` |
| 公开仓库（origin/main）是否有 `Private/` | **没有** |
| 完整版二进制能否由公开源码构建 | **不能** |

签名时 SignPath 会核对「二进制 = 该仓库源码的自动构建产物」。我们的发行版包含闭源模块，
这一条**技术上无法满足**。

**只有两种情况下能重新考虑 SignPath**：
- 把 `Private/`、`relay/`、`admin/` 一并开源；或
- 改签「开源精简版」（但这不是我们发布的版本，用户拿到的仍是未签名完整版）。

## 三、可选方案（价格为 2026 年公开报价，仅供决策）

| 方案 | 费用 | 能否用 | SmartScreen 实际效果 |
|---|---|---|---|
| **Microsoft Store（MSIX 上架）** | **免费** | 需改造 | ✅ **完全不弹**（微软重新签名）。但 MSIX 装在 `WindowsApps`，**自替换更新会失效**，与便携自更新架构冲突 |
| Azure Artifact Signing（原 Trusted Signing） | ~$9.99/月 | ❌ **地区不符** | 组织限美/加/欧盟/英；个人仅限**美国、加拿大** |
| **OV 证书**（DigiCert / Sectigo / 国内 CA） | **$150–300/年** | ✅ 全球可用 | ⚠️ 首版仍弹，但显示**真实发布者名**，信誉跨版本累积 |
| EV 证书 | $400+/年 | ✅ | ⚠️ **与 OV 完全相同**（2024 起取消免检），不建议为此加钱 |
| SignPath Foundation | 免费 | ❌ 见第二节 | — |
| 自签名证书 | 免费 | ⚠️ 仅自用 | 与不签名**完全一样**，公网分发会被拦 |

另外：OV 证书自 2023-06 起**私钥必须放 HSM/USB Token**，不能像以前那样直接把 pfx 丢进 CI。
纯 CI 自动签名需要走 CA 的云签名服务（如 SSL.com eSigner、DigiCert KeyLocker）。

## 四、本机开发期间怎么办

在**你自己这台机器**上，一条命令即可停止提示（可随时恢复）：

```powershell
# 关闭「检查应用和文件」
Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppHost' -Name EnableWebContentEvaluation -Value 0
# 恢复
Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppHost' -Name EnableWebContentEvaluation -Value 1
```

图形界面路径：Windows 安全中心 → 应用和浏览器控制 → 基于信誉的保护 → 关闭「检查应用和文件」。

代价：这台机器少一层防护。**只适合开发机，不要写进面向用户的说明。**

## 五、真要走签名，怎么落地

1. 买一张 **OV 代码签名证书**（不需要 EV）。
2. 按 CA 的说明把证书装到证书存储（USB Token / 云签名客户端），或拿到云签名的调用凭据。
3. 发布后、上传前，对 exe 签名：

```powershell
# 证书已装进本机存储（Token / 云签名客户端）
.\tools\sign-artifacts.ps1 -Path ..\..\dist\QuartzLauncher.exe -Thumbprint <证书指纹>

# 或用 pfx（仅当 CA 允许软件证书时）
.\tools\sign-artifacts.ps1 -Path ..\..\dist\QuartzLauncher.exe -PfxPath .\starfall.pfx -PfxPassword '***'
```

脚本会自动加 RFC3161 时间戳（**必须**，否则证书过期后签名作废）并验证结果。

4. **先签名，再算 SHA256、再写 `update.json`** —— 签名字节会改变文件哈希，顺序反了自动更新会校验失败。
5. 签名后不要再动文件。

> 注意：本项目是**便携自更新**架构（新版替换自身 exe）。签名不改变这个流程，
> 更新下来的新 exe 同样需要带有效签名，否则用户在新版本上又会看到一次提示。

## 六、当前建议

优先级从高到低：

1. **官网下载区加一句首次运行提示**（零成本，立刻降低用户流失）：
   「首次运行若提示『Windows 已保护你的电脑』，点『更多信息』→『仍要运行』即可。」
2. **本机开发机关掉「检查应用和文件」**（第一节的命令），开发期不再被打断。
3. 有预算再上 **OV 证书**（¥1000–2000/年），目的是「显示真实发布者 + 信誉跨版本累积」，
   而不是「立刻不弹」。
4. **不要**为 SmartScreen 买 EV。
5. **不要**指望 SignPath，除非把闭源模块开源。
