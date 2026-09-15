using System;
using System.Collections.Generic;
using Template2DCommon;
using UnityEngine;

namespace HappyHarvest
{
    [RequireComponent(typeof(Rigidbody2D))]
    public sealed class NpcWateringAgent : MonoBehaviour
    {
        public Transform ItemAttachBone;
        public WaterCan WateringCan;
        public float Speed = 3f;
        public enum FarmAction { Water, Plant, Fertilize, Weed, Harvest }
        public enum TaskState { Idle, Moving, Watering, Performing, Succeeded, Failed, Cancelled }
        public TaskState State { get; private set; }
        public FarmAction CurrentAction { get; private set; }
        public string Status { get; private set; } = "等待农务任务";
        public bool IsBusy => State == TaskState.Moving || State == TaskState.Watering || State == TaskState.Performing;
        public Vector3Int TargetCell { get; private set; }
        public event Action<TaskState, string> TaskFinished;

        Rigidbody2D body;
        Animator characterAnimator;
        Animator toolAnimator;
        TerrainManager terrain;
        FarmPathfinder navigation;
        List<Vector2> path;
        int waypoint;
        float taskTime;
        float animationTime;
        float stuckTime;
        Vector2 lastPosition;
        Vector2 facing = Vector2.down;
        bool applied;
        Crop plantingCrop;
        InventorySystem inventory;

        void Awake()
        {
            body = GetComponent<Rigidbody2D>();
            characterAnimator = GetComponentInChildren<Animator>();
            var oldMarker = transform.Find("UI Target");
            if (oldMarker != null) oldMarker.gameObject.SetActive(false);
            SetMotion(Vector2.zero);
        }

        public void Initialize(TerrainManager farm)
        {
            terrain = farm;
            var avatar = GetComponent<PlayerController>();
            inventory = avatar?.Inventory;
            if (ItemAttachBone == null && avatar != null) ItemAttachBone = avatar.ItemAttachBone;
            if (WateringCan == null) WateringCan = GameManager.Instance.ItemDatabase.GetFromID("water_can") as WaterCan;
            if (toolAnimator == null && WateringCan != null && ItemAttachBone != null)
            {
                var tool = Instantiate(WateringCan.VisualPrefab, ItemAttachBone, false);
                toolAnimator = tool.GetComponentInChildren<Animator>();
            }
            var collider = GetComponentInChildren<CircleCollider2D>();
            var offset = (Vector2)(collider.transform.TransformPoint(collider.offset) - transform.position);
            var radius = collider.radius * Mathf.Max(Mathf.Abs(collider.transform.lossyScale.x), Mathf.Abs(collider.transform.lossyScale.y));
            navigation = new FarmPathfinder(farm, transform, offset, radius,
                Physics2D.GetLayerCollisionMask(collider.gameObject.layer));
        }

        public bool TryPlaceNear(Vector3 origin)
        {
            var start = terrain.Grid.WorldToCell(origin);
            start.z = 0;
            for (int range = 1; range <= 8; range++)
                for (int x = -range; x <= range; x++)
                    for (int y = -range; y <= range; y++)
                    {
                        if (Mathf.Abs(x) != range && Mathf.Abs(y) != range) continue;
                        var cell = start + new Vector3Int(x, y, 0);
                        if (!navigation.CanStand(cell)) continue;
                        body.position = navigation.Center(cell);
                        transform.position = body.position;
                        Physics2D.SyncTransforms();
                        return true;
                    }
            return false;
        }

        public bool TryBuildTravelPath(Vector2 worldTarget, out List<Vector2> travelPath)
        {
            travelPath = null;
            if (terrain == null || navigation == null) return false;
            var targetCell = terrain.Grid.WorldToCell(worldTarget);
            targetCell.z = 0;
            return navigation.TryFind(body.position, targetCell, out travelPath);
        }

        public void MoveForTravel(Vector2 nextPosition)
        {
            var direction = nextPosition - body.position;
            SetMotion(direction);
            body.MovePosition(nextPosition);
        }

        public void StopTravelMotion()
        {
            body.velocity = Vector2.zero;
            SetMotion(Vector2.zero);
        }

