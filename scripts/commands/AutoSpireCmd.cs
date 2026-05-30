using System.Text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;
using autoSpire.scripts.core;

namespace autoSpire.scripts.commands;

/// <summary>
/// autoSpire 的游戏内置控制台命令。
///
/// 按 `（反引号/数字1左边）打开控制台后，可用以下命令：
///   autospire state      — 打印当前战斗状态（手牌/敌人/药水）
///   autospire endturn    — 结束当前回合
///   autospire play [n]   — 打出手牌中第 n 张牌（默认第 0 张），自动选目标
///   autospire map        — 打印当前地图及可选路线
///
/// 数据获取和动作执行均委托给 GameHookServer，避免重复维护。
/// 此类只负责控制台 BBCode 格式化输出。
///
/// 此类会被 ModManager 通过反射自动发现并注册到 DevConsole，
/// 因为 ReflectionHelper.GetSubtypesInMods&lt;AbstractConsoleCmd&gt;() 会扫描所有 mod assembly。
/// 必须有 public 无参构造函数（默认生成即可）。
/// </summary>
public class AutoSpireCmd : AbstractConsoleCmd
{
    public override string CmdName => "autospire";
    public override string Args => "<state|endturn|play|pick|confirm|map> [hand_index:int|card_index:int]";
    public override string Description => "autoSpire AI mod: inspect state / execute actions. subcommands: state, endturn, play [n], pick [n], confirm, map";
    public override bool IsNetworked => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (args.Length == 0)
            return new CmdResult(false, "Usage: autospire <state|endturn|play|pick|confirm|map>");

