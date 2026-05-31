# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project overview

autoSpire is a Godot mod for Slay the Spire 2 that lets an AI (Claude Code) drive gameplay decisions. Architecture:

```
AI (Claude Code) ←─ MCP stdio ─→ mcp/server.py ←─ HTTP ─→ C# GameHookServer (in-game)
```

- **C# side** (`scripts/`): `[ModInitializer]` entry point (`scripts/Entry.cs:22`), registers Harmony patches, starts embedded HTTP server on `localhost:8765` (auto-increments if port busy, up to 10 attempts).
- **Python side** (`mcp/server.py`): pure passthrough MCP adapter — no game logic, just translates MCP tool calls to HTTP GET/POST. Configured via `.mcp.json`.
- **Multi-instance support**: set `AUTOSPIRE_PORT=8766` env var for a second game client (guest). `.mcp.json` defines both `autospire` and `autospire2` MCP servers.

## Build & run

Build the C# mod and copy to the game's mod folder:

```bash
dotnet build
```

The `.csproj` post-build target (`autoSpire.csproj:28`) auto-copies `autoSpire.dll` and `autoSpire.json` to `$(Sts2Dir)\mods\autoSpire\`. Set `<Sts2Dir>` in the `.csproj` to your Slay the Spire 2 install path.

Install Python MCP dependencies:

```bash
pip install -r mcp/requirements.txt
```

The mod loads automatically when Slay the Spire 2 starts. Confirm via the log: `[autoSpire] autoSpire mod initialized!`

## Threading model (critical)

The C# side has a strict threading constraint — **Godot objects can only be accessed on the main thread**:

- **Main thread** (`UpdateNode._Process` → `GameHookServer.Update()`): refreshes `_cachedState` from game APIs each frame, then dequeues and executes pending actions from `_actionQueue`.
- **Background thread** (`ListenLoop` + `ThreadPool`): HttpListener accepts requests. `GET /state` reads `_cachedState` (lock-protected). `POST /action` enqueues a `PendingAction` and blocks on a `TaskCompletionSource` until the main thread completes it.
- `_stateLock` guards the cached snapshot; `ConcurrentQueue` + `ConcurrentDictionary<Guid, TCS>` bridge the thread boundary for writes.

## Key files

| File | Role |
|------|------|
| `scripts/Entry.cs` | Mod entry point, `[ModInitializer]` |
| `scripts/core/GameHookServer.cs` | Embedded HTTP server, state snapshot builder, action executor (all in one) |
| `scripts/core/GameStateSnapshot.cs` | All data records: `GameStateSnapshot`, `ActionRequest`, `ActionResult`, combat/map/rest/reward sub-types |
| `scripts/commands/AutoSpireCmd.cs` | In-game dev console commands (`autospire state/play/pick/map`) |
| `mcp/server.py` | MCP stdio adapter, translates tool calls to HTTP |
| `autoSpire.json` | Mod manifest (id, name, version, has_pck, has_dll) |

## Phase detection

`GameHookServer.DetectPhase()` is the single source of truth for game phase. Priority order:

1. `NOverlayStack.Peek()` is `NGameOverScreen` → `"game_over"`
2. `NMapScreen.IsOpen` → `"map"`
3. `NOverlayStack.Peek()` is `NRewardsScreen` or `NCardRewardSelectionScreen` → `"reward"`
4. Room `IsPreFinished` → `"map"`
5. `room.RoomType` switch (Map/Shop/Treasure/RestSite/Event)
6. `CombatManager.IsInProgress` → `"combat"`
7. `CurrentMapPoint != null` → `"map"`
8. `NRun.Instance?.EventRoom != null` → `"event"`
9. Otherwise → `"loading"`

When `!RunManager.Instance.IsInProgress`, `BuildMenuSnapshot()` detects the current menu screen via `NGame.Instance.RootSceneContainer.CurrentScene` + `NMainMenu.SubmenuStack`, covering 20+ screen types (main_menu, character_select, singleplayer_submenu, multiplayer_host, daily_run, custom_run, modal, etc.).

## AI action loop

AI should follow this pattern:
1. Call `get_state` to see current phase and state
2. Check `waiting_for_input` — only send actions when true
3. Call `take_action` with the appropriate action and parameters
4. **Prefer `multi_play` over `play_card`** — batch all playable cards in one call to reduce tool invocations. Only use single `play_card` for cards that trigger selections (Survivor/Purge etc.).
5. After `play_card` / `multi_play`, call `get_state` to see updated hand — card selection triggers (e.g., Survivor discard) may have opened; if so, handle `pick_card` + `confirm_selection`

## All supported actions

| Action | Key Parameters | Notes |
|--------|---------------|-------|
| `play_card` | `hand_index`, `target_id?` | Supports AOE/Random/Self targets |
| `multi_play` | `cards: [{hand_index, target_id?}]` | Batch play — 2-phase resolve then enqueue. Follow with `get_state` |
| `end_turn` | — | End player turn |
| `use_potion` | `slot_index`, `target_id?` | Use potion |
| `pick_card` | `card_index` | Select card in overlay or hand selection mode |
| `confirm_selection` | — | Confirm hand card selection |
| `move_to_map_coord` | `col`, `row` | Choose map route |
| `pick_reward` | `choice_index`, `choice_type`, `card_index?` | Pick combat reward |
| `rest_action` | `option_index`, `card_index?` | Rest site action (heal/upgrade/remove) |
| `shop_action` | `shop_action`, `item_index?` | Buy card/relic/potion, remove card, leave |
| `treasure_action` | `treasure_action` | Open chest, pick relic, skip, leave |
| `event_action` | `option_index` | Choose event option |
| `menu_action` | `menu_action`, `character_index?`, `ascension_level?` | Navigate menus, start games |

## Known limitations

- **`multi_play` is async**: `RequestEnqueue` is async — `played_card_name` is the attempt list, not guaranteed result. AI must follow with `get_state`. Cards with selection triggers (Survivor/Purge) block subsequent cards in the same batch — play them separately or at end of batch.
- **Card descriptions**: `:energyIcons` / `:starIcons` formatters produce `[img]` tags that are replaced with text. Some dynamic vars (e.g. `{BatheCurses}`) may fall back to raw text.
- **Multiplayer hosting**: requires Steam network layer; fails with error popup without Steam.
- **Phase detection**: when `NMapScreen.IsOpen` is true, it takes priority over room types. This can cause edge cases where map opens before rewards are collected.
