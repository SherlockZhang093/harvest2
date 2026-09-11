using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace HappyHarvest
{
    public enum NpcScheduleMode { Append, Priority, CancelCurrent, CancelAll }
    public enum NpcGoalKind { FarmCycle, Plant, Water, Fertilize, Weed, Harvest, Sleep, Unknown }

    [Serializable]
    public sealed class NpcAiRequest
    {
        public int schemaVersion = 1;
        public long requestId;
        public string playerText;
        public string persona;
        public string instructions;
        public string currentScene;
        public string currentTask;
        public int currentTargetCount;
        public bool currentHasWorked;
        public string[] pendingTasks;
        public string[] recentMemory;
        public NpcNeedsSnapshot needs;
        public FarmSnapshot farm;
        public NpcInventorySnapshot[] inventory;
        public string[] supportedGoals = { "farm_cycle", "plant", "water", "fertilize", "weed", "harvest", "sleep" };
        public string[] scheduleModes = { "append", "priority", "cancel_current", "cancel_all" };
    }

    [Serializable]
    public sealed class NpcAiResponse
    {
        public int schemaVersion = 1;
        public long requestId;
        public string scheduleMode;
        public string goal;
        public string cropId;
        public NpcAiTask[] tasks;
        public string dialogue;
        public string error;
    }

    [Serializable]
    public sealed class NpcAiTask
    {
        public string goal;
        public string cropId;
    }

    [Serializable]
    public sealed class NpcNeedsSnapshot
    {
        public float stamina;
        public float hunger;
        public float thirst;
        public string mood;
        public bool resting;
        public bool sleeping;
    }

    [Serializable]
    public sealed class NpcInventorySnapshot
    {
        public string itemId;
        public string displayName;
        public int count;
        public string itemType;
    }

    /// <summary>
    /// Replace this provider with the real API adapter later. Both providers use the same request/response contract.
    /// </summary>
    public interface INpcDecisionProvider
    {
        void Decide(NpcAiRequest request, Action<NpcAiResponse> completed);
    }

    /// <summary>Implemented by the separate house drinking-station module.</summary>
    public interface INpcDrinkSource
    {
        bool CanDrink { get; }
        bool TryDrink(PlayerController npc);
    }

    public sealed class LocalNpcDecisionProvider : INpcDecisionProvider
    {
        public void Decide(NpcAiRequest request, Action<NpcAiResponse> completed)
        {
            string text = (request.playerText ?? "").Trim().ToLowerInvariant();
            var response = new NpcAiResponse { requestId = request.requestId };
            response.scheduleMode = ContainsAny(text, "全部取消", "都取消", "清空任务") ? "cancel_all" :
                ContainsAny(text, "停下", "别干", "取消当前") ? "cancel_current" :
                ContainsAny(text, "先", "马上", "立刻", "优先") ? "priority" : "append";

            if (response.scheduleMode == "cancel_all" || response.scheduleMode == "cancel_current")
            {
                response.goal = "unknown";
                response.dialogue = response.scheduleMode == "cancel_all" ? "行，今天的安排全撤了。" : "好好好，这件先不干了。";
            }
            else if (ContainsAny(text, "干农活", "农活", "种地")) Set(response, "farm_cycle", "这么一整套都归我？行吧，我会做到收获。", text);
            else if (ContainsAny(text, "睡觉", "回家睡")) Set(response, "sleep", "终于肯让我收工了？这就回家。", text);
            else
            {
                var tasks = ExtractOrderedTasks(text);
                if (tasks.Count > 0)
                {
                    response.tasks = tasks.ToArray();
                    response.goal = tasks[0].goal;
                    response.cropId = tasks[0].cropId;
                    response.dialogue = tasks.Count > 1 ? "行，我按你说的顺序一件件做。" : DialogueFor(tasks[0].goal);
                }
                else
                {
                    response.goal = "unknown";
                    response.dialogue = "这话我没听懂。你可以让我去干农活、播种、浇水、除草、施肥或收获。";
                    response.error = "unsupported_intent";
                }
            }
            completed?.Invoke(response);
        }

        static void Set(NpcAiResponse response, string goal, string dialogue, string text)
        {
            response.goal = goal;
            response.dialogue = dialogue;
            if (text.Contains("胡萝卜")) response.cropId = "carrot_crop";
            else if (text.Contains("玉米")) response.cropId = "corn_crop";
            else if (text.Contains("小麦")) response.cropId = "wheat_crop";
            response.tasks = new[] { new NpcAiTask { goal = response.goal, cropId = response.cropId } };
        }

        static List<NpcAiTask> ExtractOrderedTasks(string text)
        {
            var found = new List<(int position, string goal)>();
            AddFirst(found, text, "plant", "播种", "种胡萝卜", "种玉米", "种小麦");
            AddFirst(found, text, "water", "浇水");
            AddFirst(found, text, "fertilize", "施肥");
            AddFirst(found, text, "weed", "除草", "拔草");
            AddFirst(found, text, "harvest", "收获", "收菜", "采收");
            return found.OrderBy(x => x.position).Select(x => new NpcAiTask
            {
                goal = x.goal,
                cropId = text.Contains("胡萝卜") ? "carrot_crop" : text.Contains("玉米") ? "corn_crop" :
                    text.Contains("小麦") ? "wheat_crop" : ""
            }).ToList();
        }

        static void AddFirst(List<(int position, string goal)> output, string text, string goal, params string[] words)
        {
            int position = words.Select(word => text.IndexOf(word, StringComparison.Ordinal))
                .Where(x => x >= 0).DefaultIfEmpty(-1).Min();
            if (position >= 0) output.Add((position, goal));
        }

        static string DialogueFor(string goal) => goal switch
        {
            "plant" => "又要弯腰播种了……行，我去。",
            "water" => "又要拎水壶了……我去浇。",
            "fertilize" => "行，我去施肥。",
            "weed" => "草倒是长得比庄稼积极……我去处理。",
            "harvest" => "知道了，成熟的我都收回来。",
            _ => "知道了。"
        };

        static bool ContainsAny(string value, params string[] needles) => needles.Any(value.Contains);
    }

    [DefaultExecutionOrder(-100)]
    public sealed class NpcCommandSystem : MonoBehaviour
    {
        sealed class WorkOrder
        {
            public long Id;
            public NpcGoalKind Goal;
            public string CropId;
            public string Dialogue;
            public bool Started;
            public bool HasWorked;
            public bool ResumeInterruptedAfter;
            public readonly HashSet<string> Targets = new();
        }

        static NpcCommandSystem instance;
        public static NpcCommandSystem Instance => instance;

        [Header("Needs (0-100)")]
        [SerializeField] float stamina = 100;
        [SerializeField] float hunger = 100;
        [SerializeField] float thirst = 100;
        [SerializeField] float hungerLossPerSecond = .08f;
        [SerializeField] float thirstLossPerSecond = .12f;
        [SerializeField] float eatThreshold = 35f;
        [SerializeField] float drinkThreshold = 35f;
        [SerializeField] float laborStaminaCost = 7f;
        [SerializeField] float restRecoveryPerSecond = 18f;
        [SerializeField] float sleepHour = 22f;
        [SerializeField] float wakeHour = 6f;

        readonly LinkedList<WorkOrder> queue = new();
        readonly Stack<WorkOrder> interrupted = new();
        readonly List<string> memory = new();
        INpcDecisionProvider decisionProvider;
        WorkOrder current;
        NpcWateringAgent agent;
        TerrainManager terrain;
        PlayerController npc;
        SeedBag activeSeed;
        int activeSeedIndex = -1;
        long nextRequestId;
        long newestAcceptedRequest;
        bool resting;
        bool sleeping;
        bool goingToSleep;
        bool travelling;
        bool travelArrived;
        float travelRetryAfter;
        float hungryReminderAfter;
        bool awaitingDrinkSource;
        float bubbleUntil;
        string mood = "平静";
        string stepBlockReason;

        Canvas canvas;
        InputField input;
        Text statusText;
        Text needsText;
        Text planText;
        Text bubbleText;
        GameObject bubbleObject;
        Font runtimeFont;
        string lastPlanSummary = "尚未收到指令";
        string travelPurpose;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Boot() => EnsureCreated();

        public static void EnsureCreated()
        {
            if (instance != null) return;
            var found = FindObjectOfType<NpcCommandSystem>();
            if (found != null) { instance = found; return; }
            new GameObject("NPC Command System").AddComponent<NpcCommandSystem>();
        }

        void Awake()
        {
            if (instance != null && instance != this) { Destroy(gameObject); return; }
            instance = this;
            DontDestroyOnLoad(gameObject);
            var localProvider = new LocalNpcDecisionProvider();
            decisionProvider = NpcDecisionProviderFactory.Create(this, localProvider);
            SceneManager.sceneLoaded += OnSceneLoaded;
            BuildUI();
        }

        public void SetDecisionProvider(INpcDecisionProvider provider)
        {
            decisionProvider = provider ?? throw new ArgumentNullException(nameof(provider));
        }

        IEnumerator Start()
        {
            yield return null;
            EnsureSceneEventSystem(SceneManager.GetActiveScene());
            AttachToScene(SceneManager.GetActiveScene());
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            EnsureSceneEventSystem(scene);
            StartCoroutine(AttachNextFrame(scene));
        }

        static void EnsureSceneEventSystem(Scene scene)
        {
            // Scene EventSystems register in OnEnable before sceneLoaded is raised.
            // Creating one in Awake during BeforeSceneLoad would duplicate them.
            if (EventSystem.current != null || !scene.IsValid() || !scene.isLoaded) return;

            var eventObject = new GameObject("NPC Command EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            // Keep the fallback in this scene, not under the persistent NPC system.
            SceneManager.MoveGameObjectToScene(eventObject, scene);
            eventObject.GetComponent<InputSystemUIInputModule>().AssignDefaultActions();
        }

        IEnumerator AttachNextFrame(Scene scene)
        {
            yield return null;
            AttachToScene(scene);
        }

        void AttachToScene(Scene scene)
        {
            if (canvas != null)
                canvas.gameObject.SetActive(scene.name == "Farm_Outdoor" || scene.name == "House_Interior");
            npc = GameManager.Instance != null ? GameManager.Instance.Player : FindObjectOfType<PlayerController>();
            terrain = GameManager.Instance != null ? GameManager.Instance.Terrain : FindObjectOfType<TerrainManager>();
            if (scene.name == "Farm_Outdoor" && npc != null && terrain != null)
            {
                agent = npc.GetComponent<NpcWateringAgent>() ?? npc.gameObject.AddComponent<NpcWateringAgent>();
                agent.Initialize(terrain);
                agent.TaskFinished -= OnTaskFinished;
                agent.TaskFinished += OnTaskFinished;
                var oldWorker = npc.GetComponent<NpcFarmWorker>();
                if (oldWorker != null) oldWorker.AutoWorkEnabled = false;
                if (!sleeping) SetStatus(current == null ? "等待你的安排" : "回到农场，继续之前的工作");
            }
            else
            {
                agent = npc != null
                    ? npc.GetComponent<NpcWateringAgent>() ?? npc.gameObject.AddComponent<NpcWateringAgent>()
                    : null;
                if (!travelling && goingToSleep) StartCoroutine(MoveToNamedObject("Bed", BeginSleeping));
                else if (!travelling && awaitingDrinkSource) StartCoroutine(MoveToDrinkSource());
            }
        }

        void Update()
        {
            if (GameManager.Instance == null || npc == null) return;
            TickNeeds();
            UpdateUI();
            if (travelling || sleeping) { TickSleep(); return; }
            if (Time.time < travelRetryAfter) return;

            // 生存需求优先：饥渴先于休息、到点睡觉和农活处理。
            // 否则 resting 或睡眠检查会先 return，饥渴再低也轮不到。
            if (hunger <= eatThreshold) TryEatFromInventory();
            if (thirst <= drinkThreshold && !awaitingDrinkSource)
            {
                awaitingDrinkSource = true;
                StartTripHome(true);
                return;
            }

            float hour = GameManager.GetHourFromRatio(GameManager.Instance.CurrentDayRatio) +
                         GameManager.GetMinuteFromRatio(GameManager.Instance.CurrentDayRatio) / 60f;
            if (hour >= sleepHour && SceneManager.GetActiveScene().name == "Farm_Outdoor")
            {
                StartTripHome(false);
                return;
            }
            if (hour >= sleepHour && SceneManager.GetActiveScene().name == "House_Interior")
            {
                StartTripHome(false);
                return;
            }
            if (resting)
            {
                stamina = Mathf.Min(100, stamina + restRecoveryPerSecond * Time.deltaTime);
                if (stamina >= 55) { resting = false; mood = "平静"; Say("歇够了，接着干。", "🙂"); }
                return;
            }
            if (stamina <= 12)
            {
                resting = true;
                mood = "疲惫";
                Say("这回是真累了，我歇一会儿再接着干。", "💤");
                return;
            }
            if (agent == null || agent.IsBusy) return;
            ProcessNextStep();
        }

        void TickNeeds()
        {
            if (!GameManager.Instance.IsTicking || sleeping) return;
            hunger = Mathf.Clamp(hunger - hungerLossPerSecond * Time.deltaTime, 0, 100);
            thirst = Mathf.Clamp(thirst - thirstLossPerSecond * Time.deltaTime, 0, 100);
        }

        void TickSleep()
        {
            if (!sleeping || travelling) return;
            float hour = GameManager.GetHourFromRatio(GameManager.Instance.CurrentDayRatio) +
                         GameManager.GetMinuteFromRatio(GameManager.Instance.CurrentDayRatio) / 60f;
            if (hour >= wakeHour && hour < sleepHour)
            {
                sleeping = false;
                stamina = 100;
                hunger = Mathf.Max(hunger, 55);
                thirst = Mathf.Max(thirst, 55);
                Say("天亮了……昨天的活还得接着干。", "☀");
                StartCoroutine(WalkToExitThenChangeScene("Exit_Trigger", 2, 0, "Farm_Outdoor"));
            }
        }

        void StartTripHome(bool forDrink)
        {
            if (travelling) return;
            if (SceneManager.GetActiveScene().name == "House_Interior")
            {
                if (forDrink) { awaitingDrinkSource = true; StartCoroutine(MoveToDrinkSource()); }
                else { goingToSleep = true; StartCoroutine(MoveToNamedObject("Bed", BeginSleeping)); }
                return;
            }
            if (SceneManager.GetActiveScene().name != "Farm_Outdoor") return;
            if (agent != null && agent.IsBusy) agent.Cancel();
            awaitingDrinkSource = forDrink;
            goingToSleep = !forDrink;
            mood = "疲惫";
            Say(forDrink ? "渴得不行了，我回家喝口水。" : "到点了，我得回家睡觉。", forDrink ? "💧" : "🌙");
            StartCoroutine(WalkToExitThenChangeScene("HouseEntrance", 3, 1, "House_Interior"));
        }

        IEnumerator WalkToExitThenChangeScene(string exitName, int targetScene, int targetSpawn, string sceneName)
        {
            var exitObject = GameObject.Find(exitName);
            if (exitObject == null)
            {
                travelling = false;
                // 出行失败要清掉目的标记并延迟重试，否则喝水/睡觉请求会永久挂起。
                awaitingDrinkSource = false;
                goingToSleep = false;
                travelRetryAfter = Time.time + 5f;
                SetStatus("没有找到场景出口 " + exitName);
                yield break;
            }
            yield return WalkToTarget(exitObject.transform, .45f, true);
            if (!travelArrived) yield break;
            GameManager.Instance.MoveTo(targetScene, targetSpawn);
            yield return ClearTravelAfterScene(sceneName);
        }

        IEnumerator ClearTravelAfterScene(string sceneName)
        {
            while (SceneManager.GetActiveScene().name != sceneName) yield return null;
            travelling = false;
            AttachToScene(SceneManager.GetActiveScene());
        }

        IEnumerator MoveToNamedObject(string objectName, Action arrived)
        {
            var target = GameObject.Find(objectName);
            if (target == null || npc == null)
            {
                travelling = false;
                SetStatus("家中没有找到 " + objectName);
                yield break;
            }
            yield return WalkToTarget(target.transform, .35f, false);
            travelling = false;
            if (travelArrived) arrived?.Invoke();
        }

        IEnumerator WalkToTarget(Transform target, float stoppingDistance, bool keepTravellingOnArrival)
        {
            travelling = true;
            travelArrived = false;
            travelPurpose = goingToSleep ? "前往床边睡觉" : awaitingDrinkSource ? "前往饮水处" : "前往 " + target.name;
            var body = npc.GetComponent<Rigidbody2D>();
            var route = new List<Vector2>();
            if (SceneManager.GetActiveScene().name == "Farm_Outdoor" && agent != null &&
                !agent.TryBuildTravelPath(target.position, out route))
            {
                FailTravel("找不到可以走到 " + target.name + " 的道路");
                yield break;
            }
            if (route.Count == 0)
            {
                // Furniture origins can be inside solid colliders. Approach its surface,
                // leaving space for the character's feet collider instead of walking into it.
                Vector2 destination = target.position;
                var feet = npc.GetComponentInChildren<CircleCollider2D>();
                if (feet != null)
                {
                    Vector2 center = feet.transform.TransformPoint(feet.offset);
                    float radius = feet.radius * Mathf.Max(Mathf.Abs(feet.transform.lossyScale.x), Mathf.Abs(feet.transform.lossyScale.y));
                    var solid = target.GetComponentsInChildren<Collider2D>()
                        .Where(c => c.enabled && !c.isTrigger)
                        .OrderBy(c => ((Vector2)c.ClosestPoint(center) - center).sqrMagnitude).FirstOrDefault();
                    if (solid != null)
                    {
                        Vector2 surface = solid.ClosestPoint(center);
                        Vector2 outward = (center - surface).normalized;
                        if (outward == Vector2.zero) outward = Vector2.down;
                        destination = surface + outward * (radius + .08f) - (center - body.position);
                    }
                }
                route.Add(destination);
            }
            float elapsed = 0f;
            float stalled = 0f;
            Vector2 lastPosition = body.position;
            foreach (var waypoint in route)
            {
                // Reach each waypoint accurately so corners are not cut into furniture.
                while (Vector2.Distance(body.position, waypoint) > Mathf.Min(stoppingDistance, .035f))
                {
                    if (!GameManager.Instance.IsTicking)
                    {
                        if (agent != null) agent.StopTravelMotion();
                        yield return new WaitForFixedUpdate();
                        lastPosition = body.position;
                        continue;
                    }
                    elapsed += Time.fixedDeltaTime;
                    stalled = Vector2.Distance(body.position, lastPosition) < .001f ? stalled + Time.fixedDeltaTime : 0f;
                    lastPosition = body.position;
                    if (stalled > 2f || elapsed > 60f)
                    {
                        FailTravel("前往 " + target.name + " 的道路受阻，稍后重试");
                        yield break;
                    }
                    var next = Vector2.MoveTowards(body.position, waypoint, 2.5f * Time.fixedDeltaTime);
                    if (agent != null) agent.MoveForTravel(next);
                    else body.MovePosition(next);
                    yield return new WaitForFixedUpdate();
                }
            }
            if (agent != null) agent.StopTravelMotion();
            travelArrived = true;
            travelling = keepTravellingOnArrival;
        }

        void FailTravel(string reason)
        {
            if (agent != null) agent.StopTravelMotion();
            travelling = false;
            travelArrived = false;
            travelRetryAfter = Time.time + 5f;
            SetStatus(reason);
        }

        IEnumerator MoveToDrinkSource()
        {
            travelling = true;
            var sources = FindObjectsOfType<MonoBehaviour>().OfType<INpcDrinkSource>().ToArray();
            var source = sources.FirstOrDefault(x => x.CanDrink);
            if (source == null)
            {
                travelling = false;
                // 重置等待标记并延迟重试。之前这里不清 awaitingDrinkSource，
                // 会导致 NPC 此后再渴也不会触发回家喝水，永久卡死。
                awaitingDrinkSource = false;
                travelRetryAfter = Time.time + 10f;
                SetStatus("等待家中饮水机模块接入");
                yield break;
            }
            var target = ((MonoBehaviour)source).transform;
            yield return WalkToTarget(target, .45f, false);
            travelling = false;
            if (!travelArrived) yield break;
            if (source.TryDrink(npc))
            {
                thirst = 100;
                awaitingDrinkSource = false;
                Say("总算活过来了。", "💧");
                float hour = GameManager.GetHourFromRatio(GameManager.Instance.CurrentDayRatio) +
                             GameManager.GetMinuteFromRatio(GameManager.Instance.CurrentDayRatio) / 60f;
                if (hour >= sleepHour)
                {
                    goingToSleep = true;
                    StartCoroutine(MoveToNamedObject("Bed", BeginSleeping));
                    yield break;
                }
                StartCoroutine(WalkToExitThenChangeScene("Exit_Trigger", 2, 0, "Farm_Outdoor"));
            }
        }

        void BeginSleeping()
        {
            goingToSleep = false;
            awaitingDrinkSource = false;
            sleeping = true;
            mood = "平静";
            Say("晚安。明天的活明天再说。", "💤");
            SetStatus("正在睡觉，06:00 起床");
        }

        void SubmitCommand()
        {
            string value = input != null ? input.text.Trim() : "";
            if (string.IsNullOrEmpty(value)) return;
            input.text = "";
            long requestId = ++nextRequestId;
            var request = BuildRequest(requestId, value);
            SetStatus("正在理解：" + value);
            decisionProvider.Decide(request, ApplyDecision);
        }

        NpcAiRequest BuildRequest(long id, string playerText)
        {
            return new NpcAiRequest
            {
                requestId = id,
                playerText = playerText,
                persona = "嘴上爱抱怨，但做事靠谱；轻度喜剧感；不会因心情擅自拒绝可执行工作。",
                instructions = "结合玩家指令与游戏快照生成工作安排。只能使用 supportedGoals；判断 append、priority、cancel_current 或 cancel_all；可返回有序 tasks。不要修改体力、背包、农田或声称动作已经成功。只返回 NpcAiResponse JSON。",
                currentScene = SceneManager.GetActiveScene().name,
                currentTask = current == null ? "none" : current.Goal.ToString(),
                currentTargetCount = current?.Targets.Count ?? 0,
                currentHasWorked = current?.HasWorked ?? false,
                pendingTasks = queue.Select(x => x.Goal.ToString()).ToArray(),
                recentMemory = memory.ToArray(),
                needs = NeedsSnapshot(),
                farm = terrain != null && terrain.State != null ? terrain.State.CaptureSnapshot() : null,
                inventory = CaptureInventory()
            };
        }

        void ApplyDecision(NpcAiResponse response)
        {
            if (response == null || response.schemaVersion != 1 || response.requestId <= 0 ||
                response.requestId > nextRequestId || response.requestId < newestAcceptedRequest)
            {
                SetStatus("忽略了无效或过时的 AI 回复");
                return;
            }
            newestAcceptedRequest = response.requestId;
            if (!TryParseMode(response.scheduleMode, out var mode)) { SetStatus("AI 返回了未知任务安排方式"); return; }
            if (mode == NpcScheduleMode.CancelAll)
            {
                CancelAgent(); queue.Clear(); interrupted.Clear(); current = null;
                lastPlanSummary = "AI 规划：清空全部任务";
                Say(response.dialogue, "✓"); return;
            }
            if (mode == NpcScheduleMode.CancelCurrent)
            {
                CancelAgent(); current = null;
                lastPlanSummary = "AI 规划：取消当前任务";
                Say(response.dialogue, "✓"); return;
            }
            var taskResults = response.tasks != null && response.tasks.Length > 0
                ? response.tasks
                : new[] { new NpcAiTask { goal = response.goal, cropId = response.cropId } };
            var orders = new List<WorkOrder>();
            foreach (var task in taskResults)
            {
                if (!TryParseGoal(task.goal, out var parsedGoal)) continue;
                orders.Add(new WorkOrder
                {
                    Id = response.requestId,
                    Goal = parsedGoal,
                    CropId = task.cropId,
                    Dialogue = response.dialogue
                });
            }
            if (orders.Count == 0) { Say(response.dialogue, "?"); return; }
            lastPlanSummary = "AI 规划：" + string.Join(" → ", orders.Select(x => GoalLabel(x.Goal)));
            if (mode == NpcScheduleMode.Priority)
            {
                if (current != null) interrupted.Push(current);
                CancelAgent();
                current = orders[0];
                for (int i = orders.Count - 1; i >= 1; i--) queue.AddFirst(orders[i]);
                orders[orders.Count - 1].ResumeInterruptedAfter = true;
            }
            else foreach (var order in orders) queue.AddLast(order);
            Say(response.dialogue, mode == NpcScheduleMode.Priority ? "!" : "✓");
        }

        void ProcessNextStep()
        {
            if (current == null)
            {
                if (queue.Count > 0) { current = queue.First.Value; queue.RemoveFirst(); }
                else if (interrupted.Count > 0) current = interrupted.Pop();
                else return;
            }
            if (current.Goal == NpcGoalKind.Sleep) { StartTripHome(false); current = null; return; }
            if (terrain == null || terrain.State == null)
            {
                if (SceneManager.GetActiveScene().name == "House_Interior")
                {
                    travelling = true;
                    travelPurpose = "从家中前往农场执行任务";
                    SetStatus("收到农务安排，正在出门");
                    StartCoroutine(WalkToExitThenChangeScene("Exit_Trigger", 2, 0, "Farm_Outdoor"));
                }
                else
                {
                    SetStatus("当前场景没有可用的农田，任务正在等待");
                }
                return;
            }
            if (!current.Started) StartOrder(current);
            if (TryStartFarmAction(current)) return;
            if (travelling) return;
            if (!string.IsNullOrEmpty(stepBlockReason))
            {
                SetStatus(stepBlockReason);
                return;
            }

            bool waitingForGrowth = current.Goal == NpcGoalKind.FarmCycle &&
                current.Targets.Select(terrain.State.GetPlot).Any(p => p != null && p.HasCrop);
            if (waitingForGrowth)
            {
                SetStatus("作物正在生长，等待下一项农活");
                return;
            }
            string message = current.HasWorked ? "这一轮农活完成了，终于能歇会儿。" : "现在没有符合条件的农活。";
            memory.Add(message);
            if (memory.Count > 8) memory.RemoveAt(0);
            bool resumeInterrupted = current.ResumeInterruptedAfter;
            current = resumeInterrupted && interrupted.Count > 0 ? interrupted.Pop() : null;
            Say(message, "✓");
        }

        void StartOrder(WorkOrder order)
        {
            order.Started = true;
            IEnumerable<FarmPlotState> plots = terrain.State.Plots.Where(p => !p.IsReserved);
            switch (order.Goal)
            {
                case NpcGoalKind.FarmCycle:
                    foreach (var p in plots.Where(p => p.HasCrop || p.CanPlant)) order.Targets.Add(p.Id);
                    break;
                case NpcGoalKind.Plant: foreach (var p in plots.Where(p => p.CanPlant)) order.Targets.Add(p.Id); break;
                case NpcGoalKind.Water: foreach (var p in plots.Where(p => p.NeedsWater)) order.Targets.Add(p.Id); break;
                case NpcGoalKind.Fertilize: foreach (var p in plots.Where(p => p.NeedsFertilizer)) order.Targets.Add(p.Id); break;
                case NpcGoalKind.Weed: foreach (var p in plots.Where(p => p.NeedsWeeding)) order.Targets.Add(p.Id); break;
                case NpcGoalKind.Harvest: foreach (var p in plots.Where(p => p.CanHarvest)) order.Targets.Add(p.Id); break;
            }
        }

        bool TryStartFarmAction(WorkOrder order)
        {
            stepBlockReason = null;
            var plots = order.Targets.Select(terrain.State.GetPlot).Where(p => p != null && !p.IsReserved)
                .OrderBy(p => ((Vector2)p.WorldCenter - (Vector2)npc.transform.position).sqrMagnitude).ToList();
            FarmPlotState target = null;
            NpcWateringAgent.FarmAction action = NpcWateringAgent.FarmAction.Water;

            if (order.Goal == NpcGoalKind.FarmCycle || order.Goal == NpcGoalKind.Harvest)
                target = plots.FirstOrDefault(p => p.CanHarvest);
            if (target != null) action = NpcWateringAgent.FarmAction.Harvest;
            if (target == null && (order.Goal == NpcGoalKind.FarmCycle || order.Goal == NpcGoalKind.Weed))
            { target = plots.FirstOrDefault(p => p.NeedsWeeding); action = NpcWateringAgent.FarmAction.Weed; }
            if (target == null && (order.Goal == NpcGoalKind.FarmCycle || order.Goal == NpcGoalKind.Water))
            { target = plots.FirstOrDefault(p => p.NeedsWater); action = NpcWateringAgent.FarmAction.Water; }
            if (target == null && (order.Goal == NpcGoalKind.FarmCycle || order.Goal == NpcGoalKind.Fertilize))
            { target = plots.FirstOrDefault(p => p.NeedsFertilizer); action = NpcWateringAgent.FarmAction.Fertilize; }
            if (target == null && (order.Goal == NpcGoalKind.FarmCycle || order.Goal == NpcGoalKind.Plant))
            { target = plots.FirstOrDefault(p => p.CanPlant); action = NpcWateringAgent.FarmAction.Plant; }
            if (target == null) return false;

            bool accepted;
            if (action == NpcWateringAgent.FarmAction.Plant)
            {
                if (!TryChooseSeed(order.CropId, out activeSeed, out activeSeedIndex))
                {
                    order.Targets.Remove(target.Id);
                    SetStatus("没有可用种子，无法继续播种");
                    return false;
                }
                accepted = agent.RequestPlant(target.Cell, activeSeed.PlantedCrop);
            }
            else
            {
                if (action == NpcWateringAgent.FarmAction.Harvest)
                {
                    var crop = terrain.GetCropDataAt(target.Cell)?.GrowingCrop;
                    if (crop != null && !npc.CanFitInInventory(crop.Produce, crop.ProductPerHarvest))
                    {
                        if (HasStoredProducts()) StartCoroutine(StoreProductsAtWarehouse());
                        else stepBlockReason = "背包没有可存放的农产品，请先腾出空间";
                        return false;
                    }
                }
                accepted = action switch
                {
                    NpcWateringAgent.FarmAction.Water => agent.RequestWater(target.Cell),
                    NpcWateringAgent.FarmAction.Fertilize => agent.RequestFertilize(target.Cell),
                    NpcWateringAgent.FarmAction.Weed => agent.RequestWeed(target.Cell),
                    NpcWateringAgent.FarmAction.Harvest => agent.RequestHarvest(target.Cell),
                    _ => false
                };
            }
            if (!accepted) order.Targets.Remove(target.Id);
            return accepted;
        }

        bool HasStoredProducts()
        {
            return npc != null && npc.Inventory.Entries.Any(entry => entry.Item is Product && entry.StackSize > 0);
        }

        IEnumerator StoreProductsAtWarehouse()
        {
            if (travelling) yield break;
            Transform warehouseTarget = null;
            var warehouseComponent = FindObjectOfType<Warehouse>();
            if (warehouseComponent != null) warehouseTarget = warehouseComponent.transform;
            if (warehouseTarget == null)
            {
                var warehouseObject = GameObject.Find("Warehouse");
                if (warehouseObject != null) warehouseTarget = warehouseObject.transform;
            }
            if (warehouseTarget == null)
            {
                stepBlockReason = "农场中没有找到储物箱，收获任务正在等待";
                yield break;
            }

            travelling = true;
            travelPurpose = "前往储物箱卸货";
            mood = "不满";
            Say("背包又满了……我先去把收成存起来。", "箱");
            yield return WalkToTarget(warehouseTarget, .55f, false);
            if (!travelArrived || Vector2.Distance(npc.transform.position, warehouseTarget.position) > 2f)
            {
                stepBlockReason = "无法走到储物箱，收获任务正在等待";
                yield break;
            }

            int stored = 0;
            for (int i = 0; i < npc.Inventory.Entries.Length; i++)
            {
                var entry = npc.Inventory.Entries[i];
                if (!(entry.Item is Product) || entry.StackSize <= 0) continue;
                int count = entry.StackSize;
                GameManager.Instance.Storage.Store(new InventorySystem.InventoryEntry
                {
                    Item = entry.Item,
                    StackSize = count
                });
                stored += npc.Inventory.Remove(i, count);
            }

            travelling = false;
            travelPurpose = null;
            stepBlockReason = null;
            mood = "平静";
            memory.Add($"背包已满，向储物箱存入了 {stored} 件农产品");
            if (memory.Count > 8) memory.RemoveAt(0);
            Say($"存好了，一共 {stored} 件。接着收。", "✓");
        }

        void OnTaskFinished(NpcWateringAgent.TaskState state, string message)
        {
            if (current == null) return;
            if (state == NpcWateringAgent.TaskState.Succeeded)
            {
                current.HasWorked = true;
                stamina = Mathf.Max(0, stamina - laborStaminaCost);
                hunger = Mathf.Max(0, hunger - 1.5f);
                thirst = Mathf.Max(0, thirst - 2.5f);
                mood = stamina < 35 ? "疲惫" : "平静";
                if (agent.CurrentAction == NpcWateringAgent.FarmAction.Plant && activeSeedIndex >= 0)
                    npc.Inventory.Remove(activeSeedIndex, 1);
                if (agent.CurrentAction == NpcWateringAgent.FarmAction.Harvest)
                {
                    var harvestedPlot = terrain.State.GetPlot(agent.TargetCell);
                    if (harvestedPlot == null || !harvestedPlot.HasCrop)
                        current.Targets.Remove(harvestedPlot?.Id ?? "");
                }
                if (current.Goal != NpcGoalKind.FarmCycle)
                    current.Targets.Remove(terrain.State.GetPlot(agent.TargetCell)?.Id ?? "");
            }
            activeSeed = null;
            activeSeedIndex = -1;
            if (state == NpcWateringAgent.TaskState.Failed)
            {
                mood = "不满";
                memory.Add(message);
                if (memory.Count > 8) memory.RemoveAt(0);
                Say(message, "!");
            }
            else SetStatus(message);
        }

        bool TryChooseSeed(string cropId, out SeedBag seed, out int index)
        {
            seed = null; index = -1; int best = -1;
            var entries = npc.Inventory.Entries;
            for (int i = 0; i < entries.Length; i++)
            {
                var candidate = entries[i].Item as SeedBag;
                if (candidate == null || entries[i].StackSize <= 0) continue;
                if (!string.IsNullOrEmpty(cropId) && candidate.PlantedCrop.Key != cropId) continue;
                if (entries[i].StackSize <= best) continue;
                seed = candidate; index = i; best = entries[i].StackSize;
            }
            return seed != null;
        }

        bool TryEatFromInventory()
        {
            int bestIndex = -1, bestCount = -1;
            for (int i = 0; i < npc.Inventory.Entries.Length; i++)
            {
                var entry = npc.Inventory.Entries[i];
                if (entry.Item is Product && entry.StackSize > bestCount) { bestIndex = i; bestCount = entry.StackSize; }
            }
            if (bestIndex < 0)
            {
                mood = "低落";
                // 没东西吃时每帧都会走到这里，加个冷却避免状态栏刷屏。
                if (Time.time >= hungryReminderAfter)
                {
                    hungryReminderAfter = Time.time + 10f;
                    SetStatus("饿了，但共享背包里没有可以吃的农产品");
                }
                return false;
            }
            npc.Inventory.Remove(bestIndex, 1);
            hunger = Mathf.Min(100, hunger + 45);
            Say("先垫一口，饿着肚子可干不了活。", "🍴");
            return true;
        }

        NpcInventorySnapshot[] CaptureInventory()
        {
            if (npc == null) return Array.Empty<NpcInventorySnapshot>();
            return npc.Inventory.Entries.Where(e => e.Item != null && e.StackSize > 0).Select(e => new NpcInventorySnapshot
            {
                itemId = e.Item.Key, displayName = e.Item.DisplayName, count = e.StackSize,
                itemType = e.Item is SeedBag ? "seed" : e.Item is Product ? "food" : "tool"
            }).ToArray();
        }

        NpcNeedsSnapshot NeedsSnapshot() => new()
        {
            stamina = stamina, hunger = hunger, thirst = thirst, mood = mood, resting = resting, sleeping = sleeping
        };

        void CancelAgent()
        {
            if (agent != null && agent.IsBusy) agent.Cancel();
            activeSeed = null;
            activeSeedIndex = -1;
        }

        static bool TryParseMode(string value, out NpcScheduleMode mode) => Enum.TryParse(ToPascal(value), true, out mode);
        static bool TryParseGoal(string value, out NpcGoalKind goal) => Enum.TryParse(ToPascal(value), true, out goal) && goal != NpcGoalKind.Unknown;
        static string ToPascal(string value) => string.Join("", (value ?? "").Split('_').Select(x => x.Length == 0 ? x : char.ToUpperInvariant(x[0]) + x.Substring(1)));

        void Say(string dialogue, string icon)
        {
            SetStatus(dialogue);
            if (bubbleText == null) return;
            bubbleText.text = icon + "  " + dialogue;
            if (bubbleObject != null) bubbleObject.SetActive(true);
            bubbleUntil = Time.unscaledTime + 4f;
        }

        void SetStatus(string value) { if (statusText != null) statusText.text = value; }

        void UpdateUI()
        {
            if (needsText != null)
                needsText.text = $"体力 {stamina:0}  饱腹 {hunger:0}  水分 {thirst:0}  心情 {mood}\n当前：{(current == null ? "空闲" : GoalLabel(current.Goal))}  待办：{queue.Count + interrupted.Count}";
            UpdatePlanUI();
            if (bubbleObject != null && bubbleObject.activeInHierarchy) bubbleObject.transform.position = npc.transform.position + new Vector3(0, 2.45f, 0);
            if (bubbleObject != null && Time.unscaledTime >= bubbleUntil) bubbleObject.SetActive(false);
        }

        void UpdatePlanUI()
        {
            if (planText == null) return;
            var lines = new List<string> { lastPlanSummary, "" };
            if (sleeping) lines.Add("当前执行：睡觉（06:00 起床）");
            else if (travelling) lines.Add("当前执行：" + travelPurpose);
            else if (current == null) lines.Add("当前执行：空闲");
            else
            {
                lines.Add("当前执行：" + GoalLabel(current.Goal));
                if (agent != null && agent.IsBusy)
                    lines.Add("当前动作：" + ActionLabel(agent.CurrentAction));
                lines.Add("剩余目标：" + current.Targets.Count);
            }
            if (travelling && !string.IsNullOrEmpty(travelPurpose))
                lines.Add("生活安排：" + travelPurpose);
            if ((sleeping || travelling) && current != null)
                lines.Add("暂停的农活：" + GoalLabel(current.Goal) + "，剩余目标：" + current.Targets.Count);

            if (queue.Count > 0)
            {
                lines.Add("");
                lines.Add("后续任务：");
                int number = 1;
                foreach (var order in queue.Take(5)) lines.Add($"{number++}. {GoalLabel(order.Goal)}");
                if (queue.Count > 5) lines.Add($"…另有 {queue.Count - 5} 项");
            }
            if (interrupted.Count > 0)
            {
                lines.Add("");
                lines.Add("暂停后恢复：" + GoalLabel(interrupted.Peek().Goal));
            }
            if (current?.Goal == NpcGoalKind.FarmCycle)
            {
                lines.Add("");
                lines.Add("农活流程：");
                lines.Add("播种 → 浇水/施肥/除草");
                lines.Add("等待生长 → 收获 → 完成");
            }
            planText.text = string.Join("\n", lines);
        }

        static string ActionLabel(NpcWateringAgent.FarmAction action) => action switch
        {
            NpcWateringAgent.FarmAction.Plant => "播种",
            NpcWateringAgent.FarmAction.Water => "浇水",
            NpcWateringAgent.FarmAction.Fertilize => "施肥",
            NpcWateringAgent.FarmAction.Weed => "除草",
            NpcWateringAgent.FarmAction.Harvest => "收获",
            _ => "工作"
        };

        static string GoalLabel(NpcGoalKind goal) => goal switch
        {
            NpcGoalKind.FarmCycle => "完成一轮农活", NpcGoalKind.Plant => "播种", NpcGoalKind.Water => "浇水",
            NpcGoalKind.Fertilize => "施肥", NpcGoalKind.Weed => "除草", NpcGoalKind.Harvest => "收获",
            NpcGoalKind.Sleep => "回家睡觉", _ => "未知"
        };

        void BuildUI()
        {
            runtimeFont = Font.CreateDynamicFontFromOSFont(
                new[] { "Microsoft YaHei", "Noto Sans CJK SC", "Arial" }, 20);
            var font = runtimeFont;
            var root = new GameObject("NPC Command UI", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            root.transform.SetParent(transform, false);
            canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 200;
            var scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);

            var panel = MakeRect("Command Panel", root.transform, new Vector2(12, -12), new Vector2(350, 190));
            panel.anchoredPosition = new Vector2(12, -12);
            panel.gameObject.AddComponent<Image>().color = new Color(.06f, .10f, .12f, .94f);
            MakeText("远程指挥 NPC", panel, font, new Vector2(12, -10), new Vector2(326, 28), 20);
            needsText = MakeText("", panel, font, new Vector2(12, -42), new Vector2(326, 48), 14);
            statusText = MakeText("等待你的安排", panel, font, new Vector2(12, -92), new Vector2(326, 32), 14);

            var inputRect = MakeRect("Command Input", panel, new Vector2(12, -132), new Vector2(252, 42));
            inputRect.gameObject.AddComponent<Image>().color = new Color(.92f, .94f, .90f, 1);
            input = inputRect.gameObject.AddComponent<InputField>();
            var inputText = MakeText("", inputRect, font, new Vector2(8, -5), new Vector2(236, 32), 16);
            inputText.color = new Color(.08f, .10f, .09f);
            input.textComponent = inputText;
            var placeholder = MakeText("例如：去干农活", inputRect, font, new Vector2(8, -5), new Vector2(236, 32), 16);
            placeholder.color = new Color(.35f, .38f, .36f, .75f);
            input.placeholder = placeholder;
            input.onEndEdit.AddListener(value => { if (!string.IsNullOrWhiteSpace(value)) SubmitCommand(); });

            var buttonRect = MakeRect("Send", panel, new Vector2(274, -132), new Vector2(64, 42));
            buttonRect.gameObject.AddComponent<Image>().color = new Color(.25f, .52f, .36f, 1);
            var button = buttonRect.gameObject.AddComponent<Button>();
            button.onClick.AddListener(SubmitCommand);
            var buttonText = MakeText("发送", buttonRect, font, Vector2.zero, new Vector2(64, 42), 16);
            buttonText.alignment = TextAnchor.MiddleCenter;

            var planPanel = MakeRect("Task Plan Panel", root.transform, Vector2.zero, new Vector2(280, 310));
            planPanel.anchorMin = planPanel.anchorMax = planPanel.pivot = new Vector2(1, 1);
            planPanel.anchoredPosition = new Vector2(-12, -12);
            planPanel.gameObject.AddComponent<Image>().color = new Color(.06f, .10f, .12f, .94f);
            MakeText("任务计划", planPanel, font, new Vector2(12, -10), new Vector2(256, 30), 20);
            planText = MakeText("尚未收到指令", planPanel, font, new Vector2(12, -44), new Vector2(256, 250), 15);
            planText.alignment = TextAnchor.UpperLeft;

            var bubbleRoot = new GameObject("NPC State Bubble", typeof(Canvas));
            bubbleObject = bubbleRoot;
            bubbleRoot.transform.SetParent(transform, false);
            var bubbleCanvas = bubbleRoot.GetComponent<Canvas>();
            bubbleCanvas.renderMode = RenderMode.WorldSpace;
            bubbleCanvas.sortingOrder = 300;
            var bubbleRect = bubbleRoot.GetComponent<RectTransform>();
            bubbleRect.sizeDelta = new Vector2(360, 58);
            bubbleRect.localScale = Vector3.one * .01f;
            var bg = bubbleRoot.AddComponent<Image>();
            bg.color = new Color(.05f, .07f, .08f, .88f);
            bubbleText = MakeText("", bubbleRect, font, Vector2.zero, bubbleRect.sizeDelta, 20);
            bubbleText.alignment = TextAnchor.MiddleCenter;
            bubbleRoot.SetActive(false);
        }

        static RectTransform MakeRect(string name, Transform parent, Vector2 position, Vector2 size)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            return rect;
        }

        static Text MakeText(string value, Transform parent, Font font, Vector2 position, Vector2 size, int fontSize)
        {
            var text = MakeRect("Text", parent, position, size).gameObject.AddComponent<Text>();
            text.font = font;
            text.fontSize = fontSize;
            text.text = value;
            text.color = new Color(.94f, .96f, .92f);
            text.alignment = TextAnchor.MiddleLeft;
            text.raycastTarget = false;
            return text;
        }

        void OnDestroy()
        {
            if (instance != this) return;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (agent != null) agent.TaskFinished -= OnTaskFinished;
            if (runtimeFont != null) Destroy(runtimeFont);
            instance = null;
        }
    }
}
