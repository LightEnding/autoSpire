using System.Text.Json.Serialization;

namespace autoSpire.scripts.core;

/// <summary>
/// 游戏状态的完整快照，AI 的"眼睛"。
/// 每帧在主线程刷新后缓存，HTTP GET /state 直接返回此对象序列化后的 JSON。
/// 根据 Phase 字段，只有对应场景的子快照非 null，其余为 null。
/// </summary>
public record GameStateSnapshot(
    /// <summary>
    /// 当前游戏阶段：
    /// "combat" 战斗 / "map" 选路线 / "shop" 商店 / "reward" 选奖励 /
    /// "rest" 休息点 / "event" 事件 / "treasure" 宝箱 / "game_over" 结算 /
    /// "menu" 游戏外界面 / "loading" 加载中
    /// </summary>
    string Phase,
    /// <summary>游戏是否正在等待玩家输入。AI 应仅在 WaitingForInput 为 true 时发 action。</summary>
    bool WaitingForInput,
    /// <summary>战斗状态，Phase="combat" 时非 null</summary>
    CombatSnapshot? Combat,
    /// <summary>地图状态，Phase="map" 时非 null</summary>
    MapSnapshot? Map,
    /// <summary>商店状态，Phase="shop" 时非 null</summary>
    ShopSnapshot? Shop,
    /// <summary>选奖励状态，Phase="reward" 时非 null</summary>
    RewardSnapshot? Reward,
    /// <summary>休息点状态，Phase="rest" 时非 null</summary>
    RestSnapshot? Rest,
    /// <summary>事件状态，Phase="event" 时非 null</summary>
    EventSnapshot? Event,
    /// <summary>宝箱状态，Phase="treasure" 时非 null</summary>
    TreasureSnapshot? Treasure,
    /// <summary>游戏外界面状态，Phase="menu" 时非 null</summary>
    MenuSnapshot? Menu,
    /// <summary>Run 全局信息，所有阶段都有效</summary>
    RunSnapshot Run
);

// ─── Combat ────────────────────────────────────────────────────────────

/// <summary>
/// 战斗快照：包含手牌、敌人、玩家状态、药水等 AI 决策所需的核心数据。
/// </summary>
public record CombatSnapshot(
    /// <summary>当前回合数（从 1 开始）</summary>
    int RoundNumber,
    /// <summary>是否为玩家出牌阶段（= CombatManager.IsPlayPhase）</summary>
    bool IsPlayPhase,
    /// <summary>当前能量</summary>
    int Energy,
    /// <summary>最大能量</summary>
    int MaxEnergy,
    /// <summary>手牌列表</summary>
    List<CardSnapshot> Hand,
    /// <summary>抽牌堆剩余牌数</summary>
    int DrawPileCount,
    /// <summary>弃牌堆牌数</summary>
    int DiscardPileCount,
    /// <summary>消耗堆牌数</summary>
    int ExhaustPileCount,
    /// <summary>玩家 HP / 格挡 / 遗物</summary>
    PlayerSnapshot Player,
    /// <summary>敌方单位列表</summary>
    List<EnemySnapshot> Enemies,
    /// <summary>药水栏</summary>
    List<PotionSnapshot> Potions,
    /// <summary>战斗中弹出的选牌 overlay（如搜寻/攻击药水等），非 null 时需调用 pick_card</summary>
    CardSelectionSnapshot? CardSelection,
    /// <summary>手牌选择模式（如净化消耗/丢弃等），非 null 时需调用 pick_card 选牌 + 完成后 confirm_selection</summary>
    HandCardSelectionSnapshot? HandCardSelection
);

