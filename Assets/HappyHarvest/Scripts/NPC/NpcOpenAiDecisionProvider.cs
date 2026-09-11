using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace HappyHarvest
{
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
        public int max_tokens = 600;
    }
    [Serializable]
    sealed class ChatResponse { public ChatChoice[] choices; }
    [Serializable]
    sealed class ChatChoice { public ChatMessage message; }

    /// <summary>
    /// Calls the project's small proxy service. The OpenAI key must never be stored in this client.
    /// Any transport or validation failure falls back to the deterministic local planner.
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
            coroutineOwner.StartCoroutine(Send(request, completed));
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

            Debug.LogWarning($"NPC AI 服务不可用，改用本地规划器。HTTP {webRequest.responseCode}: {webRequest.error}");
            fallback.Decide(request, completed);
        }
    }

    /// <summary>Short-term desktop test mode. Its key is included in builds and is not safe for public release.</summary>
    public sealed class NpcDirectOpenAiDecisionProvider : INpcDecisionProvider
    {
        const string SystemPrompt = "你是Unity种田游戏NPC任务规划器。只输出JSON。根对象字段必须为schemaVersion、requestId、scheduleMode、goal、cropId、tasks、dialogue、error。scheduleMode只能是append、priority、cancel_current、cancel_all；goal只能是farm_cycle、plant、water、fertilize、weed、harvest、sleep、unknown。tasks是按顺序执行的数组，每项仅含goal和cropId。必须原样返回输入requestId，schemaVersion固定为1。不要修改游戏状态或声称任务已经完成。dialogue保持嘴上抱怨但做事靠谱，简短自然。";
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
            owner.StartCoroutine(Send(request, completed));

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
            var errorBody = webRequest.downloadHandler?.text;
            if (!string.IsNullOrWhiteSpace(errorBody) && errorBody.Length > 600)
                errorBody = errorBody.Substring(0, 600);
            Debug.LogWarning($"AI 直连失败，改用本地规划器。HTTP {webRequest.responseCode}: " +
                             $"{webRequest.error}\n{errorBody}");
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
                Debug.LogWarning("NPC AI 配置读取失败，改用本地规划器：" + exception.Message);
                return fallback;
            }
        }
    }
}
