using System;
using System.IO;
using System.Linq;
using HappyHarvest;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

[CustomEditor(typeof(FarmState))]
public sealed class FarmStateEditor : Editor
{
    string search = "";
    int filter;
    bool raw;
    Vector2 scroll;
    string selectedId;
    static readonly string[] Filters = { "全部地块", "需要浇水", "需要施肥", "需要除草", "可播种", "可收获", "任务占用" };

    public override bool RequiresConstantRepaint() => Application.isPlaying;

    public override void OnInspectorGUI()
    {
        var farm = (FarmState)target;
        EditorGUILayout.LabelField("农田状态 · AI 读取源", EditorStyles.boldLabel);
        EditorGUILayout.LabelField($"{farm.FarmId}  |  版本 {farm.Revision}  |  运行 {farm.ElapsedSeconds:F1} 秒");
        EditorGUILayout.HelpBox($"共 {farm.Plots.Count} 格 · 可耕种 {farm.Plots.Count(p => p.IsCultivable)} 格\n" +
            $"已种植 {farm.Plots.Count(p => p.HasCrop)} · 缺水 {farm.Plots.Count(p => p.NeedsWater)} · " +
            $"待施肥 {farm.Plots.Count(p => p.NeedsFertilizer)} · 有杂草 {farm.Plots.Count(p => p.NeedsWeeding)} · 可收获 {farm.Plots.Count(p => p.CanHarvest)}", MessageType.Info);
        EditorGUILayout.HelpBox("运行时直接查看同一个 SO 的实时状态；退出 Play 后恢复运行前内容。", MessageType.None);
        if (GUILayout.Button("导出当前 AI 数据到 Logs/farm-state-ai.json"))
        {
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/farm-state-ai.json", farm.ToAiJson());
            Debug.Log("Exported farm state to Logs/farm-state-ai.json");
        }
        search = EditorGUILayout.TextField("编号 / 坐标搜索", search);
        filter = EditorGUILayout.Popup("筛选", filter, Filters);
        scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MaxHeight(280));
        foreach (var p in farm.Plots)
        {
            if (!string.IsNullOrEmpty(search) && p.Id.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if ((filter == 1 && !p.NeedsWater) || (filter == 2 && !p.NeedsFertilizer) ||
                (filter == 3 && !p.NeedsWeeding) || (filter == 4 && !p.CanPlant) ||
                (filter == 5 && !p.CanHarvest) || (filter == 6 && !p.IsReserved)) continue;
            string crop = p.HasCrop ? p.Crop.GrowingCrop.Key : "无作物";
            string soil = !p.IsCultivable ? "不可耕作" : !p.IsTilled ? "未开垦" : p.IsWet ? "湿润" : "干燥";
            if (GUILayout.Button($"({p.Cell.x}, {p.Cell.y})  {soil}  {crop}")) selectedId = p.Id;
        }
        EditorGUILayout.EndScrollView();
        var selected = farm.GetPlot(selectedId);
        if (selected != null)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(selected.Id, EditorStyles.boldLabel);
            EditorGUILayout.LabelField("世界位置", selected.WorldCenter.ToString());
            EditorGUILayout.LabelField("水分剩余", $"{selected.WaterSeconds:F1} 秒");
            EditorGUILayout.LabelField("需要浇水 / 可以浇水", $"{selected.NeedsWater} / {selected.CanWater}");
            if (selected.HasCrop)
            {
                EditorGUILayout.LabelField("作物 / 生长阶段", $"{selected.Crop.GrowingCrop.Key} / {selected.Crop.CurrentGrowthStage}");
                EditorGUILayout.LabelField("生长进度 / 连续缺水", $"{selected.Crop.GrowthRatio:P0} / {selected.Crop.DyingTimer:F1} 秒");
            }
            EditorGUILayout.LabelField("施肥 / 杂草", $"{selected.IsFertilized} / {selected.HasWeeds}");
            EditorGUILayout.LabelField("杂草计时", $"{selected.WeedTimer:F1} / {TerrainManager.WeedInterval:F0} 秒");
            EditorGUILayout.LabelField("执行者 / 行为", $"{selected.ReservedBy} / {selected.CurrentAction}");
        }
        raw = EditorGUILayout.Foldout(raw, "原始序列化数据（编辑初始状态）");
        if (raw)
        {
            using (new EditorGUI.DisabledScope(Application.isPlaying)) DrawDefaultInspector();
        }
    }
}

[InitializeOnLoad]
public static class FarmStateAuthoring
{
    public const string AssetPath = "Assets/HappyHarvest/Resources/FarmState.asset";
    const string BackupKey = "HappyHarvest.FarmState.BeforePlay.";

    static FarmStateAuthoring() => EditorApplication.playModeStateChanged += OnPlayModeChanged;

