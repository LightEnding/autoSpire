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

`GameHookServer.DetectPhase()` (`GameHookServer.cs:444`) is the single source of truth for game phase. Priority order:

1. `NMapScreen.IsOpen` → `"map"`
2. `NOverlayStack.Peek()` is `NRewardsScreen` or `NCardRewardSelectionScreen` → `"reward"`
3. Room `IsPreFinished` → `"map"`
4. `room.RoomType` switch (Map/Shop/Treasure/RestSite/Event)
5. `CombatManager.IsInProgress` → `"combat"`
6. Fallback: `CurrentMapPoint != null` → `"map"`
7. Otherwise → `"loading"`

## AI action loop

AI should follow this pattern:
1. Call `get_state` to see current phase and state
2. Check `waiting_for_input` — only send actions when true
3. Call `take_action` with the appropriate action and parameters
4. After `play_card`, check `ActionResult.hand_card_selection` / `card_selection` — card selection (e.g., Exhume trigger) may have opened; if so, call `pick_card` followed by another `get_state` to see updated hand

## Known limitations

- **Shop/Event not implemented**: `shop_action` and `event_action` have no C# backend; `ShopSnapshot` and `EventSnapshot` are skeleton stubs.
- **Damage/Block always null**: need to read from the ValueProp system (not yet implemented).
- **Card descriptions empty in combat**: dynamic selectors like `{Damage:diff()}` crash `GetFormattedText()`, so descriptions are intentionally left blank.
- **Async card play**: `play_card` enqueues via `ActionQueueSynchronizer.RequestEnqueue` — card selection triggers (e.g., Purge) resolve in subsequent frames, not in the `play_card` response.
