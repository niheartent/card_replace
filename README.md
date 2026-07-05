# Card Replace

高性能《Slay the Spire 2》卡面替换生成工具。

它在游戏启动前读取目标文件夹中的资源，按优先级合并卡面，导入纹理，并输出一个可以直接安装的最终 mod。游戏运行时只加载生成好的 PCK。

## 功能

- 读取 Card Art Editor 格式的 `.cardartpack.json`。
- 支持 `.cardartpack.json` 内的 `animated_gif`，生成时转换为 Godot `AnimatedTexture`。
- 读取兼容的未加密卡面 mod 文件夹、`.zip`、`.pck`。
- 支持 Godot 导入纹理：`.import + .ctex`。
- 同名卡面按优先级合并，数字越大越优先。
- 自动构建 `card_replace.dll` 并输出完整 mod。
- RitsuLib 配置页显示本次生成结果，支持中文和英文。

当前不支持加密 PCK。

## 前置条件

- .NET 9 SDK。
- Godot 4.5.1 Mono console 版。
- 《Slay the Spire 2》。
- 游戏 mod 目录中已安装 `STS2-RitsuLib`，版本至少 `0.4.51`。

## 目标文件夹

目标文件夹里放要打包的资源，以及固定文件名 `priority.json`：

```text
<目标文件夹>/
  priority.json
  <卡面包>.cardartpack.json
  <兼容卡面mod>.zip
  <兼容卡面mod>.pck
```

`priority.json` 是 KV JSON。key 是目标文件夹内的相对路径，value 是优先级：

```json
{
  "<卡面包>.cardartpack.json": 100000,
  "<兼容卡面mod>.zip": 99999,
  "<兼容卡面mod>.pck": 99998
}
```

也可以写对象值：

```json
{
  "some-pack.zip": {
    "priority": 99990,
    "enabled": true,
    "id": "some_pack",
    "type": "zip"
  }
}
```

`type` 通常可以省略，工具会按路径自动判断。支持 `cardartpack`、`zip`、`pck`、`folder`。

## 生成

在仓库根目录执行：

```powershell
dotnet run --project .\tools\CardReplaceBuilder\CardReplaceBuilder.csproj -- `
  "<Godot 4.5.1 Mono console exe>" `
  "<目标文件夹>"
```

如果要合并多个目标文件夹：

```powershell
dotnet run --project .\tools\CardReplaceBuilder\CardReplaceBuilder.csproj -- `
  "<Godot 4.5.1 Mono console exe>" `
  "<目标文件夹1>" `
  "<目标文件夹2>"
```

生成结果会放到第一个目标文件夹下：

```text
<目标文件夹>/_generated/card_replace/
  card_replace.dll
  card_replace.json
  card_replace.pck
  manifest.final.cardreplace
  conflicts.report.cardreplace
```

## 安装

把生成的 `card_replace` 文件夹放入游戏的 `mods` 文件夹下。

最终目录结构：

```text
<Slay the Spire 2>/mods/card_replace/
  card_replace.dll
  card_replace.json
  card_replace.pck
  manifest.final.cardreplace
  conflicts.report.cardreplace
```

同时确认游戏的 `mods` 文件夹下已有 `STS2-RitsuLib`。

## 验证

启动游戏后，RitsuLib 配置页会显示 Card Replace 的生成信息。

日志位置：

```text
%APPDATA%\SlayTheSpire2\logs\godot.log
```

预期日志：

```text
card_replace: loaded generated pck
card_replace: loaded replacement map entries=<count>
card_replace: Harmony patches applied
card_replace: report pckLoaded=True
```

## 报告

- `manifest.final.cardreplace`：记录本次目标文件夹、优先级和最终生效的替换。
- `conflicts.report.cardreplace`：记录同名卡面的冲突、胜出项和被覆盖项。

---

# English

Card Replace is a high-performance card art replacement generator for Slay the Spire 2.

It reads resources from a target folder before the game starts, merges card art by priority, imports textures, and outputs a ready-to-install mod. At runtime, the game only loads the generated PCK.

## Features

- Reads Card Art Editor style `.cardartpack.json`.
- Supports `animated_gif` entries by converting them to Godot `AnimatedTexture` during generation.
- Reads compatible unencrypted card art mod folders, `.zip`, and `.pck`.
- Supports Godot imported textures: `.import + .ctex`.
- Resolves duplicate card art by priority. Higher numbers win.
- Builds `card_replace.dll` automatically and outputs the final mod.
- Shows generated pack information in the RitsuLib settings page, localized in English or Chinese.

Encrypted PCK files are not supported.

## Requirements

- .NET 9 SDK.
- Godot 4.5.1 Mono console build.
- Slay the Spire 2.
- `STS2-RitsuLib` installed in the game mod directory, version `0.4.51` or newer.

## Target Folder

The target folder contains resources and a fixed `priority.json` file:

```text
<target folder>/
  priority.json
  <card art pack>.cardartpack.json
  <compatible card art mod>.zip
  <compatible card art mod>.pck
```

`priority.json` is a KV JSON file. Keys are paths relative to the target folder. Values are priorities:

```json
{
  "<card art pack>.cardartpack.json": 100000,
  "<compatible card art mod>.zip": 99999,
  "<compatible card art mod>.pck": 99998
}
```

Object values are also supported:

```json
{
  "some-pack.zip": {
    "priority": 99990,
    "enabled": true,
    "id": "some_pack",
    "type": "zip"
  }
}
```

`type` is usually optional. Supported types are `cardartpack`, `zip`, `pck`, and `folder`.

## Generate

Run from the repository root:

```powershell
dotnet run --project .\tools\CardReplaceBuilder\CardReplaceBuilder.csproj -- `
  "<Godot 4.5.1 Mono console exe>" `
  "<target folder>"
```

To merge multiple target folders:

```powershell
dotnet run --project .\tools\CardReplaceBuilder\CardReplaceBuilder.csproj -- `
  "<Godot 4.5.1 Mono console exe>" `
  "<target folder 1>" `
  "<target folder 2>"
```

The generated mod is written under the first target folder:

```text
<target folder>/_generated/card_replace/
  card_replace.dll
  card_replace.json
  card_replace.pck
  manifest.final.cardreplace
  conflicts.report.cardreplace
```

## Install

Put the generated `card_replace` folder into the game's `mods` folder.

Final layout:

```text
<Slay the Spire 2>/mods/card_replace/
  card_replace.dll
  card_replace.json
  card_replace.pck
  manifest.final.cardreplace
  conflicts.report.cardreplace
```

Also make sure `STS2-RitsuLib` exists in the game's `mods` folder.

## Verify

The RitsuLib settings page should show Card Replace build information after the game starts.

Log path:

```text
%APPDATA%\SlayTheSpire2\logs\godot.log
```

Expected log lines:

```text
card_replace: loaded generated pck
card_replace: loaded replacement map entries=<count>
card_replace: Harmony patches applied
card_replace: report pckLoaded=True
```
