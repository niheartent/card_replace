# Card Replace

`Card Replace` 是一个面向《Slay the Spire 2》的卡图替换 Mod。

这个项目的核心目标很明确：

1. 支持按文件夹组织图片替换包。
2. 支持在 RitsuLib 配置界面中启用或禁用某个图片包。
3. 支持多个图片包替换同一张卡图时，通过优先级决定谁覆盖谁。
4. 最终支持 GIF 动图卡图替换。

当前已经有一个可运行的 demo，静态图片替换和 RitsuLib 配置已经打通。接下来要做的是把这条链路从“能跑”推进到“功能完整、结构清晰、便于扩展”。

## 当前状态

### 已完成

- [x] Godot C# Mod 工程可构建。
- [x] 接入 Harmony。
- [x] 接入 RitsuLib。
- [x] 自动扫描 `packs/*/pack.json`。
- [x] 自动读取和生成 `card_replace_config.json`。
- [x] 在 RitsuLib 配置界面中列出所有 pack。
- [x] 支持对每个 pack 单独设置 `Enabled` 和 `Priority`。
- [x] 支持静态卡图替换。
- [x] 支持同一 `source_path` 的覆盖冲突按优先级解决。
- [x] build 后自动复制到游戏 `mods/card_replace` 目录。

### 当前限制

- [ ] GIF 目前只会被识别和记录日志，尚未真正播放动画。
- [ ] 当前优先级冲突解决依赖数字大小，没有专门的冲突可视化界面。
- [ ] 当前替换逻辑基于 `source_path` 精确匹配，不做模糊名称匹配。
- [ ] 当前是 demo 结构，配置、调试、预览、验证能力还比较基础。

## 当前 demo 行为

当前 demo 内置了三个示例 pack：

- `demo_conflict_low`
  - 优先级 `50`
  - 替换 `res://images/packed/card_portraits/regent/strike_regent.png`
- `demo_static`
  - 优先级 `100`
  - 也替换 `res://images/packed/card_portraits/regent/strike_regent.png`
  - 因为优先级更高，所以会覆盖 `demo_conflict_low`
- `demo_gif`
  - 优先级 `200`
  - 指向 `res://images/packed/card_portraits/ironclad/rage.png`
  - 当前只会被扫描到，不会播放 GIF

也就是说，当前 demo 已经验证了两件关键的事：

1. pack 扫描和配置开关是通的。
2. 冲突优先级逻辑是通的。

## 项目结构

```text
card_replace/
  README.md
  card_replace.csproj
  card_replace.json
  card_replace_config.json
  packs/
    README.md
    demo_conflict_low/
    demo_static/
    demo_gif/
  Scripts/
    Entry.cs
    RitsuSettingsBridge.cs
    ArtPacks/
    Patches/
```

关键模块：

- `Scripts/Entry.cs`
  - Mod 初始化入口
  - 初始化 Harmony、读取配置、加载 pack、注册 RitsuLib 设置页
- `Scripts/ArtPacks/ArtPackRegistry.cs`
  - 扫描 `packs` 目录
  - 读取 `pack.json`
  - 生成当前有效替换列表
- `Scripts/ArtPacks/ArtPackConfigStore.cs`
  - 负责 `card_replace_config.json` 的读写
- `Scripts/ArtPacks/ArtReplacementService.cs`
  - 根据最终生效的替换表返回替换纹理
- `Scripts/Patches/CardModelPortraitPatch.cs`
  - Patch `CardModel.Portrait`，把静态卡图替换接进游戏流程
- `Scripts/RitsuSettingsBridge.cs`
  - 把 pack 开关和优先级注册到 RitsuLib 配置界面

## Pack 规范

每个 pack 都是 `packs` 目录下的一个直接子文件夹，必须包含 `pack.json`。

示例：

```json
{
  "id": "my_pack",
  "name": "My Pack",
  "enabledDefault": true,
  "priorityDefault": 100,
  "overrides": [
    {
      "source_path": "res://images/packed/card_portraits/regent/strike_regent.png",
      "type": "static",
      "file": "images/strike_regent.png"
    }
  ]
}
```