/// <summary>
/// 手牌中的一张牌，包含 AI 判断出牌所需的所有信息。
/// </summary>
public record CardSnapshot(
    /// <summary>卡牌内部 ID（用于唯一标识）</summary>
    string Id,
    /// <summary>卡牌名称（已本地化）</summary>
    string Name,
    /// <summary>当前费用（已计算所有 cost modifier，如加费/减费/变为 0）</summary>
    int Cost,
    /// <summary>卡牌类型："Attack" / "Skill" / "Power" / "Status" / "Curse"</summary>
    string Type,
    /// <summary>稀有度："Basic" / "Common" / "Uncommon" / "Rare"</summary>
    string Rarity,
    /// <summary>基础伤害值（当前使用占位值，待从 ValueProp 系统正确读取）</summary>
    int? Damage,
    /// <summary>基础格挡值（当前使用占位值，待从 ValueProp 系统正确读取）</summary>
    int? Block,
    /// <summary>卡牌描述文本（含动态变量展开）</summary>
    string Description,
    /// <summary>当前是否可以打出</summary>
    bool CanPlay,
    /// <summary>若不可打出，原因描述（如"费用不足""无合法目标"）</summary>
    string? UnplayableReason,
    /// <summary>是否需要选择目标</summary>
    bool NeedsTarget,
    /// <summary>当前可打出的合法敌方 CombatId 列表（需选择目标时有效）</summary>
    List<int> ValidTargetIds
);

/// <summary>
/// 敌方单位快照。
/// </summary>
public record EnemySnapshot(
    /// <summary>敌方在本次战斗中的唯一 ID，用于指定出牌目标</summary>
    int CombatId,
    /// <summary>敌方名称</summary>
    string Name,
    /// <summary>当前 HP</summary>
    int CurrentHp,
    /// <summary>最大 HP</summary>
    int MaxHp,
    /// <summary>当前格挡值</summary>
    int Block,
    /// <summary>意图描述文本（如"造成 12 点伤害""格挡 8 点"）</summary>
    string IntentLabel,
    /// <summary>身上挂载的 buff/debuff 列表</summary>
    List<BuffSnapshot> Buffs,
    /// <summary>是否存活（HP &gt; 0）</summary>
    bool IsAlive,
    /// <summary>是否可作为攻击目标</summary>
    bool IsHittable
);

/// <summary>
/// Buff / Debuff 快照（游戏内称为 Power）。
/// </summary>
public record BuffSnapshot(
    /// <summary>名称</summary>
    string Name,
    /// <summary>层数 / 计数器值</summary>
    int StackCount,
    /// <summary>效果描述</summary>
    string Description
);

/// <summary>
/// 玩家自身状态（HP / 格挡 / 遗物）。
/// 手牌和能量放在 CombatSnapshot 层级，因为与出牌阶段更相关。
/// </summary>
public record PlayerSnapshot(
    /// <summary>当前 HP</summary>
    int CurrentHp,
    /// <summary>最大 HP</summary>
    int MaxHp,
    /// <summary>当前格挡</summary>
    int Block,
    /// <summary>已获得的遗物列表</summary>
    List<RelicSnapshot> Relics
);

/// <summary>
/// 遗物快照。
/// </summary>
public record RelicSnapshot(
    /// <summary>遗物内部 ID</summary>
    string Id,
    /// <summary>遗物名称</summary>
    string Name,
    /// <summary>遗物描述</summary>
    string Description,
    /// <summary>堆叠计数（如芒果叠了几层）</summary>
    int StackCount
);

/// <summary>
/// 药水快照。
/// </summary>
public record PotionSnapshot(
    /// <summary>药水在药水栏中的槽位 index（0-based）</summary>
    int SlotIndex,
    /// <summary>药水名称</summary>
    string Name,
    /// <summary>药水效果描述</summary>
    string Description
);

// ─── Map ───────────────────────────────────────────────────────────────

