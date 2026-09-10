using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;
using UnityEngine.VFX;


namespace HappyHarvest
{
    /// <summary>
    /// Manage everything related to the terrain where crop are planted. Hold the content of cells with the states of
    /// crop in those cells. Handle also switching tiles and the like where tilling and watering happens.
    /// </summary>
    public class TerrainManager : MonoBehaviour
    {
        public const float WeedInterval = 45f;
        public const float FertilizedGrowthMultiplier = 1.25f;
        [System.Serializable]
        public class GroundData
        {
            public const float WaterDuration = 60 * 1.0f;

            public float WaterTimer;
        }

        [Serializable]
        public class CropData
        {
            [Serializable]
            public struct SaveData
            {
                public string CropId;
                public int Stage;
                public float GrowthRatio;
                public float GrowthTimer;
                public int HarvestCount;
                public float DyingTimer;
            }
            
            public Crop GrowingCrop = null;
            public int CurrentGrowthStage = 0;

            public float GrowthRatio = 0.0f;
            public float GrowthTimer = 0.0f;

            public int HarvestCount = 0;
            
            public float DyingTimer;
            public bool HarvestDone => HarvestCount == GrowingCrop.NumberOfHarvest;

            public void Init()
            {
                GrowingCrop = null;
                GrowthRatio = 0.0f;
                GrowthTimer = 0.0f;
                CurrentGrowthStage = 0;
                HarvestCount = 0;

                DyingTimer = 0.0f;
            }

            public Crop Harvest()
            {
                var crop = GrowingCrop;

                HarvestCount += 1;

                CurrentGrowthStage = GrowingCrop.StageAfterHarvest;
                GrowthRatio = CurrentGrowthStage / (float)Mathf.Max(1, GrowingCrop.GrowthStagesTiles.Length - 1);
                GrowthTimer = GrowingCrop.GrowthTime * GrowthRatio;

                return crop;
            }

            public void Save(ref SaveData data)
            {
                data.Stage = CurrentGrowthStage;
                data.CropId = GrowingCrop.Key;
                data.DyingTimer = DyingTimer;
                data.GrowthRatio = GrowthRatio;
                data.GrowthTimer = GrowthTimer;
                data.HarvestCount = HarvestCount;
            }

            public void Load(SaveData data)
            {
                CurrentGrowthStage = data.Stage;
                GrowingCrop = GameManager.Instance.CropDatabase.GetFromID(data.CropId);
                DyingTimer = data.DyingTimer;
                GrowthRatio = data.GrowthRatio;
                GrowthTimer = data.GrowthTimer;
                HarvestCount = data.HarvestCount;
            }
        }

        public Grid Grid;
        
        public Tilemap GroundTilemap;
        public Tilemap CropTilemap;
        
        [Header("Watering")]
        public Tilemap WaterTilemap;
        public TileBase WateredTile;
        
        [Header("Tilling")] 
        public TileBase TilleableTile;
        public TileBase TilledTile;
        public VisualEffect TillingEffectPrefab;
        
        [Header("Shared farm state (also read by AI)")]
        public FarmState State;

        private Dictionary<Crop, List<VisualEffect>> m_HarvestEffectPool = new();
        private List<VisualEffect> m_TillingEffectPool = new();

        public bool IsTillable(Vector3Int target)
        {
            return State.GetPlot(target)?.CanTill == true;
        }

        public bool IsPlantable(Vector3Int target)
        {
            return State.GetPlot(target)?.CanPlant == true;
        }

        public bool IsTilled(Vector3Int target)
        {
            return State.GetPlot(target)?.IsTilled == true;
        }

        public bool NeedsWater(Vector3Int target)
        {
            // Keep the template's action contract: empty tilled soil can also be watered.
            return State.GetPlot(target)?.CanWater == true;
        }

        public bool TryWaterAt(Vector3Int target)
        {
            if (!NeedsWater(target)) return false;
            WaterAt(target);
            return true;
        }

        public void TillAt(Vector3Int target)
        {
            if (!IsTillable(target))
                return;
            
            GroundTilemap.SetTile(target, TilledTile);
            State.GetPlot(target).IsTilled = true;
            State.MarkChanged();

            var inst = m_TillingEffectPool[0];
            m_TillingEffectPool.RemoveAt(0);
            m_TillingEffectPool.Add(inst);

            inst.gameObject.transform.position = Grid.GetCellCenterWorld(target);
            
            inst.Stop();
            inst.Play();
        }

        public void PlantAt(Vector3Int target, Crop cropToPlant)
        {
            if (cropToPlant == null || !IsPlantable(target)) return;
            var cropData = new CropData();
            
            cropData.GrowingCrop = cropToPlant;
            cropData.GrowthTimer = 0.0f;
            cropData.CurrentGrowthStage = 0;
            
            State.GetPlot(target).Crop = cropData;
            State.GetPlot(target).IsFertilized = false;
            State.GetPlot(target).HasWeeds = false;
            State.GetPlot(target).WeedTimer = 0;
            State.MarkChanged();
            
            UpdateCropVisual(target);

            if (!m_HarvestEffectPool.ContainsKey(cropToPlant))
            {
                InitHarvestEffect(cropToPlant);
            }
        }

