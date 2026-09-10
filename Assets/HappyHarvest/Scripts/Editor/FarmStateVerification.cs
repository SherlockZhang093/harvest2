using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HappyHarvest;
using UnityEditor;
using UnityEngine;

public static class FarmStateVerification
{
    [MenuItem("Tools/Happy Harvest/Verify Farm State %#F9")]
    public static void Run()
    {
        if (!EditorApplication.isPlaying || GameManager.Instance?.Terrain == null) return;
        var results = new List<string>();
        var terrain = GameManager.Instance.Terrain;
        var farm = terrain.State;
        var original = farm.CaptureSnapshot();
        var worker = UnityEngine.Object.FindObjectOfType<NpcFarmWorker>();
        bool workerWasEnabled = worker != null && worker.AutoWorkEnabled;
        if (worker != null)
        {
            worker.AutoWorkEnabled = false;
            var agent = worker.GetComponent<NpcWateringAgent>();
            if (agent != null && agent.IsBusy) agent.Cancel();
        }
        void Check(bool success, string message)
        {
            if (!success) throw new Exception(message);
            results.Add("PASS: " + message);
        }
        try
        {
            Check(ReferenceEquals(farm, AssetDatabase.LoadAssetAtPath<FarmState>(FarmStateAuthoring.AssetPath)),
                "Terrain uses the actual authored SO asset, without a second runtime state");
            int tiles = 0;
            bool covered = true;
            foreach (var cell in terrain.GroundTilemap.cellBounds.allPositionsWithin)
                if (terrain.GroundTilemap.HasTile(cell)) { tiles++; covered &= farm.GetPlot(cell) != null; }
            Check(covered && tiles == farm.Plots.Count && farm.Plots.Select(p => p.Id).Distinct().Count() == tiles,
                "Complete scene coverage and unique stable plot IDs");
            var p = farm.Plots.First(plot => plot.CanTill);
            var carrot = GameManager.Instance.CropDatabase.GetFromID("carrot_crop");
            terrain.TillAt(p.Cell);
            Check(p.CanPlant && p.CanWater && !p.NeedsWater, "Empty tilled soil can be watered but is not a crop watering need");
            terrain.PlantAt(p.Cell, carrot);
            Check(ReferenceEquals(p.Crop, terrain.GetCropDataAt(p.Cell)) && p.NeedsWater,
                "Planted crop is the same object AI reads from the SO");
            Check(p.NeedsFertilizer && terrain.TryFertilizeAt(p.Cell) && p.IsFertilized,
                "Fertilizing resolves the SO need");
            p.HasWeeds = true;
            Check(p.NeedsWeeding && terrain.TryRemoveWeedsAt(p.Cell) && !p.HasWeeds && p.WeedTimer == 0,
                "Weeding clears the SO state and its regrowth timer");
            terrain.TickState(2);
            Check(p.Crop.GrowthTimer == 0 && p.Crop.DyingTimer >= 2, "Dry crop stops growth and records dry duration");
            Check(terrain.TryWaterAt(p.Cell) && p.IsWet && !p.NeedsWater && terrain.WaterTilemap.HasTile(p.Cell),
                "Water action updates SO query and tile visual immediately");
            Check(!terrain.TryWaterAt(p.Cell), "Wet soil rejects duplicate watering");
            p.WaterSeconds = 2;
            terrain.TickState(3);
            Check(p.WaterSeconds == 0 && Mathf.Approximately(p.Crop.GrowthTimer, 2 * TerrainManager.FertilizedGrowthMultiplier) && Mathf.Approximately(p.Crop.DyingTimer, 1),
                "A fertilized time step crossing dryness grows faster only for its wet portion");
            p.HasWeeds = true;
            float beforeWeeds = p.Crop.GrowthTimer;
            p.WaterSeconds = 2;
            terrain.TickState(1);
            Check(Mathf.Approximately(p.Crop.GrowthTimer, beforeWeeds), "Weeds block crop growth until removed");
            terrain.TryRemoveWeedsAt(p.Cell);
            p.WaterSeconds = 0;
            terrain.WaterTilemap.SetTile(p.Cell, null);
            Check(!terrain.WaterTilemap.HasTile(p.Cell) && farm.GetPlotsNeedingWater().Contains(p),
                "Drying removes the wet tile and returns plot to AI watering needs");
            Check(farm.TryReserve(p.Cell, "test-agent", "water") && !farm.TryReserve(p.Cell, "second-agent", "water"),
                "Plot reservation prevents two agents acquiring the same task");
            Check(!farm.GetPlotsNeedingWater().Contains(p) && farm.GetPlotsNeedingWater(true).Contains(p),
                "Default AI query excludes occupied plots");
            farm.Release(p.Cell, "second-agent");
            Check(p.IsReserved, "An unrelated agent cannot release another task");
            farm.Release(p.Cell, "test-agent");
            Check(!p.IsReserved && string.IsNullOrEmpty(p.CurrentAction), "Task release clears owner and action together");
            p.IsFertilized = p.HasWeeds = true;
            var json = farm.ToAiJson();
            var snapshot = JsonUtility.FromJson<FarmSnapshot>(json);
            var exported = snapshot.plots.Single(x => x.id == p.Id);
            Check(exported.cropId == "carrot_crop" && exported.needsWater && exported.fertilized && exported.weeds && !json.Contains("instanceID"),
                "AI JSON contains stable crop IDs, actionable needs and environment fields, without Unity references");
            var saved = new TerrainDataSave();
            terrain.Save(ref saved);
            terrain.WaterAt(p.Cell);
            p.IsFertilized = p.HasWeeds = false;
            Check(saved.Farm.plots.Single(x => x.id == p.Id).waterSeconds == 0,
                "Saved snapshot is detached from subsequent live mutations");
            terrain.Load(saved);
            Check(ReferenceEquals(p, farm.GetPlot(p.Id)) && p.NeedsWater && p.IsFertilized && p.HasWeeds && !terrain.WaterTilemap.HasTile(p.Cell),
                "Loading restores the same plot, environmental state and visuals");
            terrain.TryRemoveWeedsAt(p.Cell);
            terrain.WaterAt(p.Cell);
            terrain.TickState(carrot.GrowthTime);
            Check(p.CanHarvest && terrain.CropTilemap.HasTile(p.Cell), "Growth reaches harvestable state in the SO and scene");
            Check(terrain.HarvestAt(p.Cell) == carrot && !p.HasCrop && !terrain.CropTilemap.HasTile(p.Cell),
                "Final harvest clears crop state and crop visual together");
            var corn = GameManager.Instance.CropDatabase.GetFromID("corn_crop");
            terrain.PlantAt(p.Cell, corn);
            terrain.OverrideGrowthStage(p.Cell, corn.GrowthStagesTiles.Length - 1);
            terrain.HarvestAt(p.Cell);
            Check(p.HasCrop && p.Crop.HarvestCount == 1 && !p.CanHarvest && p.Crop.CurrentGrowthStage == corn.StageAfterHarvest,
                "Multi-harvest crop preserves its count and returns to regrowth");
            terrain.OverrideGrowthStage(p.Cell, corn.GrowthStagesTiles.Length - 1);
            terrain.HarvestAt(p.Cell);
            Check(!p.HasCrop, "Multi-harvest crop clears after its final harvest");
            terrain.PlantAt(p.Cell, carrot);
            p.WaterSeconds = 0;
            terrain.TickState(carrot.DryDeathTimer + 1);
            Check(!p.HasCrop && !p.NeedsWater && !terrain.CropTilemap.HasTile(p.Cell),
                "A dead crop is removed from SO, AI needs and visuals");
        }
        catch (Exception error) { results.Add("FAIL: " + error); }
        finally
        {
            terrain.Load(new TerrainDataSave { Farm = original });
            if (worker != null) worker.AutoWorkEnabled = workerWasEnabled;
            Directory.CreateDirectory("Logs");
            File.WriteAllLines("Logs/farm-state-verification.txt", results);
            Debug.Log($"Farm state verification: {(results.Any(r => r.StartsWith("FAIL")) ? "FAIL" : "PASS")}. See Logs/farm-state-verification.txt");
        }
    }
}
