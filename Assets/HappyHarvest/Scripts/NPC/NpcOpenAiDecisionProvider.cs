using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace HappyHarvest
{
    internal static class NpcDecisionRetry
    {
        // A failed attempt cannot mutate the queue. Retry once using the same request id.
        public static IEnumerator Run(NpcAiRequest request,
            Func<NpcAiRequest, Action<NpcAiResponse>, IEnumerator> send, Action<NpcAiResponse> completed)
        {
            NpcAiResponse result = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                yield return send(request, response => result = response);
                if (IsValid(result, request.requestId)) { completed?.Invoke(result); yield break; }
                if (attempt == 0) yield return new WaitForSecondsRealtime(1f);
            }
            completed?.Invoke(new NpcAiResponse { requestId = request.requestId, error = "invalid_ai_response" });
        }

        static bool IsValid(NpcAiResponse response, long id)
        {
            if (response == null || response.requestId != id || response.schemaVersion != 1 ||
                !string.IsNullOrEmpty(response.error) || response.tasks == null || response.tasks.Length > 8) return false;
            bool cancel = response.scheduleMode == "cancel_current" || response.scheduleMode == "cancel_all";
            if (!cancel && response.scheduleMode != "append" && response.scheduleMode != "priority") return false;
            if (cancel) return response.tasks.Length == 0 && response.goal == "unknown";
            if (response.tasks.Length == 0) return response.goal == "unknown" || ValidGoal(response.goal);
            foreach (var task in response.tasks)
                if (task == null || !ValidGoal(task.goal)) return false;
            return true;
        }

        static bool ValidGoal(string goal) => goal == "farm_cycle" || goal == "plant" || goal == "water" ||
            goal == "fertilize" || goal == "weed" || goal == "harvest" || goal == "drink" || goal == "sleep" ||
            goal == "rest" || goal == "eat" || goal == "store";
    }

    [Serializable]
    public sealed class NpcAiClientConfig
    {
        public bool enabled = true;
        public string mode = "proxy";
        public string endpoint = "http://127.0.0.1:8787/npc/decide";
        public string accessToken = "";
        public string model = "deepseek-v4-flash";
        public int timeoutSeconds = 12;
    }

    [Serializable]
    public sealed class NpcAiSecretConfig
    {
        public string apiKey = "";
        public string openAiApiKey = "";
        public string GetKey() => string.IsNullOrWhiteSpace(apiKey) ? openAiApiKey : apiKey;
    }

    [Serializable]
    sealed class ChatMessage { public string role; public string content; }
    [Serializable]
    sealed class ChatResponseFormat { public string type = "json_object"; }
    [Serializable]
    sealed class ChatThinking { public string type = "disabled"; }
    [Serializable]
    sealed class ChatRequest
    {
        public string model;
        public ChatMessage[] messages;
        public ChatResponseFormat response_format = new();
        public ChatThinking thinking = new();
        public int max_tokens = 1600;
    }
    [Serializable]
    sealed class ChatResponse { public ChatChoice[] choices; }
    [Serializable]
    sealed class ChatChoice { public ChatMessage message; }

    /// <summary>
    /// Calls the project's small proxy service. The OpenAI key must never be stored in this client.
    /// Transport failures leave the current plan intact; no local planner can invent actions.
    /// </summary>
    public sealed class NpcOpenAiDecisionProvider : INpcDecisionProvider
    {
        readonly MonoBehaviour coroutineOwner;
        readonly NpcAiClientConfig config;
        readonly INpcDecisionProvider fallback;

        public NpcOpenAiDecisionProvider(MonoBehaviour owner, NpcAiClientConfig clientConfig,
            INpcDecisionProvider fallbackProvider)
        {
            coroutineOwner = owner;
            config = clientConfig;
            fallback = fallbackProvider;
        }

        public void Decide(NpcAiRequest request, Action<NpcAiResponse> completed)
        {
            coroutineOwner.StartCoroutine(NpcDecisionRetry.Run(request, Send, completed));
        }

        IEnumerator Send(NpcAiRequest request, Action<NpcAiResponse> completed)
        {
            byte[] body = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(request));
            using var webRequest = new UnityWebRequest(config.endpoint, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(body),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = Mathf.Clamp(config.timeoutSeconds, 3, 60)
            };
            webRequest.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrWhiteSpace(config.accessToken))
                webRequest.SetRequestHeader("Authorization", "Bearer " + config.accessToken.Trim());

            yield return webRequest.SendWebRequest();
            if (webRequest.result == UnityWebRequest.Result.Success)
            {
                NpcAiResponse response = null;
                try { response = JsonUtility.FromJson<NpcAiResponse>(webRequest.downloadHandler.text); }
                catch (Exception exception) { Debug.LogWarning("NPC AI JSON 无法解析：" + exception.Message); }
                if (response != null && response.schemaVersion == 1 && response.requestId == request.requestId)
                {
                    completed?.Invoke(response);
                    yield break;
                }
            }

            Debug.LogWarning($"NPC AI 服务不可用，保留原任务。HTTP {webRequest.responseCode}: {webRequest.error}");
            fallback.Decide(request, completed);
        }
    }

    /// <summary>Short-term desktop test mode. Its key is included in builds and is not safe for public release.</summary>
    public sealed class NpcDirectOpenAiDecisionProvider : INpcDecisionProvider
    {
        internal const string SystemPrompt = "你是Unity种田游戏NPC唯一的行为决策者。只输出JSON。根对象字段必须为schemaVersion、requestId、scheduleMode、goal、cropId、tasks、dialogue、error。scheduleMode只能是append、priority、cancel_current、cancel_all；goal只能是farm_cycle、plant、water、fertilize、weed、harvest、drink、sleep、rest、eat、store、unknown。tasks是按顺序执行的数组，每项仅含goal和cropId，最多8项。必须原样返回输入requestId，schemaVersion固定为1。append只追加新任务；priority把新任务放到最前，保留并随后恢复原任务和全部待办；两种模式都只返回新增或需要提前的任务，绝不能重写整个计划或复制已有待办。cancel_current只取消当前任务，cancel_all仅在玩家明确要求清空全部时使用，取消操作必须tasks为空且goal为unknown。requestKind=needs是游戏状态通知，结合饥渴、体力、时间和blockedReason决定是否插队drink、eat、rest、sleep、store，禁止取消已有任务；没有必要时返回unknown和空tasks。requestKind=dialogue仅根据已发生的事实生成一句对白，必须append、unknown和空tasks，不能改变任何任务。玩家闲聊或无法理解也返回unknown和空tasks，不要口头承诺行动。喝水、卸货完成后角色留在原地，后续行程取决于已有任务。休息恢复体力，睡眠到早晨恢复体力；store是在农场仓库卸货。不要修改游戏状态或声称尚未执行的动作已经完成。dialogue由你根据上下文生成，嘴上抱怨但做事靠谱，明确区分立即开始、排队和暂停；正常error为空字符串。";
        readonly MonoBehaviour owner;
        readonly NpcAiClientConfig config;
        readonly string apiKey;
        readonly INpcDecisionProvider fallback;

        public NpcDirectOpenAiDecisionProvider(MonoBehaviour coroutineOwner, NpcAiClientConfig clientConfig,
            string key, INpcDecisionProvider fallbackProvider)
        {
            owner = coroutineOwner;
            config = clientConfig;
            apiKey = key;
            fallback = fallbackProvider;
        }

        public void Decide(NpcAiRequest request, Action<NpcAiResponse> completed) =>
            owner.StartCoroutine(NpcDecisionRetry.Run(request, Send, completed));

        IEnumerator Send(NpcAiRequest request, Action<NpcAiResponse> completed)
        {
            var payload = new ChatRequest
            {
                model = string.IsNullOrWhiteSpace(config.model) ? "deepseek-v4-flash" : config.model,
                messages = new[]
                {
                    new ChatMessage { role = "system", content = SystemPrompt },
                    new ChatMessage { role = "user", content = JsonUtility.ToJson(request) }
                }
            };
            var bytes = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload));
            using var webRequest = new UnityWebRequest(config.endpoint, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(bytes),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = Mathf.Clamp(config.timeoutSeconds, 3, 60)
            };
            webRequest.SetRequestHeader("Content-Type", "application/json");
            webRequest.SetRequestHeader("Authorization", "Bearer " + apiKey.Trim());
            yield return webRequest.SendWebRequest();

            if (webRequest.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    var apiResponse = JsonUtility.FromJson<ChatResponse>(webRequest.downloadHandler.text);
                    var content = apiResponse?.choices != null && apiResponse.choices.Length > 0
                        ? apiResponse.choices[0].message?.content : null;
                    var plan = string.IsNullOrWhiteSpace(content) ? null : JsonUtility.FromJson<NpcAiResponse>(content);
                    if (plan != null && plan.schemaVersion == 1 && plan.requestId == request.requestId)
                    {
                        completed?.Invoke(plan);
                        yield break;
                    }
                }
                catch (Exception exception) { Debug.LogWarning("OpenAI 回复无法解析：" + exception.Message); }
            }
            Debug.LogWarning($"AI 直连未返回有效决策，保留原任务。请求 {request.requestId}，HTTP {webRequest.responseCode}: {webRequest.error}");
            fallback.Decide(request, completed);
        }
    }

    public static class NpcDecisionProviderFactory
    {
        const string ConfigFileName = "npc-ai-config.json";
        const string SecretFileName = "npc-ai-secret.json";

        public static INpcDecisionProvider Create(MonoBehaviour owner, INpcDecisionProvider fallback)
        {
            var path = Path.Combine(Application.streamingAssetsPath, ConfigFileName);
            if (!File.Exists(path)) return fallback;
            try
            {
                var config = JsonUtility.FromJson<NpcAiClientConfig>(File.ReadAllText(path));
                if (config == null || !config.enabled) return fallback;
                if (string.Equals(config.mode, "direct", StringComparison.OrdinalIgnoreCase))
                {
                    var secretPath = Path.Combine(Application.streamingAssetsPath, SecretFileName);
                    if (!File.Exists(secretPath)) throw new InvalidOperationException("缺少 " + SecretFileName);
                    var secret = JsonUtility.FromJson<NpcAiSecretConfig>(File.ReadAllText(secretPath));
                    var apiKey = secret?.GetKey();
                    if (string.IsNullOrWhiteSpace(apiKey) || apiKey.StartsWith("PASTE_", StringComparison.Ordinal))
                        throw new InvalidOperationException("尚未填写 AI API Key");
                    if (string.IsNullOrWhiteSpace(config.endpoint))
                        throw new InvalidOperationException("尚未填写 AI API 地址");
                    return new NpcDirectOpenAiDecisionProvider(owner, config, apiKey, fallback);
                }
                if (string.IsNullOrWhiteSpace(config.endpoint)) return fallback;
                return new NpcOpenAiDecisionProvider(owner, config, fallback);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("NPC AI 配置读取失败，保留原任务：" + exception.Message);
                return fallback;
            }
        }
    }
}
