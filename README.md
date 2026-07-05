# Card Replace

Card Replace 是一个面向《Slay the Spire 2》的卡面替换生成工具。

当前 demo 已经在游戏内验证成功：生成后的替换卡面可以在百科/卡牌图鉴中看到。

## 当前目标

这个项目现在的主体不是游戏内编辑器，而是游戏外生成工具。

它读取 Card Art Editor 风格的 `.cardartpack.json`，也可以读取未加密的现有卡面替换 mod 文件夹、`.pck` 或 `.zip`，在游戏启动前完成资源整理、优先级合并、冲突处理和 PCK 生成，最后输出一个可以放进 STS2 `mods` 目录的完整 mod：

```text
dist/card_replace/
  card_replace.dll
  card_replace.json
  card_replace.pck
  manifest.final.cardreplace
  conflicts.report.cardreplace
```

用户把这个目录复制到游戏的 `mods/card_replace` 后重启游戏即可。

## 为什么不是纯 PCK 覆盖

一开始尝试过单纯把图片打进 PCK，并覆盖：

```text
res://images/packed/card_portraits/<pool>/<card>.png
```

但 STS2 的基础卡面通常优先走 atlas：

```text
res://images/atlases/card_atlas.sprites/<pool>/<card>.tres
```

所以只覆盖 fallback PNG 路径，基础卡不会稳定变化。

当前可用方案是：

1. 构建期读取 `.cardartpack.json`。
2. 构建期把胜出的图片写入临时 Godot 工程。
3. 构建期导出 `card_replace.pck`。
4. 构建期写入 `res://generated/card_replace/card_replacements.json`。
5. 游戏启动时加载 PCK。
6. DLL 读取 PCK 内的 replacement map。
7. Harmony patch `NCard.UpdateVisuals` 和 `NCard._EnterTree`。
8. 卡牌 UI 刷新时，把已导入的 `Texture2D` 指给卡面 `TextureRect`。

这不是 Card Art Editor 那种运行时编辑器逻辑。游戏内不会扫描外部图片，不会读取 `.cardartpack.json`，不会即时解码图片，也不会在游戏中计算优先级。

## 输入格式

当前支持两类输入：

```text
*.cardartpack.json
未加密卡面替换 mod 文件夹 / .pck / .zip
```

### `.cardartpack.json`

每个 override 使用：

1. `png_base64`
2. 如果没有，则使用 `edit_source_png_base64`

没有可用 PNG 字段的条目会被跳过。

GIF 动图暂时搁置。现在只能使用包里提供的静态 PNG fallback。

### 未加密 PCK mod

可以输入：

```text
D:\steam\steamapps\common\Slay the Spire 2\mods\某个卡面mod
D:\path\to\some_card_art_mod.pck
D:\path\to\some_card_art_mod.zip
```

文件夹输入会扫描该目录第一层的 `*.pck`。zip 输入会先解压到临时目录，再扫描其中的 `*.pck`。

当前识别这类 Godot 已导入卡面资源：

```text
generated/assets/card_art/MegaCrit.Sts2.Core.Models.Cards.<CardName>_card_art.jpg.import
.godot/imported/MegaCrit.Sts2.Core.Models.Cards.<CardName>_card_art.jpg-<hash>.ctex
```

Builder 会从文件名恢复 `cardId`，提取 `.import + .ctex`，再把它们纳入同一套 priority 合并。

加密 PCK 不支持。现有 DLL 的逻辑不会被通用反编译；我们只导入 PCK 里能识别出的卡面资源。

## 优先级规则

优先级在构建期解决。

`priority` 数字越大，优先级越高。多个启用包替换同一张卡时，高优先级覆盖低优先级。

冲突主键优先使用 `cardId`，没有 `cardId` 时才回退到 `source_path`。这样 `.cardartpack.json` 和从 PCK 导入的资源只要能恢复到同一张卡，也会进入同一套覆盖规则。

当前本地工作流可以把文件名前缀转换成 priority：

```text
1-xxx.cardartpack.json -> 最高优先级
2-xxx.cardartpack.json -> 次一级
3-xxx.cardartpack.json -> 再低一级
```

Builder 本身只读取 `build_config.json` 中的 `priority`。`.cardartpack.json`、PCK 文件夹和 zip 都会进入同一张候选表；如果它们指向同一个 `cardId`，高 priority 的来源胜出。

构建后会输出：

```text
dist/card_replace/manifest.final.cardreplace
dist/card_replace/conflicts.report.cardreplace
```

RitsuLib 配置页会显示：

- PCK 是否加载成功
- 本次处理了哪些输入包
- 每个包的 priority 和 replacement 数量
- 生成了多少张卡面
- 冲突数量和部分冲突样例

## 构建配置

复制：

```text
build_config.example.json
```

为：

```text
build_config.json
```

然后修改本机路径和包列表。

推荐使用数组形式，后续扩展更方便：

```json
{
  "godot_exe": "D:\\workspace\\slaythespire2\\mods\\Godot_v4.5.1-stable_mono_win64\\Godot_v4.5.1-stable_mono_win64_console.exe",
  "output_root": "build\\generated",
  "output_mod_dir": "dist\\card_replace",
  "loader_dll_path": ".godot\\mono\\temp\\bin\\Debug\\card_replace.dll",
  "mod_manifest_path": "card_replace.json",
  "staging_root": "build\\staging_godot_project",
  "pck_name": "card_replace.pck",
  "inputs": [
    {
      "id": "pack_1",
      "path": "D:\\path\\to\\1-pack.cardartpack.json",
      "enabled": true,
      "priority": 99999,
      "type": "cardartpack"
    },
    {
      "id": "existing_pck_mod",
      "path": "D:\\steam\\steamapps\\common\\Slay the Spire 2\\mods\\猎宝卡面",
      "enabled": true,
      "priority": 90000,
      "type": "folder"
    }
  ]
}
```