/// <summary>
/// 地图快照：当前节点 + 可选的下一步节点列表。
/// </summary>
public record MapSnapshot(
    /// <summary>当前章节（1-based，即第几层塔）</summary>
    int CurrentAct,
    /// <summary>当前层数（本 act 内的第几层）</summary>
    int CurrentFloor,
    /// <summary>当前节点类型字符串，对应 MapPointType 枚举</summary>
    string CurrentNodeType,
    /// <summary>可选的下一步节点列表</summary>
    List<MapNodeSnapshot> AvailableNodes,
    /// <summary>整个 Act 所有地图节点的完整列表（含坐标和类型），用于路线规划</summary>
    List<ActMapNodeSnapshot> AllNodes
);

/// <summary>
/// 地图上的一个可选节点。
/// </summary>
public record MapNodeSnapshot(
    /// <summary>列坐标</summary>
    int Col,
    /// <summary>行坐标</summary>
    int Row,
    /// <summary>节点类型："Monster" / "Elite" / "Boss" / "Shop" / "Treasure" / "RestSite" / "Unknown"</summary>
    string NodeType
);

/// <summary>
/// 整个 Act 地图上的一个节点（含坐标和行号），供路线规划使用。
/// </summary>
public record ActMapNodeSnapshot(
    /// <summary>列坐标</summary>
    int Col,
    /// <summary>行坐标（0-based，行号越大越深入，Boss 在最大行号）</summary>
    int Row,
    /// <summary>节点类型字符串</summary>
    string NodeType
);

// ─── Shop ──────────────────────────────────────────────────────────────

/// <summary>
/// 商店快照：商品列表 + 删牌费用。
/// </summary>
public record ShopSnapshot(
    /// <summary>当前金币</summary>
    int Gold,
    /// <summary>商店出售的普通卡牌</summary>
    List<ShopItemSnapshot> CharacterCards,
    /// <summary>商店出售的无色卡牌</summary>
    List<ShopItemSnapshot> ColorlessCards,
    /// <summary>商店出售的遗物</summary>
    List<ShopItemSnapshot> Relics,
    /// <summary>商店出售的药水</summary>
    List<ShopItemSnapshot> Potions,
    /// <summary>删牌费用（0 表示不可用/已使用）</summary>
    int RemoveCardCost,
    /// <summary>是否可以离开商店</summary>
    bool CanLeave,
    /// <summary>删牌 / 变换等触发的选牌界面，非 null 时需调用 pick_card</summary>
    CardSelectionSnapshot? CardSelection
);

/// <summary>
/// 商店中的一件商品。
/// </summary>
public record ShopItemSnapshot(
    /// <summary>商品在列表中的 index（用于 shop_action 指定 item_index）</summary>
    int Index,
    /// <summary>商品类型："character_card" / "colorless_card" / "relic" / "potion" / "card_removal"</summary>
    string EntryType,
    /// <summary>商品名称</summary>
    string Name,
    /// <summary>价格（金币）</summary>
    int Price,
    /// <summary>商品描述</summary>
    string Description,
    /// <summary>是否有货</summary>
    bool IsStocked,
    /// <summary>金币是否足够</summary>
    bool EnoughGold
);

// ─── Reward ────────────────────────────────────────────────────────────

/// <summary>
/// 战斗奖励选择快照。
/// Items 列表按 NRewardButton 在界面上的顺序排列，每个 item 的 Index 即为 choice_index。
/// CardChoices/RelicChoices/GoldReward 字段为便捷汇总，可能有空值。
/// 当玩家点击选牌奖励后，会弹出 NCardRewardSelectionScreen 子界面，此时 CardSelection 非 null。
/// </summary>
public record RewardSnapshot(
    /// <summary>详细奖励项列表（每一项对应一个界面按钮）</summary>
    List<RewardItemSnapshot> Items,
    /// <summary>可选卡牌列表（汇总，可能为空）</summary>
    List<CardSnapshot> CardChoices,
    /// <summary>可选遗物名称列表（汇总）</summary>
    List<string> RelicChoices,
    /// <summary>金币奖励数量</summary>
    int GoldReward,
    /// <summary>是否可以跳过（跳过即不选任何奖励）</summary>
    bool CanSkip,
    /// <summary>选牌子界面状态（点击选牌奖励后弹出），非 null 时表示正在选牌中</summary>
    CardSelectionSnapshot? CardSelection
);

