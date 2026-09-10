using System;
using System.Collections.Generic;
using UnityEngine;

namespace HappyHarvest
{
    /// <summary>Small autonomous planner: observe the farm SO, choose a need, then delegate execution.</summary>
    public sealed class NpcFarmWorker : MonoBehaviour
    {
        public bool AutoWorkEnabled = true;
        public bool HarvestEnabled = true;
        public bool WeedEnabled = true;
        public bool WaterEnabled = true;
        public bool FertilizeEnabled = true;
        public bool PlantEnabled = true;
        [Min(.1f)] public float ScanInterval = .5f;
        [Min(1f)] public float FailedTargetRetryDelay = 5f;
        public Crop DefaultCrop;
        public string Status { get; private set; } = "等待扫描农田";
        public Vector3Int CurrentTarget { get; private set; }
        public event Action<Vector3Int> TargetChosen;

        TerrainManager terrain;
        NpcWateringAgent agent;
        readonly Dictionary<string, float> retryAfter = new();
        float nextScan;

        public void Initialize(TerrainManager farm, NpcWateringAgent wateringAgent)
        {
            if (agent != null) agent.TaskFinished -= OnTaskFinished;
            terrain = farm;
            agent = wateringAgent;
            if (DefaultCrop == null && GameManager.Instance != null)
                DefaultCrop = GameManager.Instance.CropDatabase.GetFromID("carrot_crop");
            retryAfter.Clear();
            if (agent != null) agent.TaskFinished += OnTaskFinished;
            nextScan = Time.time;
            Status = "等待扫描农田";
        }

        void Update()
        {
            if (!AutoWorkEnabled || terrain == null || agent == null || agent.IsBusy ||
                GameManager.Instance == null || !GameManager.Instance.IsTicking || Time.time < nextScan) return;
            ScanNow();
        }

        public bool ScanNow()
        {
            nextScan = Time.time + ScanInterval;
            if (!AutoWorkEnabled || terrain == null || agent == null || agent.IsBusy) return false;

            var candidates = new List<(FarmPlotState plot, NpcWateringAgent.FarmAction action)>();
            if (HarvestEnabled) AddCandidates(candidates, terrain.State.GetPlotsReadyToHarvest(), NpcWateringAgent.FarmAction.Harvest);
            if (WeedEnabled) AddCandidates(candidates, terrain.State.GetPlotsNeedingWeeding(), NpcWateringAgent.FarmAction.Weed);
            if (WaterEnabled) AddCandidates(candidates, terrain.State.GetPlotsNeedingWater(), NpcWateringAgent.FarmAction.Water);
            if (FertilizeEnabled) AddCandidates(candidates, terrain.State.GetPlotsNeedingFertilizer(), NpcWateringAgent.FarmAction.Fertilize);
            if (PlantEnabled && DefaultCrop != null) AddCandidates(candidates, terrain.State.GetPlotsReadyToPlant(), NpcWateringAgent.FarmAction.Plant);
            candidates.Sort((a, b) =>
            {
                int priority = Priority(a.action).CompareTo(Priority(b.action));
                if (priority != 0) return priority;
                return ((Vector2)a.plot.WorldCenter - (Vector2)transform.position).sqrMagnitude.CompareTo(
                    ((Vector2)b.plot.WorldCenter - (Vector2)transform.position).sqrMagnitude);
            });

            foreach (var candidate in candidates)
            {
                var plot = candidate.plot;
                CurrentTarget = plot.Cell;
                if (Request(candidate.action, plot.Cell))
                {
                    Status = $"发现{ActionLabel(candidate.action)}任务 {plot.Id}";
                    TargetChosen?.Invoke(plot.Cell);
                    return true;
                }
                retryAfter[RetryKey(plot.Id, candidate.action)] = Time.time + FailedTargetRetryDelay;
            }

            Status = candidates.Count == 0 ? "当前没有待处理农活" : "待处理地块暂时都无法到达";
            return false;
        }

        void AddCandidates(List<(FarmPlotState plot, NpcWateringAgent.FarmAction action)> output,
            IEnumerable<FarmPlotState> plots, NpcWateringAgent.FarmAction action)
        {
            foreach (var plot in plots)
            {
                string key = RetryKey(plot.Id, action);
                if (!retryAfter.TryGetValue(key, out var retryTime) || Time.time >= retryTime)
                    output.Add((plot, action));
            }
        }

        bool Request(NpcWateringAgent.FarmAction action, Vector3Int cell) => action switch
        {
            NpcWateringAgent.FarmAction.Harvest => agent.RequestHarvest(cell),
            NpcWateringAgent.FarmAction.Weed => agent.RequestWeed(cell),
            NpcWateringAgent.FarmAction.Water => agent.RequestWater(cell),
            NpcWateringAgent.FarmAction.Fertilize => agent.RequestFertilize(cell),
            NpcWateringAgent.FarmAction.Plant => agent.RequestPlant(cell, DefaultCrop),
            _ => false
        };

        static string RetryKey(string plotId, NpcWateringAgent.FarmAction action) => plotId + ":" + action;
        static int Priority(NpcWateringAgent.FarmAction action) => action switch
        {
            NpcWateringAgent.FarmAction.Harvest => 0, NpcWateringAgent.FarmAction.Weed => 1,
            NpcWateringAgent.FarmAction.Water => 2, NpcWateringAgent.FarmAction.Fertilize => 3,
            NpcWateringAgent.FarmAction.Plant => 4, _ => 99
        };
        static string ActionLabel(NpcWateringAgent.FarmAction action) => action switch
        {
            NpcWateringAgent.FarmAction.Harvest => "收获", NpcWateringAgent.FarmAction.Weed => "除草",
            NpcWateringAgent.FarmAction.Water => "浇水", NpcWateringAgent.FarmAction.Fertilize => "施肥",
            NpcWateringAgent.FarmAction.Plant => "播种", _ => "农活"
        };

        void OnTaskFinished(NpcWateringAgent.TaskState state, string message)
        {
            Status = message;
            var plot = terrain?.State.GetPlot(CurrentTarget);
            if (plot != null && state != NpcWateringAgent.TaskState.Succeeded)
                retryAfter[RetryKey(plot.Id, agent.CurrentAction)] = Time.time + FailedTargetRetryDelay;
            nextScan = Time.time + ScanInterval;
        }

        void OnDestroy()
        {
            if (agent != null) agent.TaskFinished -= OnTaskFinished;
        }
    }
}
