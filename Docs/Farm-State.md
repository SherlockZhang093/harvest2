# 农田统一状态 SO

实际资产：`Assets/HappyHarvest/Resources/FarmState.asset`。场景 `Farm_Outdoor` 的 TerrainManager 已引用它，包含全部 138 个农田格子，初始 89 株作物，缺水示例位于 `(9, -15)`。

## 查看数据

Unity 菜单 **Tools → Happy Harvest → Select Farm State**（Ctrl+Shift+F7）。Inspector 显示总数、缺水数、成熟数，并可筛选和点击每块地查看细节。展开“原始序列化数据”可在编辑模式修改初始状态。地块编号与坐标是定位标识，不要手工改成重复值。

进入 Play 后，查看的是游戏实际读写的同一个 SO。播种、浇水、施肥、除草、生长、收获和任务占用都会在此变化。退出 Play 自动恢复运行前的资产内容，避免试玩覆盖初始配置。SO 是运行中的数据源和初始资产；它本身不是发行版的磁盘存档机制。

## 每个地块的数据

| 内容 | 字段 |
| --- | --- |
| 定位 | Id、Cell、WorldCenter |
| 土地 | IsCultivable、IsTilled、WaterSeconds |
| 作物 | Crop.GrowingCrop、CurrentGrowthStage、GrowthRatio、GrowthTimer |
| 照料 / 收获 | DyingTimer、HarvestCount、IsFertilized、HasWeeds、WeedTimer |
| 任务占用 | ReservedBy、CurrentAction |
| 自动推导的状态 | HasCrop、IsWet、CanTill、CanPlant、CanWater、NeedsWater、NeedsFertilizer、NeedsWeeding、CanHarvest、IsReserved |

湿润、生长比例和可操作性由当前数据计算或维护，AI 不需要扫描场景物体。`CanWater` 表示干燥耕地可以被浇水，包括空地；`NeedsWater` 进一步要求地里有存活的作物。默认任务查询都会排除已有 NPC 占用的地块。施肥会加快生长，杂草会按时间出现并暂停作物生长，除草后计时重新开始。

## AI 读取与行为执行

Unity 内的 AI 可以直接引用此 SO：

```csharp
FarmState farm = GameManager.Instance.Terrain.State;
foreach (var plot in farm.GetPlotsNeedingWater())
    Debug.Log($"{plot.Id}: {plot.Crop.GrowingCrop.Key} 需要浇水");

var target = farm.GetPlot("Farm_Outdoor:9:-15:0");
// 规划器选定目标后，把动作交给已有执行器。
if (target != null && target.NeedsWater && !target.IsReserved)
    npc.RequestWater(target.Cell);

npc.RequestPlant(cell, crop);
npc.RequestFertilize(cell);
npc.RequestWeed(cell);
npc.RequestHarvest(cell);

string observation = farm.ToAiJson();
```

外部大模型应接收 `ToAiJson()` 的结构化快照；Unity 的 SO 对象不能直接通过网络传递。JSON 使用固定地块 ID 和作物 ID，没有 Unity 对象引用，包含 schemaVersion、版本、时间、地块状态与可用能力。`npcActions` 包含 `plant`、`water`、`fertilize`、`weed`、`harvest`。

Inspector 的导出按钮将当前快照写到 `Logs/farm-state-ai.json`。该文件是按需导出的快照，并非自动实时更新。还没有接入外部模型请求或自然语言规划。

写入仍通过游戏行为完成：`TillAt / PlantAt / TryWaterAt / HarvestAt`。它们同时更新 SO 和画面。NPC 到达后由真实出水动画事件修改水分；接受任务时预占地块，完成、失败、取消或禁用时释放。

## 初始化、时间与保存

- 场景原有 CropInitializer 的初始作物已迁入 SO；`HasAuthoredInitialState` 为 true 时不再二次播种。
- `TerrainManager` 的两个土地 / 作物状态字典已移除。SO 内索引仅指向同一批地块对象。
- 湿润时生长，干燥时累计枯死时间；暂停游戏时农田计时停止。时间步跨过干燥边界时，只有湿润的那部分时间计入生长。
- `TerrainManager.Save/Load` 使用独立的值快照，覆盖全部农田状态；加载时重新同步画面并清除旧任务占用。兼容模板旧的场景缓存格式。原模板的完整磁盘存档系统仍需单独完善。
- 当前绑定与初始资产针对 `Farm_Outdoor`。新增农田场景应配置它自己的 FarmState；扩展地图后需要同步新增地块的初始资产数据。

## 验证

在 Farm_Outdoor 的 Play 模式下运行 **Tools → Happy Harvest → Verify Farm State**（Ctrl+Shift+F9），覆盖地块完整性、统一引用、播种 / 缺水 / 浇水 / 生长 / 收获 / 枯死、时间边界、占用互斥、JSON 与保存恢复。检查结束恢复测试前的农田数据，结果写入 `Logs/farm-state-verification.txt`。

原有 **Verify NPC Watering**（Ctrl+Shift+F8）额外验证 NPC 占用与释放、浇水后 SO 状态同步，结果写入 `Logs/npc-watering-verification.txt`。应在新进入 Play、示例地尚未浇水时运行。

2026-09-09 实测：21 项农田状态检查和 19 项 NPC 检查全部通过。退出 Play 后重新导出 JSON，全部 138 个地块与运行前逐项一致，计时恢复为 0，初始缺水地块恢复为 `(9, -15)`。
