using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.addons.mega_text;

namespace autoSpire.scripts.core;

/// <summary>
/// 嵌入式 HTTP 服务 + 状态管理 + 动作执行的统一入口。
///
/// 架构设计（线程模型）：
/// ┌──────────────────────────────────────────────────────┐
/// │  后台线程（HttpListener）                            │
/// │  ├─ 接收 HTTP 请求                                  │
/// │  ├─ GET  /state  → 读取 CachedState（lock 保护）     │
/// │  └─ POST /action → 写入 _actionQueue + 等待 TCS     │
/// └──────────────────────────────────────────────────────┘
///                         ↑↓ ConcurrentQueue + TaskCompletionSource
/// ┌──────────────────────────────────────────────────────┐
/// │  主线程（Godot _Process）                            │
/// │  ├─ RefreshState() → 从游戏 API 构建状态快照         │
/// │  └─ 消费 _actionQueue → 执行动作 → 完成 TCS         │
/// └──────────────────────────────────────────────────────┘
///
/// 为什么这样设计：
/// - Godot 对象只能在主线程访问，HTTP 后台线程不能碰它们
/// - 读操作（GET /state）：主线程每帧刷新缓存，后台线程直接读缓存
/// - 写操作（POST /action）：后台线程入队，主线程消费执行，通过 TCS 回传结果
/// </summary>
public class GameHookServer
{
    /// <summary>HTTP 服务监听起始端口，若被占用则自动递增</summary>
    private const int BasePort = 8765;
    private const int MaxPortAttempts = 10;

    /// <summary>实际使用的端口</summary>
    public int ActualPort { get; private set; }

    /// <summary>HTTP 监听器，运行在后台线程</summary>
    private HttpListener? _listener;
    /// <summary>待执行的动作队列：后台线程写入，主线程消费</summary>
    private readonly ConcurrentQueue<PendingAction> _actionQueue = new();
    /// <summary>等待中的 HTTP 请求映射：requestId → TCS，主线程完成后通过 TCS 通知后台线程</summary>
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<ActionResult>> _pendingResults = new();
    /// <summary>GET /state 请求映射：requestId → TCS&lt;GameStateSnapshot&gt;</summary>
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<GameStateSnapshot>> _pendingStates = new();
    /// <summary>GET /_debug 请求映射</summary>
    /// <summary>HTTP 服务运行标记，设为 false 时停止监听循环</summary>
    private bool _running;
    /// <summary>JSON 序列化选项：忽略 null 值以节省 token</summary>
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    /// <summary>挂载到 Godot 场景树的更新节点，用于驱动 _Process</summary>
    private UpdateNode? _node;

    // ── Lifecycle ──────────────────────────────────────────────────────

    /// <summary>
    /// 启动 HTTP 服务并挂载更新节点到 Godot 场景树。
    ///
    /// 步骤：
    /// 1. 创建 HttpListener，监听 localhost:DEFAULT_PORT
    /// 2. 启动后台线程，循环等待 HTTP 请求
    /// 3. 创建 UpdateNode 并挂到 NGame 下，驱动主线程每帧更新
    ///
    /// 如果端口被占用或无权限（Win 上需 urlacl 保留），会打印修复命令并跳过启动。
    /// </summary>
    public void Start()
    {
        // 从 BasePort 开始尝试，若端口被占用则自动递增
        for (int port = BasePort; port < BasePort + MaxPortAttempts; port++)
        {
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{port}/");
                _listener.Start();
                ActualPort = port;
                break;
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 5)
            {
                LogError("HttpListener access denied. Run this command as admin:");
                LogError($"  netsh http add urlacl url=http://localhost:{port}/ user=Everyone");
                return;
            }
            catch (HttpListenerException)
            {
                // 端口被占用，尝试下一个
                _listener?.Close();
                _listener = null;
            }
        }

        if (_listener == null)
        {
            LogError($"Failed to start HTTP server on ports {BasePort}-{BasePort + MaxPortAttempts - 1}");
            return;
        }

        _running = true;
        var thread = new Thread(ListenLoop) { IsBackground = true, Name = "AutoSpire-HTTP" };
        thread.Start();