        return args[0].ToLowerInvariant() switch
        {
            "state" => CmdState(issuingPlayer),
            "endturn" => CmdEndTurn(issuingPlayer),
            "play" => CmdPlayCard(issuingPlayer, args),
            "pick" => CmdPickCard(args),
            "confirm" => CmdConfirm(),
            "map" => CmdMap(issuingPlayer),
            _ => new CmdResult(false, $"Unknown subcommand: {args[0]}. Use state/endturn/play/pick/confirm/map.")
        };
    }

    // ── autospire state ───────────────────────────────────────────────

    /// <summary>
    /// 打印当前战斗状态到控制台。
    /// Phase 检测委托给 GameHookServer.DetectPhase()，
    /// 战斗详情通过 GameHookServer.BuildCombatSnapshot() 获取结构化数据后格式化为 BBCode。
    /// </summary>
    private static CmdResult CmdState(Player? issuingPlayer)
    {
        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
            return new CmdResult(false, "No run in progress.");

        var player = issuingPlayer ?? LocalContext.GetMe(runState);
        if (player == null)
            return new CmdResult(false, "No local player found.");

        var sb = new StringBuilder();

        // Phase 检测委托给 GameHookServer（唯一真实来源）
        var phase = GameHookServer.DetectPhase(runState);
        sb.AppendLine($"[color=yellow]Phase: {phase}[/color]  Act: {runState.CurrentActIndex + 1}  Floor: {runState.ActFloor}  Gold: {player.Gold}");

        if (CombatManager.Instance.IsInProgress && player.PlayerCombatState != null)
        {
            // 使用 GameHookServer 构建结构化战斗数据，避免重复遍历游戏对象
            var combatSnap = GameHookServer.BuildCombatSnapshot(runState, player);
            if (combatSnap != null)
            {
                sb.AppendLine($"HP: {combatSnap.Player.CurrentHp}/{combatSnap.Player.MaxHp}  Block: {combatSnap.Player.Block}  Energy: [color=aqua]{combatSnap.Energy}/{combatSnap.MaxEnergy}[/color]");
                sb.AppendLine($"Draw: {combatSnap.DrawPileCount}  Discard: {combatSnap.DiscardPileCount}");

                // 手牌：从快照数据格式化为 BBCode
                sb.AppendLine("[color=orange]--- Hand ---[/color]");
                for (int i = 0; i < combatSnap.Hand.Count; i++)
                {
                    var card = combatSnap.Hand[i];
                    var ok = card.CanPlay ? "[color=green]√[/color]" : "[color=red]×[/color]";
                    var why = card.CanPlay ? "" : $" [color=gray]({card.UnplayableReason})[/color]";
                    sb.AppendLine($"  [{i}] {ok} {card.Name} 费{card.Cost} {card.Description} [{card.Type}] Target:{card.NeedsTarget}{why}");
                }

                // 敌人：从快照数据格式化为 BBCode（含意图，已在快照中通过 HoverTip.Description 正确获取）
                sb.AppendLine("[color=orange]--- Enemies ---[/color]");
                foreach (var enemy in combatSnap.Enemies)
                {
                    var dead = enemy.IsAlive ? "" : " [color=red]DEAD[/color]";
                    sb.AppendLine($"  [{enemy.CombatId}] {enemy.Name} HP:{enemy.CurrentHp}/{enemy.MaxHp} Block:{enemy.Block}{dead}");
                    sb.AppendLine($"    Intent: {enemy.IntentLabel}");
                }

                // 药水
                sb.AppendLine("[color=orange]--- Potions ---[/color]");
                foreach (var p in combatSnap.Potions)
                {
                    sb.AppendLine($"  [{p.SlotIndex}] {p.Name}");
                }
            }
        }
        else
        {
            sb.AppendLine($"Room: {runState.CurrentRoom?.RoomType}");
        }

        return new CmdResult(true, sb.ToString());
    }

    // ── autospire endturn ─────────────────────────────────────────────

    /// <summary>
    /// 结束当前回合。直接委托给 GameHookServer.ExecuteEndTurn()。
    /// </summary>
    private static CmdResult CmdEndTurn(Player? issuingPlayer)
    {
        var runState = RunManager.Instance.DebugOnlyGetState();
        var player = issuingPlayer ?? LocalContext.GetMe(runState);
        if (player == null)
            return new CmdResult(false, "No local player found.");

        var result = GameHookServer.ExecuteEndTurn(player);
        return result.Success
            ? new CmdResult(true, "End turn enqueued.")
            : new CmdResult(false, result.Error ?? "Unknown error");
    }

    // ── autospire play [n] ────────────────────────────────────────────

    /// <summary>
    /// 打出手牌中第 n 张牌（默认第 0 张）。自动选择合法目标。
    ///
    /// 目标选择规则：
    /// - TargetType.None / Self（防御/技能/能力）：target 为 null（游戏自身逻辑）
    /// - 其余类型（敌方目标）：自动选第一个可攻击的敌人
    ///
    /// 实际执行委托给 GameHookServer.ExecuteAction()。
    /// </summary>
    private static CmdResult CmdPlayCard(Player? issuingPlayer, string[] args)
    {
        // 解析手牌 index
        int cardIndex = 0;
        if (args.Length >= 2 && !int.TryParse(args[1], out cardIndex))
            return new CmdResult(false, $"Invalid hand index: {args[1]}");

        // 构建 ActionRequest 并委托给 GameHookServer
        var request = new ActionRequest(
            Action: "play_card",
            HandIndex: cardIndex,
            TargetCombatId: null, // 不指定目标，让 ExecutePlayCard 自动选择
            SlotIndex: null,
            Col: null,
            Row: null,
            ChoiceIndex: null,
            ChoiceType: null,
            ShopAction: null,
            ItemIndex: null,
            OptionIndex: null,
            CardIndex: null,
            MenuAction: null,
            CharacterIndex: null,
            AscensionLevel: null
        );

        var result = GameHookServer.ExecuteAction(request);
        return result.Success
            ? new CmdResult(true, $"[color=green]Playing card at hand index {cardIndex}[/color]")
            : new CmdResult(false, result.Error ?? "Unknown error");
    }

    // ── autospire pick [n] ────────────────────────────────────────────

    /// <summary>
    /// 在当前选牌子界面中选择第 n 张卡牌（默认第 0 张）。
    /// 通用选牌：奖励选牌、战斗选牌（搜寻/灰水）、休息点升级/移除选牌。
    /// 实际执行委托给 GameHookServer.TryExecuteCardSelection()。
    /// </summary>
    private static CmdResult CmdPickCard(string[] args)
    {
        int cardIndex = 0;
        if (args.Length >= 2 && !int.TryParse(args[1], out cardIndex))
            return new CmdResult(false, $"Invalid card index: {args[1]}");

        var request = new ActionRequest(
            Action: "pick_card",
            HandIndex: null,
            TargetCombatId: null,
            SlotIndex: null,
            Col: null,
            Row: null,
            ChoiceIndex: null,
            ChoiceType: null,
            ShopAction: null,
            ItemIndex: null,
            OptionIndex: null,
            CardIndex: cardIndex,
            MenuAction: null,
            CharacterIndex: null,
            AscensionLevel: null
        );

        var result = GameHookServer.ExecuteAction(request);
        return result.Success
            ? new CmdResult(true, $"[color=green]Picked card at index {cardIndex}[/color]")
            : new CmdResult(false, result.Error ?? "Unknown error");
    }

    // ── autospire confirm ──────────────────────────────────────────────

    /// <summary>
    /// 确认当前手牌选择（净化/丢弃等效果选够牌后按确认）。
    /// 实际执行委托给 GameHookServer.ConfirmHandSelection()。
    /// </summary>
    private static CmdResult CmdConfirm()
    {
        var request = new ActionRequest(
            Action: "confirm_selection",
            HandIndex: null, TargetCombatId: null, SlotIndex: null,
            Col: null, Row: null, ChoiceIndex: null, ChoiceType: null,
            ShopAction: null, ItemIndex: null, OptionIndex: null, CardIndex: null,
            MenuAction: null, CharacterIndex: null, AscensionLevel: null
        );
        var result = GameHookServer.ExecuteAction(request);
        return result.Success
            ? new CmdResult(true, "[color=green]Selection confirmed[/color]")
            : new CmdResult(false, result.Error ?? "Unknown error");
    }

    // ── autospire map ─────────────────────────────────────────────────

    /// <summary>
    /// 打印当前地图节点及可选路线。
    /// Phase 检测委托给 GameHookServer.DetectPhase()。
    /// </summary>
    private static CmdResult CmdMap(Player? issuingPlayer)
    {
        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState?.CurrentMapPoint == null)
            return new CmdResult(false, "Not on a map node.");

        var cur = runState.CurrentMapPoint;
        var sb = new StringBuilder();
        sb.AppendLine($"[color=yellow]Current:[/color] ({cur.coord.col}, {cur.coord.row}) [color=aqua]{cur.PointType}[/color]");
        sb.AppendLine("[color=orange]Available next:[/color]");
        foreach (var child in cur.Children)
        {
            sb.AppendLine($"  ({child.coord.col}, {child.coord.row}) [color=aqua]{child.PointType}[/color]");
        }

        return new CmdResult(true, sb.ToString());
    }
}
