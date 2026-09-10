using System;
using System.Collections.Generic;
using UnityEngine;

namespace HappyHarvest
{
    [Serializable]
    public sealed class FarmPlotState
    {
        public string Id;
        public Vector3Int Cell;
        public Vector3 WorldCenter;
        public bool IsCultivable;
        public bool IsTilled;
        [Min(0)] public float WaterSeconds;
        public TerrainManager.CropData Crop = new();
        public bool IsFertilized;
        public bool HasWeeds;
        [Min(0)] public float WeedTimer;
        public string ReservedBy = "";
        public string CurrentAction = "";

        public bool HasCrop => Crop != null && Crop.GrowingCrop != null;
        public bool IsWet => IsTilled && WaterSeconds > 0;
        public bool CanTill => IsCultivable && !IsTilled;
        public bool CanPlant => IsCultivable && IsTilled && !HasCrop;
        // Watering empty tilled soil is valid; an agricultural need requires a living crop.
        public bool CanWater => IsCultivable && IsTilled && !IsWet;
        public bool NeedsWater => CanWater && HasCrop;
        public bool NeedsFertilizer => HasCrop && !IsFertilized;
        public bool NeedsWeeding => HasCrop && HasWeeds;
        public bool CanHarvest => HasCrop && Crop.GrowthRatio >= 1;
        public bool IsReserved => !string.IsNullOrEmpty(ReservedBy);
    }

    /// <summary>The single live farm state, shared by terrain actions, inspection and AI.</summary>
    [CreateAssetMenu(menuName = "Happy Harvest/Farm State", fileName = "FarmState")]
    public sealed class FarmState : ScriptableObject
    {
        public string FarmId = "Farm_Outdoor";
        public bool HasAuthoredInitialState;
        [SerializeField] List<FarmPlotState> plots = new();
        [SerializeField] float elapsedSeconds;
        [SerializeField] int revision;
        [NonSerialized] Dictionary<Vector3Int, FarmPlotState> byCell;
        [NonSerialized] Dictionary<string, FarmPlotState> byId;

        public IReadOnlyList<FarmPlotState> Plots => plots;
        public float ElapsedSeconds => elapsedSeconds;
        public int Revision => revision;

        void OnEnable() => RebuildIndex();
        void OnValidate() => RebuildIndex();

        public void RebuildIndex()
        {
            byCell = new Dictionary<Vector3Int, FarmPlotState>();
            byId = new Dictionary<string, FarmPlotState>(StringComparer.Ordinal);
            foreach (var plot in plots)
            {
                if (plot == null || string.IsNullOrEmpty(plot.Id)) continue;
                byCell.Add(plot.Cell, plot);
                byId.Add(plot.Id, plot);
            }
        }

        public FarmPlotState GetPlot(Vector3Int cell)
        {
            if (byCell == null) RebuildIndex();
            return byCell.TryGetValue(cell, out var plot) ? plot : null;
        }

        public FarmPlotState GetPlot(string id)
        {
            if (byId == null) RebuildIndex();
            return id != null && byId.TryGetValue(id, out var plot) ? plot : null;
        }

        public FarmPlotState Register(Vector3Int cell, Vector3 center, bool cultivable, bool tilled)
        {
            var existing = GetPlot(cell);
            if (existing != null) return existing;
            var plot = new FarmPlotState {
                Id = $"{FarmId}:{cell.x}:{cell.y}:{cell.z}", Cell = cell,
                WorldCenter = center, IsCultivable = cultivable, IsTilled = tilled
            };
            plots.Add(plot);
            byCell.Add(cell, plot);
            byId.Add(plot.Id, plot);
            MarkChanged();
            return plot;
        }

        public IEnumerable<FarmPlotState> GetPlotsNeedingWater(bool includeReserved = false)
        {
            foreach (var plot in plots)
                if (plot.NeedsWater && (includeReserved || !plot.IsReserved)) yield return plot;
        }

        public IEnumerable<FarmPlotState> GetPlotsNeedingFertilizer(bool includeReserved = false)
        {
            foreach (var plot in plots)
                if (plot.NeedsFertilizer && (includeReserved || !plot.IsReserved)) yield return plot;
        }

        public IEnumerable<FarmPlotState> GetPlotsNeedingWeeding(bool includeReserved = false)
        {
            foreach (var plot in plots)
                if (plot.NeedsWeeding && (includeReserved || !plot.IsReserved)) yield return plot;
        }

        public IEnumerable<FarmPlotState> GetPlotsReadyToHarvest(bool includeReserved = false)
        {
            foreach (var plot in plots)
                if (plot.CanHarvest && (includeReserved || !plot.IsReserved)) yield return plot;
        }

        public IEnumerable<FarmPlotState> GetPlotsReadyToPlant(bool includeReserved = false)
        {
            foreach (var plot in plots)
                if (plot.CanPlant && (includeReserved || !plot.IsReserved)) yield return plot;
        }

        public bool TryReserve(Vector3Int cell, string agentId, string action)
        {
            var plot = GetPlot(cell);
            if (plot == null || plot.IsReserved || string.IsNullOrEmpty(agentId) || string.IsNullOrEmpty(action)) return false;
            plot.ReservedBy = agentId;
            plot.CurrentAction = action;
            MarkChanged();
            return true;
        }

