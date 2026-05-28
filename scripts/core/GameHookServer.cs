using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
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
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
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
    /// <summary>缓存的游戏状态快照：主线程写入，后台线程读取</summary>
    private GameStateSnapshot _cachedState = CreateEmptyState();
    /// <summary>保护 _cachedState 的锁（主线程写、后台线程读）</summary>
    private readonly object _stateLock = new();
    /// <summary>HTTP 服务运行标记，设为 false 时停止监听循环</summary>
    private bool _running;
    /// <summary>挂载到 Godot 场景树的更新节点，用于驱动 _Process</summary>
    private UpdateNode? _node;

    /// <summary>
    /// 线程安全的状态缓存访问器。
    /// GET /state 直接返回此缓存值，无需每次动态构建。
    /// </summary>
    private GameStateSnapshot CachedState
    {
        get { lock (_stateLock) return _cachedState; }
        set { lock (_stateLock) _cachedState = value; }
    }

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
    internal void Update()
    {
        RefreshState();

        while (_actionQueue.TryDequeue(out var pending))
        {
            var result = ExecuteAction(pending.Request);
            if (_pendingResults.TryRemove(pending.Id, out var tcs))
                tcs.SetResult(result);
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
    /// 处理 GET /state：返回缓存的最新游戏状态快照。
    ///
    /// 为什么读缓存而不是实时构建：
    /// - 状态快照在主线程 _Process 中每帧刷新
    /// - 后台线程直接读缓存（加锁），避免跨线程访问 Godot 对象
    /// - HTTP 请求不会阻塞主线程
    /// </summary>
    private async Task HandleGetState(HttpListenerResponse res)
    {
        var state = CachedState;
        var json = JsonSerializer.Serialize(state);
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
            await WriteJson(res, 400, JsonSerializer.Serialize(new ActionResult(false, "Invalid JSON")));
            return;
        }

        if (actionReq == null)
        {
            await WriteJson(res, 400, JsonSerializer.Serialize(new ActionResult(false, "Empty request")));
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
        await WriteJson(res, statusCode, JsonSerializer.Serialize(result));
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
    /// 刷新缓存的游戏状态快照。每帧在 Update() 中调用一次。
    ///
    /// 流程：
    /// 1. 从 RunManager 获取 RunState → 判断游戏阶段（战斗 / 地图 / 商店 / ...）
    /// 2. 根据阶段调用对应的 Build*Snapshot 方法
    /// 3. 构建 Run 级别的全局信息
    /// 4. 更新缓存（加锁）
    ///
    /// 异常安全：构建过程中的任何异常会被捕获并记录日志，不会导致 HTTP 服务崩溃。
    /// </summary>
    private void RefreshState()
    {
        try
        {
            var runState = RunManager.Instance.DebugOnlyGetState();
            if (runState == null)
            {
                CachedState = CreateEmptyState();
                return;
            }

            // 获取本地玩家（多人模式下需要取正确的玩家，而非固定第一个）
            var player = LocalContext.GetMe(runState);
            var phase = DetectPhase(runState);
            var waiting = IsWaitingForInput(phase, runState);

            // 每个阶段构建对应的子快照（非当前阶段为 null）
            CombatSnapshot? combat = null;
            MapSnapshot? map = null;
            ShopSnapshot? shop = null;
            RewardSnapshot? reward = null;
            RestSnapshot? rest = null;
            EventSnapshot? eventSnap = null;

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
            }

            var run = new RunSnapshot(
                AscensionLevel: runState.AscensionLevel,
                CurrentAct: runState.CurrentActIndex + 1,  // 转为 1-based
                CurrentFloor: runState.ActFloor,
                Gold: player?.Gold ?? 0,
                DeckCards: player?.Deck.Cards.Select(c => c.Id.ToString()).ToList() ?? []
            );

            CachedState = new GameStateSnapshot(phase, waiting, combat, map, shop, reward, rest, eventSnap, run);
        }
        catch (Exception ex)
        {
            LogError($"State refresh error: {ex.Message}");
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

        // 地图界面可见 — 最直接的 map 检测（地图不是 Room，MapRoom 另有所指）
        if (NMapScreen.Instance?.IsOpen == true)
            return "map";

        // 奖励界面：overlay 覆盖在战斗/宝藏房间之上
        // 选牌子界面（NCardRewardSelectionScreen）也是奖励流程的一部分
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
                    return "reward";
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
            "map" or "shop" or "reward" or "rest" or "event" => true,
            _ => false
        };
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
            .Select(c => BuildCardSnapshot(c, combatState, player))
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
                    r.Id.ToString(), r.Title.GetFormattedText() ?? "", r.DynamicDescription.GetFormattedText() ?? "", r.StackCount
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
    private static CardSnapshot BuildCardSnapshot(CardModel card, CombatState combatState, Player player)
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

        // 卡牌描述含 {Damage:diff()} 等动态选择器，GetFormattedText() 无法解析会刷屏报错
        // AI 决策不需要 flavor text，留空即可
        return new CardSnapshot(
            Id: card.Id.ToString(),
            Name: card.Title.ToString() ?? "",
            Cost: card.EnergyCost.GetWithModifiers(CostModifiers.All),  // 含 X 费 / 增减费修正
            Type: card.Type.ToString(),
            Rarity: card.Rarity.ToString(),
            Damage: null, // TODO: 从 ValueProp 系统读取 CanonicalDamage
            Block: null,  // TODO: 从 ValueProp 系统读取 CanonicalBlock
            Description: "",
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
    /// 构建商店快照（骨架实现）。
    /// TODO: 从 ShopRoom 获取商品列表、价格、删牌费用等。
    /// 当前仅填充玩家金币，商品列表为空。
    /// </summary>
    private static ShopSnapshot? BuildShopSnapshot(Player? player)
    {
        if (player == null) return null;
        // RemoveCardCost 75 为 STS 删牌基础费用
        return new ShopSnapshot(player.Gold, [], [], [], 75);
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
            return BuildCardSnapshot(c, null!, null!); // combatState/player not needed for selection cards
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
            return new CardSnapshot(
                Id: cardModel.Id.ToString(),
                Name: cardModel.Title.ToString() ?? "",
                Cost: cardModel.EnergyCost.GetWithModifiers(CostModifiers.All),
                Type: cardModel.Type.ToString(),
                Rarity: cardModel.Rarity.ToString(),
                Damage: null,
                Block: null,
                Description: "",
                CanPlay: true,
                UnplayableReason: null,
                NeedsTarget: false,
                ValidTargetIds: []
            );
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
            .Select(m => new CardSnapshot(
                Id: m!.Id.ToString(),
                Name: m.Title.ToString() ?? "",
                Cost: m.EnergyCost.GetWithModifiers(CostModifiers.All),
                Type: m.Type.ToString(),
                Rarity: m.Rarity.ToString(),
                Damage: null,
                Block: null,
                Description: "",
                CanPlay: true,
                UnplayableReason: null,
                NeedsTarget: false,
                ValidTargetIds: []
            ))
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
    /// 构建事件快照（骨架实现）。
    /// TODO: 从 EventRoom 获取事件文本和选项。
    /// </summary>
    private static EventSnapshot? BuildEventSnapshot()
    {
        return new EventSnapshot("", "", []);
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
                "use_potion" => ExecuteUsePotion(player, request),
                "move_to_map_coord" => ExecuteMoveToMapCoord(player, request),
                "pick_reward" => ExecutePickReward(request),
                "pick_card" => TryExecuteCardSelection(request),
                "confirm_selection" => ConfirmHandSelection(),
                "rest_action" => ExecuteRestAction(request),
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

        // 结束回合后构建战斗快照（IsPlayPhase 将变为 false）
        var runState = RunManager.Instance.DebugOnlyGetState();
        var combat = runState != null ? BuildCombatSnapshot(runState, player) : null;
        return new ActionResult(true, null, Combat: combat);
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
        var playedCardId = card.Id.ToString();
        var playedCardName = card.Title.ToString() ?? card.Id.ToString();

        // 选择目标：
        // - AI 明确指定 target_id → 查找对应的敌方 Creature
        // - TargetType.None（防御/技能/能力牌）→ 目标是自己 (player.Creature)
        // - TargetType.Enemy 且未指定 target_id → 不合法，需要目标
        Creature? target;
        if (request.TargetCombatId.HasValue)
        {
            target = combatState.Enemies.FirstOrDefault(e =>
                e.CombatId.HasValue && (int)e.CombatId.Value == request.TargetCombatId.Value);
            if (target == null)
                return new ActionResult(false, $"Target not found with CombatId: {request.TargetCombatId}");
        }
        else if (card.TargetType == TargetType.None || card.TargetType == TargetType.Self)
        {
            // 防御牌 / 技能牌 / 能力牌 — 自身目标牌，传 null 即可（游戏自身逻辑如此）
            target = null;
        }
        else
        {
            return new ActionResult(false, $"Card requires a target (TargetType={card.TargetType}) but no target_id provided");
        }

        var action = new PlayCardAction(card, target);
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);

        // 出牌后立即构建更新后的战斗快照，避免 AI 额外调用 get_state
        var runState = RunManager.Instance.DebugOnlyGetState();
        var combat = runState != null ? BuildCombatSnapshot(runState, player) : null;

        return new ActionResult(true, null,
            PlayedCardId: playedCardId,
            PlayedCardName: playedCardName,
            Combat: combat);
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

        // 用药后立即构建更新后的战斗快照（药水槽变化、可能的选牌触发等）
        var runState = RunManager.Instance.DebugOnlyGetState();
        var combat = runState != null ? BuildCombatSnapshot(runState, player) : null;
        return new ActionResult(true, null, Combat: combat);
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

    // ── Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// 创建默认空状态快照，用于 RunState 不可用时的降级返回。
    /// </summary>
    private static GameStateSnapshot CreateEmptyState()
    {
        return new GameStateSnapshot("loading", false, null, null, null, null, null, null,
            new RunSnapshot(0, 1, 0, 0, []));
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
