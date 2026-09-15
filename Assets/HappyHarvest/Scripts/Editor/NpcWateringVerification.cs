using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HappyHarvest;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

// Runs against the real farm, real collisions and real animation events in Play Mode.
public static class NpcWateringVerification
{
    static IEnumerator run;
    static readonly List<string> results = new();
    static readonly List<GameObject> obstacles = new();
    static double deadline;

    [MenuItem("Tools/Happy Harvest/Verify NPC Watering %#F8")]
    public static void Run()
    {
        if (!EditorApplication.isPlaying || run != null) return;
        results.Clear();
        deadline = EditorApplication.timeSinceStartup + 50;
        run = Check();
        EditorApplication.update += Tick;
    }

    static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        results.Add("PASS: " + message);
    }

    static void Tick()
    {
        try
        {
            if (!EditorApplication.isPlaying) throw new Exception("Play Mode ended during verification");
            if (EditorApplication.timeSinceStartup > deadline) throw new Exception("Verification timed out");
            if (run.MoveNext()) return;
            Complete(true);
        }
        catch (Exception error)
        {
            results.Add("FAIL: " + error);
            Complete(false);
        }
    }

    static void Complete(bool passed)
    {
        EditorApplication.update -= Tick;
        run = null;
        foreach (var obstacle in obstacles) if (obstacle != null) Object.DestroyImmediate(obstacle);
        obstacles.Clear();
        Directory.CreateDirectory("Logs");
        File.WriteAllLines("Logs/npc-watering-verification.txt", results);
        Debug.Log($"NPC watering verification: {(passed ? "PASS" : "FAIL")}. See Logs/npc-watering-verification.txt");
    }

    static IEnumerator Check()
    {
        var demo = Object.FindObjectOfType<NpcWateringDemo>();
        Assert(demo != null && demo.Agent != null && demo.HasSelection, "Demo bootstraps and prepares a selected plot");
        var npc = demo.Agent;
        var worker = demo.Worker;
        var farm = GameManager.Instance.Terrain;
        var target = demo.SelectedCell;
        Assert(worker != null, "Autonomous farm worker is installed");
        worker.AutoWorkEnabled = false;
        if (npc.IsBusy) npc.Cancel();
        farm.State.GetPlot(target).WaterSeconds = 0;
        farm.WaterTilemap.SetTile(target, null);
        var startPosition = npc.transform.position;
        Assert(!GameManager.Instance.Player.ManualControlEnabled, "Original avatar is autonomous, manual controls disabled");
        Assert(!npc.RequestWater(new Vector3Int(9999, 9999, 0)), "Invalid terrain is rejected");
        Assert(farm.NeedsWater(target), "Test crop starts dry");
        var growthBefore = farm.GetCropDataAt(target).GrowthTimer;

        worker.Initialize(farm, npc);
        worker.HarvestEnabled = worker.WeedEnabled = worker.FertilizeEnabled = worker.PlantEnabled = false;
        worker.WaterEnabled = true;
        worker.AutoWorkEnabled = true;
        var autoStarted = worker.ScanNow();
        var targetPlot = farm.State.GetPlot(target);
        Assert(autoStarted && npc.IsBusy, $"NPC finds a dry plot from FarmState without a button: worker={worker.Status}, agent={npc.Status}, " +
            $"tilled={targetPlot.IsTilled}, crop={targetPlot.HasCrop}, wet={targetPlot.IsWet}, reserved={targetPlot.ReservedBy}");
        Assert(farm.State.GetPlot(target).ReservedBy == npc.AgentId && farm.State.GetPlot(target).CurrentAction == "water",
            "NPC task is visible in the shared farm SO");
        Assert(!npc.RequestWater(target), "Repeated requests cannot replace the running task");
        npc.ApplyWater();
        Assert(farm.NeedsWater(target), "Water cannot apply while NPC is still moving");
        npc.Cancel();
        worker.AutoWorkEnabled = false;
        Assert(!npc.IsBusy && farm.NeedsWater(target), "Cancel before arrival leaves the plot dry");
        Assert(!farm.State.GetPlot(target).IsReserved, "Cancel releases the shared plot reservation");

        // Block all four usable standing cells; no remote watering or unbounded search.
        foreach (var direction in FarmPathfinder.Directions)
        {
            var obstacle = new GameObject("Verification obstruction", typeof(BoxCollider2D));
            obstacle.transform.position = farm.Grid.GetCellCenterWorld(target + direction) + new Vector3(0, .25f);
            obstacle.GetComponent<BoxCollider2D>().size = new Vector2(.8f, .8f);
            obstacles.Add(obstacle);
        }
        Physics2D.SyncTransforms();
        Assert(!npc.RequestWater(target) && farm.NeedsWater(target), "Surrounded target is rejected without watering");
        foreach (var obstacle in obstacles) Object.DestroyImmediate(obstacle);
        obstacles.Clear();
        Physics2D.SyncTransforms();
        worker.Initialize(farm, npc);
        worker.HarvestEnabled = worker.WeedEnabled = worker.FertilizeEnabled = worker.PlantEnabled = false;
        worker.WaterEnabled = true;
        worker.AutoWorkEnabled = true;
        Assert(worker.ScanNow() && npc.IsBusy, "Automatic scan restarts task after obstacle removal");
        while (npc.IsBusy) yield return null;
        worker.AutoWorkEnabled = false;
        Assert(npc.State == NpcWateringAgent.TaskState.Succeeded, "NPC arrives and completes animation: " + npc.Status);
        Assert(Vector3.Distance(startPosition, npc.transform.position) > .1f, "NPC physically moves to the plot");
        Assert(!farm.NeedsWater(target) && farm.WaterTilemap.HasTile(target), "Water state and wet-soil tile both update");
        Assert(!farm.State.GetPlot(target).NeedsWater && !farm.State.GetPlot(target).IsReserved,
            "Completed watering updates AI need and clears reservation in the same SO");
        Assert(farm.GetCropDataAt(target).GrowthTimer > growthBefore, "Watered crop resumes growth");
        Assert(!npc.RequestWater(target), "Already-wet plot rejects duplicate watering");
        Assert(npc.GetComponentInChildren<Animator>().GetFloat("Speed") == 0, "NPC returns to idle motion");
        Assert(worker.Status.Contains("浇水完成"), "Autonomous worker receives completion feedback");

        int fertilizerBefore = GameManager.Instance.Player.Inventory.GetItemCount<Fertilizer>();
        Assert(fertilizerBefore > 0, "NPC starts with fertilizer in the shared inventory");
        Assert(npc.RequestFertilize(target), "NPC accepts fertilizing when fertilizer is available");
        while (npc.IsBusy) yield return null;
        Assert(farm.State.GetPlot(target).IsFertilized && npc.State == NpcWateringAgent.TaskState.Succeeded,
            "NPC performs fertilizing and updates the SO");
        Assert(GameManager.Instance.Player.Inventory.GetItemCount<Fertilizer>() == fertilizerBefore - 1,
            "Successful fertilizing consumes exactly one fertilizer");
        farm.State.GetPlot(target).IsFertilized = false;
        var fertilizer = GameManager.Instance.ItemDatabase.GetFromID("fertilizer") as Fertilizer;
        int fertilizerRemaining = GameManager.Instance.Player.Inventory.GetItemCount<Fertilizer>();
        Assert(fertilizer != null && GameManager.Instance.Player.Inventory.TryRemoveItem<Fertilizer>(fertilizerRemaining),
            "Verification can exhaust the remaining fertilizer");
        Assert(!npc.RequestFertilize(target) && !farm.State.GetPlot(target).IsFertilized,
            "Fertilizing without fertilizer is rejected and leaves the plot unchanged");
        Assert(GameManager.Instance.Player.Inventory.AddItem(fertilizer, fertilizerBefore),
            "Verification restores its consumed fertilizer");
        farm.State.GetPlot(target).HasWeeds = true;
        Assert(npc.RequestWeed(target), "NPC accepts a weeding need");
        while (npc.IsBusy) yield return null;
        Assert(!farm.State.GetPlot(target).HasWeeds && npc.State == NpcWateringAgent.TaskState.Succeeded,
            "NPC performs weeding and clears the SO state");
        farm.OverrideGrowthStage(target, farm.GetCropDataAt(target).GrowingCrop.GrowthStagesTiles.Length - 1);
        Assert(npc.RequestHarvest(target), "NPC accepts a mature crop for harvest");
        while (npc.IsBusy) yield return null;
        Assert(farm.GetCropDataAt(target) == null && npc.State == NpcWateringAgent.TaskState.Succeeded,
            "NPC performs harvest and clears the final-harvest crop");
        var carrot = GameManager.Instance.CropDatabase.GetFromID("carrot_crop");
        var carrotSeed = GameManager.Instance.ItemDatabase.GetFromID("carrot_seed") as SeedBag;
        int carrotSeedsBefore = carrotSeed == null ? 0 : GameManager.Instance.Player.Inventory.Entries
            .Where(entry => entry.Item == carrotSeed).Sum(entry => entry.StackSize);
        Assert(carrotSeed != null && carrotSeedsBefore > 0, "NPC starts with carrot seed in the shared inventory");
        for (int i = 0; i < GameManager.Instance.Player.Inventory.Entries.Length; i++)
        {
            var entry = GameManager.Instance.Player.Inventory.Entries[i];
            if (entry.Item == carrotSeed) GameManager.Instance.Player.Inventory.Remove(i, entry.StackSize);
        }
        Assert(!npc.RequestPlant(target, carrot) && farm.GetCropDataAt(target) == null,
            "Planting without the matching seed is rejected and leaves the plot unchanged");
        Assert(GameManager.Instance.Player.Inventory.AddItem(carrotSeed, carrotSeedsBefore),
            "Verification restores carrot seeds before successful planting");
        Assert(npc.RequestPlant(target, carrot), "NPC accepts an empty tilled plot for planting");
        while (npc.IsBusy) yield return null;
        Assert(farm.GetCropDataAt(target)?.GrowingCrop == carrot && npc.State == NpcWateringAgent.TaskState.Succeeded,
            "NPC performs planting and writes the crop into the SO");
        int carrotSeedsAfter = GameManager.Instance.Player.Inventory.Entries
            .Where(entry => entry.Item == carrotSeed).Sum(entry => entry.StackSize);
        Assert(carrotSeedsAfter == carrotSeedsBefore - 1, "Successful planting consumes exactly one matching seed");
        Assert(GameManager.Instance.Player.Inventory.AddItem(carrotSeed), "Verification restores its consumed seed");

        worker.HarvestEnabled = worker.WeedEnabled = worker.WaterEnabled = worker.FertilizeEnabled = worker.PlantEnabled = true;
    }
}
