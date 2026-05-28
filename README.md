# autoSpire — AI 自动爬塔

[杀戮尖塔 2](https://store.steampowered.com/app/2868840) 的 AI 自走 mod。通过嵌入式 HTTP 服务暴露游戏状态，配合 [Claude Code](https://claude.ai/code) MCP 协议实现 AI 决策驱动游戏。

## 架构

```
AI (Claude Code) ←─ MCP (stdio) ─→ mcp/server.py ←─ HTTP ─→ C# GameHookServer (游戏内)
```

- **C# GameHookServer**：嵌入游戏进程的 HTTP 服务，主线程每帧刷新状态快照，后台线程处理 HTTP 请求
- **Python MCP 适配器**：将 HTTP API 封装为 Claude Code 可调用的 MCP 工具（`get_state` / `take_action`）
- **Godot Mod**：通过 `[ModInitializer]` 入口加载，注册 Harmony patch + 启动 HTTP 服务

## 快速开始

1. 将 `autoSpire` 文件夹放入 SlayTheSpire2 的 mod 目录
2. 启动游戏，确认 mod 已加载（日志输出 "autoSpire mod initialized!"）
3. 配置 Claude Code 的 MCP（见 `.mcp.json`）
4. 在 Claude Code 中调用 `get_state` 和 `take_action` 开始 AI 自走

## API

### GET `/state` — 获取游戏状态快照

返回 `GameStateSnapshot` JSON，根据当前阶段包含对应子快照：

| 阶段 | 对应字段 | 说明 |
|------|----------|------|
| `combat` | `Combat` | 手牌、能量、敌人（含意图/buff）、药水、选牌状态 |
| `map` | `Map` | 当前节点、可选下一层、全地图节点 |
| `shop` | `Shop` | 金币、出售卡牌/遗物/药水 |
| `reward` | `Reward` | 奖励项列表、可选卡牌/遗物 |
| `rest` | `Rest` | 休息选项、升级选牌、目标选择 |
| `event` | `Event` | 事件名称、描述、选项 |

### POST `/action` — 提交动作

| 动作 | 参数 | 说明 |
|------|------|------|
| `play_card` | `hand_index`, `target_id?` | 出牌，返回打出卡牌 ID/名称 + 更新后战斗状态 |
| `end_turn` | — | 结束回合 |
| `use_potion` | `slot_index`, `target_id?` | 使用药水 |
| `pick_card` | `card_index` | 通用选牌（手牌选择 / overlay 选牌） |
| `confirm_selection` | — | 确认手牌选择（净化等多选手动确认） |
| `move_to_map_coord` | `col`, `row` | 选路线 |
| `pick_reward` | `choice_index`, `choice_type` | 选奖励（card/relic/gold/skip） |
| `rest_action` | `option_index` | 休息点操作 |
| `shop_action` | `shop_action`, `item_index` | 商店操作 |
| `event_action` | `option_index` | 事件选项 |

## 功能实现状态

### 已完成 & 已测试

- [x] 战斗状态获取 — 手牌、能量、敌人（意图/buff）、药水、牌堆计数
- [x] 出牌（有目标 / 无目标）— 主客机双端
- [x] 使用药水（自目标 / 敌方目标）— 主客机双端
- [x] Overlay 选牌 — 攻击药水、头槌（弃牌堆）、洁净（抽牌堆）、奖励选牌
- [x] 手牌选择模式 — 净化（消耗手牌，pick + confirm）
- [x] 选牌状态即时返回 — pick_card 后 ActionResult 含更新后索引
- [x] 选牌 Prompt 文本提取
- [x] Action 返回值增强 — play_card 返回打出卡牌 + 更新后 Combat 快照
- [x] 地图状态获取 — 当前节点 + 可选下一层 + 全地图节点
- [x] 结束回合
- [x] 奖励选择 — card/gold/relic/potion/skip
- [x] 休息点操作 — 回复/锻造/移除，含选牌子界面
- [x] 选路线 — VoteForMapCoordAction 兼容多人

### 待完成

- [ ] **商店功能** — `shop_action` 暂无 C# 实现，`ShopSnapshot` 商品列表为空
- [ ] **事件功能** — `event_action` 暂无 C# 实现，`EventSnapshot` 为骨架
- [ ] **卡牌数值** — `Damage` / `Block` 始终为 null（需从 ValueProp 系统读取）
- [ ] **卡牌描述** — 战斗中卡牌描述字段为空（避免本地化动态文本报错）

## 多端支持

同一份 MCP 适配器通过环境变量 `AUTOSPIRE_PORT` 区分端口，支持多人联机主机和客机：

- `autospire`（默认端口 8765）— 主机
- `autospire2`（端口 8766）— 客机