        // Shared entry point for the button and future planners. A busy task cannot be overwritten.
        public bool RequestWater(Vector3Int target)
        {
            return RequestAction(FarmAction.Water, target, null);
        }

        public bool RequestPlant(Vector3Int target, Crop crop) => RequestAction(FarmAction.Plant, target, crop);
        public bool RequestFertilize(Vector3Int target) => RequestAction(FarmAction.Fertilize, target, null);
        public bool RequestWeed(Vector3Int target) => RequestAction(FarmAction.Weed, target, null);
        public bool RequestHarvest(Vector3Int target) => RequestAction(FarmAction.Harvest, target, null);

        bool RequestAction(FarmAction action, Vector3Int target, Crop crop)
        {
            if (IsBusy) return false;
            TargetCell = target;
            if (terrain == null || navigation == null || characterAnimator == null)
                return Reject("NPC 或农田尚未准备好");
            var plot = terrain.State.GetPlot(target);
            if (plot == null) return Reject("目标不在农田数据中");
            if (action == FarmAction.Water && (!plot.NeedsWater || toolAnimator == null)) return Reject("目标作物不需要浇水");
            if (action == FarmAction.Plant && (!plot.CanPlant || crop == null)) return Reject("目标不能播种");
            if (action == FarmAction.Plant && !HasSeedFor(crop))
                return Reject("背包里没有对应的种子，无法播种");
            if (action == FarmAction.Fertilize && !plot.NeedsFertilizer) return Reject("目标不需要施肥");
            if (action == FarmAction.Fertilize && (inventory == null || inventory.GetItemCount<Fertilizer>() <= 0))
                return Reject("背包里没有肥料，无法施肥");
            if (action == FarmAction.Weed && !plot.NeedsWeeding) return Reject("目标没有杂草");
            if (action == FarmAction.Harvest && !plot.CanHarvest) return Reject("目标还不能收获");
            Physics2D.SyncTransforms();
            if (!navigation.TryFind(body.position, target, out path))
                return Reject("无法到达这块地，请选择其他地块或移开障碍");
            string actionName = ActionName(action);
            if (!terrain.State.TryReserve(target, AgentId, actionName))
                return Reject("这块地已有 NPC 正在处理");
            CurrentAction = action;
            plantingCrop = crop;
            waypoint = 0;
            taskTime = stuckTime = animationTime = 0;
            applied = false;
            lastPosition = body.position;
            State = TaskState.Moving;
            Status = $"正在前往{ActionLabel(action)}目标";
            return true;
        }

        static string ActionName(FarmAction action) => action.ToString().ToLowerInvariant();
        static string ActionLabel(FarmAction action) => action switch
        {
            FarmAction.Water => "浇水", FarmAction.Plant => "播种", FarmAction.Fertilize => "施肥",
            FarmAction.Weed => "除草", FarmAction.Harvest => "收获", _ => "工作"
        };

        bool Reject(string reason)
        {
            Finish(TaskState.Failed, reason);
            return false;
        }

        public string AgentId => "npc:" + GetInstanceID();

        void FixedUpdate()
        {
            if (!IsBusy) return;
            if (terrain == null) { Finish(TaskState.Failed, "农田已离开，任务结束"); return; }
            if (!GameManager.Instance.IsTicking) { SetMotion(Vector2.zero); return; }
            taskTime += Time.fixedDeltaTime;
            if (taskTime > 60f) { Finish(TaskState.Failed, "移动超时，请重新选择目标"); return; }
            if (State == TaskState.Watering)
            {
                animationTime += Time.fixedDeltaTime;
                if (animationTime >= 1.6f && applied) Finish(TaskState.Succeeded, "浇水完成，土地已湿润");
                else if (animationTime > 4f) Finish(TaskState.Failed, "出水动画未触发，土地未被修改");
                return;
            }
            if (State == TaskState.Performing)
            {
                animationTime += Time.fixedDeltaTime;
                if (animationTime >= .25f && !applied) ApplyCurrentAction();
                if (animationTime >= .7f)
                    Finish(applied ? TaskState.Succeeded : TaskState.Failed,
                        applied ? ActionLabel(CurrentAction) + "完成" : ActionLabel(CurrentAction) + "未生效");
                return;
            }
            if (!IsCurrentActionStillNeeded())
            {
                Finish(TaskState.Cancelled, "目标状态已变化，停止当前工作");
                return;
            }
            if (Vector2.Distance(body.position, path[waypoint]) < 0.035f)
            {
                waypoint++;
                if (waypoint >= path.Count)
                {
                    BeginAction();
                    return;
                }
            }
            var next = Vector2.MoveTowards(body.position, path[waypoint], Speed * Time.fixedDeltaTime);
            if (!navigation.CanMove(body.position, next))
            {
                Finish(TaskState.Failed, "道路被挡住，已停止；移开障碍后可重试");
                return;
            }
            stuckTime = Vector2.Distance(body.position, lastPosition) < 0.001f ? stuckTime + Time.fixedDeltaTime : 0;
            lastPosition = body.position;
            if (stuckTime > 2f) { Finish(TaskState.Failed, "NPC 无法继续移动"); return; }
            SetMotion(next - body.position);
            body.MovePosition(next);
        }