        public void InitHarvestEffect(Crop crop)
        {
            m_HarvestEffectPool[crop] = new List<VisualEffect>();
            for (int i = 0; i < 4; ++i)
            {
                var inst = Instantiate(crop.HarvestEffect);
                inst.Stop();
                m_HarvestEffectPool[crop].Add(inst);
            }
        }

        public void WaterAt(Vector3Int target)
        {
            var plot = State.GetPlot(target);
            if (plot == null || !plot.IsTilled) return;
            plot.WaterSeconds = GroundData.WaterDuration;
            State.MarkChanged();
            
            WaterTilemap.SetTile(target, WateredTile);
            //GroundTilemap.SetColor(target, WateredTiledColorTint);
        }

        public Crop HarvestAt(Vector3Int target)
        {
            var data = GetCropDataAt(target);

            if (data == null || !Mathf.Approximately(data.GrowthRatio,1.0f)) return null;
            
            var produce = data.Harvest();

            if (data.HarvestDone)
            {
                var plot = State.GetPlot(target);
                plot.Crop = new CropData();
                plot.IsFertilized = false;
                plot.HasWeeds = false;
                plot.WeedTimer = 0;
            }
            State.MarkChanged();
            
            UpdateCropVisual(target);

            var effect = m_HarvestEffectPool[data.GrowingCrop][0];
            effect.transform.position = Grid.GetCellCenterWorld(target);
            m_HarvestEffectPool[data.GrowingCrop].RemoveAt(0);
            m_HarvestEffectPool[data.GrowingCrop].Add(effect);
            effect.Play();

            return produce;
        }

        public bool TryFertilizeAt(Vector3Int target)
        {
            var plot = State.GetPlot(target);
            if (plot == null || !plot.NeedsFertilizer || plot.IsReserved && plot.CurrentAction != "fertilize") return false;
            plot.IsFertilized = true;
            State.MarkChanged();
            return true;
        }

        public bool TryRemoveWeedsAt(Vector3Int target)
        {
            var plot = State.GetPlot(target);
            if (plot == null || !plot.NeedsWeeding || plot.IsReserved && plot.CurrentAction != "weed") return false;
            plot.HasWeeds = false;
            plot.WeedTimer = 0;
            State.MarkChanged();
            return true;
        }

        public CropData GetCropDataAt(Vector3Int target)
        {
            var plot = State.GetPlot(target);
            return plot != null && plot.HasCrop ? plot.Crop : null;
        }

        public void OverrideGrowthStage(Vector3Int target, int newGrowthStage)
        {
            var data = GetCropDataAt(target);
            if (data == null) return;
            newGrowthStage = Mathf.Clamp(newGrowthStage, 0, data.GrowingCrop.GrowthStagesTiles.Length - 1);
            data.GrowthRatio = newGrowthStage / (float)Mathf.Max(1, data.GrowingCrop.GrowthStagesTiles.Length - 1);
            data.GrowthTimer = data.GrowthRatio * data.GrowingCrop.GrowthTime;
            data.CurrentGrowthStage = newGrowthStage;
            State.MarkChanged();
            
            UpdateCropVisual(target);
        }

        private void Awake()
        {
            GameManager.Instance.Terrain = this;
            if (State == null) State = Resources.Load<FarmState>("FarmState");
            if (State == null)
                throw new InvalidOperationException("Missing FarmState asset. Use Tools/Happy Harvest/Create Farm State from Scene.");
            State.RebuildIndex();
            RegisterScenePlots();
            SynchronizeVisuals();

            for (int i = 0; i < 4; ++i)
            {
                var effect = Instantiate(TillingEffectPrefab);
                effect.gameObject.SetActive(true);
                effect.Stop();
                m_TillingEffectPool.Add(effect);
            }
        }

        private void Update()
        {
            if (!GameManager.Instance.IsTicking) return;
            TickState(Time.deltaTime);
        }

        public void RegisterScenePlots()
        {
            foreach (var cell in GroundTilemap.cellBounds.allPositionsWithin)
            {
                var tile = GroundTilemap.GetTile(cell);
                if (tile == null) continue;
                State.Register(cell, Grid.GetCellCenterWorld(cell), tile == TilleableTile || tile == TilledTile, tile == TilledTile);
            }
        }

        public void SynchronizeVisuals()
        {
            CropTilemap.ClearAllTiles();
            WaterTilemap.ClearAllTiles();
            foreach (var plot in State.Plots)
            {
                if (plot.IsCultivable) GroundTilemap.SetTile(plot.Cell, plot.IsTilled ? TilledTile : TilleableTile);
                if (plot.IsWet) WaterTilemap.SetTile(plot.Cell, WateredTile);
                UpdateCropVisual(plot.Cell);
                if (Application.isPlaying && plot.HasCrop && !m_HarvestEffectPool.ContainsKey(plot.Crop.GrowingCrop))
                    InitHarvestEffect(plot.Crop.GrowingCrop);
            }
        }

