# AI NPC 自动农务原型

## 使用

1. 打开 `Assets/HappyHarvest/Scenes/Farm_Outdoor.unity`，进入 Play Mode。
2. 左上方出现“AI NPC 自动农务”。NPC 直接读取统一 FarmState SO，无需点击按钮。
3. NPC 按“收获 → 除草 → 浇水 → 施肥 → 播种”的优先级选择最近的可达任务，自主寻路到相邻地格，面向目标，播放对应动画并写回 SO。摄像机跟随角色。
4. 也可左键点击其他已翻过的耕地切换目标。黄色框表示干燥，青色表示湿润。
5. “立即扫描”可要求 NPC 立刻重新检查；“取消”或 Esc 可中断当前任务。已经实际浇下去的水不会撤销，取消后稍后仍会重新发现缺水地块。

键盘移动、手动用工具、工具切换默认关闭，底部工具栏隐藏。当前已经具备完整农务闭环；自然语言层以后只需把用户意图转成任务开关、优先级或指定目标。

## 行为边界

- 每种动作都在执行前检查 SO 条件：空耕地才能播种、缺水作物才能浇水、未施肥作物才能施肥、有杂草才能除草、成熟作物才能收获。
- 四方向网格寻路检查角色身体尺寸、场景碰撞层以及两格之间的扫掠碰撞。
- 到达相邻格后才能执行。浇水沿用水壶原有 VFX 动画事件；其余动作沿用模板已有的播种、挥工具和采摘角色动画。
- 临时障碍导致停止并给出原因，移开障碍后可重新点击按钮。目前不自动重规划。
- 沿用模板的无限水壶，无水量扣除。土地湿润持续时间沿用 60 秒。
- 初始试验田已保存在 FarmState SO 中；运行时的地块变化在退出 Play 后恢复，控制面板仅存在于运行期间。

## 代码入口

- `NpcWateringDemo`：农场场景启动、按钮、选地块和标记。
- `NpcWateringAgent`：提供 `RequestPlant / RequestWater / RequestFertilize / RequestWeed / RequestHarvest`，负责寻路、动画、执行和反馈。
- `NpcFarmWorker`：周期读取 SO 中未占用的全部农务需求，按优先级与距离选择目标，失败目标延迟重试，完成后继续扫描；五类任务可独立启停。
- `NpcWateringAgent.State / Status / TaskFinished`：运行、成功、失败和取消反馈。
- `FarmPathfinder`：四方向网格路径与碰撞检查。
- `TerrainManager.NeedsWater / TryWaterAt`：查询和验证后修改土地状态。
- `TerrainManager.State`：实际共享的农田 SO，保存地块、作物、水分和任务占用。详见 [Farm-State.md](Farm-State.md)。
- `ToolAnimationEventHandler`：在真实出水事件中通知 NPC 应用浇水。
- `PlayerController`：保留摄像机、物品数据等模板兼容接口，默认 `ManualControlEnabled=false`。

## 验证

在刚进入农场且尚未执行浇水时，使用 Unity 菜单 `Tools > Happy Harvest > Verify NPC Watering`（Ctrl+Shift+F8）。
测试在实际 Play Mode 场景中执行，临时创建障碍并清理，不保存场景。结果写入 `Logs/npc-watering-verification.txt`。
覆盖无效目标、重复任务、禁止远程生效、取消、围堵目标、障碍移除后重试、真实移动、动画出水，以及播种、施肥、除草、收获对 SO 的实际修改。

验证脚本会在实际场景内依次执行五种农务，并把每项结果写入日志。