        bool InReach()
        {
            var cell = terrain.Grid.WorldToCell(body.position);
            cell.z = 0;
            var delta = cell - TargetCell;
            return Mathf.Abs(delta.x) + Mathf.Abs(delta.y) == 1 &&
                   navigation.CanMove(body.position, navigation.Center(cell));
        }

        bool IsCurrentActionStillNeeded()
        {
            var plot = terrain.State.GetPlot(TargetCell);
            if (plot == null) return false;
            return CurrentAction switch
            {
                FarmAction.Water => plot.NeedsWater, FarmAction.Plant => plot.CanPlant,
                FarmAction.Fertilize => plot.NeedsFertilizer, FarmAction.Weed => plot.NeedsWeeding,
                FarmAction.Harvest => plot.CanHarvest, _ => false
            };
        }

        void BeginAction()
        {
            if (!InReach()) { Finish(TaskState.Failed, "尚未到达可工作的位置"); return; }
            SetMotion(navigation.Center(TargetCell) - body.position);
            SetMotion(Vector2.zero);
            animationTime = 0;
            if (CurrentAction != FarmAction.Water)
            {
                State = TaskState.Performing;
                Status = "正在" + ActionLabel(CurrentAction);
                string trigger = CurrentAction == FarmAction.Plant || CurrentAction == FarmAction.Fertilize
                    ? "Planting" : CurrentAction == FarmAction.Harvest ? "Picking" : "ToolSwing";
                characterAnimator.SetTrigger(trigger);
                return;
            }
            State = TaskState.Watering;
            Status = "正在浇水";
            characterAnimator.SetTrigger(WateringCan.PlayerAnimatorTriggerUse);
            // The template hides the hand/tool bone in its idle pose.
            // Enable the hierarchy before setting the tool animator's parameters.
            for (var current = toolAnimator.transform; current != null && current != transform; current = current.parent)
                current.gameObject.SetActive(true);
            toolAnimator.SetFloat("DirX", facing.x);
            toolAnimator.SetFloat("DirY", facing.y);
            toolAnimator.SetTrigger("Use");
        }

