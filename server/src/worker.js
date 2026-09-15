const recentRequests = new Map();
const plannerInstructions = "你是Unity种田游戏NPC唯一的行为决策者。只输出JSON。根对象字段必须为schemaVersion、requestId、scheduleMode、goal、cropId、tasks、dialogue、error。scheduleMode只能是append、priority、cancel_current、cancel_all；goal只能是farm_cycle、plant、water、fertilize、weed、harvest、drink、sleep、rest、eat、store、unknown。tasks是按顺序执行的数组，每项仅含goal和cropId，最多8项。必须原样返回输入requestId，schemaVersion固定为1。append只追加新任务；priority把新任务放到最前，保留并随后恢复原任务和全部待办；两种模式都只返回新增或需要提前的任务，绝不能重写整个计划或复制已有待办。cancel_current只取消当前任务，cancel_all仅在玩家明确要求清空全部时使用，取消操作必须tasks为空且goal为unknown。requestKind=needs是游戏状态通知，结合饥渴、体力、时间和blockedReason决定是否插队drink、eat、rest、sleep、store，禁止取消已有任务；没有必要时返回unknown和空tasks。requestKind=dialogue仅根据已发生的事实生成一句对白，必须append、unknown和空tasks，不能改变任何任务。玩家闲聊或无法理解也返回unknown和空tasks，不要口头承诺行动。喝水、卸货完成后角色留在原地，后续行程取决于已有任务。休息恢复体力，睡眠到早晨恢复体力；store是在农场仓库卸货。不要修改游戏状态或声称尚未执行的动作已经完成。dialogue由你根据上下文生成，嘴上抱怨但做事靠谱，明确区分立即开始、排队和暂停；正常error为空字符串。";

const responseSchema = {
  type: "object",
  additionalProperties: false,
  required: ["schemaVersion", "requestId", "scheduleMode", "goal", "cropId", "tasks", "dialogue", "error"],
  properties: {
    schemaVersion: { type: "integer", const: 1 },
    requestId: { type: "integer", minimum: 1 },
    scheduleMode: { type: "string", enum: ["append", "priority", "cancel_current", "cancel_all"] },
    goal: { type: "string", enum: ["farm_cycle", "plant", "water", "fertilize", "weed", "harvest", "drink", "sleep", "rest", "eat", "store", "unknown"] },
    cropId: { type: "string" },
    tasks: {
      type: "array",
      maxItems: 8,
      items: {
        type: "object",
        additionalProperties: false,
        required: ["goal", "cropId"],
        properties: {
          goal: { type: "string", enum: ["farm_cycle", "plant", "water", "fertilize", "weed", "harvest", "drink", "sleep", "rest", "eat", "store", "unknown"] },
          cropId: { type: "string" }
        }
      }
    },
    dialogue: { type: "string", maxLength: 100 },
    error: { type: "string", maxLength: 100 }
  }
};

function json(value, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store" }
  });
}

function allowedByRateLimit(request, env) {
  const ip = request.headers.get("cf-connecting-ip") || "local";
  const minute = Math.floor(Date.now() / 60000);
  const key = `${ip}:${minute}`;
  const count = (recentRequests.get(key) || 0) + 1;
  recentRequests.set(key, count);
  if (recentRequests.size > 2000) recentRequests.clear();
  return count <= Number(env.MAX_REQUESTS_PER_MINUTE || 20);
}

function extractOutputText(apiResponse) {
  for (const item of apiResponse.output || []) {
    for (const content of item.content || []) {
      if (content.type === "output_text" && content.text) return content.text;
    }
  }
  return "";
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (request.method === "GET" && url.pathname === "/health") return json({ ok: true });
    if (request.method !== "POST" || url.pathname !== "/npc/decide") return json({ error: "not_found" }, 404);

    const suppliedToken = (request.headers.get("authorization") || "").replace(/^Bearer\s+/i, "");
    if (!env.NPC_DEMO_ACCESS_TOKEN || suppliedToken !== env.NPC_DEMO_ACCESS_TOKEN)
      return json({ error: "unauthorized" }, 401);
    if (!allowedByRateLimit(request, env)) return json({ error: "rate_limited" }, 429);
    if (!env.OPENAI_API_KEY) return json({ error: "server_not_configured" }, 503);

    const raw = await request.text();
    if (raw.length > 100000) return json({ error: "request_too_large" }, 413);
    let gameState;
    try { gameState = JSON.parse(raw); }
    catch { return json({ error: "invalid_json" }, 400); }
    if (!Number.isInteger(gameState.requestId) || typeof gameState.playerText !== "string")
      return json({ error: "invalid_request" }, 400);

    const openAiResponse = await fetch("https://api.openai.com/v1/responses", {
      method: "POST",
      headers: {
        "authorization": `Bearer ${env.OPENAI_API_KEY}`,
        "content-type": "application/json"
      },
      body: JSON.stringify({
        model: env.OPENAI_MODEL || "gpt-5-mini",
        store: false,
        max_output_tokens: 1600,
        instructions: plannerInstructions,
        input: JSON.stringify(gameState),
        text: {
          format: {
            type: "json_schema",
            name: "npc_plan",
            strict: true,
            schema: responseSchema
          }
        }
      })
    });

    if (!openAiResponse.ok) {
      const requestId = openAiResponse.headers.get("x-request-id") || "";
      return json({ error: "openai_request_failed", requestId }, 502);
    }
    const apiJson = await openAiResponse.json();
    const outputText = extractOutputText(apiJson);
    if (!outputText) return json({ error: "empty_model_output" }, 502);

    let plan;
    try { plan = JSON.parse(outputText); }
    catch { return json({ error: "invalid_model_output" }, 502); }
    if (plan.requestId !== gameState.requestId || plan.schemaVersion !== 1)
      return json({ error: "mismatched_model_output" }, 502);
    return json(plan);
  }
};
