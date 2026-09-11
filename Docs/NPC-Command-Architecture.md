# NPC natural-language command architecture

## Runtime flow

1. The persistent command UI accepts a player's natural-language instruction.
2. `NpcCommandSystem` builds an `NpcAiRequest` from the instruction, NPC needs, inventory, farm snapshot, current task, and queued tasks.
3. An `INpcDecisionProvider` returns an `NpcAiResponse`. The demo uses `LocalNpcDecisionProvider`; the future network adapter must implement the same interface.
4. The response is validated and converted into an append, priority, cancel-current, or cancel-all queue operation.
5. The game selects concrete plots and delegates one atomic action at a time to `NpcWateringAgent`.
6. Only successful game actions change farm state, inventory, stamina, hunger, and thirst.

The AI response is a scheduling request. It never writes authoritative world state or declares that an action succeeded.

## AI contract

`NpcAiRequest` contains a monotonically increasing `requestId`, the fixed model instructions, current progress, and recent memory. A delayed response older than the newest accepted response is discarded. The future API adapter should serialize the request with `JsonUtility.ToJson` and deserialize exactly one `NpcAiResponse`. A response contains an ordered `tasks` array, so one instruction can schedule sequences such as watering followed by harvesting.

Supported response goals are:

- `farm_cycle`
- `plant`
- `water`
- `fertilize`
- `weed`
- `harvest`
- `sleep`

Supported scheduling modes are:

- `append`
- `priority`
- `cancel_current`
- `cancel_all`

For `farm_cycle`, the game snapshots the eligible farm plots when the order starts. It plants with the most abundant eligible seed, tends those crops, waits through growth, and finishes after the final harvest. Harvested plots leave the current cycle and are not replanted.

## Needs and daily routine

- Successful farm actions consume stamina and slightly increase hunger and thirst.
- Low stamina pauses the current work in place. Work resumes after recovery.
- Low hunger consumes one edible `Product` from the shared player inventory.
- Before harvesting, insufficient inventory space triggers an internal warehouse trip. The NPC first looks for a `Warehouse` component and falls back to the farm scene's `Warehouse` Tilemap as the drop-off point. It stores every `Product`, keeps seeds and tools, then resumes the same harvest order without asking the AI again.
- At 22:00 the NPC retains the current order, travels to `House_Interior`, approaches the `Bed`, and sleeps.
- At 06:00 the NPC returns to `Farm_Outdoor` and resumes the retained order.
- Drinking is an integration point. The house drinking-station module implements `INpcDrinkSource`; the command system finds it, walks to it, and restores thirst only after `TryDrink` succeeds.

NPC needs, memory, and task queues are intentionally runtime-only for this demo.

## Local test phrases

- `去干农活`
- `浇水`
- `浇完以后收菜`
- `先去除草`
- `马上施肥`
- `停下`
- `全部取消`
- `回家睡觉`

The local parser is only a deterministic stand-in for the API. Gameplay and queue behavior remain the same when the provider is replaced.

## OpenAI test integration

`NpcOpenAiDecisionProvider` sends the same request contract to the proxy configured in
`Assets/StreamingAssets/npc-ai-config.json`. The proxy under `server/` owns the OpenAI key,
uses the Responses API with a strict JSON schema, and returns only `NpcAiResponse`.
Transport errors, timeouts, malformed JSON, and request-id mismatches fall back to
`LocalNpcDecisionProvider`, so AI downtime does not stop the NPC command UI.
