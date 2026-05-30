# autoSpire — AI 自动爬塔

[杀戮尖塔 2](https://store.steampowered.com/app/2868840) 的 AI 自走 mod。嵌入式 HTTP 服务 + MCP 协议，让 Claude Code 驱动游戏决策。

## 架构

```
AI (Claude Code) ←─ MCP stdio ─→ mcp/server.py ←─ HTTP ─→ C# GameHookServer (游戏内)
```

- **C# GameHookServer**：嵌入游戏进程，主线程每帧刷新状态快照 + 消费 HTTP 动作队列
- **Python MCP 适配器**：将 HTTP API 封装为 MCP 工具（`get_state` / `take_action`），纯透传无业务逻辑
- **Godot Mod**：`[ModInitializer]` 入口，注册 Harmony patch + 启动 HTTP 服务

## 快速开始

1. 将 `autoSpire` 放入 SlayTheSpire2 mod 目录
2. 启动游戏，确认日志输出 "autoSpire mod initialized!"
3. Claude Code 通过 `.mcp.json` 自动发现并启动 MCP 适配器
4. 对话中直接调用 `get_state` / `take_action`

## API

### GET `/state` — 游戏状态快照

返回 `GameStateSnapshot` JSON。根据 `phase` 字段，仅当前场景对应子快照非 null：

| Phase | 字段 | 内容 |
|-------|------|------|
| `combat` | `combat` | 手牌/能量/敌人(意图+buff)/药水/牌堆计数/卡牌选牌状态 |
| `map` | `map` | 当前节点 + 下一层可选 + 全地图节点 |
| `shop` | `shop` | 金币 + 商品列表（待完善） |
| `reward` | `reward` | 奖励项列表 + 可选卡牌/遗物 |
| `rest` | `rest` | 休息选项 + 升级/移除选牌界面 |
| `event` | `event` | 事件名称/描述/选项（待完善） |

所有阶段均有 `run` 字段：章节/层数/金币/牌组。

### POST `/action` — 提交动作

返回 `ActionResult` JSON：

```json
{
  "success": true,
  "error": null,
  "played_card_name": "打击",          // 仅 play_card
  "played_card_target": {              // 仅 play_card
    "type": "enemy",                   // enemy / self / none
    "combat_id": 2,
    "name": "小啃兽"
  },
  "hand_card_selection": { ... },      // 仅 pick_card / confirm_selection
  "card_selection": { ... }            // 仅 pick_card / confirm_selection
}
```

| 动作 | 必填参数 | 可选参数 | 说明 |
|------|----------|----------|------|
| `play_card` | `hand_index` | `target_id` | 出牌。返回 `played_card_name` + `played_card_target` |
| `end_turn` | — | — | 结束回合 |
| `use_potion` | `slot_index` | `target_id` | 使用药水 |
| `pick_card` | `card_index` | — | 通用选牌（手牌选择 / overlay 选牌）。返回最新选牌状态 |
| `confirm_selection` | — | — | 确认手牌选择（净化等多选手动确认） |
| `move_to_map_coord` | `col`, `row` | — | 选路线 |
| `pick_reward` | `choice_index`, `choice_type` | `card_index` | 选奖励（card/relic/gold/skip）；有 CardSelection 子界面时传 card_index |
| `rest_action` | `option_index` | `card_index` | 休息点操作 |
| `shop_action` | — | — | ⚠️ 未实现 |
| `event_action` | — | — | ⚠️ 未实现 |

## 功能状态

### 已完成 & 已测试（主客机双端）

- 战斗状态获取：手牌(含 Damage/Block/描述)/能量/敌人(HP+意图+buff)/药水(含描述)/牌堆计数/选牌状态
- 出牌：有目标(敌方格)、自目标(技能/能力)、卡牌选牌触发(净化/头槌/洁净)
- 用药水：自目标 + 敌方目标，药水选牌触发(灰水)
- 选牌：Overlay 选牌/手牌选择模式/奖励选牌/商店删牌选牌，描述含正确数值
- 选牌状态即时返回：`pick_card` 后 ActionResult 含更新后牌列表
- 出牌返回值：`played_card_name` + `played_card_target`（含目标类型/ID/名称）
- 地图状态 + 选路线(VoteForMapCoordAction)
- 奖励选择：card/gold/relic/potion/skip
- 休息点：回复/锻造/移除 + 升级/移除选牌界面
- **商店全链路**：商品列表（角色卡/无色卡/遗物/药水/删牌含价格库存）+ buy / remove_card / leave（自动开/关界面）
- **事件全链路**：事件描述 + 选项（含关联卡牌/遗物预览）+ 单人/合作多人投票 + 事件中选牌
- **宝箱房间**：宝箱开启 + 遗物选择（单人/多人投票，自动分配）
- Run 级别信息：遗物(名称+描述)、牌组(合并计数)
- JSON 输出优化：省略所有 null 字段，显著减少 token 消耗

### 待完成

- **游戏外功能** — 主菜单 / 开始游戏 / 多人联机创建 & 加入

## 多端支持

同一份 MCP 适配器通过 `AUTOSPIRE_PORT` 环境变量区分：

- `autospire`（8765）— 主机
- `autospire2`（8766）— 客机

若端口被占用，GameHookServer 自动递增（最多尝试 10 个端口）。

## 已知限制

- **出牌异步**：`play_card` 通过 `ActionQueueSynchronizer.RequestEnqueue` 入队，卡牌选牌触发在后续帧处理，AI 需随后 `get_state` 检查
- **卡牌选牌触发后手牌索引变化**：如净化每选一张牌后手牌索引重排，需依赖 `pick_card` 返回值中的最新索引
- **卡牌描述**：`:energyIcons` / `:starIcons` 等特殊 formatter 输出 `[img]` 标签被正则清除，不影响 AI 语义理解
- **事件选项**：部分动态变量（如 `{BatheCurses}`）在选项描述中可能未注入，`SafeFormat` 回退到原始文本