        _node = new UpdateNode(this);
        NGame.Instance.AddChildSafely(_node);
        LogInfo($"HTTP server started on http://localhost:{ActualPort}/");
    }

    /// <summary>
    /// 停止 HTTP 服务并从场景树移除更新节点。
    /// </summary>
    public void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }

        if (_node != null)
            NGame.Instance.RemoveChildSafely(_node);
        LogInfo("HTTP server stopped");
    }

    // ── Main thread update ─────────────────────────────────────────────

    /// <summary>
    /// 主线程每帧更新入口，由 UpdateNode._Process 调用。
    ///
    /// 执行顺序：
    /// 1. 刷新状态缓存（从游戏 API 重新构建 GameStateSnapshot）
    /// 2. 消费所有待处理动作（后台 HTTP 线程入队的）
    /// 3. 每完成一个动作，通过 TCS 唤醒对应的 HTTP 请求
    /// </summary>
    /// <summary>
    /// 主线程每帧更新：刷新状态 + 消费动作队列。
    /// </summary>
    internal void Update()
    {
        while (_actionQueue.TryDequeue(out var pending))
        {
            if (pending.Request == null)
            {
                if (_pendingStates.TryRemove(pending.Id, out var stateTcs))
                    stateTcs.SetResult(BuildState());
            }
            else
            {
                var result = ExecuteAction(pending.Request);
                if (_pendingResults.TryRemove(pending.Id, out var actTcs))
                    actTcs.SetResult(result);
            }
        }
    }

    // ── HTTP listener loop ─────────────────────────────────────────────

    /// <summary>
    /// 后台线程的主循环：阻塞等待 HTTP 请求，收到后分发给线程池处理。
    ///
    /// GetContext() 是阻塞调用，线程在这里 sleep 直到有请求到来。
    /// 请求处理委托给 ThreadPool，本循环立即回到阻塞等待状态。
    /// </summary>
    private void ListenLoop()
    {
        while (_running && _listener?.IsListening == true)
        {
            try
            {
                var ctx = _listener.GetContext();
                // 不在此线程处理请求，交由线程池以避免阻塞后续请求
                ThreadPool.QueueUserWorkItem(_ => HandleRequestAsync(ctx));
            }
            catch (HttpListenerException)
            {
                // listener 被 Stop() 关闭时会抛出此异常，正常退出循环
                if (!_running) break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// HTTP 请求处理入口（运行在线程池线程）。
    ///
    /// 路由：
    ///   OPTIONS /       → 204（CORS 预检）
    ///   GET    /state   → 返回缓存的状态快照 JSON
    ///   POST   /action  → 解析 ActionRequest，入队等待主线程执行，返回 ActionResult
    ///   其他             → 404
    /// </summary>
    private async void HandleRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            var res = ctx.Response;
            SetCorsHeaders(res);

            // 浏览器 CORS 预检请求，直接返回空响应
            if (req.HttpMethod == "OPTIONS")
            {
                res.StatusCode = 204;
                res.Close();
                return;
            }

            switch (req.HttpMethod)
            {
                case "GET" when req.Url?.AbsolutePath == "/state":
                    await HandleGetState(res);
                    break;
                case "POST" when req.Url?.AbsolutePath == "/action":
                    await HandlePostAction(req, res);
                    break;
                default:
                    res.StatusCode = 404;
                    res.Close();
                    break;
            }
        }
        catch (Exception ex)
        {
            LogError($"HTTP error: {ex.Message}");
            try { ctx.Response.Close(); } catch { /* 可能已关闭，忽略 */ }
        }
    }

    /// <summary>
    /// 设置 CORS 响应头，允许任意来源的请求。
    /// 本地开发时方便用浏览器 / 外部工具调试。
    /// </summary>
    private static void SetCorsHeaders(HttpListenerResponse res)
    {
        res.AddHeader("Access-Control-Allow-Origin", "*");
        res.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        res.AddHeader("Access-Control-Allow-Headers", "Content-Type");
    }

    // ── GET /state ─────────────────────────────────────────────────────

    /// <summary>
    /// 处理 GET /state：入队 state 请求，主线程构建后通过 TCS 返回。
    /// </summary>
    private async Task HandleGetState(HttpListenerResponse res)
    {
        var requestId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<GameStateSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingStates[requestId] = tcs;
        _actionQueue.Enqueue(new PendingAction(requestId, null!)); // null ActionRequest = state 请求
        var state = await tcs.Task;
        _pendingStates.TryRemove(requestId, out _);

        var json = JsonSerializer.Serialize(state, _jsonOpts);
        await WriteJson(res, 200, json);
    }

    // ── POST /action ───────────────────────────────────────────────────

    /// <summary>
    /// 处理 POST /action：解析动作请求，入队等待主线程执行，返回结果。
    ///
    /// 请求生命周期：
    /// 1. 解析 JSON → ActionRequest
    /// 2. 创建 TaskCompletionSource + 生成 requestId
    /// 3. 将 (requestId, ActionRequest) 入队到 _actionQueue
    /// 4. await TCS.Task（阻塞当前 HTTP 线程，不占用主线程）
    /// 5. 主线程 Update() 消费队列，执行动作，完成 TCS
    /// 6. 本方法恢复执行，返回 ActionResult JSON
    ///
    /// 使用 RunContinuationsAsynchronously 避免 TCS 完成回调在当前线程同步执行导致死锁。
    /// </summary>
    private async Task HandlePostAction(HttpListenerRequest req, HttpListenerResponse res)
    {
        string body;
        using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
        {
            body = await reader.ReadToEndAsync();
        }

        ActionRequest? actionReq;
        try
        {
            actionReq = JsonSerializer.Deserialize<ActionRequest>(body);
        }
        catch (JsonException)
        {
            await WriteJson(res, 400, JsonSerializer.Serialize(new ActionResult(false, "Invalid JSON"), _jsonOpts));
            return;
        }

        if (actionReq == null)
        {
            await WriteJson(res, 400, JsonSerializer.Serialize(new ActionResult(false, "Empty request"), _jsonOpts));
            return;
        }

        // 生成唯一 ID，用于匹配 HTTP 请求和主线程执行结果
        var requestId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<ActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingResults[requestId] = tcs;
        _actionQueue.Enqueue(new PendingAction(requestId, actionReq));

        // 等待主线程执行完成（不阻塞 UI，因为 await 的是 TCS 而非同步等待）
        var result = await tcs.Task;
        _pendingResults.TryRemove(requestId, out _);

        int statusCode = result.Success ? 200 : 422;
        await WriteJson(res, statusCode, JsonSerializer.Serialize(result, _jsonOpts));
    }

    /// <summary>
    /// 向 HTTP Response 写入 JSON 并关闭连接。
    /// </summary>
    private static async Task WriteJson(HttpListenerResponse res, int statusCode, string json)
    {
        var buffer = Encoding.UTF8.GetBytes(json);
        res.StatusCode = statusCode;
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = buffer.Length;
        await res.OutputStream.WriteAsync(buffer);
        res.Close();
    }

    // ── State refresh ──────────────────────────────────────────────────

    /// <summary>
    /// 构建当前游戏状态快照，每次 GET /state 时由主线程调用。
    /// </summary>
    private static GameStateSnapshot BuildState()
    {
        try
        {
            // 游戏外界面：当 RootSceneContainer 不是 NRun 场景时才算菜单
            // 注意：不可以用 !IsInProgress，奖励/选牌等过渡阶段 IsInProgress 可能短暂为 false
            if (NGame.Instance?.CurrentRunNode == null)
            {
                var menuSnap = BuildMenuSnapshot();
                var emptyRun = new RunSnapshot(0, 1, 0, 0, [], []);
                LogInfo($"[BuildState] phase=menu, screen={menuSnap.Screen}");
                return new GameStateSnapshot("menu", true, null, null, null, null, null, null, null, menuSnap, emptyRun);
            }

            var runState = RunManager.Instance.DebugOnlyGetState();
            if (runState == null)
                return CreateEmptyState();

            var player = LocalContext.GetMe(runState);
            var phase = DetectPhase(runState);
            var waiting = IsWaitingForInput(phase, runState);

            CombatSnapshot? combat = null;
            MapSnapshot? map = null;
            ShopSnapshot? shop = null;
            RewardSnapshot? reward = null;
            RestSnapshot? rest = null;
            EventSnapshot? eventSnap = null;
            TreasureSnapshot? treasure = null;
            MenuSnapshot? menu = null;

            switch (phase)
            {
                case "combat":
                    combat = BuildCombatSnapshot(runState, player);
                    break;
                case "map":
                    map = BuildMapSnapshot(runState);
                    break;
                case "shop":
                    shop = BuildShopSnapshot(player);
                    break;
                case "reward":
                    reward = BuildRewardSnapshot();
                    break;
                case "rest":
                    rest = BuildRestSnapshot();
                    break;
                case "event":
                    eventSnap = BuildEventSnapshot();
                    break;
                case "treasure":
                    treasure = BuildTreasureSnapshot(runState);
                    break;
            }

            var run = new RunSnapshot(
                AscensionLevel: runState.AscensionLevel,
                CurrentAct: runState.CurrentActIndex + 1,
                CurrentFloor: runState.ActFloor,
                Gold: player?.Gold ?? 0,
                DeckCards: player?.Deck.Cards
                    .GroupBy(c => c.Id)
                    .Select(g => new DeckCardSnapshot(
                        g.Key.ToString(),
                        g.First().Title.ToString() ?? g.Key.ToString(),
                        g.Count()))
                    .ToList() ?? [],
                Relics: player?.Relics.Select(r =>
                    new RunRelicSnapshot(
                        SafeFormat(r.Title),
                        SafeFormat(r.DynamicDescription))).ToList() ?? []
            );

            LogInfo($"[BuildState] phase={phase}, waiting={waiting}, eventSnap={eventSnap != null}");
            return new GameStateSnapshot(phase, waiting, combat, map, shop, reward, rest, eventSnap, treasure, menu, run);
        }
        catch (Exception ex)
        {
            LogError($"State build error: {ex.Message}");
            return CreateEmptyState();
        }
    }

    /// <summary>
    /// 根据当前 RunState 和 CombatManager 判断游戏阶段。
    ///
    /// 核心逻辑：
    /// 1. 奖励 overlay（NRewardsScreen / NCardRewardSelectionScreen）→ reward
    /// 2. NMapScreen.IsOpen（地图界面可见）→ map
    /// 3. 房间已 pre-finished → map（CombatRoom/EventRoom 内容完成，选路线中）
    /// 4. 其他房间类型（Shop/Treasure/RestSite/Event）→ 对应阶段
    /// 5. CombatManager.IsInProgress → combat
    /// 6. 降级判断：CurrentMapPoint != null → map
    ///
    /// 地图界面并非一个 Room（MapRoom 另有所指），因此用 NMapScreen.IsOpen 检测。
    /// IsPreFinished 退化为 CombatRoom/EventRoom 的兜底判断。
    /// </summary>
    internal static string DetectPhase(IRunState runState)
    {
        var room = runState.CurrentRoom;
        // 游戏结束优先 — 角色死亡/通关后弹出结算界面，地图可能在背后打开
        if (NOverlayStack.Instance?.Peek() is NGameOverScreen)
            return "game_over";

        // 地图优先 — 地图打开时一定是 map（奖励覆盖层在宝藏房等场景单独检测）
        if (NMapScreen.Instance?.IsOpen == true)
            return "map";

        // 奖励界面覆盖层（地图未打开时）
        if (NOverlayStack.Instance?.Peek() is NRewardsScreen or NCardRewardSelectionScreen)
            return "reward";


        // 房间内容已完成（CombatRoom/EventRoom 的 IsPreFinished），选路线中
        if (room != null && room.IsPreFinished)
            return "map";

        // 其他非战斗房间类型
        if (room != null)
        {
            switch (room.RoomType)
            {
                case RoomType.Map:
                    return "map";
                case RoomType.Shop:
                    return "shop";
                case RoomType.Treasure:
                    return "treasure";
                case RoomType.RestSite:
                    return "rest";
                case RoomType.Event:
                    return "event";
            }
        }

        // 战斗阶段：IsInProgress 为 true
        if (CombatManager.Instance.IsInProgress)
            return "combat";

        // room 为 null 或 Unassigned 时，以 CurrentMapPoint 降级判断地图
        if (runState.CurrentMapPoint != null)
            return "map";

        // 古之民等预 Run 事件：CurrentRoom 可能为 null 但 NEventRoom 已创建
        if (NRun.Instance?.EventRoom != null)
            return "event";

        return "loading";
    }

    /// <summary>
    /// 判断游戏是否正在等待玩家输入。
    ///
    /// 战斗阶段：等待出牌阶段 且 玩家动作未被禁用
    ///   - IsPlayPhase: 玩家回合中，可以出牌 / 用技能 / 结束回合
    ///   - PlayerActionsDisabled: 游戏主动禁止操作（如动画播放中、特殊效果）
    /// 非战斗阶段：简单判断为 true（后续可以细化，如商店未加载完成时返回 false）
    /// </summary>
    private static bool IsWaitingForInput(string phase, IRunState runState)
    {
        return phase switch
        {
            "combat" => CombatManager.Instance.IsPlayPhase
                     && !CombatManager.Instance.PlayerActionsDisabled,
            "map" or "shop" or "reward" or "rest" or "event" or "treasure" or "game_over" => true,
            _ => false
        };
    }

    // ── Menu snapshot (out-of-run) ──────────────────────────────────────

    /// <summary>
    /// 构建游戏外界面快照。
    /// 通过 NGame.Instance.RootSceneContainer.CurrentScene 判断当前在哪个界面：
    /// NLogoAnimation → 启动动画，NMainMenu → 主菜单（含子菜单检测），其他 → loading。
    /// </summary>
    private static MenuSnapshot BuildMenuSnapshot()
    {
        // 模态弹窗优先 — 确认对话框/错误提示等覆盖在所有界面之上
        var openModal = NModalContainer.Instance?.OpenModal;
        if (openModal != null)
        {
            var modalType = openModal.GetType().Name;
            var screen = modalType switch
            {
                "NAbandonRunConfirmPopup" => "modal_confirm",
                "NDisconnectConfirmPopup" => "modal_confirm",
                _ => "modal"
            };
            return new MenuSnapshot(screen, false, false);
        }

        var currentScene = NGame.Instance?.RootSceneContainer.CurrentScene;
        if (currentScene == null)
            return new MenuSnapshot("loading", false, false);

        // 启动 Logo 动画
        if (currentScene is NLogoAnimation)
            return new MenuSnapshot("logo", false, false);

        // 主菜单及其子菜单
        if (currentScene is NMainMenu mainMenu)
        {
            // 更新日志覆盖层
            if (mainMenu.PatchNotesScreen?.IsOpen == true)
                return new MenuSnapshot("patch_notes", false, CanContinueRun());

            // 子菜单栈
            if (mainMenu.SubmenuStack?.SubmenusOpen == true)
            {
                var submenu = mainMenu.SubmenuStack.Peek();
                var screen = submenu != null ? SubmenuTypeToScreen(submenu.GetType().Name) : "submenu";
                return new MenuSnapshot(screen, true, false);
            }

            // 主菜单根界面
            return new MenuSnapshot("main_menu", false, CanContinueRun());
        }

        return new MenuSnapshot("loading", false, false);
    }

    /// <summary>
    /// 将 NSubmenu 子类型名称映射为简短的界面标识字符串。
    /// </summary>
    private static string SubmenuTypeToScreen(string typeName)
    {
        return typeName switch
        {
            "NSingleplayerSubmenu" => "singleplayer_submenu",
            "NMultiplayerSubmenu" => "multiplayer_submenu",
            "NMultiplayerHostSubmenu" => "multiplayer_host",
            "NJoinFriendScreen" => "join_friend",
            "NCharacterSelectScreen" => "character_select",
            "NCompendiumSubmenu" => "compendium",
            "NSettingsScreen" => "settings",
            "NCustomRunScreen" => "custom_run",
            "NDailyRunScreen" => "daily_run",
            "NRunHistoryScreen" => "run_history",
            "NStatsScreen" => "stats",
            "NTimelineScreen" => "timeline",
            "NCardLibrary" => "card_library",
            "NRelicCollection" => "relic_collection",
            "NBestiary" => "bestiary",
            "NPotionLab" => "potion_lab",
            "NModdingScreen" => "modding",
            "NProfileScreen" => "profile",
            "NCreditsScreen" => "credits",
            _ => "submenu"
        };
    }

    /// <summary>
    /// 检查主菜单上是否存在"继续游戏"按钮（即是否有存档记录）。
    /// </summary>
    private static bool CanContinueRun()
    {
        return SaveManager.Instance?.HasRunSave ?? false;
    }

    // ── Combat snapshot ────────────────────────────────────────────────

    /// <summary>
    /// 构建战斗状态快照。
    ///
    /// 数据来源：
    /// - 手牌/牌堆: Player.PlayerCombatState.Hand / DrawPile / DiscardPile / ExhaustPile
    /// - 能量: PlayerCombatState.Energy / MaxEnergy
    /// - 敌人: CombatState.Enemies → Creature (HP / Block / Powers / Intent)
    /// - 玩家: Player.Creature (HP / Block) + Player.Relics
    /// - 药水: Player.PotionSlots
    /// </summary>
    internal static CombatSnapshot? BuildCombatSnapshot(IRunState runState, Player? player)
    {
        var combatState = CombatManager.Instance.DebugOnlyGetState();
        if (combatState == null || player?.PlayerCombatState == null)
            return null;

        // 手牌：每张卡牌构建 CardSnapshot（含费用、是否可出、合法目标等）
        var hand = player.PlayerCombatState.Hand.Cards
            .Select(c => BuildCardSnapshot(c, combatState))
            .ToList();

        // 敌人：每个 Creature 构建 EnemySnapshot（含 HP / 格挡 / 意图 / buff）
        var enemies = combatState.Enemies
            .Select(BuildEnemySnapshot)
            .ToList();

        // 药水：PotionSlots 是定长数组，null 槽位表示无药水，需过滤
        var potions = player.PotionSlots
            .Select((p, i) => p != null ? new PotionSnapshot(i, p.Title.GetFormattedText() ?? "", p.DynamicDescription.GetFormattedText() ?? "") : null)
            .Where(p => p != null)
            .Cast<PotionSnapshot>()
            .ToList();

        // 战斗中可能弹出选牌子界面（如搜寻、灰水等），检测并构建 CardSelection
        CardSelectionSnapshot? cardSelection = null;
        var overlay = NOverlayStack.Instance?.Peek();
        // 检查 overlay 选牌（NCardGridSelectionScreen 覆盖 DeckCardSelect/Upgrade/Transform/Enchant/SimpleCard 等子类）
        if (overlay is NCardRewardSelectionScreen or NCardGridSelectionScreen or NChooseACardSelectionScreen)
        {
            cardSelection = BuildCardSelectionFromOverlay((Node)overlay);
        }

        // 检查手牌选择模式（净化/丢弃/消耗等效果，NPlayerHand 进入 SimpleSelect/UpgradeSelect 模式）
        HandCardSelectionSnapshot? handCardSelection = null;
        var playerHand = NCombatRoom.Instance?.Ui?.Hand;
        if (playerHand is { IsInCardSelection: true })
        {
            handCardSelection = BuildHandCardSelection(playerHand);
        }

        return new CombatSnapshot(
            RoundNumber: combatState.RoundNumber,
            IsPlayPhase: CombatManager.Instance.IsPlayPhase,
            Energy: player.PlayerCombatState.Energy,
            MaxEnergy: player.PlayerCombatState.MaxEnergy,
            Hand: hand,
            DrawPileCount: player.PlayerCombatState.DrawPile.Cards.Count,
            DiscardPileCount: player.PlayerCombatState.DiscardPile.Cards.Count,
            ExhaustPileCount: player.PlayerCombatState.ExhaustPile.Cards.Count,
            Player: new PlayerSnapshot(
                CurrentHp: player.Creature.CurrentHp,
                MaxHp: player.Creature.MaxHp,
                Block: player.Creature.Block,
                Relics: player.Relics.Select(r => new RelicSnapshot(
                    r.Id.ToString(), r.Title.GetFormattedText() ?? "", SafeFormat(r.DynamicDescription), r.StackCount
                )).ToList()
            ),
            Enemies: enemies,
            Potions: potions,
            CardSelection: cardSelection,
            HandCardSelection: handCardSelection
        );
    }

    /// <summary>
    /// 构建单张卡牌的详细信息快照。
    ///
    /// 关键逻辑：
    /// - Cost: 通过 EnergyCost.GetWithModifiers(CostModifiers.All) 获取实际费用（含所有增减费效果）
    /// - CanPlay: 调用 CardModel.CanPlay() 获取是否可出牌及不能出的原因
    /// - ValidTargetIds: 如果卡牌需要目标且可出，列出所有可攻击的敌方 CombatId
    /// - Damage/Block: 当前为占位值 null，需要从 ValueProp 系统正确读取（后续完善）
    /// </summary>
    private static CardSnapshot BuildCardSnapshot(CardModel card, CombatState combatState)
    {
        // 判断此卡是否可出及不能出的原因（如费用不足 / 无合法目标）
        var canPlay = card.CanPlay(out var reason, out _);

        // 若卡牌需要敌方目标且当前可出，枚举所有合法目标（可被攻击的敌人）
        // 排除 None 和 Self 类型，只对真正的敌方目标类型做列表
        var validTargets = card.TargetType != TargetType.None && card.TargetType != TargetType.Self && canPlay
            ? combatState.HittableEnemies
                .Select(e => e.CombatId)       // CombatId 是 uint? 类型
                .Where(id => id.HasValue)       // 过滤 null
                .Select(id => (int)id!.Value)   // uint → int（CombatId 不会超过 int 范围）
                .ToList()
            : [];

        // 从 DynamicVars 安全读取 Damage/Block 基础值
        int? damage = null;
        if (card.DynamicVars.TryGetValue("Damage", out var dmgVar))
            damage = dmgVar.IntValue;
        int? block = null;
        if (card.DynamicVars.TryGetValue("Block", out var blkVar))
            block = blkVar.IntValue;

        // 卡牌描述：复用 GetCardDescription 统一逻辑
        var description = GetCardDescription(card);

        return new CardSnapshot(
            Id: card.Id.ToString(),
            Name: card.Title.ToString() ?? "",
            Cost: card.EnergyCost.GetWithModifiers(CostModifiers.All),  // 含 X 费 / 增减费修正
            Type: card.Type.ToString(),
            Rarity: card.Rarity.ToString(),
            Damage: damage,
            Block: block,
            Description: description,
            CanPlay: canPlay,
            UnplayableReason: canPlay ? null : reason.ToString(),
            NeedsTarget: card.TargetType != TargetType.None && card.TargetType != TargetType.Self,
            ValidTargetIds: validTargets
        );
    }

    /// <summary>
    /// 构建单个敌方单位的快照。
    ///
    /// 意图获取：Monster.NextMove.Intents[0].GetHoverTip()
    /// 意图标签是已本地化的描述文本，如"造成 12 点伤害"。
    /// 如果敌人没有意图（如已死亡），返回空字符串。
    ///
    /// Buff/Debuff：通过 Creature.Powers 获取所有挂载的能力效果，
    /// 用 PowerModel.Amount 作为层数 / 堆叠计数。
    /// </summary>
    private static EnemySnapshot BuildEnemySnapshot(Creature enemy)
    {
        // 获取意图：取第一个 intent 的本地化标签文本
        var intents = enemy.Monster?.NextMove?.Intents;
        var intentLabel = intents?.FirstOrDefault()?.GetHoverTip([enemy], enemy).Description ?? "";

        return new EnemySnapshot(
            CombatId: (int)(enemy.CombatId ?? 0),
            Name: enemy.Monster?.Title.GetFormattedText() ?? "Unknown",
            CurrentHp: enemy.CurrentHp,
            MaxHp: enemy.MaxHp,
            Block: enemy.Block,
            IntentLabel: intentLabel,
            Buffs: enemy.Powers.Select(p => new BuffSnapshot(
                p.Title.GetFormattedText() ?? "", p.Amount, p.Description.GetFormattedText() ?? ""
            )).ToList(),
            IsAlive: enemy.IsAlive,
            IsHittable: enemy.IsHittable
        );
    }

    // ── Map snapshot ───────────────────────────────────────────────────

    /// <summary>
    /// 构建地图快照：当前节点 + 可选择的下一步节点。
    ///
    /// 数据来源：
    /// - 当前节点: RunState.CurrentMapPoint (MapPoint)
    /// - 可选节点: CurrentMapPoint.Children → 下一层的所有可达节点
    /// - 节点类型: MapPoint.PointType (Monster / Elite / Shop / Treasure / RestSite 等)
    /// - 坐标: MapPoint.coord.col / coord.row
    /// </summary>
    internal static MapSnapshot BuildMapSnapshot(IRunState runState)
    {
        var actMap = runState.Map;
        var currentPoint = runState.CurrentMapPoint;

        // 获取可选节点：未选起始节点时用默认坐标 (3, 0)
        var availableNodes = currentPoint?.Children;
        if (currentPoint == null)
        {
            var startNode = actMap?.GetPoint(new MapCoord(3, 0));
            availableNodes = startNode != null ? new HashSet<MapPoint> { startNode } : [];
        }

        var availableList = availableNodes
            .Select(p => new MapNodeSnapshot(p.coord.col, p.coord.row, p.PointType.ToString()))
            .ToList();

        // 全地图节点列表（Grid + startMapPoints），用于路线规划
        var allPoints = new HashSet<MapPoint>(actMap?.GetAllMapPoints() ?? []);
        if (actMap != null)
        {
            foreach (var sp in actMap.startMapPoints)
                allPoints.Add(sp);
        }
        var allNodes = allPoints
            .Select(p => new ActMapNodeSnapshot(p.coord.col, p.coord.row, p.PointType.ToString()))
            .ToList();

        return new MapSnapshot(
            CurrentAct: runState.CurrentActIndex + 1,
            CurrentFloor: runState.ActFloor,
            CurrentNodeType: currentPoint?.PointType.ToString() ?? "",
            AvailableNodes: availableList,
            AllNodes: allNodes
        );
    }

    // ── Shop / Reward / Rest / Event (skeleton for now) ────────────────

    /// <summary>
    /// 安全格式化 LocString：try GetFormattedText，失败回退 GetRawText。
    /// </summary>
    private static string SafeFormat(LocString? loc)
    {
        if (loc == null) return "";
        try
        {
            var result = loc.GetFormattedText() ?? "";
            result = CleanIcons(result);
            return result;
        }
        catch
        {
            try { return CleanIcons(loc.GetRawText()); }
            catch { return ""; }
        }
    }

    /// <summary>将 [img] 图标标签替换为文字：能量图标→"能量"，星图标→"星"</summary>
    private static string CleanIcons(string text)
    {
        text = Regex.Replace(text, @"\[img\][^]]*energy_icon\.png\[/img\]", "能量");
        text = Regex.Replace(text, @"\[img\][^]]*star_icon\.png\[/img\]", "星");
        text = Regex.Replace(text, @"\[img\][^]]*\[/img\]", "");
        return text;
    }

    /// <summary>
    /// 格式化卡牌描述（非战斗场景）。
    /// DynamicVars + 上下文变量注入后调用 GetFormattedText，
    /// 若结果仍残留 {xxx} 模板（如 energyIcons/starIcons 因缺变量被 SmartFormat fallback 到 raw text），
    /// 用正则清除。异常时兜底同样清除所有模板标记。
    /// </summary>
    private static string GetCardDescription(CardModel? card)
    {
        if (card == null) return "";
        try
        {
            LocString desc = card.Description;
            card.DynamicVars.AddTo(desc);
            desc.Add(new IfUpgradedVar(card.IsUpgraded ? UpgradeDisplay.Upgraded : UpgradeDisplay.Normal));
            desc.Add("InCombat", CombatManager.Instance.IsInProgress);
            desc.Add("OnTable", false);
            desc.Add("IsTargeting", false);
            desc.Add("energyPrefix", EnergyIconHelper.GetPrefix(card));
            var result = desc.GetFormattedText();
            result = CleanIcons(result);
            // 清除仍未解析的 {xxx} 模板标记（兜底）
            if (result.Contains('{'))
                result = Regex.Replace(result, @"\{[^}]+\}", "").Trim();
            return result;
        }
        catch
        {
            var raw = card.Description.GetRawText();
            return Regex.Replace(raw, @"\{[^}]+\}", "");
        }
    }

    /// 构建商店快照。从 NMerchantRoom 读取 Inventory，遍历所有商品分类。
    /// </summary>
    private static ShopSnapshot? BuildShopSnapshot(Player? player)
    {
        var merchantRoom = NRun.Instance?.MerchantRoom;
        if (merchantRoom?.Room.Inventory == null) return null;
        var inventory = merchantRoom.Room.Inventory;

        int gold = player?.Gold ?? 0;
        bool canLeave = merchantRoom.ProceedButton.IsEnabled;

        // 角色卡牌（5 张：2 攻击 + 2 技能 + 1 能力）
        var characterCards = new List<ShopItemSnapshot>();
        foreach (var entry in inventory.CharacterCardEntries)
        {
            var cardName = entry.CreationResult?.Card?.Title.ToString() ?? "Unknown";
            var cardDesc = GetCardDescription(entry.CreationResult?.Card);
            characterCards.Add(new ShopItemSnapshot(
                characterCards.Count, "character_card",
                cardName, entry.Cost, cardDesc,
                entry.IsStocked, entry.EnoughGold));
        }

        // 无色卡牌（2 张）
        var colorlessCards = new List<ShopItemSnapshot>();
        foreach (var entry in inventory.ColorlessCardEntries)
        {
            var cardName = entry.CreationResult?.Card?.Title.ToString() ?? "Unknown";
            var cardDesc = GetCardDescription(entry.CreationResult?.Card);
            colorlessCards.Add(new ShopItemSnapshot(
                colorlessCards.Count, "colorless_card",
                cardName, entry.Cost, cardDesc,
                entry.IsStocked, entry.EnoughGold));
        }

        // 遗物（3 个）
        var relics = new List<ShopItemSnapshot>();
        foreach (var entry in inventory.RelicEntries)
        {
            var relicName = entry.Model?.Title.GetFormattedText() ?? "Unknown";
            relics.Add(new ShopItemSnapshot(
                relics.Count, "relic",
                relicName, entry.Cost, "",
                entry.IsStocked, entry.EnoughGold));
        }

        // 药水（3 个）
        var potions = new List<ShopItemSnapshot>();
        foreach (var entry in inventory.PotionEntries)
        {
            var potionName = entry.Model?.Title.GetFormattedText() ?? "Unknown";
            var potionDesc = entry.Model?.DynamicDescription.GetFormattedText() ?? "";
            potions.Add(new ShopItemSnapshot(
                potions.Count, "potion",
                potionName, entry.Cost, potionDesc,
                entry.IsStocked, entry.EnoughGold));
        }

        // 删牌
        int removeCardCost = 0;
        if (inventory.CardRemovalEntry is { } removalEntry)
        {
            removeCardCost = removalEntry.IsStocked ? removalEntry.Cost : 0;
        }

        // 删牌 / 变换等触发的选牌 overlay
        CardSelectionSnapshot? cardSelection = null;
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is NDeckCardSelectScreen or NDeckUpgradeSelectScreen)
        {
            cardSelection = BuildCardSelectionFromOverlay((Node)overlay);
        }

        return new ShopSnapshot(gold, characterCards, colorlessCards, relics, potions, removeCardCost, canLeave, cardSelection);
    }

    /// <summary>
    /// 构建战斗奖励选择快照。
    ///
    /// 数据来源：
    /// - 从 NOverlayStack 获取顶部 NRewardsScreen
    /// - 遍历其子树中的 NRewardButton，读取每个按钮的 Reward 属性
    /// - 根据 Reward 子类型（CardReward / GoldReward / RelicReward / PotionReward）分类
    /// - 如果当前顶部是 NCardRewardSelectionScreen（选牌子界面），填充 CardSelection 字段
    ///
    /// 每个 NRewardButton 对应一个 RewardItemSnapshot，Index 即为 choice_index。
    /// </summary>
    private static RewardSnapshot? BuildRewardSnapshot()
    {
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay == null) return null;

        CardSelectionSnapshot? cardSelection = null;

        // 如果当前显示的是选牌子界面，构建 CardSelection（委托给通用 helper）
        if (overlay is NCardRewardSelectionScreen)
        {
            cardSelection = BuildCardSelectionFromOverlay((Node)overlay);
        }

        // 尝试获取 NRewardsScreen（可能在选牌子界面下方，也可能是当前顶层）
        var screen = overlay as NRewardsScreen;
        if (screen == null && overlay is NCardRewardSelectionScreen)
        {
            // 从 overlay stack 中查找 NRewardsScreen
            // NOverlayStack 内部是 List<IOverlayScreen>，但 Peek 只返回顶部
            // 尝试用反射或者遍历场景树找到 NRewardsScreen
            var overlays = NOverlayStack.Instance?.GetType()
                .GetField("_overlays", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
                .GetValue(NOverlayStack.Instance) as System.Collections.IList;
            if (overlays != null)
            {
                foreach (var o in overlays)
                {
                    if (o is NRewardsScreen rs)
                    {
                        screen = rs;
                        break;
                    }
                }
            }
        }

        if (screen == null && cardSelection == null)
            return null;

        var items = new List<RewardItemSnapshot>();
        var allCardChoices = new List<CardSnapshot>();
        var allRelicChoices = new List<string>();
        int totalGold = 0;
        bool canSkip = false;

        if (screen != null)
        {
            // 递归查找屏幕中所有 NRewardButton 子节点
            var buttons = new List<NRewardButton>();
            FindNodesRecursive((Node)screen, buttons);

            int idx = 0;
            foreach (var btn in buttons)
            {
                var reward = btn.Reward;
                if (reward == null) continue;

                switch (reward)
                {
                    case CardReward cardReward:
                        {
                            // 卡牌奖励：列出可选卡牌，每张作为子选项
                            var cardOptions = cardReward.Cards.Select(c => new CardSnapshot(
                                Id: c.Id.ToString(),
                                Name: c.Title.ToString() ?? "",
                                Cost: c.EnergyCost.GetWithModifiers(CostModifiers.All),
                                Type: c.Type.ToString(),
                                Rarity: c.Rarity.ToString(),
                                Damage: null,
                                Block: null,
                                Description: c.Description?.GetFormattedText() ?? "",
                                CanPlay: true,
                                UnplayableReason: null,
                                NeedsTarget: false,
                                ValidTargetIds: []
                            )).ToList();

                            items.Add(new RewardItemSnapshot(
                                Index: idx,
                                Type: "card",
                                Name: "选牌",
                                Description: cardReward.Description.GetFormattedText() ?? "",
                                CardOptions: cardOptions
                            ));
                            allCardChoices.AddRange(cardOptions);
                            break;
                        }
                    case GoldReward goldReward:
                        {
                            items.Add(new RewardItemSnapshot(
                                Index: idx,
                                Type: "gold",
                                Name: "金币",
                                Description: goldReward.Description.GetFormattedText() ?? $"获得 {goldReward.Amount} 金币",
                                CardOptions: []
                            ));
                            totalGold += goldReward.Amount;
                            break;
                        }
                    case RelicReward relicReward:
                        {
                            var relicName = relicReward.Description.GetFormattedText() ?? "Unknown Relic";
                            items.Add(new RewardItemSnapshot(
                                Index: idx,
                                Type: "relic",
                                Name: relicName,
                                Description: relicName,
                                CardOptions: []
                            ));
                            allRelicChoices.Add(relicName);
                            break;
                        }
                    case PotionReward:
                        {
                            // 药水奖励：当前仅显示类型标记，后续可扩展
                            items.Add(new RewardItemSnapshot(
                                Index: idx,
                                Type: "potion",
                                Name: "药水",
                                Description: "获得一瓶药水",
                                CardOptions: []
                            ));
                            break;
                        }
                }
                idx++;
            }

            // 检查是否存在跳过/继续按钮
            var proceedBtn = FindNodesRecursive<NProceedButton>((Node)screen).FirstOrDefault();
            canSkip = proceedBtn != null;
        }

        return new RewardSnapshot(items, allCardChoices, allRelicChoices, totalGold, canSkip, cardSelection);
    }

    /// <summary>
    /// 递归查找 Node 子树中所有指定类型的节点。
    /// 用于遍历 Godot UI 树，提取 NRewardButton / NProceedButton 等控件。
    /// </summary>
    private static List<T> FindNodesRecursive<T>(Node parent, List<T>? results = null) where T : Node
    {
        results ??= new List<T>();
        foreach (Node child in parent.GetChildren())
        {
            if (child is T t) results.Add(t);
            FindNodesRecursive(child, results);
        }
        return results;
    }

    // ── Card selection helpers (shared across reward / rest / combat) ────

    /// <summary>
    /// 从任意选牌 overlay 构建 CardSelectionSnapshot。
    /// 支持的 overlay 类型：NCardRewardSelectionScreen / NDeckUpgradeSelectScreen / NDeckCardSelectScreen。
    /// </summary>
    private static CardSelectionSnapshot? BuildCardSelectionFromOverlay(Node overlay)
    {
        return overlay switch
        {
            NCardRewardSelectionScreen cardScreen => BuildCardSelectionFromRewardScreen(cardScreen),
            NCardGridSelectionScreen or NChooseACardSelectionScreen => BuildCardSelectionFromGridScreen(overlay),
            _ => null
        };
    }

    /// <summary>
    /// 从 NCardRewardSelectionScreen 构建 CardSelectionSnapshot。
    /// 卡牌信息从 NCardHolder 的 NCard 子节点提取。
    /// </summary>
    private static CardSelectionSnapshot? BuildCardSelectionFromRewardScreen(NCardRewardSelectionScreen cardScreen)
    {
        var holders = FindNodesRecursive<NCardHolder>((Node)cardScreen);
        var options = holders.Select(h =>
        {
            var cardNode = h.GetChildren().OfType<NCard>().FirstOrDefault();
            if (cardNode?.Model == null) return null;
            var c = cardNode.Model;
            return BuildCardSnapshot(c, null!); // combatState not needed for selection cards
        }).Where(c => c != null).Cast<CardSnapshot>().ToList();

        var prompt = ExtractOverlayPrompt((Node)cardScreen);
        return options.Count > 0 ? new CardSelectionSnapshot(prompt, options, CanSkip: false) : null;
    }

    /// <summary>
    /// 从 NDeckUpgradeSelectScreen / NDeckCardSelectScreen 构建 CardSelectionSnapshot。
    /// 卡牌信息从 NGridCardHolder.CardModel 提取。
    /// </summary>
    private static CardSelectionSnapshot? BuildCardSelectionFromGridScreen(Node screen)
    {
        var holders = FindNodesRecursive<NGridCardHolder>(screen);
        var options = holders.Select(h =>
        {
            var cardModel = h.CardModel;
            if (cardModel == null) return null;
            return BuildCardSnapshot(cardModel, null!);
        }).Where(c => c != null).Cast<CardSnapshot>().ToList();

        var prompt = ExtractOverlayPrompt(screen);
        return options.Count > 0 ? new CardSelectionSnapshot(prompt, options, CanSkip: true) : null;
    }

    /// <summary>
    /// 从选牌 overlay 提取提示文本（如"选择一张牌消耗"）。
    /// NCardGridSelectionScreen 子类使用 %BottomLabel（MegaRichTextLabel），
    /// NCardRewardSelectionScreen / NChooseACardSelectionScreen 使用 Banner → Label。
    /// </summary>
    private static string ExtractOverlayPrompt(Node overlay)
    {
        // NCardGridSelectionScreen 子类：%BottomLabel
        var bottomLabel = overlay.GetNodeOrNull<MegaRichTextLabel>("%BottomLabel");
        if (bottomLabel != null && !string.IsNullOrEmpty(bottomLabel.Text))
            return bottomLabel.Text;

        // 有的子类 BottomLabel 在 %BottomText 容器下
        var bottomText = overlay.GetNodeOrNull("%BottomText");
        if (bottomText != null)
        {
            var label = bottomText.GetNodeOrNull<MegaRichTextLabel>("%BottomLabel");
            if (label != null && !string.IsNullOrEmpty(label.Text))
                return label.Text;
        }

        // NCardRewardSelectionScreen: "UI/Banner"；NChooseACardSelectionScreen: "Banner"
        var banner = overlay.GetNodeOrNull<NCommonBanner>("UI/Banner")
                  ?? overlay.GetNodeOrNull<NCommonBanner>("Banner");
        if (banner != null)
        {
            var label = banner.GetNodeOrNull<MegaLabel>("%Label");
            if (label != null && !string.IsNullOrEmpty(label.Text))
                return label.Text;
        }

        return "";
    }

    /// <summary>
    /// 通用卡牌选择执行入口。根据当前 overlay 类型分发到对应的点击逻辑。
    /// 覆盖所有场景：奖励选牌、战斗选牌（搜寻/灰水等）、休息点升级/移除选牌。
    /// </summary>
    private static ActionResult TryExecuteCardSelection(ActionRequest request)
    {
        if (request.CardIndex == null)
            return new ActionResult(false, "card_index is required for card selection");

        // 检查手牌选择模式（NPlayerHand.IsInCardSelection）
        var playerHand = NCombatRoom.Instance?.Ui?.Hand;
        if (playerHand is { IsInCardSelection: true })
        {
            var result = ClickHandCardSelection(playerHand, request.CardIndex.Value);
            return result.Success ? WrapWithSelectionState(null) : result;
        }

        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay == null)
            return new ActionResult(false, "No overlay active and hand not in selection mode");

        ActionResult clickResult = overlay switch
        {
            NCardRewardSelectionScreen => ClickCardRewardSelection(request.CardIndex.Value),
            NDeckUpgradeSelectScreen => ClickUpgradeCardSelection(request.CardIndex.Value),
            NDeckCardSelectScreen => ClickDeckCardSelection(request.CardIndex.Value),
            NChooseACardSelectionScreen => ClickChooseACardSelection(request.CardIndex.Value),
            NCardGridSelectionScreen => ClickGridCardSelection(request.CardIndex.Value),
            _ => new ActionResult(false, $"No card selection screen active (found: {overlay.GetType().Name})")
        };
        return clickResult.Success ? WrapWithSelectionState(null) : clickResult;
    }

    /// <summary>
    /// 将当前选牌状态（手牌选择模式或 overlay 选牌）包装进 ActionResult。
    /// 用于 pick_card / confirm_selection 后让 AI 无需额外 get_state 即可看到最新状态。
    /// </summary>
    private static ActionResult WrapWithSelectionState(string? error)
    {
        HandCardSelectionSnapshot? handSel = null;
        CardSelectionSnapshot? cardSel = null;

        var playerHand = NCombatRoom.Instance?.Ui?.Hand;
        if (playerHand is { IsInCardSelection: true })
            handSel = BuildHandCardSelection(playerHand);

        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is NCardRewardSelectionScreen or NCardGridSelectionScreen or NChooseACardSelectionScreen)
            cardSel = BuildCardSelectionFromOverlay((Node)overlay);

        return new ActionResult(error == null, error,
            HandCardSelection: handSel,
            CardSelection: cardSel);
    }

    /// <summary>
    /// 点击 NCardRewardSelectionScreen 中的卡牌（单选出预览 → 由游戏自动处理）。
    /// </summary>
    private static ActionResult ClickCardRewardSelection(int cardIndex)
    {
        var cardScreen = NOverlayStack.Instance?.Peek() as NCardRewardSelectionScreen;
        if (cardScreen == null)
            return new ActionResult(false, "Card reward selection screen is not active");

        var holders = FindNodesRecursive<NCardHolder>((Node)cardScreen);
        if (cardIndex < 0 || cardIndex >= holders.Count)
            return new ActionResult(false, $"Invalid card_index: {cardIndex}. {holders.Count} cards available");

        holders[cardIndex].EmitSignal(NCardHolder.SignalName.Pressed, holders[cardIndex]);
        return new ActionResult(true, null);
    }

    /// <summary>
    /// 点击 NDeckUpgradeSelectScreen 中的卡牌（单选出预览 → 自动点击确认）。
    /// </summary>
    private static ActionResult ClickUpgradeCardSelection(int cardIndex)
    {
        var upgradeScreen = NOverlayStack.Instance?.Peek() as NDeckUpgradeSelectScreen;
        if (upgradeScreen == null)
            return new ActionResult(false, "Upgrade selection screen is not active");

        var holders = FindNodesRecursive<NGridCardHolder>((Node)upgradeScreen);
        if (cardIndex < 0 || cardIndex >= holders.Count)
            return new ActionResult(false, $"Invalid card_index: {cardIndex}. {holders.Count} cards available");

        holders[cardIndex].EmitSignal(NCardHolder.SignalName.Pressed, holders[cardIndex]);

        var confirmBtn = ((Node)upgradeScreen).GetNodeOrNull<NConfirmButton>("%UpgradeSinglePreviewContainer/Confirm");
        if (confirmBtn == null || !confirmBtn.IsEnabled)
            return new ActionResult(false, "Confirm button not available after card selection");

        confirmBtn.ForceClick();
        return new ActionResult(true, null);
    }

    /// <summary>
    /// 点击 NDeckCardSelectScreen 中的卡牌（多选切换 → 选够后自动确认预览）。
    /// </summary>
    private static ActionResult ClickDeckCardSelection(int cardIndex)
    {
        var cardSelectScreen = NOverlayStack.Instance?.Peek() as NDeckCardSelectScreen;
        if (cardSelectScreen == null)
            return new ActionResult(false, "Deck card selection screen is not active");

        var holders = FindNodesRecursive<NGridCardHolder>((Node)cardSelectScreen);
        if (cardIndex < 0 || cardIndex >= holders.Count)
            return new ActionResult(false, $"Invalid card_index: {cardIndex}. {holders.Count} cards available");

        // 点击卡牌切换选中状态
        holders[cardIndex].EmitSignal(NCardHolder.SignalName.Pressed, holders[cardIndex]);

        // 选够数量后预览面板自动出现 → 自动确认
        var previewContainer = ((Node)cardSelectScreen).GetNodeOrNull<Control>("%PreviewContainer");
        if (previewContainer is { Visible: true })
        {
            var previewConfirm = previewContainer.GetNodeOrNull<NConfirmButton>("%PreviewConfirm");
            if (previewConfirm is { IsEnabled: true })
            {
                previewConfirm.ForceClick();
            }
        }
        return new ActionResult(true, null);
    }

    /// <summary>
    /// 点击 NChooseACardSelectionScreen 中的卡牌（攻击药水/搜寻等触发的通用选牌）。
    /// 使用 NGridCardHolder，点击即选（SelectHolder 直接完成 TCS，无需确认按钮）。
    /// </summary>
    private static ActionResult ClickChooseACardSelection(int cardIndex)
    {
        var chooseScreen = NOverlayStack.Instance?.Peek() as NChooseACardSelectionScreen;
        if (chooseScreen == null)
            return new ActionResult(false, "Choose-a-card selection screen is not active");

        var holders = FindNodesRecursive<NGridCardHolder>((Node)chooseScreen);
        if (cardIndex < 0 || cardIndex >= holders.Count)
            return new ActionResult(false, $"Invalid card_index: {cardIndex}. {holders.Count} cards available");

        holders[cardIndex].EmitSignal(NCardHolder.SignalName.Pressed, holders[cardIndex]);
        return new ActionResult(true, null);
    }

    /// <summary>
    /// 通用 NCardGridSelectionScreen 子类点击（Transform/Enchant/SimpleCard 等）。
    /// 所有 NCardGridSelectionScreen 子类都用 NGridCardHolder + Pressed 信号。
    /// </summary>
    private static ActionResult ClickGridCardSelection(int cardIndex)
    {
        var gridScreen = NOverlayStack.Instance?.Peek() as NCardGridSelectionScreen;
        if (gridScreen == null)
            return new ActionResult(false, "Grid card selection screen is not active");

        var holders = FindNodesRecursive<NGridCardHolder>((Node)gridScreen);
        if (cardIndex < 0 || cardIndex >= holders.Count)
            return new ActionResult(false, $"Invalid card_index: {cardIndex}. {holders.Count} cards available");

        holders[cardIndex].EmitSignal(NCardHolder.SignalName.Pressed, holders[cardIndex]);
        return new ActionResult(true, null);
    }

    /// <summary>
    /// 手牌选择模式：构建 HandCardSelectionSnapshot。
    /// 从 NPlayerHand 获取可见的可选卡牌及选择参数。
    /// </summary>
    private static HandCardSelectionSnapshot? BuildHandCardSelection(NPlayerHand playerHand)
    {
        // 获取手牌中可见的卡牌持有者（过滤模式会隐藏不满足条件的卡牌）
        var holders = FindNodesRecursive<NHandCardHolder>(playerHand)
            .Where(h => h.Visible)
            .ToList();

        var cards = holders
            .Select(h => h.CardNode?.Model)
            .Where(m => m != null)
            .Select(m => BuildCardSnapshot(m!, null!))
            .ToList();

        // 通过反射获取私有字段获取选择参数
        var prefsField = typeof(NPlayerHand).GetField("_prefs",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var selectedField = typeof(NPlayerHand).GetField("_selectedCards",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var confirmBtnField = typeof(NPlayerHand).GetField("_selectModeConfirmButton",
            BindingFlags.NonPublic | BindingFlags.Instance);

        int minSelect = 1, maxSelect = 1, currentCount = 0;
        bool canConfirm = false;

        if (prefsField?.GetValue(playerHand) is { } prefs)
        {
            var minProp = prefs.GetType().GetProperty("MinSelect");
            var maxProp = prefs.GetType().GetProperty("MaxSelect");
            minSelect = (int)(minProp?.GetValue(prefs) ?? 1);
            maxSelect = (int)(maxProp?.GetValue(prefs) ?? 1);
        }

        if (selectedField?.GetValue(playerHand) is System.Collections.IList list)
            currentCount = list.Count;

        if (confirmBtnField?.GetValue(playerHand) is NConfirmButton btn)
            canConfirm = btn.IsEnabled;

        // 提取提示文本：_selectionHeader (MegaRichTextLabel) 中已有格式化后的文本
        var headerField = typeof(NPlayerHand).GetField("_selectionHeader",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var prompt = "";
        if (headerField?.GetValue(playerHand) is MegaRichTextLabel header && !string.IsNullOrEmpty(header.Text))
            prompt = header.Text;

        return new HandCardSelectionSnapshot(prompt, cards, minSelect, maxSelect, currentCount, canConfirm);
    }

    /// <summary>
    /// 手牌选择模式：点击第 cardIndex 张可见可选卡牌切换选中状态。
    /// </summary>
    private static ActionResult ClickHandCardSelection(NPlayerHand playerHand, int cardIndex)
    {
        var holders = FindNodesRecursive<NHandCardHolder>(playerHand)
            .Where(h => h.Visible)
            .ToList();

        if (cardIndex < 0 || cardIndex >= holders.Count)
            return new ActionResult(false, $"Invalid card_index: {cardIndex}. {holders.Count} selectable cards");

        holders[cardIndex].EmitSignal(NCardHolder.SignalName.Pressed, holders[cardIndex]);
        return new ActionResult(true, null);
    }

    /// <summary>
    /// 确认手牌选择：点击 _selectModeConfirmButton。
    /// </summary>
    private static ActionResult ConfirmHandSelection()
    {
        var playerHand = NCombatRoom.Instance?.Ui?.Hand;
        if (playerHand is not { IsInCardSelection: true })
            return new ActionResult(false, "Hand is not in card selection mode");

        var confirmBtnField = typeof(NPlayerHand).GetField("_selectModeConfirmButton",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (confirmBtnField?.GetValue(playerHand) is not NConfirmButton btn)
            return new ActionResult(false, "Confirm button not found");

        if (!btn.IsEnabled)
            return new ActionResult(false, "Confirm button is not enabled (selection count not in valid range)");

        btn.ForceClick();
        return WrapWithSelectionState(null);
    }

    /// <summary>
    /// 构建休息点快照。
    ///
    /// 数据来源：
    /// - NRestSiteRoom.Instance.Options → RestSiteOption 列表（HEAL/SMITH/DIG/...）
    /// - 如果选择了锻造（SMITH），NDeckUpgradeSelectScreen 会弹出，填充 CardSelection
    ///
    /// 每个 RestSiteOption 对应一个 RestOptionSnapshot，Index 即为 option_index。
    /// </summary>
    private static RestSnapshot? BuildRestSnapshot()
    {
        var restRoom = NRestSiteRoom.Instance;
        if (restRoom == null) return null;

        var options = restRoom.Options
            .Select((opt, i) => new RestOptionSnapshot(
                Index: i,
                Name: opt.Title.GetFormattedText() ?? opt.OptionId,
                Description: opt.Description.GetFormattedText() ?? ""
            ))
            .ToList();

        // 检查是否有升级选牌 / 移除选牌等子界面
        CardSelectionSnapshot? cardSelection = null;
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is NCardGridSelectionScreen)
        {
            cardSelection = BuildCardSelectionFromOverlay((Node)overlay);
        }

        // 检查是否有目标玩家选择（如多人模式下的"愈合"选项）
        TargetSelectionSnapshot? targetSelection = null;
        var targetManager = NTargetManager.Instance;
        if (targetManager is { IsInSelection: true })
        {
            var targetChars = FindNodesRecursive<NRestSiteCharacter>((Node)restRoom);
            var targets = targetChars
                .Where(c => targetManager.AllowedToTargetNode(c))
                .Select((c, i) => new TargetOptionSnapshot(
                    Index: i,
                    PlayerName: c.Player?.Creature?.Name ?? $"Player {i}"
                ))
                .ToList();
            if (targets.Count > 0)
                targetSelection = new TargetSelectionSnapshot(targets);
        }

        return new RestSnapshot(options, cardSelection, targetSelection);
    }

    /// <summary>
    /// 构建事件快照。从 NEventRoom 读取当前事件名称、描述和选项。
    /// </summary>
    private static EventSnapshot? BuildEventSnapshot()
    {
        // NEventRoom 创建滞后于 RoomType.Event 设置（await LoadRoomEventAssets 间隙）。
        // 按优先级查找：NRun.EventRoom → 场景树搜索 → EventRoom.CanonicalEvent
        var eventRoom = NRun.Instance?.EventRoom;
        if (eventRoom == null && NRun.Instance != null)
            eventRoom = FindNodesRecursive<NEventRoom>(NRun.Instance).FirstOrDefault();
        if (eventRoom == null)
        {
            var runState = RunManager.Instance.DebugOnlyGetState();
            if (runState?.CurrentRoom is EventRoom er)
            {
                var canTitle = SafeFormat(er.CanonicalEvent.Title);
                var canDesc = SafeFormat(er.CanonicalEvent.InitialDescription);
                return new EventSnapshot(canTitle, canDesc, false, [], null);
            }
            return new EventSnapshot("", "事件房间加载中...", false, [], null);
        }

        // 通过反射读取 _event 字段获取 EventModel
        var eventField = typeof(NEventRoom).GetField("_event",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (eventField?.GetValue(eventRoom) is not EventModel evt)
            return new EventSnapshot("", "事件加载中...", false, [], null);

        var title = SafeFormat(evt.Title);
        var description = SafeFormat(evt.Description);
        var options = evt.CurrentOptions
            .Select((opt, i) =>
            {
                var hoverCards = opt.HoverTips
                    .OfType<CardHoverTip>()
                    .Select(ht => BuildCardSnapshot(ht.Card, null!))
                    .ToList();
                RelicInfo? relicInfo = null;
                if (opt.Relic != null)
                {
                    relicInfo = new RelicInfo(
                        SafeFormat(opt.Relic.Title),
                        SafeFormat(opt.Relic.DynamicDescription));
                }
                return new EventOptionSnapshot(
                    i,
                    SafeFormat(opt.Title),
                    SafeFormat(opt.Description),
                    opt.IsLocked,
                    opt.IsProceed,
                    hoverCards,
                    relicInfo);
            })
            .ToList();

        // 事件完成但 UI 会生成一个 proceed 按钮离开，快照也应暴露
        if (evt.IsFinished && options.Count == 0)
        {
            options.Add(new EventOptionSnapshot(0, "离开", "离开事件", false, true, [], null));
        }

        // 事件中弹出的选牌遮盖（如沐浴/升级/变换等）
        CardSelectionSnapshot? cardSelection = null;
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is NDeckCardSelectScreen or NCardGridSelectionScreen)
        {
            cardSelection = BuildCardSelectionFromOverlay((Node)overlay);
        }

        return new EventSnapshot(title, description, evt.IsFinished, options, cardSelection);
    }

    /// <summary>
    /// 构建宝箱房间快照。从 TreasureRoomRelicSynchronizer 获取可选遗物和投票状态。
    /// </summary>
    private static TreasureSnapshot? BuildTreasureSnapshot(IRunState runState)
    {
        var treasureRoom = NRun.Instance?.TreasureRoom;
        if (treasureRoom == null) return null;

        // 通过反射读取私有字段判断当前阶段
        var chestOpenedField = typeof(NTreasureRoom).GetField("_hasChestBeenOpened",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var isPickingField = typeof(NTreasureRoom).GetField("_isRelicCollectionOpen",
            BindingFlags.NonPublic | BindingFlags.Instance);
        bool chestOpened = (bool)(chestOpenedField?.GetValue(treasureRoom) ?? false);
        bool isPicking = (bool)(isPickingField?.GetValue(treasureRoom) ?? false);
        bool canLeave = treasureRoom.ProceedButton.IsEnabled;

        // 从 synchronizer 读取可选遗物
        var sync = RunManager.Instance.TreasureRoomRelicSynchronizer;
        var relics = new List<TreasureRelicSnapshot>();
        int? myVote = null;

        if (sync.CurrentRelics != null)
        {
            for (int i = 0; i < sync.CurrentRelics.Count; i++)
            {
                var relic = sync.CurrentRelics[i];
                relics.Add(new TreasureRelicSnapshot(
                    i,
                    relic.Title.GetFormattedText() ?? relic.Id.ToString(),
                    relic.DynamicDescription.GetFormattedText() ?? ""));
            }

            // 获取当前玩家的投票
            var player = LocalContext.GetMe(runState);
            if (player != null)
            {
                // 优先读 _predictedVote（本地客户端即时反馈）
                var predictedField = typeof(TreasureRoomRelicSynchronizer)
                    .GetField("_predictedVote", BindingFlags.NonPublic | BindingFlags.Instance);
                if (predictedField?.GetValue(sync) is { } pv)
                {
                    var indexField = pv.GetType().GetField("index");
                    var receivedField = pv.GetType().GetField("voteReceived");
                    if (receivedField?.GetValue(pv) is true && indexField?.GetValue(pv) is int idx)
                        myVote = idx;
                }
            }
        }

        return new TreasureSnapshot(chestOpened, isPicking, canLeave, relics, myVote);
    }

    // ── Action execution ───────────────────────────────────────────────

    /// <summary>
    /// 执行 AI 发来的动作请求（运行在主线程）。
    ///
    /// 支持的动作类型：end_turn / play_card / use_potion / move_to_map_coord
    /// 每个动作执行前会进行前置校验（如是否在战斗中、是否在出牌阶段等）。
    /// </summary>
    internal static ActionResult ExecuteAction(ActionRequest request)
    {
        try
        {
            // _refresh 在 Update() 中直接处理，不走 ExecuteAction

            // menu_action 不需要 Run 在运行中
            if (request.Action == "menu_action")
                return ExecuteMenuAction(request);

            if (!RunManager.Instance.IsInProgress)
                return new ActionResult(false, "No run in progress");

            var runState = RunManager.Instance.DebugOnlyGetState();
            if (runState == null)
                return new ActionResult(false, "No run state");

            var player = LocalContext.GetMe(runState);
            if (player == null)
                return new ActionResult(false, "No local player found");

            return request.Action switch
            {
                "end_turn" => ExecuteEndTurn(player),
                "play_card" => ExecutePlayCard(player, request),
                "multi_play" => ExecuteMultiPlay(player, request),
                "use_potion" => ExecuteUsePotion(player, request),
                "move_to_map_coord" => ExecuteMoveToMapCoord(player, request),
                "pick_reward" => ExecutePickReward(request),
                "pick_card" => TryExecuteCardSelection(request),
                "confirm_selection" => ConfirmHandSelection(),
                "rest_action" => ExecuteRestAction(request),
                "shop_action" => ExecuteShopAction(request),
                "treasure_action" => ExecuteTreasureAction(request),
                "event_action" => ExecuteEventAction(request),
                _ => new ActionResult(false, $"Unknown action: {request.Action}")
            };
        }
        catch (Exception ex)
        {
            return new ActionResult(false, ex.Message);
        }
    }

    /// <summary>
    /// 执行"结束回合"动作。
    ///
    /// 前提条件：
    // ── Menu action ─────────────────────────────────────────────────────

    /// <summary>
    /// 执行游戏外界面操作（主菜单 / 子菜单 / 角色选择等）。
    ///
    /// 根据当前界面（由 BuildMenuSnapshot 检测）和 menu_action 子类型分发：
    /// - main_menu: continue_run / abandon_run / singleplayer / multiplayer / settings / compendium / quit
    /// - singleplayer_submenu: standard / daily / custom / back
    /// - multiplayer_submenu: host_standard / host_daily / host_custom / join_friend / back
    /// - character_select: select_character / set_ascension / embark / back
    /// - 通用: back（子菜单返回）
    /// </summary>
    private static ActionResult ExecuteMenuAction(ActionRequest request)
    {
        var menuSnap = BuildMenuSnapshot();
        var mainMenu = NGame.Instance?.MainMenu;
        var subAction = request.MenuAction ?? "";

        try
        {
            switch (menuSnap.Screen)
            {
                case "main_menu":
                    return ExecuteMainMenuAction(mainMenu!, subAction);
                case "singleplayer_submenu":
                    return ExecuteSingleplayerSubmenuAction(mainMenu!, subAction);
                case "multiplayer_submenu":
                    return ExecuteMultiplayerSubmenuAction(mainMenu!, subAction);
                case "multiplayer_host":
                    return ExecuteMultiplayerHostAction(mainMenu!, subAction);
                case "character_select":
                    return ExecuteCharacterSelectAction(mainMenu!, request);
                case "daily_run":
                    return ExecuteDailyRunAction(mainMenu!, subAction);
                case "custom_run":
                    return ExecuteCustomRunAction(mainMenu!, request);
                case "modal_confirm":
                case "modal":
                    return ExecuteModalAction(subAction);
                default:
                    // 任何子菜单都支持 back
                    if (subAction == "back" && menuSnap.IsSubmenu)
                    {
                        mainMenu?.SubmenuStack.Pop();
                        return new ActionResult(true, null);
                    }
                    return new ActionResult(false, $"No menu actions available on screen '{menuSnap.Screen}'");
            }
        }
        catch (Exception ex)
        {
            return new ActionResult(false, $"Menu action '{subAction}' failed: {ex.Message}");
        }
    }

    /// <summary>主菜单根界面操作。</summary>
    private static ActionResult ExecuteMainMenuAction(NMainMenu menu, string subAction)
    {
        switch (subAction)
        {
            case "continue_run":
                ClickButton(menu, "MainMenuTextButtons/ContinueButton");
                return new ActionResult(true, null);
            case "abandon_run":
                ClickButton(menu, "MainMenuTextButtons/AbandonRunButton");
                return new ActionResult(true, null);
            case "singleplayer":
                menu.OpenSingleplayerSubmenu();
                return new ActionResult(true, null);
            case "multiplayer":
                menu.OpenMultiplayerSubmenu();
                return new ActionResult(true, null);
            case "settings":
                menu.OpenSettingsMenu();
                return new ActionResult(true, null);
            case "compendium":
                ClickButton(menu, "MainMenuTextButtons/CompendiumButton");
                return new ActionResult(true, null);
            case "quit":
                ClickButton(menu, "MainMenuTextButtons/QuitButton");
                return new ActionResult(true, null);
            default:
                return new ActionResult(false, $"Unknown main_menu action: {subAction}. Available: continue_run, abandon_run, singleplayer, multiplayer, settings, compendium, quit");
        }
    }

    /// <summary>单机子菜单操作。</summary>
    private static ActionResult ExecuteSingleplayerSubmenuAction(NMainMenu menu, string subAction)
    {
        var submenu = menu.SubmenuStack.Peek();
        if (submenu == null)
            return new ActionResult(false, "Singleplayer submenu not found");

        switch (subAction)
        {
            case "standard":
                ClickButton(submenu, "StandardButton");
                return new ActionResult(true, null);
            case "daily":
                ClickButton(submenu, "DailyButton");
                return new ActionResult(true, null);
            case "custom":
                ClickButton(submenu, "CustomRunButton");
                return new ActionResult(true, null);
            case "back":
                menu.SubmenuStack.Pop();
                return new ActionResult(true, null);
            default:
                return new ActionResult(false, $"Unknown singleplayer_submenu action: {subAction}. Available: standard, daily, custom, back");
        }
    }

    /// <summary>多人子菜单操作。</summary>
    private static ActionResult ExecuteMultiplayerSubmenuAction(NMainMenu menu, string subAction)
    {
        var submenu = menu.SubmenuStack.Peek();
        if (submenu == null)
            return new ActionResult(false, "Multiplayer submenu not found");

        switch (subAction)
        {
            case "host_standard":
                ClickButton(submenu, "ButtonContainer/HostButton");
                return new ActionResult(true, null);
            case "host_daily":
                ClickButton(submenu, "ButtonContainer/HostButton");
                return new ActionResult(true, null);
            case "host_custom":
                ClickButton(submenu, "ButtonContainer/HostButton");
                return new ActionResult(true, null);
            case "join_friend":
                ClickButton(submenu, "ButtonContainer/JoinButton");
                return new ActionResult(true, null);
            case "back":
                menu.SubmenuStack.Pop();
                return new ActionResult(true, null);
            default:
                return new ActionResult(false, $"Unknown multiplayer_submenu action: {subAction}. Available: host_standard, host_daily, host_custom, join_friend, back");
        }
    }

    /// <summary>角色选择界面操作。</summary>
    private static ActionResult ExecuteCharacterSelectAction(NMainMenu menu, ActionRequest request)
    {
        var submenu = menu.SubmenuStack.Peek();
        if (submenu == null)
            return new ActionResult(false, "Character select screen not found");

        var subAction = request.MenuAction ?? "";

        switch (subAction)
        {
            case "select_character":
            {
                if (request.CharacterIndex == null)
                    return new ActionResult(false, "character_index is required for select_character");
                var btnContainer = submenu.GetNode<Control>("CharSelectButtons/ButtonContainer");
                // 获取所有角色按钮（NCharacterSelectButton），跳过锁定和 Random 按钮
                var buttons = btnContainer.GetChildren()
                    .Where(c => c.GetType().Name == "NCharacterSelectButton")
                    .ToList();
                if (request.CharacterIndex.Value < 0 || request.CharacterIndex.Value >= buttons.Count)
                    return new ActionResult(false, $"Invalid character_index: {request.CharacterIndex}. Available: 0-{buttons.Count - 1}");
                // 通过反射调用 Select() 方法
                var selectMethod = buttons[request.CharacterIndex.Value].GetType().GetMethod("Select");
                selectMethod?.Invoke(buttons[request.CharacterIndex.Value], null);
                return new ActionResult(true, null);
            }
            case "set_ascension":
            {
                if (request.AscensionLevel == null)
                    return new ActionResult(false, "ascension_level is required for set_ascension");
                var ascPanel = submenu.GetNode<Control>("%AscensionPanel");
                // 先扩展最大可设进阶（若当前 max 不够），再设目标级别
                var setMaxMethod = ascPanel.GetType().GetMethod("SetMaxAscension");
                setMaxMethod?.Invoke(ascPanel, new object[] { request.AscensionLevel.Value });
                var setAscMethod = ascPanel.GetType().GetMethod("SetAscensionLevel");
                setAscMethod?.Invoke(ascPanel, new object[] { request.AscensionLevel.Value });
                return new ActionResult(true, null);
            }
            case "embark":
                ClickButton(submenu, "ConfirmButton");
                return new ActionResult(true, null);
            case "back":
                menu.SubmenuStack.Pop();
                return new ActionResult(true, null);
            default:
                return new ActionResult(false, $"Unknown character_select action: {subAction}. Available: select_character, set_ascension, embark, back");
        }
    }

    /// <summary>多人建房子菜单操作。</summary>
    private static ActionResult ExecuteMultiplayerHostAction(NMainMenu menu, string subAction)
    {
        var submenu = menu.SubmenuStack.Peek();
        if (submenu == null)
            return new ActionResult(false, "Multiplayer host submenu not found");

        switch (subAction)
        {
            case "standard":
                ClickButton(submenu, "StandardButton");
                return new ActionResult(true, null);
            case "daily":
                ClickButton(submenu, "DailyButton");
                return new ActionResult(true, null);
            case "custom":
                ClickButton(submenu, "CustomRunButton");
                return new ActionResult(true, null);
            case "back":
                menu.SubmenuStack.Pop();
                return new ActionResult(true, null);
            default:
                return new ActionResult(false, $"Unknown multiplayer_host action: {subAction}. Available: standard, daily, custom, back");
        }
    }

    /// <summary>每日挑战界面操作。</summary>
    private static ActionResult ExecuteDailyRunAction(NMainMenu menu, string subAction)
    {
        var submenu = menu.SubmenuStack.Peek();
        if (submenu == null)
            return new ActionResult(false, "Daily run screen not found");

        switch (subAction)
        {
            case "embark":
                ClickButton(submenu, "%ConfirmButton");
                return new ActionResult(true, null);
            case "back":
                menu.SubmenuStack.Pop();
                return new ActionResult(true, null);
            default:
                return new ActionResult(false, $"Unknown daily_run action: {subAction}. Available: embark, back");
        }
    }

    /// <summary>自定义局界面操作。</summary>
    private static ActionResult ExecuteCustomRunAction(NMainMenu menu, ActionRequest request)
    {
        var submenu = menu.SubmenuStack.Peek();
        if (submenu == null)
            return new ActionResult(false, "Custom run screen not found");

        var subAction = request.MenuAction ?? "";

        switch (subAction)
        {
            case "select_character":
            {
                if (request.CharacterIndex == null)
                    return new ActionResult(false, "character_index is required for select_character");
                // 自定义局角色按钮在 LeftContainer/CharSelectButtons/ButtonContainer
                var btnContainer = submenu.GetNode<Control>("LeftContainer/CharSelectButtons/ButtonContainer");
                var buttons = btnContainer.GetChildren()
                    .Where(c => c.GetType().Name == "NCharacterSelectButton")
                    .ToList();
                if (request.CharacterIndex.Value < 0 || request.CharacterIndex.Value >= buttons.Count)
                    return new ActionResult(false, $"Invalid character_index: {request.CharacterIndex}. Available: 0-{buttons.Count - 1}");
                var selectMethod = buttons[request.CharacterIndex.Value].GetType().GetMethod("Select");
                selectMethod?.Invoke(buttons[request.CharacterIndex.Value], null);
                return new ActionResult(true, null);
            }
            case "set_ascension":
            {
                if (request.AscensionLevel == null)
                    return new ActionResult(false, "ascension_level is required for set_ascension");
                var ascPanel = submenu.GetNode<Control>("%AscensionPanel");
                // 自定义局需先 set max ascension，再设置级别
                var setMaxMethod = ascPanel.GetType().GetMethod("SetMaxAscension");
                setMaxMethod?.Invoke(ascPanel, new object[] { request.AscensionLevel.Value });
                var setAscMethod = ascPanel.GetType().GetMethod("SetAscensionLevel");
                setAscMethod?.Invoke(ascPanel, new object[] { request.AscensionLevel.Value });
                return new ActionResult(true, null);
            }
            case "embark":
                ClickButton(submenu, "ConfirmButton");
                return new ActionResult(true, null);
            case "back":
                menu.SubmenuStack.Pop();
                return new ActionResult(true, null);
            default:
                return new ActionResult(false, $"Unknown custom_run action: {subAction}. Available: select_character, set_ascension, embark, back");
        }
    }

    /// <summary>
    /// 模态弹窗操作（确认对话框 / 错误提示等）。
    /// 适用场景：abandon_run 确认、断线确认等。
    /// </summary>
    private static ActionResult ExecuteModalAction(string subAction)
    {
        var modal = NModalContainer.Instance?.OpenModal as Control;
        if (modal == null)
            return new ActionResult(false, "No modal open");

        switch (subAction)
        {
            case "confirm":
                // 寻找 YesButton / ConfirmButton
                var yesBtn = modal.GetNode<NButton>("VerticalPopup/YesButton");
                if (yesBtn != null)
                {
                    yesBtn.ForceClick();
                    return new ActionResult(true, null);
                }
                // 备选：直接找 YesButton
                yesBtn = modal.GetNodeOrNull<NButton>("%YesButton");
                if (yesBtn != null)
                {
                    yesBtn.ForceClick();
                    return new ActionResult(true, null);
                }
                return new ActionResult(false, "No confirm button found in modal");
            case "cancel":
                // 寻找 NoButton / CancelButton
                var noBtn = modal.GetNode<NButton>("VerticalPopup/NoButton");
                if (noBtn != null)
                {
                    noBtn.ForceClick();
                    return new ActionResult(true, null);
                }
                noBtn = modal.GetNodeOrNull<NButton>("%NoButton");
                if (noBtn != null)
                {
                    noBtn.ForceClick();
                    return new ActionResult(true, null);
                }
                return new ActionResult(false, "No cancel button found in modal");
            default:
                return new ActionResult(false, $"Unknown modal action: {subAction}. Available: confirm, cancel");
        }
    }

    /// <summary>通过节点路径找到按钮并 ForceClick。</summary>
    private static void ClickButton(Node parent, string nodePath)
    {
        var button = parent.GetNode<NButton>(nodePath);
        button?.ForceClick();
    }

    /// <summary>
    /// 执行"结束回合"动作。
    ///
    /// 前提条件：
    /// - 必须在战斗中（CombatManager.IsInProgress）
    /// - 必须在出牌阶段（IsPlayPhase），不能在敌人回合结束
    ///
    /// 实现：
    /// 创建 EndPlayerTurnAction 并通过 ActionQueueSynchronizer.RequestEnqueue() 提交。
    /// 游戏会在当前回合所有效果结算完后自动进入敌方回合。
    /// </summary>
    internal static ActionResult ExecuteEndTurn(Player player)
    {
        if (!CombatManager.Instance.IsInProgress)
            return new ActionResult(false, "Not in combat");

        if (!CombatManager.Instance.IsPlayPhase)
            return new ActionResult(false, "Not in play phase");

        var combatState = CombatManager.Instance.DebugOnlyGetState();
        if (combatState == null)
            return new ActionResult(false, "No combat state");

        var action = new EndPlayerTurnAction(player, combatState.RoundNumber);
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);
        return new ActionResult(true, null);
    }

    /// <summary>
    /// 执行"出牌"动作。
    ///
    /// 前提条件：
    /// - 必须在战斗中且在出牌阶段
    /// - 必须提供 hand_index
    /// - 手牌 index 必须在有效范围内
    /// - 目标（若有）必须是有效的敌方 Creature
    ///
    /// 实现：
    /// 1. 通过 hand_index 从手牌中取出 CardModel
    /// 2. 如果指定了 target_id，从 CombatState 查找对应的敌方 Creature
    /// 3. 创建 PlayCardAction 并提交到 ActionQueueSynchronizer
    /// </summary>
    private static ActionResult ExecutePlayCard(Player player, ActionRequest request)
    {
        if (!CombatManager.Instance.IsInProgress)
            return new ActionResult(false, "Not in combat");

        if (!CombatManager.Instance.IsPlayPhase)
            return new ActionResult(false, "Not in play phase");

        if (request.HandIndex == null)
            return new ActionResult(false, "hand_index is required for play_card");

        // 获取战斗状态和手牌
        var combatState = CombatManager.Instance.DebugOnlyGetState();
        if (combatState == null)
            return new ActionResult(false, "No combat state");

        var handCards = player.PlayerCombatState?.Hand.Cards;
        if (handCards == null || request.HandIndex.Value < 0 || request.HandIndex.Value >= handCards.Count)
            return new ActionResult(false, $"Invalid hand_index: {request.HandIndex}. Hand has {handCards?.Count ?? 0} cards");

        var card = handCards[request.HandIndex.Value];
        var playedCardName = card.Title.ToString() ?? card.Id.ToString();

        // 选择目标：
        // - AI 明确指定 target_id → 查找对应的敌方 Creature
        // - TargetType.None / Self / AllEnemies / RandomEnemy / AllAllies → 无需指定目标或自身目标，游戏自动处理
        // - TargetType.AnyEnemy / AnyPlayer / AnyAlly 等未指定 target_id → 不合法
        Creature? target;
        CardTargetSnapshot targetSnapshot;
        if (request.TargetCombatId.HasValue)
        {
            target = combatState.Enemies.FirstOrDefault(e =>
                e.CombatId.HasValue && (int)e.CombatId.Value == request.TargetCombatId.Value);
            if (target == null)
                return new ActionResult(false, $"Target not found with CombatId: {request.TargetCombatId}");
            targetSnapshot = new CardTargetSnapshot("enemy",
                (int)target.CombatId!.Value,
                target.Monster?.Title.GetFormattedText() ?? "Unknown");
        }
        else if (card.TargetType is TargetType.None or TargetType.Self
                or TargetType.AllEnemies or TargetType.RandomEnemy or TargetType.AllAllies)
        {
            // 自身目标 / AOE / 随机目标牌 — 传 null 即可（游戏自身逻辑处理目标选择）
            target = null;
            var targetTypeStr = card.TargetType == TargetType.None || card.TargetType == TargetType.Self
                ? "self" : "all";
            var targetName = card.TargetType switch
            {
                TargetType.AllEnemies => "全体敌人",
                TargetType.RandomEnemy => "随机敌人",
                TargetType.AllAllies => "全体友方",
                _ => "自身"
            };
            targetSnapshot = new CardTargetSnapshot(targetTypeStr, null, targetName);
        }
        else
        {
            return new ActionResult(false, $"Card requires a target (TargetType={card.TargetType}) but no target_id provided");
        }

        var action = new PlayCardAction(card, target);
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);

        return new ActionResult(true, null,
            PlayedCardName: playedCardName,
            PlayedCardTarget: targetSnapshot);
    }

    /// <summary>
    /// 执行"一次出多张牌"动作（multi_play）。
    ///
    /// 与单张 play_card 不同，此方法一次性获取所有指定手牌的 CardModel 引用，
    /// 构建 PlayCardAction 后全部入队。因为 PlayCardAction 存储的是 CardModel
    /// 对象引用而非 index，所以后续手牌变化不影响已入队的 action。
    ///
    /// 安全边界：
    /// - 普通攻击/技能/能力 ✅ 安全
    /// - 抽牌效果 ✅ 已入队的 ref 不受影响
    /// - 选牌触发（净化等）⚠️ 后续牌可能失败，AI 应单独用 play_card
    /// - 前牌杀死后牌目标 ⚠️ 后牌目标可能已死亡
    /// </summary>
    private static ActionResult ExecuteMultiPlay(Player player, ActionRequest request)
    {
        if (!CombatManager.Instance.IsInProgress)
            return new ActionResult(false, "Not in combat");
        if (!CombatManager.Instance.IsPlayPhase)
            return new ActionResult(false, "Not in play phase");
        if (request.Cards == null || request.Cards.Count == 0)
            return new ActionResult(false, "cards array is required for multi_play");

        var handCards = player.PlayerCombatState?.Hand.Cards;
        if (handCards == null)
            return new ActionResult(false, "No hand cards");

        var combatState = CombatManager.Instance.DebugOnlyGetState();
        if (combatState == null)
            return new ActionResult(false, "No combat state");

        var playedNames = new List<string>();
        var errors = new List<string>();

        // Phase 1: 一次性解析所有 hand_index → CardModel 引用
        // 必须在入队前完成，因为入队后手牌列表会变化（index 前移）
        var cardsToPlay = new List<(CardModel card, Creature? target, string name)>();
        foreach (var spec in request.Cards)
        {
            if (spec.HandIndex < 0 || spec.HandIndex >= handCards.Count)
            {
                errors.Add($"hand_index {spec.HandIndex} out of range (hand has {handCards.Count})");
                continue;
            }

            var card = handCards[spec.HandIndex];
            var cardName = card.Title.ToString() ?? card.Id.ToString();

            // 目标选择：复用 ExecutePlayCard 的逻辑
            Creature? target = null;
            if (spec.TargetId.HasValue)
            {
                target = combatState.Enemies.FirstOrDefault(e =>
                    e.CombatId.HasValue && (int)e.CombatId.Value == spec.TargetId.Value);
                if (target == null)
                {
                    errors.Add($"{cardName}: target {spec.TargetId} not found");
                    continue;
                }
            }
            else if (card.TargetType is not (TargetType.None or TargetType.Self
                    or TargetType.AllEnemies or TargetType.RandomEnemy or TargetType.AllAllies))
            {
                errors.Add($"{cardName}: requires target (TargetType={card.TargetType})");
                continue;
            }

            cardsToPlay.Add((card, target, cardName));
        }

        // Phase 2: 全部入队（使用已解析的 CardModel 引用，不受手牌变化影响）
        foreach (var (card, target, cardName) in cardsToPlay)
        {
            var action = new PlayCardAction(card, target);
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);
            playedNames.Add(cardName);
        }

        // 注意: 入队是异步的，卡牌可能尚未处理完毕（抽牌/选牌触发会阻塞后续）。
        // AI 应在 multi_play 后调用 get_state 获取真实手牌和选牌状态。
        var errorMsg = errors.Count > 0 ? string.Join("; ", errors) : null;
        var success = playedNames.Count > 0;
        return new ActionResult(success, errorMsg,
            PlayedCardName: string.Join(", ", playedNames));
    }

    /// <summary>
    /// 执行"使用药水"动作。
    ///
    /// 前提条件：
    /// - 必须在战斗中且在出牌阶段
    /// - 必须提供 slot_index
    /// - 药水槽位必须有药水（非 null）
    ///
    /// 实现：
    /// 1. 通过 slot_index 从 Player.PotionSlots 取出 PotionModel
    /// 2. 如果指定了 target_id，找到对应的敌方 Creature
    /// 3. 创建 UsePotionAction 并提交
    /// </summary>
    private static ActionResult ExecuteUsePotion(Player player, ActionRequest request)
    {
        if (!CombatManager.Instance.IsInProgress)
            return new ActionResult(false, "Not in combat");

        if (!CombatManager.Instance.IsPlayPhase)
            return new ActionResult(false, "Not in play phase");

        if (request.SlotIndex == null)
            return new ActionResult(false, "slot_index is required for use_potion");

        var potion = player.GetPotionAtSlotIndex(request.SlotIndex.Value);
        if (potion == null)
            return new ActionResult(false, $"No potion in slot {request.SlotIndex}");

        // 查找目标：
        // - AI 指定了 target_id → 查找敌方 Creature
        // - 药水是敌方目标型（AnyEnemy 等）但 AI 没指定 → 需要 target，报错
        // - 其他（自我/无目标型药水如敏捷药水/熔炉的祝福）→ target = player.Creature（对齐 NPotionHolder.UsePotion）
        Creature? target = null;
        if (request.TargetCombatId.HasValue)
        {
            var combatState = CombatManager.Instance.DebugOnlyGetState();
            if (combatState != null)
            {
                target = combatState.Enemies.FirstOrDefault(e =>
                    e.CombatId.HasValue && (int)e.CombatId.Value == request.TargetCombatId.Value);
            }
        }
        else if (potion.TargetType == TargetType.AnyEnemy || potion.TargetType == TargetType.TargetedNoCreature)
        {
            return new ActionResult(false, $"Potion requires a target (TargetType={potion.TargetType}) but no target_id provided");
        }
        else
        {
            target = player.Creature;
        }

        potion.EnqueueManualUse(target);
        return new ActionResult(true, null);
    }

    /// <summary>
    /// 执行"选路线"动作，从当前地图节点移动到指定坐标。
    ///
    /// 前提条件：
    /// - 不能处于战斗中（选路线发生在 MapRoom）
    /// - 必须提供 col 和 row
    /// - 目标坐标必须是当前节点的合法子节点之一
    ///
    /// 实现：
    /// 1. 通过 col/row 构造 MapCoord
    /// 2. 创建 MoveToMapCoordAction 并提交
    /// </summary>
    private static ActionResult ExecuteMoveToMapCoord(Player player, ActionRequest request)
    {
        if (CombatManager.Instance.IsInProgress)
            return new ActionResult(false, "Cannot move on map while in combat");

        if (request.Col == null || request.Row == null)
            return new ActionResult(false, "col and row are required for move_to_map_coord");

        var runState = RunManager.Instance.DebugOnlyGetState();
        var actMap = runState?.Map;
        if (runState == null || actMap == null)
            return new ActionResult(false, "No map data");

        var targetCoord = new MapCoord(request.Col.Value, request.Row.Value);

        // 未选起始节点时，验证目标坐标是否存在地图节点
        if (runState.CurrentMapPoint == null)
        {
            if (actMap.GetPoint(targetCoord) == null)
                return new ActionResult(false, $"Target MapCoord ({targetCoord.col}, {targetCoord.row}) does not exist on the map");
        }
        else
        {
            var isLegalTarget = runState.CurrentMapPoint.Children.Any(p => p.coord == targetCoord);
            if (!isLegalTarget)
                return new ActionResult(false, $"Target MapCoord ({targetCoord.col}, {targetCoord.row}) is not reachable from current node");
        }

        // 通过 VoteForMapCoordAction 入队，单人/多人统一走投票流程
        // 多人模式下 action 会通过网络同步到所有玩家
        var vote = new MapVote
        {
            mapGenerationCount = RunManager.Instance.MapSelectionSynchronizer.MapGenerationCount,
            coord = targetCoord
        };
        var action = new VoteForMapCoordAction(player, runState.MapLocation, vote);
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);
        return new ActionResult(true, null);
    }

    /// <summary>
    /// 执行"选奖励"动作。
    ///
    /// 根据当前显示的界面分两种情况：
    ///
    /// 情况 A：选牌子界面（NCardRewardSelectionScreen）正在显示
    ///   - AI 需提供 card_index（0-based，对应 CardSelection.Options 中的卡牌）
    ///   - 通过 EmitSignal 模拟点击对应 NCardHolder，完成选牌
    ///
    /// 情况 B：奖励主界面（NRewardsScreen）正在显示
    ///   - choice_type="skip"：点击跳过/继续按钮（NProceedButton.ForceClick）
    ///   - choice_type 为其他值：通过 choice_index 点击对应的 NRewardButton.ForceClick()
    ///     如果是选牌奖励，游戏会自动弹出选牌子界面，AI 需随后调用 pick_reward + card_index
    /// </summary>
    private static ActionResult ExecutePickReward(ActionRequest request)
    {
        // 情况 A：选牌子界面 — 委托给通用卡牌选择逻辑
        if (NOverlayStack.Instance?.Peek() is NCardRewardSelectionScreen)
        {
            return TryExecuteCardSelection(request);
        }

        // 情况 B：奖励主界面
        var screen = NOverlayStack.Instance?.Peek() as NRewardsScreen;
        if (screen == null)
            return new ActionResult(false, "No reward screen or card selection screen active");

        // 跳过 / 继续
        if (request.ChoiceType == "skip")
        {
            var proceedBtn = FindNodesRecursive<NProceedButton>((Node)screen).FirstOrDefault();
            if (proceedBtn == null)
                return new ActionResult(false, "No proceed/skip button found");
            proceedBtn.ForceClick();
            return new ActionResult(true, null);
        }

        // 选择某个奖励项
        if (request.ChoiceIndex == null)
            return new ActionResult(false, "choice_index is required to pick a reward item");

        var buttons = new List<NRewardButton>();
        FindNodesRecursive((Node)screen, buttons);

        if (request.ChoiceIndex.Value < 0 || request.ChoiceIndex.Value >= buttons.Count)
            return new ActionResult(false, $"Invalid choice_index: {request.ChoiceIndex}. {buttons.Count} rewards available");

        buttons[request.ChoiceIndex.Value].ForceClick();
        return new ActionResult(true, null);
    }

    /// <summary>
    /// 执行"休息点操作"动作。
    ///
    /// 根据当前显示的界面分三种情况：
    ///
    /// 情况 A：升级选牌（NDeckUpgradeSelectScreen）— 单选出预览 → 自动确认
    /// 情况 B：牌组选牌（NDeckCardSelectScreen）— 多选（COOK移除等），每次调用选/取消一张牌，
    ///         选够数量后预览自动出现 → 自动确认
    /// 情况 C：休息点主界面 — 点击对应 NRestSiteButton
    /// </summary>
    private static ActionResult ExecuteRestAction(ActionRequest request)
    {
        // 情况 P（Player Target）：目标玩家选择（如多人模式下"愈合"需要选择其他玩家）
        var targetManager = NTargetManager.Instance;
        if (targetManager is { IsInSelection: true })
        {
            if (request.CardIndex == null)
                return new ActionResult(false, "card_index is required to select a target player");

            var targetRoom = NRestSiteRoom.Instance;
            if (targetRoom == null)
                return new ActionResult(false, "Not in a rest site");

            var validTargets = FindNodesRecursive<NRestSiteCharacter>(targetRoom)
                .Where(c => targetManager.AllowedToTargetNode(c))
                .ToList();

            if (request.CardIndex.Value < 0 || request.CardIndex.Value >= validTargets.Count)
                return new ActionResult(false, $"Invalid card_index: {request.CardIndex}. {validTargets.Count} targets available");

            var tgt = validTargets[request.CardIndex.Value];
            targetManager.OnNodeHovered(tgt);
            var finishMethod = typeof(NTargetManager).GetMethod("FinishTargeting",
                BindingFlags.NonPublic | BindingFlags.Instance);
            finishMethod!.Invoke(targetManager, new object[] { false });
            return new ActionResult(true, null);
        }

        var overlay = NOverlayStack.Instance?.Peek();

        // 情况 A：升级选牌（单选出预览 → 自动确认）
        if (overlay is NDeckUpgradeSelectScreen)
        {
            return TryExecuteCardSelection(request);
        }

        // 情况 B：牌组选牌（多选，如 COOK 移除牌）
        if (overlay is NDeckCardSelectScreen)
        {
            return TryExecuteCardSelection(request);
        }

        // 情况 C：休息点主界面
        var restRoom = NRestSiteRoom.Instance;
        if (restRoom == null)
            return new ActionResult(false, "Not in a rest site");

        // 无 option_index → 尝试离开休息点（所有选项消耗完毕后的前进按钮）
        if (request.OptionIndex == null)
        {
            if (restRoom.ProceedButton is { IsEnabled: true })
            {
                restRoom.ProceedButton.ForceClick();
                return new ActionResult(true, null);
            }
            return new ActionResult(false, "option_index is required for rest_action");
        }

        var buttons = FindNodesRecursive<NRestSiteButton>((Node)restRoom);
        if (request.OptionIndex.Value < 0 || request.OptionIndex.Value >= buttons.Count)
            return new ActionResult(false, $"Invalid option_index: {request.OptionIndex}. {buttons.Count} options available");

        buttons[request.OptionIndex.Value].ForceClick();
        return new ActionResult(true, null);
    }

    /// <summary>
    /// 执行商店操作。
    ///
    /// 子动作：
    ///   "open_inventory"       — 打开商店界面（通常 AI 不需要手动调用，buy/remove_card 会自动打开）
    ///   "leave"               — 离开商店（点击 ProceedButton）
    ///   "buy"                 — 购买商品，需配合 choice_type（entry 类型）和 item_index（该类型内的 index）
    ///   "remove_card"         — 删牌（触发选牌界面，AI 需随后调用 pick_card）
    ///
    /// choice_type 取值对应 EntryType 字段：
    ///   "character_card" / "colorless_card" / "relic" / "potion"
    ///
    /// 使用 UI 按钮 ForceClick 而非直接调用 MerchantEntry.OnTryPurchaseWrapper，
    /// 因为后者是 async Task 方法，.Result 会阻塞主线程导致死锁（尤其是删牌需要 UI 交互）。
    /// </summary>
    private static ActionResult ExecuteShopAction(ActionRequest request)
    {
        var merchantRoom = NRun.Instance?.MerchantRoom;
        if (merchantRoom == null || merchantRoom.Room.Inventory == null)
            return new ActionResult(false, "Not in a shop");

        if (request.ShopAction == "open_inventory")
        {
            merchantRoom.MerchantButton.ForceClick();
            return new ActionResult(true, null);
        }

        if (request.ShopAction == "leave")
        {
            // 如果商店界面开着，ProceedButton 被禁用了，先关掉
            if (merchantRoom.Inventory.IsOpen)
            {
                // 找到 backButton 关闭商店界面
                var backButtons = FindNodesRecursive<NBackButton>(merchantRoom.Inventory);
                if (backButtons.Count > 0)
                    backButtons[0].ForceClick();
            }
            if (!merchantRoom.ProceedButton.IsEnabled)
                return new ActionResult(false, "Proceed button not available");
            merchantRoom.ProceedButton.ForceClick();
            return new ActionResult(true, null);
        }

        // buy / remove_card 需要商店界面已打开，自动打开
        if (!merchantRoom.Inventory.IsOpen)
            merchantRoom.OpenInventory();

        // 获取数据模型和 UI 槽位
        var dataInv = merchantRoom.Room.Inventory;
        var uiInv = merchantRoom.Inventory;
        var allSlots = uiInv.GetAllSlots().ToList();

        if (request.ShopAction == "remove_card")
        {
            var removalEntry = dataInv.CardRemovalEntry;
            if (removalEntry == null || !removalEntry.IsStocked)
                return new ActionResult(false, "Card removal not available");
            // remove_card 触发 OneOffSynchronizer.DoLocalMerchantCardRemoval 需要 UI 选牌，
            // 不能 .Result 主线程死锁。OnReleased 是 NMerchantSlot 基类的 private 方法，
            // TaskHelper.RunSafely(OnReleased()) 走 fire-and-forget 避免阻塞主线程。
            var removalSlot = allSlots.OfType<NMerchantCardRemoval>().FirstOrDefault();
            var onReleased = typeof(NMerchantSlot)
                .GetMethod("OnReleased", BindingFlags.NonPublic | BindingFlags.Instance);
            if (onReleased != null)
                TaskHelper.RunSafely((Task)onReleased.Invoke(removalSlot, null)!);
            else
                return new ActionResult(false, "Failed to invoke card removal");

            return new ActionResult(true, null);
        }

        if (request.ShopAction == "buy")
        {
            var entryType = request.ChoiceType;
            var itemIndex = request.ItemIndex;
            if (entryType == null || itemIndex == null)
                return new ActionResult(false, "choice_type and item_index are required for shop buy");

            MerchantEntry? entry = entryType switch
            {
                "character_card" => itemIndex < dataInv.CharacterCardEntries.Count
                    ? dataInv.CharacterCardEntries[itemIndex.Value] : null,
                "colorless_card" => itemIndex < dataInv.ColorlessCardEntries.Count
                    ? dataInv.ColorlessCardEntries[itemIndex.Value] : null,
                "relic" => itemIndex < dataInv.RelicEntries.Count
                    ? dataInv.RelicEntries[itemIndex.Value] : null,
                "potion" => itemIndex < dataInv.PotionEntries.Count
                    ? dataInv.PotionEntries[itemIndex.Value] : null,
                _ => null
            };

            if (entry == null)
                return new ActionResult(false, $"Invalid entry: type={entryType}, index={itemIndex}");

            if (!entry.IsStocked)
                return new ActionResult(false, "Item is out of stock");

            if (!entry.EnoughGold)
                return new ActionResult(false, "Not enough gold");

            var buySuccess = entry.OnTryPurchaseWrapper(dataInv);
            return buySuccess.Result
                ? new ActionResult(true, null)
                : new ActionResult(false, "Purchase failed");
        }

        return new ActionResult(false, $"Unknown shop_action: {request.ShopAction}. Use leave/buy/remove_card.");
    }

    /// <summary>
    /// 执行宝箱房间操作。
    ///
    /// 子动作：
    ///   "open_chest" — 打开宝箱（触发开箱动画 + 遗物可选）
    ///   "pick_relic" — 选择遗物，需配合 choice_index（可选遗物的 index）
    ///   "skip"       — 跳过该遗物选择（多人模式中不参与抢遗物）
    ///   "leave"      — 离开宝箱房间
    ///
    /// 多人模式中 pick_relic/skip 是投票行为，等所有人投票后随机分配。
    /// </summary>
    private static ActionResult ExecuteTreasureAction(ActionRequest request)
    {
        var treasureRoom = NRun.Instance?.TreasureRoom;
        if (treasureRoom == null)
            return new ActionResult(false, "Not in a treasure room");

        var sync = RunManager.Instance.TreasureRoomRelicSynchronizer;
        var chestOpenedField = typeof(NTreasureRoom).GetField("_hasChestBeenOpened",
            BindingFlags.NonPublic | BindingFlags.Instance);
        bool chestOpened = (bool)(chestOpenedField?.GetValue(treasureRoom) ?? false);

        if (request.ShopAction == "leave")
        {
            if (!treasureRoom.ProceedButton.IsEnabled)
                return new ActionResult(false, "Proceed button not available");
            treasureRoom.ProceedButton.ForceClick();
            return new ActionResult(true, null);
        }

        if (request.ShopAction == "open_chest")
        {
            var chestBtn = treasureRoom.GetNodeOrNull<NButton>("%Chest");
            if (chestBtn == null)
                return new ActionResult(false, "Chest button not found");
            chestBtn.EmitSignal(NClickableControl.SignalName.Released, chestBtn);
            return new ActionResult(true, null);
        }

        if (request.ShopAction == "pick_relic" || request.ShopAction == "skip")
        {
            // 确保宝箱已开启，否则先开箱
            if (!chestOpened)
            {
                var chestBtn = treasureRoom.GetNodeOrNull<NButton>("%Chest");
                if (chestBtn == null)
                    return new ActionResult(false, "Chest button not found");
                chestBtn.EmitSignal(NClickableControl.SignalName.Released, chestBtn);
            }

            if (request.ShopAction == "skip")
            {
                sync.SkipRelicLocally();
                return new ActionResult(true, null);
            }

            if (request.ChoiceIndex == null)
                return new ActionResult(false, "choice_index is required for pick_relic");
            if (sync.CurrentRelics == null || sync.CurrentRelics.Count == 0)
                return new ActionResult(false, "No relics available");
            if (request.ChoiceIndex.Value < 0 || request.ChoiceIndex.Value >= sync.CurrentRelics.Count)
                return new ActionResult(false, $"Invalid choice_index: {request.ChoiceIndex}. {sync.CurrentRelics.Count} relics available");
            sync.PickRelicLocally(request.ChoiceIndex.Value);
            return new ActionResult(true, null);
        }

        return new ActionResult(false, $"Unknown treasure_action: {request.ShopAction}. Use pick_relic/skip/leave.");
    }

    /// <summary>
    /// 执行事件选择操作。
    ///
    /// 参数：
    ///   option_index — 要选择的选项 index（0-based，对应 EventSnapshot.Options）
    ///
    /// 通过 NEventRoom.OptionButtonClicked 触发选择，自动处理锁定检查、多人投票、多页翻页等。
    /// 选完后 AI 应再次 get_state 查看事件新状态（可能翻页显示新选项或已完成）。
    /// </summary>
    private static ActionResult ExecuteEventAction(ActionRequest request)
    {
        var eventRoom = NRun.Instance?.EventRoom;
        if (eventRoom == null)
            return new ActionResult(false, "Not in an event room");

        // 通过反射读取 EventModel 获取当前选项
        var eventField = typeof(NEventRoom).GetField("_event",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (eventField?.GetValue(eventRoom) is not EventModel evt)
            return new ActionResult(false, "No event data");

        // 事件已完成 → 直接 proceed 离开
        if (evt.IsFinished)
        {
            NEventRoom.Proceed();
            return new ActionResult(true, null);
        }

        if (request.OptionIndex == null)
            return new ActionResult(false, "option_index is required for event_action");

        var options = evt.CurrentOptions;
        if (request.OptionIndex.Value < 0 || request.OptionIndex.Value >= options.Count)
            return new ActionResult(false, $"Invalid option_index: {request.OptionIndex}. {options.Count} options available");

        var option = options[request.OptionIndex.Value];

        if (option.IsLocked)
            return new ActionResult(false, $"Option {request.OptionIndex} is locked");

        // 委托给 NEventRoom 的点击处理（自动处理 multiplayer vote / proceed / 锁定等逻辑）
        eventRoom.OptionButtonClicked(option, request.OptionIndex.Value);

        return new ActionResult(true, null);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// 创建默认空状态快照，用于 RunState 不可用时的降级返回。
    /// </summary>
    private static GameStateSnapshot CreateEmptyState()
    {
        return new GameStateSnapshot("loading", false, null, null, null, null, null, null, null, null,
            new RunSnapshot(0, 1, 0, 0, [], []));
    }

    /// <summary>日志输出（info 级别），格式 [autoSpire] 消息</summary>
    private static void LogInfo(string msg) => Console.WriteLine($"[autoSpire] {msg}");
    /// <summary>日志输出（error 级别），格式 [autoSpire] ERROR: 消息</summary>
    private static void LogError(string msg) => Console.WriteLine($"[autoSpire] ERROR: {msg}");

    /// <summary>
    /// 内部类：待处理动作的配对记录。
    /// Id 用于在 _pendingResults 字典中匹配对应的 TCS。
    /// </summary>
    private record PendingAction(Guid Id, ActionRequest Request);
}

// ── Godot Node that drives per-frame update ──────────────────────────

/// <summary>
/// Godot 场景树的叶子节点，唯一职责是每帧调用 GameHookServer.Update()。
///
/// 必须用 partial 声明，因为 Godot 的源码生成器要求所有继承 GodotObject 的类加上 partial。
/// 通过 NGame.Instance.AddChildSafely() 挂载到场景树，由 Godot 主循环驱动 _Process 回调。
/// </summary>
public partial class UpdateNode : Node
{
    private readonly GameHookServer _server;

    public UpdateNode(GameHookServer server)
    {
        _server = server;
    }

    /// <summary>
    /// Godot 主循环回调，每帧触发一次。
    /// </summary>
    public override void _Process(double delta)
    {
        _server.Update();
    }
}
