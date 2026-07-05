# Card Replace

Card Replace is a high-performance card art replacement generator for Slay the Spire 2.

It is not an in-game editor. Instead, it merges card art sources before the game starts and outputs a ready-to-use mod:

```text
dist/card_replace/
  card_replace.dll
  card_replace.json
  card_replace.pck
  manifest.final.cardreplace
  conflicts.report.cardreplace
```

After placing `dist/card_replace` into the game's `mods/card_replace` directory, the game only loads the generated PCK textures at startup. Runtime does not read `.cardartpack.json`, scan external image folders, or dynamically decode images.

## Features

- Reads Card Art Editor style `.cardartpack.json` files.
- Supports `animated_gif` entries inside `.cardartpack.json`; frames are converted to Godot `AnimatedTexture` during build.
- Reads compatible unencrypted card art replacement mod folders, `.pck` files, and `.zip` files.
- Supports Godot imported texture assets: `.import + .ctex`.
- Supports some existing PCK mods that include `card_replacements.json`.
- Merges duplicate card art by `priority`; higher numbers win.
- Writes a final manifest and a conflict report. The RitsuLib settings page shows the generated pack status in English or Chinese based on the game language.

Encrypted PCK files are not supported.

## Configuration

Copy:

```text
build_config.example.json
```

to:

```text
build_config.json
```

Then edit local paths and input sources:

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
      "id": "some_existing_mod_zip",
      "path": "D:\\path\\to\\2-existing-card-art-mod.zip",
      "enabled": true,
      "priority": 99998,
      "type": "zip"
    },
    {
      "id": "some_existing_mod_folder",
      "path": "D:\\steam\\steamapps\\common\\Slay the Spire 2\\mods\\Some Card Art Mod",
      "enabled": true,
      "priority": 99997,
      "type": "folder"
    }
  ]
}
```

Supported `type` values:

```text
cardartpack
zip
pck
folder
```

`type` can be omitted when the Builder can infer it from the path.

## Priority

`priority` is higher-wins. When multiple inputs replace the same card, the input with the highest priority wins.

You can keep a human-readable priority file like this:

```json
{
  "D:\\workspace\\slaythespire2\\mods\\resources\\cardartpack\\pack1\\1-pack.cardartpack.json": 99999,
  "D:\\workspace\\slaythespire2\\mods\\resources\\cardartpack\\pack1\\2-pack.zip": 99998,
  "D:\\workspace\\slaythespire2\\mods\\resources\\cardartpack\\pack1\\3-pack.cardartpack.json": 99997
}
```

This KV file is useful as a readable priority table. The actual build still uses `build_config.json`.

## Build

Run from the repository root:

```powershell
cd D:\workspace\slaythespire2\mods\card_replace
dotnet build .\card_replace.csproj -v:minimal -p:SkipModDeploy=true
dotnet run --project .\tools\CardReplaceBuilder\CardReplaceBuilder.csproj -- .\build_config.json
```

The generated mod is written to:

```text
dist/card_replace
```

## Install

Close the game, then copy the generated mod folder into the game mod directory:

```powershell
$src = "D:\workspace\slaythespire2\mods\card_replace\dist\card_replace"
$dst = "D:\steam\steamapps\common\Slay the Spire 2\mods\card_replace"

if (Test-Path $dst) {
  Remove-Item -LiteralPath $dst -Recurse -Force
}

New-Item -ItemType Directory -Path $dst | Out-Null
Copy-Item -LiteralPath (Join-Path $src "card_replace.dll") -Destination $dst -Force
Copy-Item -LiteralPath (Join-Path $src "card_replace.json") -Destination $dst -Force
Copy-Item -LiteralPath (Join-Path $src "card_replace.pck") -Destination $dst -Force
Copy-Item -LiteralPath (Join-Path $src "manifest.final.cardreplace") -Destination $dst -Force
Copy-Item -LiteralPath (Join-Path $src "conflicts.report.cardreplace") -Destination $dst -Force
```

Expected log lines:

```text
card_replace: loaded generated pck
card_replace: loaded replacement map entries=<count>
card_replace: Harmony patches applied
card_replace: report pckLoaded=True
```

Log path:

```text
C:\Users\<user>\AppData\Roaming\SlayTheSpire2\logs\godot.log
```

## Reports

The build writes:

```text
manifest.final.cardreplace
conflicts.report.cardreplace
```

`manifest.final.cardreplace` records final winning replacements and input pack metadata.

`conflicts.report.cardreplace` records duplicate-card conflicts, winners, and overridden sources.

---

# Card Replace 中文说明

Card Replace 是一个面向《Slay the Spire 2》的高性能卡面替换生成工具。

它不是游戏内编辑器，而是在游戏启动前把多个卡面来源合并成一个最终 mod：

```text
dist/card_replace/
  card_replace.dll
  card_replace.json
  card_replace.pck
  manifest.final.cardreplace
  conflicts.report.cardreplace