        // A deterministic step also used by the state verification. All mutable values live in State.
        public void TickState(float seconds)
        {
            if (seconds <= 0) return;
            State.AdvanceClock(seconds);
            foreach (var plot in State.Plots)
            {
                if (!plot.IsTilled) continue;
                var cell = plot.Cell;
                float wetSeconds = Mathf.Min(seconds, Mathf.Max(0, plot.WaterSeconds));
                if (plot.WaterSeconds > 0.0f)
                {
                    plot.WaterSeconds = Mathf.Max(0, plot.WaterSeconds - seconds);

                    if (plot.WaterSeconds <= 0.0f)
                    {
                        WaterTilemap.SetTile(cell, null);
                        //GroundTilemap.SetColor(cell, Color.white);
                    }
                }

                if (plot.HasCrop)
                {
                    var cropData = plot.Crop;
                    if (!plot.HasWeeds)
                    {
                        plot.WeedTimer += seconds;
                        if (plot.WeedTimer >= WeedInterval) plot.HasWeeds = true;
                    }
                    if (wetSeconds > 0)
                    {
                        cropData.DyingTimer = 0;
                        float growthSeconds = plot.HasWeeds ? 0 : wetSeconds * (plot.IsFertilized ? FertilizedGrowthMultiplier : 1f);
                        cropData.GrowthTimer = Mathf.Clamp(cropData.GrowthTimer + growthSeconds, 0.0f,
                            cropData.GrowingCrop.GrowthTime);
                        cropData.GrowthRatio = cropData.GrowingCrop.GrowthTime <= 0 ? 1 : cropData.GrowthTimer / cropData.GrowingCrop.GrowthTime;
                        int growthStage = cropData.GrowingCrop.GetGrowthStage(cropData.GrowthRatio);

                        if (growthStage != cropData.CurrentGrowthStage)
                        {
                            cropData.CurrentGrowthStage = growthStage;
                            UpdateCropVisual(cell);
                        }
                    }
                    cropData.DyingTimer += seconds - wetSeconds;
                    if (cropData.DyingTimer > cropData.GrowingCrop.DryDeathTimer)
                    {
                        plot.Crop = new CropData();
                        UpdateCropVisual(cell);
                    }
                }
            }
        }

        void UpdateCropVisual(Vector3Int target)
        {
            var data = GetCropDataAt(target);
            if (data == null)
            {
                CropTilemap.SetTile(target, null);
            }
            else
            {
                CropTilemap.SetTile(target, data.GrowingCrop.GrowthStagesTiles[data.CurrentGrowthStage]);
            }
        }

        public void Save(ref TerrainDataSave data)
        {
            // Detached values: future watering/growth must not mutate the saved snapshot.
            data = new TerrainDataSave { Farm = State.CaptureSnapshot() };
        }

        public void Load(TerrainDataSave data)
        {
            var snapshot = data.Farm;
            if (snapshot == null || string.IsNullOrEmpty(snapshot.farmId))
            {
                // Compatibility with the template's old in-memory scene saves.
                snapshot = new FarmSnapshot { farmId = State.FarmId };
                for (int i = 0; i < (data.GroundDatas?.Count ?? 0); i++)
                {
                    var plot = State.GetPlot(data.GroundDataPositions[i]);
                    if (plot == null) continue;
                    snapshot.plots.Add(new FarmPlotSnapshot { id = plot.Id, cell = plot.Cell,
                        tilled = true, waterSeconds = data.GroundDatas[i].WaterTimer });
                }
                for (int i = 0; i < (data.CropDatas?.Count ?? 0); i++)
                {
                    var saved = snapshot.plots.Find(p => p.cell == data.CropDataPositions[i]);
                    if (saved == null) continue;
                    var crop = data.CropDatas[i];
                    saved.cropId = crop.CropId;
                    saved.growthStage = crop.Stage;
                    saved.growthProgress = crop.GrowthRatio;
                    saved.growthSeconds = crop.GrowthTimer;
                    saved.drySeconds = crop.DyingTimer;
                    saved.harvestCount = crop.HarvestCount;
                }
            }
            State.RestoreSnapshot(snapshot, id => GameManager.Instance.CropDatabase.GetFromID(id));
            SynchronizeVisuals();
        }
    }

    [Serializable]
    public struct TerrainDataSave
    {
        public FarmSnapshot Farm;
        public List<Vector3Int> GroundDataPositions;
        public List<TerrainManager.GroundData> GroundDatas;

        public List<Vector3Int> CropDataPositions;
        public List<TerrainManager.CropData.SaveData> CropDatas;
    }
}
