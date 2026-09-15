// Standalone regression checks against the compiled game assembly. No scene or API calls.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using HappyHarvest;

public static class NpcAuthorityChecks
{
    const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    static int passed;
    static NpcCommandSystem system;
    static object Get(string field) => typeof(NpcCommandSystem).GetField(field, Fields).GetValue(system);
    static void Set(string field, object value) => typeof(NpcCommandSystem).GetField(field, Fields).SetValue(system, value);
    static object[] QueueItems() => ((IEnumerable)Get("queue")).Cast<object>().ToArray();
    static string Goal(object order) => order.GetType().GetField("Goal").GetValue(order).ToString();
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name); passed++;
    }
    static void Reset()
    {
        system = (NpcCommandSystem)FormatterServices.GetUninitializedObject(typeof(NpcCommandSystem));
        foreach (var name in new[] { "queue", "memory", "observations", "playerCommands" })
        {
            var field = typeof(NpcCommandSystem).GetField(name, Fields);
            field.SetValue(system, Activator.CreateInstance(field.FieldType));
        }
        Set("nextRequestId", 100L);
    }
    static void Apply(long id, string mode, bool autonomous = false, params string[] goals)
    {
        var response = new NpcAiResponse
        {
            requestId = id, scheduleMode = mode, goal = goals.FirstOrDefault() ?? "unknown",
            tasks = goals.Select(x => new NpcAiTask { goal = x, cropId = "" }).ToArray(), dialogue = "AI dialogue"
        };
        typeof(NpcCommandSystem).GetMethod("ApplyDecision", Fields).Invoke(system, new object[] { response, autonomous });
    }
    static object StartFirst()
    {
        var queue = Get("queue"); var first = QueueItems()[0];
        queue.GetType().GetMethod("RemoveFirst").Invoke(queue, null);
        Set("current", first); return first;
    }
    public static int Main()
    {
        try
        {
            Reset();
            Apply(1, "append", false, "water", "harvest");
            var original = StartFirst();
            original.GetType().GetField("HasWorked").SetValue(original, true);
            ((HashSet<string>)original.GetType().GetField("Targets").GetValue(original)).Add("plot-7");
            Apply(2, "append", false, "plant");
            Check(ReferenceEquals(Get("current"), original) && QueueItems().Select(Goal).SequenceEqual(new[] { "Harvest", "Plant" }), "append preserves current task and existing queue");
            Apply(3, "priority", false, "drink", "rest");
            Check(QueueItems().Select(Goal).SequenceEqual(new[] { "Drink", "Rest", "Water", "Harvest", "Plant" }), "priority sequence precedes original task and all pending work");
            Check(ReferenceEquals(QueueItems()[2], original) && ((HashSet<string>)original.GetType().GetField("Targets").GetValue(original)).Contains("plot-7"), "priority preserves original target progress and object identity");
            StartFirst();
            typeof(NpcCommandSystem).GetMethod("CompleteCurrentOrder", Fields).Invoke(system, new object[] { "drink completed" });
            Check(Goal(StartFirst()) == "Rest", "multi-step priority continues before original work");
            typeof(NpcCommandSystem).GetMethod("CompleteCurrentOrder", Fields).Invoke(system, new object[] { "rest completed" });
            Check(ReferenceEquals(StartFirst(), original), "completed priority resumes original work");
            Set("travelling", true);
            Apply(4, "priority", false, "water");
            Check(ReferenceEquals(Get("current"), original) && (bool)Get("travelling"), "reminder does not restart active journey");
            Set("travelling", false);
            var before = QueueItems();
            Apply(5, "priority", false, "drink", "bad_goal");
            Check(ReferenceEquals(Get("current"), original) && QueueItems().SequenceEqual(before), "one invalid task rejects entire response without partial mutation");
            Apply(6, "append", false, "water", "harvest");
            Check(QueueItems().SequenceEqual(before), "repeated requests do not duplicate active or queued work");
            Apply(7, "cancel_all", true);
            Check(ReferenceEquals(Get("current"), original) && QueueItems().SequenceEqual(before), "autonomous needs notification cannot clear player plan");
            Apply(8, "cancel_current");
            Check(Get("current") == null && QueueItems().SequenceEqual(before), "cancel current retains every pending task");
            Apply(9, "append");
            Check(QueueItems().SequenceEqual(before), "dialogue-only response retains queue");
            Apply(10, "priority", false, "harvest");
            Check(QueueItems().Select(Goal).SequenceEqual(new[] { "Harvest", "Plant" }), "reprioritizing queued work reuses it without duplication");
            Apply(8, "cancel_all");
            Check(QueueItems().Length == 2, "stale response cannot cancel newer plan");
            Apply(11, "cancel_all");
            Check(QueueItems().Length == 0, "explicit cancel all clears plan");
            NpcAiResponse failure = null;
            new UnavailableNpcDecisionProvider().Decide(new NpcAiRequest { requestId = 11 }, r => failure = r);
            Check(failure.error == "ai_unavailable" && failure.tasks == null && failure.dialogue == null, "unavailable AI invents neither actions nor speech");
            Console.WriteLine("All " + passed + " checks passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