        void ApplyCurrentAction()
        {
            if (State != TaskState.Performing || applied || !InReach()) return;
            switch (CurrentAction)
            {
                case FarmAction.Plant:
                    if (!terrain.IsPlantable(TargetCell)) return;
                    if (!TryConsumeSeed(plantingCrop, out var consumedSeed))
                    {
                        Finish(TaskState.Failed, "对应种子已用完，播种未生效");
                        return;
                    }
                    terrain.PlantAt(TargetCell, plantingCrop);
                    applied = terrain.GetCropDataAt(TargetCell)?.GrowingCrop == plantingCrop;
                    if (!applied)
                        inventory.AddItem(consumedSeed);
                    break;
                case FarmAction.Fertilize:
                    if (inventory == null || inventory.GetItemCount<Fertilizer>() <= 0)
                    {
                        Finish(TaskState.Failed, "肥料已用完，施肥未生效");
                        return;
                    }
                    if (!terrain.TryFertilizeAt(TargetCell)) return;
                    try
                    {
                        applied = inventory.TryRemoveItem<Fertilizer>(1);
                    }
                    catch (Exception error)
                    {
                        Debug.LogException(error, this);
                        applied = false;
                    }
                    if (!applied)
                    {
                        var plot = terrain.State.GetPlot(TargetCell);
                        if (plot != null)
                        {
                            plot.IsFertilized = false;
                            terrain.State.MarkChanged();
                        }
                        Finish(TaskState.Failed, "肥料扣除失败，已撤销施肥效果");
                    }
                    break;
                case FarmAction.Weed: applied = terrain.TryRemoveWeedsAt(TargetCell); break;
                case FarmAction.Harvest:
                    var crop = terrain.GetCropDataAt(TargetCell)?.GrowingCrop;
                    if (crop == null || GameManager.Instance.Player == null ||
                        !GameManager.Instance.Player.CanFitInInventory(crop.Produce, crop.ProductPerHarvest)) return;
                    var harvested = terrain.HarvestAt(TargetCell);
                    if (harvested == null) return;
                    for (int i = 0; i < harvested.ProductPerHarvest; i++)
                        GameManager.Instance.Player.AddItem(harvested.Produce);
                    applied = true;
                    break;
            }
        }

        bool HasSeedFor(Crop crop)
        {
            if (inventory == null || crop == null) return false;
            foreach (var entry in inventory.Entries)
                if (entry.Item is SeedBag seed && seed.PlantedCrop == crop && entry.StackSize > 0)
                    return true;
            return false;
        }

        bool TryConsumeSeed(Crop crop, out SeedBag consumedSeed)
        {
            consumedSeed = null;
            if (inventory == null || crop == null) return false;
            for (int i = 0; i < inventory.Entries.Length; i++)
            {
                var entry = inventory.Entries[i];
                if (!(entry.Item is SeedBag seed) || seed.PlantedCrop != crop || entry.StackSize <= 0) continue;
                if (inventory.Remove(i, 1) != 1) return false;
                consumedSeed = seed;
                return true;
            }
            return false;
        }

        // Called by the existing watering-can VFX animation event, never by the UI.
        public void ApplyWater()
        {
            if (State != TaskState.Watering || applied || terrain == null) return;
            if (!InReach()) { Finish(TaskState.Failed, "位置改变，浇水未生效"); return; }
            if (!terrain.TryWaterAt(TargetCell))
            {
                Finish(TaskState.Cancelled, "目标已湿润或失效，未重复浇水");
                return;
            }
            applied = true;
            if (WateringCan.UseSound.Length > 0 && SoundManager.Instance != null)
                SoundManager.Instance.PlaySFXAt(transform.position, WateringCan.UseSound[0], false);
        }

        public void Cancel()
        {
            if (IsBusy) Finish(applied ? TaskState.Succeeded : TaskState.Cancelled,
                applied ? ActionLabel(CurrentAction) + "已生效" : "已取消，土地未被修改");
        }

        void Finish(TaskState state, string message)
        {
            if (terrain != null) terrain.State.Release(TargetCell, AgentId);
            State = state;
            Status = message;
            body.velocity = Vector2.zero;
            SetMotion(Vector2.zero);
            if (toolAnimator != null) { toolAnimator.Rebind(); if (toolAnimator.gameObject.activeInHierarchy) toolAnimator.Update(0); }
            if (characterAnimator != null) { characterAnimator.Rebind(); characterAnimator.Update(0); SetMotion(Vector2.zero); }
            TaskFinished?.Invoke(state, message);
        }

        void SetMotion(Vector2 direction)
        {
            if (direction.sqrMagnitude > 0.000001f)
                facing = Mathf.Abs(direction.x) > Mathf.Abs(direction.y)
                    ? new Vector2(Mathf.Sign(direction.x), 0) : new Vector2(0, Mathf.Sign(direction.y));
            if (characterAnimator == null) return;
            characterAnimator.SetFloat("DirX", facing.x);
            characterAnimator.SetFloat("DirY", facing.y);
            characterAnimator.SetFloat("Speed", direction.sqrMagnitude > 0.000001f ? Speed * Speed : 0);
        }

        void OnDisable()
        {
            if (IsBusy) Cancel();
        }
    }
}