/// <summary>
/// 选牌子界面快照（overlay 类型: NCardRewardSelectionScreen / NChooseACardSelectionScreen 等）。
/// 当玩家在奖励界面点击了"选牌"后，游戏弹出此子界面展示可选卡牌。
/// AI 需使用 pick_card + card_index 选择其中一张。
/// </summary>
public record CardSelectionSnapshot(
    /// <summary>选牌提示文本（如"选择一张牌消耗""Choose a card to Exhaust"）</summary>
    string Prompt,
    /// <summary>可选卡牌列表（通常 3 张）</summary>
    List<CardSnapshot> Options,
    /// <summary>是否可以跳过不选（通常为 false，必须选一张）</summary>
    bool CanSkip
);

/// <summary>
/// 手牌选择模式快照（NPlayerHand 进入 SimpleSelect/UpgradeSelect 模式）。
/// 触发于净化/丢弃/消耗等需要从手牌中选择卡牌的效果。
/// 与 CardSelection 不同，卡牌保留在手牌区域而非弹窗，点击切换选中状态。
/// AI 应调用 pick_card(card_index=N) 切换选中，完成选牌后调用 confirm_selection。
/// </summary>
public record HandCardSelectionSnapshot(
    /// <summary>选牌提示文本（如"选择一张牌消耗"）</summary>
    string Prompt,
    /// <summary>手牌中可选中的卡牌列表（已过滤，仅显示可选中的牌）</summary>
    List<CardSnapshot> SelectableCards,
    /// <summary>最少需选中数</summary>
    int MinSelect,
    /// <summary>最多可选中数</summary>
    int MaxSelect,
    /// <summary>当前已选中数</summary>
    int CurrentSelectCount,
    /// <summary>确认按钮是否可用（选中数在 [MinSelect, MaxSelect] 范围内时为 true）</summary>
    bool CanConfirm
);

/// <summary>
/// 奖励界面上的一项奖励（对应一个 NRewardButton）。
/// </summary>
public record RewardItemSnapshot(
    /// <summary>界面 index，用于 pick_reward 时指定 choice_index</summary>
    int Index,
    /// <summary>奖励类型："gold" / "card" / "relic" / "potion"</summary>
    string Type,
    /// <summary>奖励名称（卡牌名/遗物名/药水名/"金币"）</summary>
    string Name,
    /// <summary>奖励描述</summary>
    string Description,
    /// <summary>卡牌奖励中可选的卡牌列表（仅 Type="card" 时非空）</summary>
    List<CardSnapshot> CardOptions
);

// ─── Rest site ─────────────────────────────────────────────────────────

/// <summary>
/// 休息点快照：可选的操作列表（回复 / 锻造 / 挖掘等）。
/// 当选择锻造后弹出 NDeckUpgradeSelectScreen 选牌界面时，CardSelection 非 null。
/// </summary>
public record RestSnapshot(
    /// <summary>可选操作列表</summary>
    List<RestOptionSnapshot> Options,
    /// <summary>选牌升级子界面（选择锻造后弹出），非 null 时表示正在选牌中</summary>
    CardSelectionSnapshot? CardSelection,
    /// <summary>目标选择子状态（如愈合需要选择其他玩家），非 null 时表示正在选择治疗目标</summary>
    TargetSelectionSnapshot? TargetSelection
);

/// <summary>
/// 休息点的一个可选操作。
/// </summary>
public record RestOptionSnapshot(
    /// <summary>选项 index（用于 POST /action 指定选择）</summary>
    int Index,
    /// <summary>选项名称（如"回复""锻造"）</summary>
    string Name,
    /// <summary>选项效果描述</summary>
    string Description
);