字段说明：

- `id`
  - pack 的稳定唯一标识
  - 会写入配置文件
- `name`
  - 在 RitsuLib 配置界面显示的名称
- `enabledDefault`
  - 首次生成配置时的默认启用状态
- `priorityDefault`
  - 首次生成配置时的默认优先级
- `overrides`
  - 替换条目列表

替换条目说明：

- `source_path`
  - 目标原图路径
  - 当前覆盖判定完全基于这个字段
- `type`
  - 当前已实现 `static`
  - `gif` 已被预留，但还没实现动画展示
- `file`
  - pack 内相对路径

## 配置文件格式

当前配置文件为 `card_replace_config.json`。

示例：

```json
{
  "packs": [
    {
      "id": "demo_conflict_low",
      "enabled": true,
      "priority": 50
    },
    {
      "id": "demo_static",
      "enabled": true,
      "priority": 100
    },
    {
      "id": "demo_gif",
      "enabled": true,
      "priority": 200
    }
  ]
}
```

规则：

- 同一个 pack 是否生效由 `enabled` 决定。
- 多个 pack 替换同一个 `source_path` 时，`priority` 更高者胜出。
- 当前的合并顺序是从低优先级到高优先级，后者覆盖前者。

## 构建与部署

当前工程配置为：

- Godot `4.5.1 mono`
- `net9.0`
- `STS2.RitsuLib 0.4.51`
- Slay the Spire 2 Mod 目录自动复制

构建命令：

```powershell
dotnet build .\card_replace.csproj -v:minimal
```

构建完成后会自动复制到游戏目录：

```text
<Slay the Spire 2>\mods\card_replace
```

同时，RitsuLib 运行时依赖也已经接入。

## 当前设计原则

为了后续扩展不把代码写成一团，这个项目暂时遵守下面几条原则：

1. 优先走显式 patch 和稳定 API，不靠大面积反射猜内部结构。
2. pack 资源组织和用户配置分离。
3. 冲突解决逻辑集中在 registry，不把优先级判断散落到 UI 或 patch 里。
4. 先把静态图链路打稳，再做 GIF。
5. GIF 的实现不能破坏当前静态图替换链路。

## 开发路线

下面是建议按阶段推进的路线。顺序是刻意排过的，先收口基础，再上 GIF，不然动画这块会把调试复杂度一下抬高。

### Phase 0: 基线 demo

目标：打通最小可用闭环。

状态：`已完成`

内容：

- pack 扫描
- 配置生成和保存
- RitsuLib 设置页
- 静态图替换
- 基础优先级冲突解决

验收标准：

- 能在游戏内看到 `Card Replace` 设置页
- 能独立开关各 pack
- 能通过调整优先级看到覆盖结果变化

### Phase 1: 收口和稳固基础

目标：把当前 demo 从“能跑”整理成“可以继续扩”的底座。

状态：`下一步建议立即做`

内容：

- [ ] 完善 README 和 pack 编写说明
- [ ] 补充配置和 manifest 校验日志
- [ ] 对非法路径、缺失资源、重复 `id` 给出明确错误
- [ ] 增加冲突日志的可读性
- [ ] 明确当前支持的静态图片格式

建议输出：

- 一个稳定的 pack 编写规范
- 一个更可信的日志层，方便后面做 GIF 调试

验收标准：

- pack 写错时，日志能明确指出是哪一个 pack、哪一条 override、哪一个字段有问题
- 新 pack 作者不需要读源码就能照 README 写出一个可用 pack

### Phase 2: GIF 支持

目标：让 `type: "gif"` 真正变成可显示动画的卡图替换能力。

状态：`核心待实现`

这是当前最重要的未完成功能，但实现时要注意一个事实：

