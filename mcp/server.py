"""
autoSpire MCP 适配器。

架构：
  AI (Claude Code) ←─ stdio MCP ─→ mcp/server.py ←─ HTTP ─→ C# GameHookServer

职责：
  1. 通过 MCP 协议向 AI 暴露工具：get_state（获取游戏状态）、take_action（执行动作）
  2. 将 MCP 工具调用翻译为对 GameHookServer HTTP API 的 GET/POST 请求
  3. 解析 JSON 响应并返回给 AI

启动方式：
  Claude Code 在 mcp.json 中配置此脚本，由 Claude Code 作为子进程启动。
  python mcp/server.py
"""

import json
import os
import asyncio
from urllib.parse import urljoin

import httpx
from mcp.server import Server
from mcp import stdio_server
from mcp.types import Tool, TextContent

# ── 配置 ──────────────────────────────────────────────────────────────

_port = os.environ.get("AUTOSPIRE_PORT", "8765")
GAME_URL = f"http://localhost:{_port}"
HTTP_TIMEOUT = 10.0  # 动作请求超时（秒），主线程每帧才消费队列，可能需要等几帧

# ── HTTP 客户端 ────────────────────────────────────────────────────────

# 复用一个 httpx AsyncClient，避免每次请求都重新建立连接
_client: httpx.AsyncClient | None = None


async def get_client() -> httpx.AsyncClient:
    """获取或创建共享的 HTTP 客户端（懒初始化，确保在 asyncio event loop 内创建）。"""
    global _client
    if _client is None:
        _client = httpx.AsyncClient(timeout=HTTP_TIMEOUT)
    return _client


# ── MCP Server 定义 ────────────────────────────────────────────────────

app = Server("autospire")


@app.list_tools()
async def list_tools() -> list[Tool]:
    """
    向 AI 声明可用的工具列表。

    get_state:
      获取当前游戏状态的完整快照。
      AI 应在每次决策前调用此工具。
      返回 JSON 包含 Phase（阶段）、Combat（战斗状态）、Map（地图）等。

    take_action:
      向游戏提交一个动作。
      action 取值及所需参数：
        - end_turn        → 结束回合（无额外参数）
        - play_card       → 出牌，需 hand_index，可选 target_id
        - use_potion      → 用药水，需 slot_index，可选 target_id
        - move_to_map_coord → 选路线，需 col, row
        - pick_reward     → 选奖励，需 choice_index + choice_type
        - shop_action     → 商店操作，需 shop_action 子类型 + 相关参数
        - rest_action     → 休息点，需 option_index
        - event_action    → 事件选项，需 option_index
        - pick_card       → 选牌，需 card_index（战斗中/奖励/休息点选牌子界面通用）
        - confirm_selection → 确认手牌选择（净化/丢弃等多选手动确认时使用）
    """
    return [
        Tool(
            name="get_state",
            description="获取当前游戏状态快照。返回当前阶段（combat/map/shop/reward/rest/event）、战斗详情（手牌/敌人/能量/药水）、地图可选路线等信息。AI 应在每次决策前调用。",
            inputSchema={
                "type": "object",
                "properties": {},
                "required": [],
            },
        ),
        Tool(
            name="take_action",
            description="向游戏提交一个动作。根据 action 类型填写相应参数。可用动作：end_turn（结束回合）、play_card（出牌）、use_potion（用药水）、move_to_map_coord（选路线）、pick_reward（选奖励）、pick_card（选牌，战斗中/奖励/休息点通用）、confirm_selection（确认手牌选择）、shop_action（商店操作）、rest_action（休息）、event_action（事件选项）。",
            inputSchema={
                "type": "object",
                "properties": {
                    "action": {
                        "type": "string",
                        "description": "动作类型：end_turn / play_card / use_potion / move_to_map_coord / pick_reward / shop_action / rest_action / event_action",
                        "enum": ["end_turn", "play_card", "use_potion", "move_to_map_coord", "pick_reward", "pick_card", "confirm_selection", "shop_action", "rest_action", "event_action"],
                    },
                    "hand_index": {
                        "type": "integer",
                        "description": "手牌 index（0-based），play_card 时使用",
                    },
                    "target_id": {
                        "type": "integer",
                        "description": "目标敌方 CombatId，play_card / use_potion 时可选",
                    },
                    "slot_index": {
                        "type": "integer",
                        "description": "药水槽位 index，use_potion 时使用",
                    },
                    "col": {
                        "type": "integer",
                        "description": "目标节点列坐标，move_to_map_coord 时使用",
                    },
                    "row": {
                        "type": "integer",
                        "description": "目标节点行坐标，move_to_map_coord 时使用",
                    },
                    "choice_index": {
                        "type": "integer",
                        "description": "选项 index，pick_reward 时使用",
                    },
                    "choice_type": {
                        "type": "string",
                        "description": "选择类型：card / relic / gold / skip，pick_reward 时使用",
                    },
                    "shop_action": {
                        "type": "string",
                        "description": "商店操作：buy_card / buy_relic / buy_potion / remove_card / leave",
                    },
                    "item_index": {
                        "type": "integer",
                        "description": "商品 index，shop_action 时使用",
                    },
                    "option_index": {
                        "type": "integer",
                        "description": "选项 index，rest_action / event_action 时使用",
                    },
                    "card_index": {
                        "type": "integer",
                        "description": "选牌子界面中的卡牌 index（0-based）。pick_card 时使用；pick_reward 时若 CardSelection 非 null 也需提供",
                    },
                },
                "required": ["action"],
            },
        ),
    ]