        public void Release(Vector3Int cell, string agentId)
        {
            var plot = GetPlot(cell);
            if (plot == null || plot.ReservedBy != agentId) return;
            plot.ReservedBy = plot.CurrentAction = "";
            MarkChanged();
        }

        public void MarkChanged() => revision++;
        public void AdvanceClock(float seconds) { elapsedSeconds += seconds; MarkChanged(); }

        public FarmSnapshot CaptureSnapshot()
        {
            var snapshot = new FarmSnapshot { farmId = FarmId, revision = revision, elapsedSeconds = elapsedSeconds };
            foreach (var p in plots)
            {
                var c = p.Crop;
                snapshot.plots.Add(new FarmPlotSnapshot {
                    id = p.Id, cell = p.Cell, worldCenter = p.WorldCenter, cultivable = p.IsCultivable,
                    tilled = p.IsTilled, waterSeconds = p.WaterSeconds, wet = p.IsWet,
                    cropId = p.HasCrop ? c.GrowingCrop.Key : "", growthStage = p.HasCrop ? c.CurrentGrowthStage : 0,
                    growthProgress = p.HasCrop ? c.GrowthRatio : 0, growthSeconds = p.HasCrop ? c.GrowthTimer : 0,
                    drySeconds = p.HasCrop ? c.DyingTimer : 0, harvestCount = p.HasCrop ? c.HarvestCount : 0,
                    growthDuration = p.HasCrop ? c.GrowingCrop.GrowthTime : 0,
                    dryDeathDuration = p.HasCrop ? c.GrowingCrop.DryDeathTimer : 0,
                    fertilized = p.IsFertilized, weeds = p.HasWeeds, reservedBy = p.ReservedBy,
                    weedSeconds = p.WeedTimer,
                    currentAction = p.CurrentAction, needsWater = p.NeedsWater,
                    needsFertilizer = p.NeedsFertilizer, needsWeeding = p.NeedsWeeding,
                    canTill = p.CanTill, canPlant = p.CanPlant, canWater = p.CanWater, canHarvest = p.CanHarvest
                });
            }
            return snapshot;
        }

        public string ToAiJson(bool prettyPrint = true) => JsonUtility.ToJson(CaptureSnapshot(), prettyPrint);

        // Restore mutable state onto the same registered plots. Geometry/IDs belong to this scene.
        public void RestoreSnapshot(FarmSnapshot snapshot, Func<string, Crop> resolveCrop)
        {
            if (snapshot == null || snapshot.schemaVersion != 1 || snapshot.farmId != FarmId)
                throw new ArgumentException("Farm snapshot schema or farm ID does not match.");
            foreach (var p in plots)
            {
                p.IsTilled = p.IsFertilized = p.HasWeeds = false;
                p.WaterSeconds = 0;
                p.WeedTimer = 0;
                p.Crop = new TerrainManager.CropData();
                p.ReservedBy = p.CurrentAction = "";
            }
            foreach (var saved in snapshot.plots)
            {
                var p = GetPlot(saved.id);
                if (p == null) continue;
                p.IsTilled = p.IsCultivable && saved.tilled;
                p.WaterSeconds = p.IsTilled ? Mathf.Max(0, saved.waterSeconds) : 0;
                p.IsFertilized = saved.fertilized;
                p.HasWeeds = saved.weeds;
                p.WeedTimer = Mathf.Max(0, saved.weedSeconds);
                var crop = string.IsNullOrEmpty(saved.cropId) ? null : resolveCrop(saved.cropId);
                if (p.IsTilled && crop != null)
                    p.Crop = new TerrainManager.CropData { GrowingCrop = crop,
                        CurrentGrowthStage = Mathf.Clamp(saved.growthStage, 0, crop.GrowthStagesTiles.Length - 1),
                        GrowthRatio = Mathf.Clamp01(saved.growthProgress), GrowthTimer = Mathf.Max(0, saved.growthSeconds),
                        DyingTimer = Mathf.Max(0, saved.drySeconds), HarvestCount = Mathf.Max(0, saved.harvestCount) };
                // Running tasks are not resumed when loading; stale reservations must be released.
            }
            elapsedSeconds = snapshot.elapsedSeconds;
            MarkChanged();
        }
    }

    [Serializable]
    public sealed class FarmSnapshot
    {
        public int schemaVersion = 1;
        public string farmId;
        public int revision;
        public float elapsedSeconds;
        public string[] terrainActions = { "till", "plant", "water", "harvest" };
        public string[] npcActions = { "plant", "water", "fertilize", "weed", "harvest" };
        public List<FarmPlotSnapshot> plots = new();
    }

    [Serializable]
    public sealed class FarmPlotSnapshot
    {
        public string id;
        public Vector3Int cell;
        public Vector3 worldCenter;
        public bool cultivable, tilled, wet;
        public float waterSeconds;
        public string cropId;
        public int growthStage, harvestCount;
        public float growthProgress, growthSeconds, drySeconds, growthDuration, dryDeathDuration, weedSeconds;
        public bool fertilized, weeds;
        public string reservedBy, currentAction;
        public bool needsWater, needsFertilizer, needsWeeding, canTill, canPlant, canWater, canHarvest;
    }
}