当前 patch 点是 `CardModel.Portrait`，它返回的是 `Texture2D`。这条链路适合静态图，但不天然适合直接承载 GIF 动画。因此 GIF 很可能不能只靠当前 getter patch 完成，需要改到“显示层”。

建议技术路线：

1. 保留当前静态图替换路径，继续让 `static` 稳定工作。
2. 为 `gif` 条目增加单独的运行时处理器。
3. 研究卡牌节点实际展示卡图的位置，在显示层挂接动画控制。
4. 对 GIF 至少准备一个静态首帧回退，避免动画失败时整张卡变空白。
5. 做资源缓存，避免同一张 GIF 被重复解码。

建议拆解任务：

- [ ] 明确 GIF 展示要 patch 的最终节点层
- [ ] 选择 GIF 解码/播放方案
- [ ] 建立 GIF 缓存
- [ ] 在手牌、奖励、牌组浏览等主要场景验证显示
- [ ] 增加 GIF 失败时的回退策略

验收标准：

- `type: "gif"` 的卡图在至少一个主要场景中能稳定播放
- 同场景多张相同卡牌不会明显卡顿或疯狂重复解码
- 动画失败时仍能显示一个静态回退结果

### Phase 3: 冲突管理增强

目标：让“谁覆盖谁”这件事从可用变成清晰。

状态：`GIF 之后优先做`

内容：

- [ ] 在设置页展示每个 pack 的替换数量
- [ ] 展示哪些 `source_path` 存在冲突
- [ ] 提供更直观的优先级说明
- [ ] 视情况考虑从“纯数字优先级”升级到“可排序列表”

说明：

当前设计已经能解决冲突，但用户只能靠数字猜。后面如果 pack 多起来，这会很难管理。

验收标准：

- 用户能直接看出某张卡图当前由哪个 pack 生效
- 用户不需要靠反复试错理解优先级关系

### Phase 4: 作者体验和调试能力

目标：让这个 Mod 不只是你自己能用，而是别人也能写 pack。

状态：`中后期`

内容：

- [ ] 增加更多示例 pack
- [ ] 增加 pack 模板
- [ ] 增加开发模式日志
- [ ] 视情况加入资源重载或快速刷新能力
- [ ] 补充 workshop 发布准备

验收标准：

- 新 pack 制作者可以照模板快速产出一个 pack
- 问题出现时能从日志快速定位

## 推荐的近期任务顺序

如果按最稳的节奏推进，建议下一步按这个顺序做：

1. 收口 README 和 pack 规范
2. 增加 manifest 与资源校验
3. 明确 GIF 最终 patch/display 方案
4. 做 GIF 最小可用版本
5. 做冲突可视化和设置页增强

这条顺序的好处很简单：先把地板铺平，再去搬最重的家具。

## 当前非目标

为了保持范围清晰，下面这些东西暂时不作为当前阶段目标：

- 非卡牌图片的大范围替换
- 通用资源浏览器
- 自动识别游戏内所有资源路径
- 无约束的模糊匹配替换
- 一上来就做复杂编辑器

## 之后实现时要注意的风险

### 1. GIF 不适合硬塞进当前 `Texture2D` 返回链路

这不是说做不到，而是强行塞进去大概率会让结构变脆。更合理的做法是把 GIF 和静态图分层处理。

### 2. 多场景显示路径可能不同

手牌、奖励、图鉴、卡组浏览的卡图节点不一定完全共用一套显示流程。GIF 方案需要至少验证几个主要场景。

### 3. 资源缓存要早做

如果 GIF 每次出现都重新解码，性能和内存都会很快变丑。

### 4. 配置更新后的热刷新要谨慎

现在配置变更后会重建替换表，这对静态图已经够用。GIF 接进来之后，热刷新还要考虑旧动画实例的清理。

## 一句话总结

这个项目现在已经有了一个靠谱的底座：

- 静态图替换能跑
- pack 扫描能跑
- RitsuLib 配置能跑
- 优先级冲突能跑

接下来的主线任务就是：把 GIF 真正做出来，然后把冲突管理和作者体验补齐。