@app.call_tool()
async def call_tool(name: str, arguments: dict) -> list[TextContent]:
    """
    处理 AI 的工具调用。

    根据 tool name 分发：
      - get_state → GET /state → 返回 JSON
      - take_action → POST /action → 返回执行结果

    HTTP 请求失败时返回错误信息而非抛异常，让 AI 能够理解并重试。
    """
    client = await get_client()

    try:
        if name == "get_state":
            return await handle_get_state(client)
        elif name == "take_action":
            return await handle_take_action(client, arguments)
        else:
            return [TextContent(type="text", text=f"Unknown tool: {name}")]
    except httpx.RequestError as e:
        # 网络错误（游戏未运行 / 连接被拒）：返回明确提示而非崩溃
        return [TextContent(type="text", text=f"HTTP error: {e}. Is the game running?")]


# ── 工具处理函数 ──────────────────────────────────────────────────────


async def handle_get_state(client: httpx.AsyncClient) -> list[TextContent]:
    """
    从 GameHookServer 获取当前游戏状态。

    GET /state 返回 JSON：
      - phase: 当前阶段（combat/map/shop/reward/rest/event/loading）
      - waiting_for_input: 是否等待玩家输入
      - combat/map/shop/reward/rest/event: 各阶段对应快照（仅当前阶段非 null）
      - run: 全局 run 信息（章节/层数/金币/牌组）
    """
    url = urljoin(GAME_URL, "/state")
    resp = await client.get(url)
    resp.raise_for_status()
    state = resp.json()
    # 格式化输出，方便 AI 阅读
    return [TextContent(type="text", text=json.dumps(state, ensure_ascii=False, indent=2))]


async def handle_take_action(client: httpx.AsyncClient, args: dict) -> list[TextContent]:
    """
    向 GameHookServer 提交动作。

    将 MCP 参数组装为 ActionRequest JSON，POST /action。
    服务端返回 ActionResult（success + 可选 error 消息）。
    """
    body = {
        "action": args["action"],
        "hand_index": args.get("hand_index"),
        "target_id": args.get("target_id"),
        "slot_index": args.get("slot_index"),
        "col": args.get("col"),
        "row": args.get("row"),
        "choice_index": args.get("choice_index"),
        "choice_type": args.get("choice_type"),
        "shop_action": args.get("shop_action"),
        "item_index": args.get("item_index"),
        "option_index": args.get("option_index"),
        "card_index": args.get("card_index"),
    }
    # 移除值为 None 的字段，减小 JSON 体积
    body = {k: v for k, v in body.items() if v is not None}

    url = urljoin(GAME_URL, "/action")
    resp = await client.post(url, json=body)
    result = resp.json()
    return [TextContent(type="text", text=json.dumps(result, ensure_ascii=False))]


# ── 入口 ───────────────────────────────────────────────────────────────


async def main():
    """
    MCP stdio 服务器主循环。

    通过 stdin/stdout 与 Claude Code 通信。
    服务器生命周期完全由 Claude Code 管理：启动子进程 → 初始化 → 调用工具 → 关闭。
    """
    async with stdio_server() as (read_stream, write_stream):
        await app.run(read_stream, write_stream, app.create_initialization_options())


if __name__ == "__main__":
    asyncio.run(main())
