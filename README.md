# Harvest2

基于 Unity 和 Happy Harvest 项目内容扩展的 2D 像素风农场原型，当前重点是统一农田状态与 NPC 自动农务：让角色读取地块需求，自主移动并完成播种、浇水、施肥、除草和收获。

## 当前功能

- **农场模拟**：耕地、作物生长与照料，以及日夜、天气、物品和库存相关系统。
- **统一农田状态**：通过 `FarmState` ScriptableObject 管理地块、作物、水分和任务占用，供场景表现与 NPC 共用。
- **NPC 自动农务**：根据需求、优先级和距离选择任务，执行网格寻路、动作动画与状态更新。
- **状态检查与导出**：在 Unity Inspector 中查看农田数据，并导出供外部规划器使用的 JSON 快照。
- **编辑器验证工具**：检查农田状态同步和 NPC 行为执行。

项目仍在开发中。当前自动农务基于规则与状态查询；外部大模型请求和自然语言规划尚未接入。

## 开发环境

| 组件 | 项目版本 |
| --- | --- |
| Unity Editor | `2022.3.62f3c1` |
| Universal Render Pipeline | `14.0.12` |
| Input System | `1.14.0` |
| Cinemachine | `2.10.3` |

编辑器版本以 [ProjectVersion.txt](ProjectSettings/ProjectVersion.txt) 为准，依赖配置见 [manifest.json](Packages/manifest.json)。

## 快速开始

1. 克隆仓库：

   ```bash
   git clone https://github.com/SherlockZhang093/harvest2.git
   ```

2. 在 Unity Hub 中添加克隆后的 `harvest2` 文件夹，使用上述编辑器版本打开。
3. 等待 Unity 完成依赖解析与资源导入。首次打开需要重建 `Library` 缓存。
4. 打开 `Assets/HappyHarvest/Scenes/Farm_Outdoor.unity`，点击 **Play**，体验 NPC 自动农务。

农务演示默认关闭键盘移动、手动工具操作和工具切换，由 NPC 执行任务。具体交互与行为边界见 [NPC 自动农务说明](Docs/NPC-Watering.md)。

如需从菜单体验项目，可打开 `Assets/HappyHarvest/Scenes/MainMenu.unity`。构建场景顺序已保存在 `ProjectSettings/EditorBuildSettings.asset` 中。

## 项目结构

```text
Assets/HappyHarvest/
├── Scenes/          # 主菜单、加载、室外农场与室内场景
├── Scripts/         # 农场逻辑、NPC、物品、UI 等
├── Resources/       # GameManager、FarmState 等运行资源
├── Settings/        # 输入等配置
└── Manuals/         # 作物与物品配置说明
Docs/               # 农田状态与 NPC 开发文档
Packages/           # Unity 包依赖
ProjectSettings/    # Unity 项目设置
```

## 农田状态与 NPC

初始农田资产位于 `Assets/HappyHarvest/Resources/FarmState.asset`。在 Unity 中使用 **Tools > Happy Harvest > Select Farm State** 可查看地块状态。

NPC 读取共享农田状态，选择任务并预占目标地块，再移动到可执行位置完成动作。任务结束、失败或取消后释放占用。运行时的农田变化在退出 Play 模式后恢复，避免试玩覆盖初始资产；该资产本身不是完整的磁盘存档系统。

详细说明：

- [农田统一状态与 JSON 接口](Docs/Farm-State.md)
- [NPC 自动农务、执行接口与行为边界](Docs/NPC-Watering.md)
- [作物配置](Assets/HappyHarvest/Manuals/Crops.md)
- [物品配置](Assets/HappyHarvest/Manuals/Items.md)

## 验证

在 `Farm_Outdoor` 场景进入 Play 模式后，可运行以下编辑器菜单：

| 菜单 | 检查内容 | 输出 |
| --- | --- | --- |
| Tools > Happy Harvest > Verify Farm State | 地块状态、农务变化、占用、JSON 与保存恢复 | `Logs/farm-state-verification.txt` |
| Tools > Happy Harvest > Verify NPC Watering | NPC 移动、动作执行、取消、障碍与状态同步 | `Logs/npc-watering-verification.txt` |

运行条件和具体覆盖范围请参阅上述开发文档。

## 版本管理

提交项目资源时应保留对应的 `.meta` 文件，以维持 Unity 资源引用。仓库的 `.gitignore` 已排除 `Library`、`Temp`、`Logs`、`UserSettings` 和 IDE 自动生成文件。