    static void OnPlayModeChanged(PlayModeStateChange change)
    {
        if (change != PlayModeStateChange.ExitingEditMode && change != PlayModeStateChange.EnteredEditMode) return;
        foreach (var guid in AssetDatabase.FindAssets("t:FarmState"))
        {
            var farm = AssetDatabase.LoadAssetAtPath<FarmState>(AssetDatabase.GUIDToAssetPath(guid));
            if (change == PlayModeStateChange.ExitingEditMode)
            {
                SessionState.SetString(BackupKey + guid, EditorJsonUtility.ToJson(farm));
                SessionState.SetBool(BackupKey + guid + ".dirty", EditorUtility.IsDirty(farm));
            }
            else
            {
                var json = SessionState.GetString(BackupKey + guid, "");
                if (string.IsNullOrEmpty(json)) continue;
                EditorJsonUtility.FromJsonOverwrite(json, farm);
                farm.RebuildIndex();
                if (SessionState.GetBool(BackupKey + guid + ".dirty", false)) EditorUtility.SetDirty(farm);
                else EditorUtility.ClearDirty(farm);
                SessionState.EraseString(BackupKey + guid);
            }
        }
    }

    [MenuItem("Tools/Happy Harvest/Select Farm State %#F7")]
    public static void SelectState() => Selection.activeObject = AssetDatabase.LoadAssetAtPath<FarmState>(AssetPath);

    [MenuItem("Tools/Happy Harvest/Create Farm State from Scene %#F6")]
    public static void CreateFromScene()
    {
        if (EditorApplication.isPlaying) return;
        var terrain = Object.FindObjectOfType<TerrainManager>();
        if (terrain == null || terrain.gameObject.scene.name != "Farm_Outdoor")
            throw new InvalidOperationException("Open Farm_Outdoor first.");
        var existing = AssetDatabase.LoadAssetAtPath<FarmState>(AssetPath);
        if (existing != null) { Selection.activeObject = existing; return; }
        bool wasDirty = terrain.gameObject.scene.isDirty;
        var farm = ScriptableObject.CreateInstance<FarmState>();
        farm.FarmId = terrain.gameObject.scene.name;
        terrain.State = farm;
        terrain.RegisterScenePlots();
        foreach (var initializer in Object.FindObjectsOfType<CropInitializer>())
            foreach (var seed in initializer.InitList)
            {
                var p = farm.GetPlot((Vector3Int)seed.Cell);
                if (p == null || !p.IsCultivable || seed.CropToPlant == null) continue;
                p.IsTilled = true;
                p.WaterSeconds = TerrainManager.GroundData.WaterDuration;
                int stage = Mathf.Clamp(seed.StartingStage, 0, seed.CropToPlant.GrowthStagesTiles.Length - 1);
                float ratio = stage / (float)Mathf.Max(1, seed.CropToPlant.GrowthStagesTiles.Length - 1);
                p.Crop = new TerrainManager.CropData { GrowingCrop = seed.CropToPlant,
                    CurrentGrowthStage = stage, GrowthRatio = ratio, GrowthTimer = ratio * seed.CropToPlant.GrowthTime };
            }
        // Make the minimum demo's initial dry crop part of the authored data as well.
        var player = Object.FindObjectOfType<PlayerController>();
        var carrot = AssetDatabase.LoadAssetAtPath<Crop>("Assets/HappyHarvest/Data/Crops/CarrotCrop.asset");
        if (player != null && carrot != null)
        {
            var origin = terrain.Grid.WorldToCell(player.transform.position);
            var dry = farm.Plots.Where(p => p.CanTill).OrderBy(p => (p.Cell - origin).sqrMagnitude).FirstOrDefault();
            if (dry != null) { dry.IsTilled = true; dry.Crop = new TerrainManager.CropData { GrowingCrop = carrot }; }
        }
        farm.HasAuthoredInitialState = true;
        Directory.CreateDirectory(Path.GetDirectoryName(AssetPath));
        AssetDatabase.CreateAsset(farm, AssetPath);
        AssetDatabase.SaveAssets();
        EditorUtility.SetDirty(terrain);
        EditorSceneManager.MarkSceneDirty(terrain.gameObject.scene);
        if (!wasDirty) EditorSceneManager.SaveScene(terrain.gameObject.scene);
        Selection.activeObject = farm;
        Directory.CreateDirectory("Logs");
        File.WriteAllText("Logs/farm-state-ai.json", farm.ToAiJson());
        File.WriteAllText("Logs/farm-state-bake.txt", $"Created {AssetPath}\nPlots: {farm.Plots.Count}\nCultivable: {farm.Plots.Count(p => p.IsCultivable)}\nCrops: {farm.Plots.Count(p => p.HasCrop)}\nNeed water: {farm.Plots.Count(p => p.NeedsWater)}\n");
        Debug.Log($"Created farm state: {farm.Plots.Count} registered cells. Select the asset to inspect AI data.");
    }
}