```

把生成后的 `dist/card_replace` 放到游戏的 `mods/card_replace` 目录后，游戏启动时只加载已经打包好的 PCK 纹理。运行时不会读取 `.cardartpack.json`，不会扫描外部图片目录，也不会动态解码图片。

## 功能

- 读取 Card Art Editor 风格的 `.cardartpack.json`。
- 支持 `.cardartpack.json` 内的 `animated_gif`，构建期会转成 Godot `AnimatedTexture`。
- 读取兼容的未加密卡面替换 mod 文件夹、`.pck` 或 `.zip`。
- 支持 Godot 已导入纹理资源：`.import + .ctex`。
- 支持部分现有 PCK 内自带的 `card_replacements.json`。
- 按 `priority` 合并同名卡面，数字越大优先级越高。
- 输出最终 manifest 和冲突报告。RitsuLib 配置页会根据游戏语言显示中文或英文。

当前不支持加密 PCK。

## 配置

复制：

```text
build_config.example.json
```

为：

```text
build_config.json
```

然后修改本机路径和输入列表：

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
      "id": "some_existing_mod_zip",
      "path": "D:\\path\\to\\2-existing-card-art-mod.zip",
      "enabled": true,
      "priority": 99998,
      "type": "zip"
    },
    {
      "id": "some_existing_mod_folder",
      "path": "D:\\steam\\steamapps\\common\\Slay the Spire 2\\mods\\某个卡面mod",
      "enabled": true,
      "priority": 99997,
      "type": "folder"
    }
  ]
}
```

`type` 可选值：

```text
cardartpack
zip
pck
folder
```

也可以省略 `type`，Builder 会按路径自动判断。

## 优先级

`priority` 数字越大优先级越高。多个输入替换同一张卡时，高优先级胜出。

如果用文件名前缀排序，可以这样写：

```json
{
  "D:\\workspace\\slaythespire2\\mods\\resources\\cardartpack\\pack1\\1-pack.cardartpack.json": 99999,
  "D:\\workspace\\slaythespire2\\mods\\resources\\cardartpack\\pack1\\2-pack.zip": 99998,
  "D:\\workspace\\slaythespire2\\mods\\resources\\cardartpack\\pack1\\3-pack.cardartpack.json": 99997
}
```

这个 KV 文件适合做人看的优先级表；实际构建仍以 `build_config.json` 为准。

## 生成

在仓库根目录执行：

```powershell
cd D:\workspace\slaythespire2\mods\card_replace
dotnet build .\card_replace.csproj -v:minimal -p:SkipModDeploy=true
dotnet run --project .\tools\CardReplaceBuilder\CardReplaceBuilder.csproj -- .\build_config.json
```

成功后会输出：

```text
dist/card_replace
```

## 安装

关闭游戏，然后复制生成结果到游戏 mod 目录：

```powershell
$src = "D:\workspace\slaythespire2\mods\card_replace\dist\card_replace"
$dst = "D:\steam\steamapps\common\Slay the Spire 2\mods\card_replace"

if (Test-Path $dst) {
  Remove-Item -LiteralPath $dst -Recurse -Force
}

New-Item -ItemType Directory -Path $dst | Out-Null
Copy-Item -LiteralPath (Join-Path $src "card_replace.dll") -Destination $dst -Force
Copy-Item -LiteralPath (Join-Path $src "card_replace.json") -Destination $dst -Force
Copy-Item -LiteralPath (Join-Path $src "card_replace.pck") -Destination $dst -Force
Copy-Item -LiteralPath (Join-Path $src "manifest.final.cardreplace") -Destination $dst -Force
Copy-Item -LiteralPath (Join-Path $src "conflicts.report.cardreplace") -Destination $dst -Force
```

重启游戏后，在日志里应看到：

```text
card_replace: loaded generated pck
card_replace: loaded replacement map entries=<count>
card_replace: Harmony patches applied
card_replace: report pckLoaded=True
```

日志位置：

```text
C:\Users\<user>\AppData\Roaming\SlayTheSpire2\logs\godot.log
```

## 输出报告

生成后会得到两个报告：

```text
manifest.final.cardreplace
conflicts.report.cardreplace
```

`manifest.final.cardreplace` 记录最终胜出的卡面和输入包信息。

`conflicts.report.cardreplace` 记录同名卡冲突、胜出来源和被覆盖来源。
