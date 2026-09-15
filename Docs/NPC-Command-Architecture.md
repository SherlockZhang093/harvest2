# NPC natural-language command architecture

## Runtime flow

1. The persistent command UI accepts a player's natural-language instruction.
2. `NpcCommandSystem` builds an `NpcAiRequest` from the instruction, NPC needs, inventory, farm snapshot, current task, and queued tasks.
3. An `INpcDecisionProvider` returns an `NpcAiResponse`. Only the configured network AI can authorize behavior. An unavailable provider returns an error without inventing dialogue or tasks.
4. The response is validated and converted into an append, priority, cancel-current, or cancel-all queue operation.
5. The game selects concrete plots and delegates one atomic action at a time to `NpcWateringAgent`.
6. Only successful game actions change farm state, inventory, stamina, hunger, and thirst.

The AI response is a scheduling request. It never writes authoritative world state or declares that an action succeeded.

## AI contract

`NpcAiRequest` contains a monotonically increasing `requestId`, current progress, scene, time, needs, crop-specific current/queued orders, and recent memory. Player commands are dispatched serially with fresh snapshots so later responses cannot discard earlier instructions. Responses during a scene transition wait for the transition to finish. The entire response is validated before any queue mutation.

`requestKind` is `player`, `needs`, or `dialogue`. Needs notifications ask the AI whether to insert rest, food, water, sleep, or storage work; they cannot cancel player tasks. Dialogue notifications describe completed events and never mutate the task queue. Only network-generated dialogue enters the NPC speech bubble. System execution/failure messages remain factual UI status.

Supported response goals are:

- `farm_cycle`
- `plant`
- `water`
- `fertilize`
- `weed`
- `harvest`
- `sleep`
- `drink`
- `rest`
- `eat`
- `store`

`unknown` with an empty task list is a conversational/no-change response.

Supported scheduling modes are:

- `append`
- `priority`
- `cancel_current`
- `cancel_all`

These are incremental operations, not replacement plans. Append retains the current task and all queued work. Priority inserts its entire ordered sequence ahead of the interrupted task and existing queue. Existing matching tasks are reused with their target progress intact. Repeated matching tasks are deduplicated. Cancel-current removes only the active task; cancel-all clears the plan. Cancel operations cannot contain new tasks. Active travel is cancelled along with an interrupted task, without cancelling pending AI requests.

For `farm_cycle`, the game snapshots the eligible farm plots when the order starts. It plants with the most abundant eligible seed, tends those crops, waits through growth, and finishes after the final harvest. Harvested plots leave the current cycle and are not replanted.

## Needs and daily routine

- Successful farm actions consume stamina and slightly increase hunger and thirst.
- Low stamina blocks labor and reports the need to the AI. An AI-issued `rest` task restores stamina in place.
- An AI-issued `eat` task consumes one edible `Product` from the shared player inventory.
- Insufficient inventory space blocks harvesting and is reported to the AI. Only an AI-issued `store` task starts a warehouse trip. It stores every `Product`, retaining seeds/tools and the original harvest targets.
- Low needs and late hours are notifications, not local travel decisions. The AI decides whether to insert `drink` or `sleep`.
- An AI-issued sleep task approaches the bed and sleeps until morning, restoring stamina. Waking does not automatically send the NPC outside.
- Drinking and storage finish at their destination. Further movement occurs only to execute an existing AI-authorized task. Pathfinding and scene transitions implement those tasks; they do not choose new goals.
- Drinking is an integration point. The house drinking-station module implements `INpcDrinkSource`; the command system finds it, walks to it, and restores thirst only after `TryDrink` succeeds.

NPC needs, memory, and task queues are intentionally runtime-only for this demo.

## Example player phrases

- `去干农活`
- `浇水`
- `浇完以后收菜`
- `先去除草`
- `马上施肥`
- `停下`
- `全部取消`
- `回家睡觉`

There is no local keyword parser or special drink-command shortcut. Enter submits the input; losing focus alone does not submit it.

## OpenAI test integration

`NpcOpenAiDecisionProvider` sends the same request contract to the proxy configured in
`Assets/StreamingAssets/npc-ai-config.json`. The proxy under `server/` owns the OpenAI key,
uses the Responses API with a strict JSON schema, and returns only `NpcAiResponse`.
The desktop direct provider also supports the configured chat-completions endpoint.
Transport errors, timeouts, malformed/invalid responses, and request-id mismatches retry once with the same request id. Failure keeps the current plan unchanged and reports an AI error; it never falls back to local behavior.

## Verification

`Tools/NpcAuthorityChecks.cs` checks queue preservation, multi-step interruption and resumption, retained progress, atomic validation, deduplication, cancellation scope, stale-response rejection, and unavailable-provider behavior against a freshly compiled game assembly. These are standalone scheduling checks, not scene navigation or live-network tests.