/// <summary>
/// 休息点中的目标玩家选择状态（多人模式下愈合/挖掘等需要指定目标角色）。
/// </summary>
public record TargetSelectionSnapshot(
    /// <summary>可选目标玩家列表</summary>
    List<TargetOptionSnapshot> Targets
);

/// <summary>
/// 一个可选的目标玩家。
/// </summary>
public record TargetOptionSnapshot(
    /// <summary>目标 index（用于 rest_action 时指定 target_index）</summary>
    int Index,
    /// <summary>玩家名称</summary>
    string PlayerName
);

// ─── Event ─────────────────────────────────────────────────────────────

/// <summary>
/// 事件快照。
/// </summary>
public record EventSnapshot(
    /// <summary>事件名称</summary>
    string Name,
    /// <summary>事件描述文本</summary>
    string Description,
    /// <summary>事件是否已完成（只能离开）</summary>
    bool IsFinished,
    /// <summary>当前可选选项列表（可能有多页）</summary>
    List<EventOptionSnapshot> Options,
    /// <summary>事件中触发的选牌界面（如移除/升级/变换），非 null 时需调用 pick_card</summary>
    CardSelectionSnapshot? CardSelection
);

// ─── Treasure Room ──────────────────────────────────────────────────────

/// <summary>
/// 宝箱房间快照。
/// </summary>
public record TreasureSnapshot(
    /// <summary>是否已开启宝箱</summary>
    bool ChestOpened,
    /// <summary>是否正在选遗物</summary>
    bool IsPicking,
    /// <summary>是否可以离开（重掷/跳过/继续按钮可用）</summary>
    bool CanLeave,
    /// <summary>可选遗物列表（宝箱开启后非空，单人有 1 个，多人有多选）</summary>
    List<TreasureRelicSnapshot> Relics,
    /// <summary>当前玩家投票的遗物 index（null = 未投 / skip）</summary>
    int? MyVoteIndex
);

/// <summary>
/// 宝箱房间中可选的一个遗物。
/// </summary>
public record TreasureRelicSnapshot(
    int Index,
    string Name,
    string Description
);

/// <summary>
/// 事件的一个选项。
/// </summary>
public record EventOptionSnapshot(
    /// <summary>选项 index（用于 event_action option_index）</summary>
    int Index,
    /// <summary>选项标题</summary>
    string Text,
    /// <summary>选项详细描述</summary>
    string DetailDescription,
    /// <summary>是否锁定（无法选择）</summary>
    bool IsLocked,
    /// <summary>是否离开按钮</summary>
    bool IsProceed,
    /// <summary>选项关联的卡牌预览（诅咒/奖励等），含完整数值</summary>
    List<CardSnapshot> HoverCards,
    /// <summary>选项关联的遗物（null 表示无关联遗物）</summary>
    RelicInfo? HoverRelic
);

/// <summary>
/// 事件选项关联的遗物简要信息。
/// </summary>
public record RelicInfo(
    string Name,
    string Description
);

// ─── Run (always present) ──────────────────────────────────────────────

/// <summary>
/// Run 级别的全局信息，所有游戏阶段都有效。
/// </summary>
/// <summary>
/// 牌组中的一种卡牌（同名合并，含数量）。
/// </summary>
public record DeckCardSnapshot(
    string Id,
    string Name,
    int Count
);

/// <summary>
/// 持有的一个遗物（名称和描述）。
/// </summary>
public record RunRelicSnapshot(
    string Name,
    string Description
);

public record RunSnapshot(
    /// <summary>进阶等级（Ascension 0-20）</summary>
    int AscensionLevel,
    /// <summary>当前章节（1-based）</summary>
    int CurrentAct,
    /// <summary>当前层数</summary>
    int CurrentFloor,
    /// <summary>当前金币</summary>
    int Gold,
    /// <summary>完整牌组</summary>
    List<DeckCardSnapshot> DeckCards,
    /// <summary>持有的遗物</summary>
    List<RunRelicSnapshot> Relics
);

