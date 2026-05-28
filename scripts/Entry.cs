using Godot.Bridge;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using autoSpire.scripts.core;

namespace autoSpire.scripts;

/// <summary>
/// autoSpire mod 入口。
///
/// 加载流程：
/// 1. ModManager 加载 mod DLL 后，通过反射找到标记了 [ModInitializer] 的类
/// 2. 调用指定的初始化方法 Init()
/// 3. Init() 中：注册 Harmony patch → 注册自定义 Godot 脚本 → 启动 HTTP 服务
///
/// HTTP 服务启动后，AI 可通过 localhost:8765 与游戏交互：
///   GET  /state   — 获取当前游戏状态快照
///   POST /action  — 提交动作（出牌/结束回合/用药水等）
/// </summary>
[ModInitializer(nameof(Init))]
public class Entry
{
    /// <summary>HTTP 服务实例，生命周期与 mod 一致</summary>
    private static GameHookServer? _server;

    /// <summary>
    /// mod 初始化入口，由 ModManager 在加载 mod 时调用。
    /// 必须是 static 方法，方法名需与 ModInitializer 特性的参数一致。
    /// </summary>
    public static void Init()
    {
        // Harmony patch：用于 Hook 游戏方法（当前未使用，保留以备后续需要）
        // Harmony ID 格式："sts2.author.modId"，避免与其他 mod 冲突
        var harmony = new Harmony("sts2.lightEnding.autoSpire");
        harmony.PatchAll();

        // 注册当前 assembly 中的自定义 Godot 脚本，使得 .tscn 文件可以引用此 mod 的类
        ScriptManagerBridge.LookupScriptsInAssembly(typeof(Entry).Assembly);

        // 启动嵌入式 HTTP 服务，后台线程监听，主线程每帧更新状态并消费动作队列
        _server = new GameHookServer();
        _server.Start();

        Log.Info("autoSpire mod initialized!");
    }
}
