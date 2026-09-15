# NPC AI 中转服务

这个 Cloudflare Worker 保存 OpenAI API Key，并向 Unity 暴露 `/npc/decide`。Unity 安装包中不包含 OpenAI Key。

## 本机测试

1. 安装 Node.js 18 或更高版本。
2. 在 `server` 目录执行 `npm install`。
3. 将 `.dev.vars.example` 复制为 `.dev.vars`，填写 `OPENAI_API_KEY` 和一个较长的 `NPC_DEMO_ACCESS_TOKEN`。
4. 执行 `npm run dev`。
5. 让 `Assets/StreamingAssets/npc-ai-config.json` 中的 `accessToken` 与测试 token 相同。

本机地址默认为 `http://127.0.0.1:8787/npc/decide`。服务不可用时重试一次，仍失败则保留已有任务并提示错误，不使用本地规划器。AI 是唯一的行为决策来源；新任务采用追加或插队，保留原有任务和进度。

## 发布给朋友

1. 登录 Cloudflare：`npx wrangler login`。
2. 分别执行 `npx wrangler secret put OPENAI_API_KEY` 和 `npx wrangler secret put NPC_DEMO_ACCESS_TOKEN`。
3. 执行 `npm run deploy`，记录返回的 Worker 地址。
4. 把 Unity 配置中的 `endpoint` 改为 `https://你的-worker地址/npc/decide`，并填写相同测试 token，再构建游戏。
5. 在 OpenAI 项目中设置较低的月度预算并观察 Usage。

测试访问码只能阻挡随意请求，无法抵御专门提取客户端配置的人。公开发布前应换成账号登录和服务端额度系统。