// ─── Menu (out-of-run) ──────────────────────────────────────────────────

/// <summary>
/// 游戏外界面快照（主菜单 / 角色选择 / 多人子菜单等）。
/// Phase="menu" 时非 null。
/// </summary>
public record MenuSnapshot(
    /// <summary>
    /// 当前界面标识：
    /// "logo" 启动动画 / "main_menu" 主菜单 / "singleplayer_submenu" 单机子菜单 /
    /// "multiplayer_submenu" 多人子菜单 / "multiplayer_host" 创建多人 /
    /// "join_friend" 加入好友 / "character_select" 角色选择 /
    /// "compendium" 百科 / "settings" 设置 / "custom_run" 自定义局 /
    /// "daily_run" 每日挑战 / "run_history" 历史记录 / "stats" 统计 /
    /// "timeline" 时间线 / "card_library" 卡牌图鉴 /
    /// "relic_collection" 遗物图鉴 / "bestiary" 怪物图鉴 /
    /// "potion_lab" 药水实验室 / "modding" 模组 / "profile" 个人资料 /
    /// "patch_notes" 更新日志 / "modal" 弹窗 / "early_access" EA 免责声明 /
    /// "feedback" 反馈 / "credits" 鸣谢
    /// </summary>
    string Screen,
    /// <summary>是否为子菜单（从 NMainMenu.SubmenuStack 打开）</summary>
    bool IsSubmenu,
    /// <summary>主菜单上是否有继续游戏的选项（存在未完成的 Run）</summary>
    bool CanContinue
);

// ─── Action request (AI → game) ────────────────────────────────────────

/// <summary>
/// AI 发送给游戏的动作请求。
/// 使用单一 record + action 字段区分类型，比 JSON 多态更简单可靠。
/// JSON 字段名使用 snake_case，与 Python MCP 侧自然对接。
///
/// action 取值：
///   "play_card"      — 出牌，需填 hand_index，可选 target_id
///   "end_turn"       — 结束回合
///   "use_potion"     — 使用药水，需填 slot_index，可选 target_id
///   "move_to_map_coord" — 选路线，需填 col, row
///   "pick_reward"    — 选奖励，需填 choice_index, choice_type ("card"/"relic"/"gold"/"skip")，若 CardSelection 非 null 也可用 pick_card
///   "pick_card"      — 通用选牌，需填 card_index（奖励/战斗 overlay/手牌选择均适用）
///   "confirm_selection" — 确认手牌选择（净化等需手动确认的多选效果）
///   "shop_action"    — 商店操作，需填 shop_action ("buy_card"/"buy_relic"/"buy_potion"/"remove_card"/"leave"), item_index
///   "rest_action"    — 休息点操作，需填 option_index
///   "event_action"   — 事件选项，需填 option_index
/// </summary>
public record ActionRequest(
    /// <summary>动作类型（见类注释）</summary>
    [property: JsonPropertyName("action")] string Action,
    /// <summary>手牌 index，play_card 时使用</summary>
    [property: JsonPropertyName("hand_index")] int? HandIndex,
    /// <summary>目标敌方 CombatId，play_card / use_potion 时可选</summary>
    [property: JsonPropertyName("target_id")] int? TargetCombatId,
    /// <summary>药水槽位 index，use_potion 时使用</summary>
    [property: JsonPropertyName("slot_index")] int? SlotIndex,
    /// <summary>目标节点列坐标，move_to_map_coord 时使用</summary>
    [property: JsonPropertyName("col")] int? Col,
    /// <summary>目标节点行坐标，move_to_map_coord 时使用</summary>
    [property: JsonPropertyName("row")] int? Row,
    /// <summary>选项 index，pick_reward / rest_action / event_action 时使用</summary>
    [property: JsonPropertyName("choice_index")] int? ChoiceIndex,
    /// <summary>选择类型，pick_reward 时使用："card" / "relic" / "gold" / "skip"</summary>
    [property: JsonPropertyName("choice_type")] string? ChoiceType,
    /// <summary>商店操作类型，shop_action 时使用："buy_card" / "buy_relic" / "buy_potion" / "remove_card" / "leave"</summary>
    [property: JsonPropertyName("shop_action")] string? ShopAction,
    /// <summary>商品 index，shop_action 时使用</summary>
    [property: JsonPropertyName("item_index")] int? ItemIndex,
    /// <summary>选项 index，rest_action / event_action 时使用</summary>
    [property: JsonPropertyName("option_index")] int? OptionIndex,
    /// <summary>选牌界面中的卡牌 index（0-based），pick_reward 时 CardSelection 子界面使用</summary>
    [property: JsonPropertyName("card_index")] int? CardIndex,
    /// <summary>游戏外操作子类型，menu_action 时使用："continue_run" / "singleplayer" / "multiplayer" / "standard" / "daily" / "custom" / "select_character" / "set_ascension" / "embark" / "back" 等</summary>
    [property: JsonPropertyName("menu_action")] string? MenuAction,
    /// <summary>角色 index（0-based），select_character 时使用</summary>
    [property: JsonPropertyName("character_index")] int? CharacterIndex,
    /// <summary>进阶等级（0-20），set_ascension 时使用</summary>
    [property: JsonPropertyName("ascension_level")] int? AscensionLevel,
    /// <summary>多牌连出列表，multi_play 时使用。每项含 hand_index(int) 和可选的 target_id(int?)</summary>
    [property: JsonPropertyName("cards")] List<MultiPlayCardSpec>? Cards
);