也支持简单 KV 形式：

```json
{
  "inputs": {
    "D:\\path\\to\\1-pack.cardartpack.json": 99999,
    "D:\\steam\\steamapps\\common\\Slay the Spire 2\\mods\\猎宝卡面": 90000
  }
}
```

如果省略 `type`，Builder 会按路径自动判断：`.cardartpack.json`、`.pck`、`.zip` 或文件夹。

旧版 `packs` 数组仍然兼容，但新配置建议使用 `inputs`。

`build_config.json` 已经被 `.gitignore` 忽略，不会把本机绝对路径提交到仓库。

## 构建命令

构建 loader DLL：

```powershell
dotnet build .\card_replace.csproj -v:minimal -p:SkipModDeploy=true
```

生成最终 mod：

```powershell
dotnet run --project .\tools\CardReplaceBuilder\CardReplaceBuilder.csproj -- .\build_config.json
```

安装到游戏目录：

```powershell
$src = "D:\workspace\slaythespire2\mods\card_replace\dist\card_replace"
$dst = "D:\steam\steamapps\common\Slay the Spire 2\mods\card_replace"
Copy-Item -Force -Recurse -Path $src -Destination (Split-Path $dst)
```

复制后重启游戏。

## 调试

如果要直接从游戏目录启动，先在游戏根目录创建：

```text
D:\steam\steamapps\common\Slay the Spire 2\steam_appid.txt
```

内容为：

```text
2868840
```

然后从游戏根目录启动 `SlayTheSpire2.exe`。

日志路径：

```text
C:\Users\<user>\AppData\Roaming\SlayTheSpire2\logs\godot.log
```

正常加载时应该能看到：

```text
card_replace: loaded generated pck
card_replace: loaded replacement map entries=<count>
card_replace: Harmony patches applied
card_replace: report pckLoaded=True
```

## 运行时行为

游戏启动时：

1. STS2 加载 `card_replace.dll`。
2. STS2 加载 `card_replace.pck`。
3. Card Replace 加载 PCK 内的 replacement map。
4. Card Replace 应用卡面 UI patch。
5. RitsuLib 注册信息展示页。

卡牌 UI 刷新时，patch 根据卡牌模型找到对应生成纹理，并设置到卡面节点上。

运行时不读取 `.cardartpack.json`，不扫描外部资源目录，不进行图片解码，也不重新计算包优先级。

## 现有 DLL/PCK 卡面 Mod 能不能调整优先级

如果它们仍然作为独立 mod 加载，不能可靠调整。

原因是这类 mod 通常自带 DLL 和 PCK。它自己的 DLL 会用自己的 Harmony patch 或资源加载逻辑决定卡面替换。多个 DLL 同时 patch 同一个 UI 节点时，谁最后生效取决于加载顺序、patch 顺序和具体实现，不是一个稳定、可解释的优先级系统。

稳定路线是把它们转换成 Card Replace 的输入，再由 CardReplaceBuilder 统一处理：

```text
现有卡面来源
        |
        v
转换成 replacement entries
        |
        v
CardReplaceBuilder 统一按 priority 合并
        |
        v
一个最终 card_replace.dll + card_replace.pck
```

目前支持的输入：

- `.cardartpack.json`
- 未加密 PCK 文件
- 包含 PCK 的 mod 文件夹
- 包含 PCK 的 zip

仍不支持的输入：

- 加密 PCK
- 只靠 DLL 运行时逻辑、PCK 内没有可识别卡面资源的 mod
- 没有路径映射信息的普通散图目录

## 关于 PCK 导入

以 `D:\steam\steamapps\common\Slay the Spire 2\mods\猎宝卡面` 为例，里面的 `AI_silent_NSFW_MOD.pck` 可以读取目录表。

这个 PCK 包含大量类似这样的资源：

```text
.godot/imported/MegaCrit.Sts2.Core.Models.Cards.<CardName>_card_art.jpg-<hash>.ctex
```

也就是说，它不是原始 `.cardartpack.json`，而是 Godot 导入后的纹理缓存。它现在已经作为输入源接入，处理方式是：

1. 读取 PCK 目录表。
2. 提取 `.ctex` 和对应 `.import` 文件。
3. 从文件名恢复 card id。
4. 生成 Card Replace replacement map。
5. 按 priority 和其他输入源合并。

更理想的路线是将 PCK 中的卡面提取为中间包格式，再参与统一构建，而不是让多个卡面 DLL 在运行时互相覆盖。

## 路线图

- 保持 `.cardartpack.json` 作为主输入格式。
- 增加 pack1 目录扫描，把 `1-*.cardartpack.json` 自动转换成最高 priority。
- 继续完善现有 PCK 目录解析和导入器。
- 支持更多 PCK 卡面命名格式。
- 生成更完整的 manifest，让 RitsuLib 页面能显示每个来源的胜出/被覆盖关系。
- 单独设计 GIF 支持。GIF 不适合强塞进当前静态 `Texture2D` 路线。

## 当前限制

- GIF 动图未实现。
- 现有未加密 PCK 卡面 mod 可以作为输入导入，但只识别当前已知的 `MegaCrit.Sts2.Core.Models.Cards.*_card_art` 资源格式。
- 当前 runtime patch 使用 `NCard` 的私有字段 `_portrait` 和 `_ancientPortrait`。这是小范围反射，作用是设置 UI 节点贴图，不参与图片解码或资源扫描。
- PCK 体积取决于 Godot 纹理导入设置。当前已经改为每张胜出卡面只输出一份生成图，不再生成原路径、beta 镜像、JPG 诊断三份资源。
