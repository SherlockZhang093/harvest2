using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace HappyHarvest
{
    public sealed class NpcWateringDemo : MonoBehaviour
    {
        static NpcWateringDemo instance;
        public static bool BlocksPlayerToolInput => instance != null;
        public NpcWateringAgent Agent { get; private set; }
        public NpcFarmWorker Worker { get; private set; }
        public Vector3Int SelectedCell { get; private set; }
        public bool HasSelection { get; private set; }
        TerrainManager terrain;
        Text status;
        Text targetLabel;
        Button waterButton;
        Button cancelButton;
        RectTransform panel;
        LineRenderer marker;
        Material markerMaterial;
        Font font;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Register()
        {
            instance = null;
            SceneManager.sceneLoaded -= SceneLoaded;
            SceneManager.sceneLoaded += SceneLoaded;
        }

        static void SceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name != "Farm_Outdoor" || instance != null) return;
            new GameObject("NPC Watering Demo").AddComponent<NpcWateringDemo>();
        }

        IEnumerator Start()
        {
            instance = this;
            yield return null;
            var manager = GameManager.Instance;
            terrain = manager.Terrain;
            if (terrain == null || manager.Player == null) { Debug.LogError("Watering demo requires terrain and avatar."); yield break; }
            var avatar = manager.Player;
            Agent = avatar.GetComponent<NpcWateringAgent>();
            if (Agent == null) Agent = avatar.gameObject.AddComponent<NpcWateringAgent>();
            Agent.Initialize(terrain);
            BuildUI();
            UIHandler.ShowAutonomousControls();
            Agent.TaskFinished += OnTaskFinished;
            BuildMarker();
            // Create one dry planted plot in the existing farm, without resetting any existing crop.
            var origin = terrain.Grid.WorldToCell(avatar.transform.position);
            var best = Vector3Int.zero;
            float bestDistance = float.PositiveInfinity;
            bool foundExistingNeed = false;
            foreach (var plot in terrain.State.GetPlotsNeedingWater())
            {
                float distance = (plot.Cell - origin).sqrMagnitude;
                if (distance < bestDistance) { best = plot.Cell; bestDistance = distance; foundExistingNeed = true; }
            }
            if (foundExistingNeed)
            {
                DisplayCell(best);
            }
            else foreach (var cell in terrain.GroundTilemap.cellBounds.allPositionsWithin)
            {
                if (!terrain.IsTillable(cell) || terrain.IsTilled(cell)) continue;
                float distance = (cell - origin).sqrMagnitude;
                if (distance < bestDistance) { best = cell; bestDistance = distance; }
            }
            if (!foundExistingNeed && !float.IsPositiveInfinity(bestDistance))
            {
                terrain.TillAt(best);
                var crop = manager.CropDatabase.GetFromID("carrot_crop");
                if (crop != null) terrain.PlantAt(best, crop);
                SelectCell(best);
            }
            Worker = avatar.GetComponent<NpcFarmWorker>();
            if (Worker == null) Worker = avatar.gameObject.AddComponent<NpcFarmWorker>();
            Worker.Initialize(terrain, Agent);
            Worker.TargetChosen += OnTargetChosen;
        }

        public bool SelectCell(Vector3Int cell)
        {
            if (Agent == null || Agent.IsBusy) return false;
            cell.z = 0;
            if (!terrain.IsTilled(cell))
            {
                status.text = "这里不是耕地，请点击翻过的土地。";
                return false;
            }
            DisplayCell(cell);
            return true;
        }

        void DisplayCell(Vector3Int cell)
        {
            SelectedCell = cell;
            HasSelection = true;
            var a = terrain.Grid.CellToWorld(cell);
            var b = terrain.Grid.CellToWorld(cell + new Vector3Int(1, 1, 0));
            marker.SetPositions(new[] { a, new Vector3(b.x, a.y, a.z), b, new Vector3(a.x, b.y, a.z) });
            marker.enabled = true;
            status.text = terrain.State.GetPlot(cell)?.NeedsWater == true ? "发现缺水作物，NPC 将自动处理。" : "该地块当前不需要浇水。";
        }

        public void SendWateringTask()
        {
            if (Worker != null) { Worker.ScanNow(); status.text = Worker.Status; return; }
            if (HasSelection && Agent != null && !Agent.IsBusy) Agent.RequestWater(SelectedCell);
        }

        void Update()
        {
            if (Agent == null || terrain == null || panel == null) return;
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) Agent.Cancel();
            var mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame && !Agent.IsBusy &&
                !RectTransformUtility.RectangleContainsScreenPoint(panel, mouse.position.ReadValue()) &&
                (EventSystem.current == null || !EventSystem.current.IsPointerOverGameObject()) && Camera.main != null)
            {
                var world = Camera.main.ScreenToWorldPoint(mouse.position.ReadValue());
                SelectCell(terrain.Grid.WorldToCell(world));
            }
            waterButton.interactable = !Agent.IsBusy && GameManager.Instance.IsTicking;
            cancelButton.interactable = Agent.IsBusy;
            if (HasSelection)
            {
                targetLabel.text = $"目标地块 ({SelectedCell.x}, {SelectedCell.y}) · {(terrain.NeedsWater(SelectedCell) ? "干燥" : "湿润")}";
                marker.startColor = marker.endColor = terrain.NeedsWater(SelectedCell) ? new Color(1f, .8f, .2f) : Color.cyan;
            }
            status.text = Agent.IsBusy ? Agent.Status : Worker != null ? Worker.Status : Agent.Status;
        }

        void OnTaskFinished(NpcWateringAgent.TaskState state, string message) => status.text = message;
        void OnTargetChosen(Vector3Int cell) => DisplayCell(cell);

        void BuildMarker()
        {
            var go = new GameObject("Selected Farm Cell");
            go.transform.SetParent(transform);
            marker = go.AddComponent<LineRenderer>();
            markerMaterial = new Material(Shader.Find("Sprites/Default"));
            marker.sharedMaterial = markerMaterial;
            marker.positionCount = 4;
            marker.loop = true;
            marker.widthMultiplier = .055f;
            marker.sortingLayerName = "Foreground";
            marker.sortingOrder = 100;
            marker.enabled = false;
        }

        void BuildUI()
        {
            font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "Noto Sans CJK SC", "Arial" }, 20);
            var canvasObject = new GameObject("Watering Controls", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform);
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.matchWidthOrHeight = .5f;
            if (EventSystem.current == null)
            {
                var events = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
                events.transform.SetParent(transform);
                events.GetComponent<InputSystemUIInputModule>().AssignDefaultActions();
            }
            panel = Rect("NPC 浇水试验", canvasObject.transform, 20, 92, 360, 230);
            panel.gameObject.AddComponent<Image>().color = new Color(.08f, .13f, .15f, .96f);
            Label("AI NPC 自动农务", panel, 16, 12, 328, 32, 23);
            Label("自动播种、浇水、施肥、除草和收获", panel, 16, 49, 328, 30, 16);
            targetLabel = Label("请选择一块耕地", panel, 16, 83, 328, 28, 17);
            status = Label("等待浇水任务", panel, 16, 116, 328, 50, 16);
            waterButton = MakeButton("立即扫描", 16, 174, 206, SendWateringTask);
            cancelButton = MakeButton("取消", 234, 174, 110, () => Agent.Cancel());
        }

        static RectTransform Rect(string name, Transform parent, float x, float y, float width, float height)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
            return rect;
        }

        Text Label(string value, Transform parent, float x, float y, float width, float height, int size)
        {
            var text = Rect(value, parent, x, y, width, height).gameObject.AddComponent<Text>();
            text.font = font;
            text.text = value;
            text.fontSize = size;
            text.color = new Color(.94f, .96f, .9f);
            text.raycastTarget = false;
            text.alignment = TextAnchor.MiddleLeft;
            return text;
        }

        Button MakeButton(string title, float x, float y, float width, UnityEngine.Events.UnityAction action)
        {
            var rect = Rect(title, panel, x, y, width, 40);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = new Color(.25f, .45f, .35f);
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(action);
            var colors = button.colors;
            colors.disabledColor = new Color(.4f, .4f, .4f, .6f);
            button.colors = colors;
            Label(title, rect, 0, 0, width, 40, 18).alignment = TextAnchor.MiddleCenter;
            return button;
        }

        void OnDestroy()
        {
            if (Agent != null) { Agent.TaskFinished -= OnTaskFinished; Agent.Cancel(); }
            if (Worker != null) Worker.TargetChosen -= OnTargetChosen;
            if (instance == this) instance = null;
            if (markerMaterial != null) Destroy(markerMaterial);
            if (font != null) Destroy(font);
        }
    }
}