/// <summary>
/// multi_play 动作中的单张牌规格。
/// </summary>
public record MultiPlayCardSpec(
    [property: JsonPropertyName("hand_index")] int HandIndex,
    [property: JsonPropertyName("target_id")] int? TargetId
);

// ─── Action result (game → AI) ─────────────────────────────────────────

/// <summary>
/// 游戏对 ActionRequest 的响应。
/// 成功时 Success=true；失败时 Success=false 且 Error 包含原因。
/// play_card 成功时附带 PlayedCardName + PlayedCardTarget，
/// pick_card / confirm_selection 附带选牌状态（见 HandCardSelection / CardSelection）。
/// 出牌/用药/结束回合后游戏状态可能异步变化，AI 应在需要最新状态时调用 get_state。
/// </summary>
public record ActionResult(
    /// <summary>执行是否成功</summary>
    bool Success,
    /// <summary>失败时的错误信息，成功时为 null</summary>
    string? Error,
    /// <summary>刚打出的卡牌名称（仅 play_card 成功时有值）</summary>
    [property: JsonPropertyName("played_card_name")] string? PlayedCardName = null,
    /// <summary>出牌目标（仅 play_card 成功时有值）</summary>
    [property: JsonPropertyName("played_card_target")] CardTargetSnapshot? PlayedCardTarget = null,
    /// <summary>手牌选择模式状态（pick_card/confirm_selection 后当前的 HandCardSelection），无手牌选择时为 null</summary>
    [property: JsonPropertyName("hand_card_selection")] HandCardSelectionSnapshot? HandCardSelection = null,
    /// <summary>Overlay 选牌状态（pick_card 后当前的 CardSelection），无 overlay 选牌时为 null</summary>
    [property: JsonPropertyName("card_selection")] CardSelectionSnapshot? CardSelection = null
);

/// <summary>
/// 出牌的目标信息。
/// </summary>
public record CardTargetSnapshot(
    /// <summary>目标类型："enemy" / "self" / "none"</summary>
    string Type,
    /// <summary>敌方 CombatId（仅 Type="enemy" 时有值）</summary>
    int? CombatId,
    /// <summary>目标名称（敌方名或"自身"）</summary>
    string? Name
);
