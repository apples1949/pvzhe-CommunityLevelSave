using System;
using System.Collections.Generic;
using Godot;
using PVZHE.ModEditor.ModSystem;

/// <summary>
/// 「每日挑战存档」Mod v3 —— 解除每日挑战的自动存档限制（进关即恢复）。
///
/// ── 限制的真相（unpack 实锤）──────────────────────────────────────
/// 游戏自带完整关卡进度管线：SaveLevelProgress() → TowerDefenseLevelSaveConfigCSharp.Save()
/// → 原子写 user://Csharp/Progress/&lt;用户&gt;/&lt;关卡名&gt;.tres；
/// 进关时 GameInitEntered 读 hasProgress → TryGetLoadableLevelProgress →
/// 组件走 GameInitFromProgress → save.Load() 全量还原。
/// 但 CanUseLevelProgressInCurrentMode() 白名单排除 DailyLevel ⇒ 存/恢复都被跳过。
///
/// ── v3 方案：写原生路径 + 设 hasProgress，整条恢复链全部走游戏原生代码 ──
/// v2.1 的滑动保存已解决「保存」；「恢复」失败根因是时机——在 isGameRunning 后
/// 调 Load() 时各组件已按空场初始化完毕，快照塞不进去。
/// v3 发现：恢复链全部由 hasProgress 一个 public 字段驱动（TowerDefenseControl.hasProgress），
/// 且存/读路径 = user://Csharp/Progress/&lt;用户&gt;/&lt;关卡名&gt;.tres（按关卡名隔离）。
/// ⇒ 滑动保存**直接写原生路径**（不做 DailySave_ 前缀），进关后在控制器出现的最早一帧
///   设 ctl.hasProgress = true ⇒ 游戏自己走 GameInitFromProgress 全套原生恢复
///   （含 TryGetLoadableLevelProgress 校验/备份/RejectProgressLoad 弹窗）。
/// DailyLevel-&lt;日期&gt;-&lt;ID&gt; 关卡名只有每日挑战用，与正常存档零冲突。
///
/// ── 时序要点 ─────────────────────────────────────────────────────
/// 控制器 _Ready → … → SendStateEvent → GameInitEntered（读 hasProgress）。
/// currentControl 赋值发生在控制器初始化早期（TowerDefenseManager），本 Mod 每帧扫：
///   发现「新控制器 + 是每日挑战关卡名」⇒ 立刻设 hasProgress=true（在状态机 SendStateEvent
///   之前来得及：_Ready 里 CallDeferred(SendStateEvent)，至少有一帧空档）。
///
/// ── v3.2 修复（2026-10-27）：消除「无法恢复关卡进度 / 关卡进度文件不存在」弹窗 ──
/// ⚠ 那条弹窗**不是本 Mod 产生的**（本文件里没有这条文案，grep 也搜不到）：
///   host 侧 TowerDefenseNew/TowerDefenseControlNew.cs:1376 拿 hasProgress 去读档，
///   读不到就 RejectProgressLoad()（同文件 1420-1442）弹「无法恢复关卡进度，原存档已保留。」
///   文案来自 Core/GameSaveManager/GameSaveManager.cs:1479「关卡进度文件不存在。」
///   与 1605 的 " 备份：" 拼接（主档 + .pre-relaxed 备份都读不到就是
///   「关卡进度文件不存在。 备份：关卡进度文件不存在。」——和玩家截图逐字一致）。
/// ⇒ 结论：**hasProgress 一旦被置 true 而宿主读不到档，就必然弹窗并把玩家踢回主菜单**
///   （同文件 1422 isGameRunning=false、1452 ChangeScene）。宿主没有任何"模组可撤销"的钩子。
///
/// v3.1 及以前的武装条件只有一个**瞬时的 FileAccess.FileExists**（HasSave），而宿主的真正
/// 读取发生在异步 GameInitEntered 里（前面还有 await EnsureGameplayAtlasesReadyAsync），
/// 两边隔着若干帧；同时"路径口径"与宿主不一致（宿主 _safeCharRegex=[^\w\-\.]，
/// 旧实现是 [^\w-]：用户名含 . 时本 Mod 写进去的目录宿主根本不会去看），
/// 而且完全不校验内容（文件在但坏了 ⇒ 照样武装 ⇒ 照样弹窗）。
///
/// v3.2 改为：**只有"宿主自己的读取器确认可读"时才武装**（TryArmRestore 先用
/// GameSaveManager.TryGetLoadableLevelProgress 预演一次），并把三种"没有可用档"的情况
/// ——全新进入 / 已被死亡通关清掉 / 跨天过期——全部静默按正常新局处理；
/// 只有"文件在但内容损坏/反序列化失败"才提示，且文案写明具体文件与原因。
///
/// ── v3.3.0 修复（2026-10-28）：玩家反馈「无法读取自动保存的存档」──
/// 现场证据（玩家 godot.log + 本机 user:// 目录状态）：
///   · 日志有 4 条 `[Save] 保存Feature[Map]...`（unpack Registry/Battle/Feature/Map/
///     TowerDefenseBattleFeatureMap.cs:198，**只可能由 TowerDefenseLevelSaveConfigCSharp.Save()
///     触发**，而宿主那条路已被 `CanUseLevelProgressInCurrentMode()` 拒掉并打印
///     `Skip level progress save: external level mode DailyLevel.`）
///     ⇒ 这 4 次快照**都是本 Mod 抓的**，即：
///       `CanCreateProgressSave()` 已通过、`snapshot.Save()` 已真正跑过 4 次。
///   · 但 `user://Csharp/Progress/2/` 的**目录 mtime 停在 2026-10-14 00:31**，
///     且**没有任何当天的 DailyLevel-*.tres**（写文件/删文件都会刷新目录 mtime）
///     ⇒ **快照在内存里抓成功了，但一次都没落盘**。
///   · 落盘失败为什么没有任何痕迹？因为 v3.2 的 Log/Warn 被写死成 `if (true) return;`，
///     而 `ResourceSaver.Save` 失败 / `File.Move` 抛异常 / `snapshot.Save()` 抛异常
///     三条错误分支**全部只走 Warn** ⇒ 全被静默吞掉。
///   ⇒ 结论：真凶在**写档端**（不是读档端）：写路径失败且不可观测。
///
/// v3.3.0 的四项改造（对应任务 A/B/C/D）：
///   A 写档端：Move 前把旧档复制成 `.pre-relaxed.tres`（宿主语义：仅当备份缺失时生成，
///     见 GameSaveManager.EnsureProgressBackup:1531-1542）；写后**用宿主同款读取器回读自检**
///     （ResourceLoader.Load(path,"",CacheMode.Ignore) + CanLoad）；自检失败先重试一次
///     「直接写最终路径」，仍失败则用备份把旧档放回去（不覆盖好档）+ 打 Error 日志。
///     ★ uid 结论（任务 A.4，已实测查证，见 TrySaveByName 内联注释）：**不需要改成直写**。
///   B 读档端：`TryGetLoadableLevelProgress` 的失败按 reason 分类——暂时性（「不可用」
///     「未就绪」等）**不判损坏、不弹窗**，在 ~120 帧窗口内每 15 帧重试；内容类
///     （「无法读取关卡进度资源。」「读取关卡进度失败」）才提示；文件不存在=静默跳过。
///     重试期间若 `ctl.isGameRunning` 变 true ⇒ 立刻停手并 Warn「错过武装窗口」。
///   C 稳健性：路径派生后校验"文件名 == 当前关卡名"并打日志；新增 `strictRestore`
///     保守开关（默认 false）：为 true 时恢复前用 `XWModEnvironmentService.CheckSnapshot`
///     比对档内 `modSnapshot` 与当前 Mod 环境，不一致就静默新局（避免 Load() 半途失败被踢）。
///   D 可观测性：进入关卡 / 预演结果 / 写档 / 回读自检 / 最终决策 这几行**不受 enableLog
///     门控**，无条件以 `[DailySave]` 前缀输出（Diag / DiagErr）；其余细节仍受 enableLog。
///
/// ── v3.4.0 修复（2026-10-28）：恢复失败**不自毁** ──
/// 现场（玩家 v3.3.0 日志）：写档端已彻底打通（`save ok size=173888/173901/192464` +
/// `verify ok` + `backup ok … .pre-relaxed.tres`），但**第 2 次进入恢复失败**
/// （弹窗 + 被踢回主菜单），之后连读都读不了。⇒ 问题在**读档/恢复**，且是"连环自毁"：
///
///   ① 【最可能】恢复后立刻被"半成品"覆盖：v3.3.0 在恢复成功的同一个 GameRunning 里，
///      3 秒（180 帧）后就把**刚初始化、组件尚未全部走完 GameInitFromProgress**的战场
///      快照写回主档。宿主 `Load()` 是"边恢复边初始化"，恢复到 `isGameRunning` 为 true
///      时战场往往还没稳定（暂存角色/掉落物/进程状态仍在补齐）⇒ 主档被半成品覆盖
///      ⇒ 下次读进来就是"能反序列化但恢复时抛异常"，然后再次覆盖……**逐次恶化**。
///   ② `PrepareProgressRecovery` 每次把 `AwaitingDecision` 置 true（:1595-1600，字段默认
///      true 见 :29），而 `GameInitEntered` 那条路径**从不 AcceptProgressRecovery**
///      （只有 `LoadLevelProgress()`:1249 会 Accept）⇒ 恢复过一次之后
///      `CanWriteRecoveredProgress()`:1544-1561 恒 false、`DeleteLevelProgress()`:1287-1290
///      直接 return。宿主的"备份保护"因此永久失效，坏档永远不会被自动隔离。
///   ③ 长路径：该玩家用户名脱敏后 >100 字符，`user://Csharp/Progress/<长目录>/DailyLevel-…tres`
///      逼近 Windows MAX_PATH(260)；`DirAccess.MakeDirRecursiveAbsolute` 与手写的
///      `System.IO.File.Move/Copy` 最容易踩（宿主内部的 FileAccess 走 Godot 自己的封装，
///      与 .NET 的 MAX_PATH 行为不同）⇒ 可能出现"写成功但读不到"或"备份复制失败"。
///
/// v3.4.0 的四项改造（对应任务 A/B/C/D）：
///   A 恢复后保护期：arm 成功起 **480 帧（≈8 秒）内不写主档**，且必须"真的开跑 + 已稳定
///     N 个 tick"；期间只写 `*.recover.tres` 旁路（不参与恢复），日志 `save hold(recover grace)`。
///   B verified 副本：每次自检通过保存后同步一份 `*.verified.tres`（只保留最近一次好档）；
///     arm 前若主档预演失败/恢复报错，**自动把 verified 复制回主档**再重试一次。
///     恢复失败**绝不删档**（代码里显式断言并打日志）。
///   C 长路径加固：所有文件 I/O 统一走 `IoPath()`（Globalize + Windows `\\?\` 前缀）与
///     `IoMove/IoCopy/IoDelete/IoExists`，每次都记录路径长度，失败标 `stage=longpath`。
///   D 可观测性：arm 后按 30 帧节流记录恢复过程快照；判定"恢复失败（场景退回菜单）"时
///     无条件打 `restore failed (scene changed back)` 并把主档另存 `*.failed-<时间戳>.tres` 取证。
///     ★ 排障口径已写进 mod.json changelog：**看 godot.log 里 `[ProgressLoad]` 开头的行**
///     （宿主 `RejectProgressLoad` 于 TowerDefenseControlNew.cs:1424 打的，含真正原因），
///     以及宿主 `ProgressRestoreReport.Record()` 打的 `[ProgressLoad:<类别>] <细节>` 行。
/// </summary>
public sealed class CommunityLevelSaveEntry : IXWModRuntimeEntry
{
	// 赞助码注入器（SponsorQrInjector.cs 共享组件）用的包根缓存
	internal static string SponsorQrRoot = null;

	private const string P = "[DailySave] ";
	private const string KEnabled = "enabled";
	private const string KLog = "enableLog";
	private const string KAutoSave = "autoSaveOnExit";
	private const string KNotify = "showNotify";

	private XWModRuntimeContext _context;
	private XWModSettings _settings;
	private bool _subscribed;
	private SceneTree _tree;
	private Callable _tick;
	private bool _started;

	// 状态机
	private bool _wasInDailyBattle;      // 上一帧是否处于「每日挑战战斗中」
	private string _dailyLevelName = ""; // 当前每日挑战关卡名
	private int _saveTimer;              // 周期保存计时器（帧）
	/// <summary>周期保存间隔（帧）≈ 3 秒 @60fps。</summary>
	private const int SaveIntervalFrames = 180;

	// ── 恢复武装（arm）状态机（v3.3.0：由单个 ulong 改成"控制器 id + 显式阶段"）──
	// 时序假设（务必遵守）：**只在 `!ctl.isGameRunning` 时武装**。宿主在
	// GameInitEntered(:1376) 读 hasProgress，而 isGameRunning 在 GameRunningEntered(:1600)
	// 才置 true ⇒ 只要还没开跑，就还在"可武装窗口"内。一旦 isGameRunning 变 true，
	// 再改 hasProgress 也已经来不及（本次不恢复，但存档保留）。
	private ulong _armControlId;         // 已做过决策的控制器 instanceId（防重复决策）
	private string _armLevelName = "";   // 该控制器对应的关卡名
	private bool _armDecided;            // 是否已有最终结论（arm 或 skip），有则不再动
	private int _armRetryFrames;         // 暂时性失败的重试预算（帧）
	private int _armRetryCooldown;       // 距离下次重试的帧数
	/// <summary>暂时性失败的重试总窗口（帧）≈ 2 秒 @60fps。</summary>
	private const int ArmRetryWindowFrames = 120;
	/// <summary>两次重试之间的间隔（帧）≈ 0.25 秒 @60fps。</summary>
	private const int ArmRetryIntervalFrames = 15;

	// ── v3.4.0：恢复后保护期（A）──
	// 目标：arm 成功之后**绝不**用"刚初始化、尚未稳定"的战场去覆盖主档。
	private bool _restoreGraceActive;        // 是否处于恢复保护期
	private int _restoreGraceLeft;           // 保护期剩余帧
	private int _restoreStableTicks;         // 保护期内"看起来稳定"的帧计数
	/// <summary>恢复保护期总长度（帧）≈ 8 秒 @60fps（任务 A 建议值）。</summary>
	private const int RestoreGraceFrames = 480;
	/// <summary>保护期至少要观察到的稳定 tick 数（≈1 秒）后，才认为战场已稳定。</summary>
	private const int RestoreStableTicksNeeded = 60;

	// ── v3.4.0：恢复失败判定（D）──
	private bool _restoreWatched;            // 已 arm，正在观察恢复结果
	private int _restoreWatchFrames;         // 观察窗口剩余帧
	private string _restoreWatchScene = "";  // arm 时的场景名（用于判断"退回菜单"）
	private int _restoreSnapCooldown;        // 恢复过程快照节流
		/// <summary>恢复观察窗口（帧）≈ 2 秒 @60fps（任务 D 建议值）。</summary>
	private const int RestoreWatchFrames = 120;
	/// <summary>恢复过程快照的节流间隔（帧）。</summary>
	private const int RestoreSnapshotIntervalFrames = 30;
	/// <summary>
	/// 观察窗口最多延长几次（每次再等 RestoreWatchFrames 帧）。
	/// 为什么需要：宿主 GameInitEntered 先 await EnsureGameplayAtlasesReadyAsync，
	/// 玩家日志里 `fullWallMs≈5 秒` ⇒ 2 秒窗口在慢机器上必然不够，不能据此判失败。
	/// </summary>
	private const int RestoreWatchExtendMax = 8;
	private int _restoreWatchExtend;

	// ── v3.4.0：verified 回退尝试过没有（每控制器只试一次，避免死循环）──
	private bool _verifiedFallbackTried;

	/// <summary>武装决策结果（v3.3.0：把"损坏"与"暂时性失败"区分开）。</summary>
	private enum ArmDecision
	{
		/// <summary>宿主读取器确认可读 ⇒ 可以设 hasProgress。</summary>
		Arm,
		/// <summary>没有可用档（全新进入 / 已清档 / 跨天过期 / 文件名不符）⇒ 静默新局。</summary>
		SkipSilent,
		/// <summary>档在但内容明确损坏 ⇒ 提示一次，静默新局。</summary>
		SkipCorrupt,
		/// <summary>暂时性失败（控制器/用户尚未就绪等）⇒ 稍后重试，不判定损坏。</summary>
		RetryLater
	}

	/// <summary>失败原因分类（决定"重试"还是"报损坏"）。</summary>
	private enum ReasonKind
	{
		Transient,
		Content,
		NotFound,
		Unknown
	}

	public void Initialize(XWModRuntimeContext context)
	{
		SponsorQrRoot = context.PackageRoot;
		try
		{
			_context = context;
			try { _settings = context.Settings; }
			catch (Exception ex) { Warn("配置不可用：" + ex.GetBaseException().Message); }
			Log("初始化完成（v3.5.0：日期校验改为逐位解析+手写闰年，不再调用任何带格式串的日期解析 API；写后回读自检 + .pre-relaxed/.verified 备份；读档区分暂时性失败并重试；关键路径无条件日志）。");
			Diag("mod loaded v3.5.0（date-parse: digit-wise, no BCL format parsing）");
		}
		catch (Exception ex) { try { GD.PrintErr(P + "Initialize 异常（已吞）：" + ex.Message); } catch { } }
	}

	public void OnAllModsLoaded()
	{
		try
		{
			if (_started) { return; }
			_tree = Engine.GetMainLoop() as SceneTree;
			if (_tree == null) { Warn("拿不到 SceneTree，本 Mod 不生效。"); DiagErr("skip: no SceneTree"); return; }
			_tick = Callable.From(new Action(OnFrame));
			_tree.Connect("process_frame", _tick);
			Subscribe();
			_started = true;
			Log("已挂载 process_frame。");
			Diag("process_frame attached");
		}
		catch (Exception ex) { try { GD.PrintErr(P + "OnAllModsLoaded 异常（已吞）：" + ex.Message); } catch { } }
	}

	public void Shutdown()
	{
		try
		{
			Unsubscribe();
			if (_started && _tree != null && GodotObject.IsInstanceValid(_tree))
			{
				_tree.Disconnect("process_frame", _tick);
			}
			if (_wasInDailyBattle && ReadBool(KAutoSave, true)) { TrySaveCurrent(); }
		}
		catch (Exception ex) { try { GD.PrintErr(P + "Shutdown 异常（已吞）：" + ex.Message); } catch { } }
		finally { _started = false; Diag("shutdown"); }
	}

	private void Subscribe()
	{
		try
		{
			if (_settings == null || _subscribed) { return; }
			_settings.Changed += OnSettingsChanged;
			_subscribed = true;
		}
		catch (Exception ex) { Warn("订阅配置失败：" + ex.GetBaseException().Message); }
	}

	private void Unsubscribe()
	{
		try
		{
			if (_settings != null && _subscribed)
			{
				_settings.Changed -= OnSettingsChanged;
				_subscribed = false;
			}
		}
		catch { }
	}

	private void OnSettingsChanged(IReadOnlyList<string> keys)
	{
		try { Log("配置已更新：" + string.Join("、", keys)); } catch { }
	}

	// ============================================================ 每帧状态机

	private void OnFrame()
	{
		try { SponsorQrInjector.Tick(Engine.GetMainLoop() as SceneTree, "每日挑战存档", SponsorQrRoot, "Resources/SponsorQR.png"); } catch { }
		TickNotify();   // ★ v3.1 修复：提示文字倒计时（此前未调用 ⇒ 提示永不消失）
		try
		{
			if (!ReadBool(KEnabled, true)) { _wasInDailyBattle = false; return; }

			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			TowerDefenseControlNew ctl = (mgr != null && GodotObject.IsInstanceValid(mgr)) ? mgr.currentControl : null;

			if (ctl == null || !GodotObject.IsInstanceValid(ctl))
			{
				LeaveDaily();
				return;
			}

			bool inDaily = IsDailyLevel(ctl.levelConfig?.name);
			if (!inDaily)
			{
				// 非每日挑战关卡（或已离开）——通关/失败清档逻辑在离开瞬间处理
				if (_wasInDailyBattle)
				{
					string name = _dailyLevelName;
					LeaveDaily();
					if (!string.IsNullOrEmpty(name) && !HasSaveKeepDecision(ctl))
					{
						// 离开到非每日场景：无法读 isGameFail/awardCreate（控制器可能已销毁），
						// 保守起见保留档（下次同关可续）；防刷依赖「关卡内死亡/通关」的即时清档。
					}
				}
				return;
			}

			// ── 在每日挑战关卡里 ─────────────────────────────────────
			if (_dailyLevelName != ctl.levelConfig.name)
			{
				_dailyLevelName = ctl.levelConfig.name;
				Log("进入每日挑战：" + _dailyLevelName);
			}

			// ★ 恢复钩子（v3.3.0）：把"武装决策"收进 TickRestoreArm（含暂时性失败重试）。
			if (ReadBool(KAutoLoadEnabled, true)) { TickRestoreArm(ctl); }

			// ★ v3.4.0：恢复保护期推进 + 恢复结果观察（两者都必须在保存判定之前跑）
			TickRestoreGrace(ctl);
			TickRestoreWatch(ctl);

			// ── 战斗中：周期滑动保存 ─────────────────────────────────
			if (ctl.isGameRunning && !ctl.isGameFail)
			{
				_wasInDailyBattle = true;
				if (ReadBool(KAutoSave, true))   // ★ 还原：这是自动存档功能开关，不是日志开关
				{
					if (++_saveTimer >= SaveIntervalFrames)
					{
						_saveTimer = 0;
						TrySaveCurrent(quiet: true);
					}
				}
			}
			else if (_wasInDailyBattle)
			{
				// 状态机离开 GameRunning：死亡（GameFailEntered）或通关（awardCreate）⇒ 清档防刷
				_wasInDailyBattle = false;
				_saveTimer = 0;
				bool failed = ctl.isGameFail;
				bool finished = false;
				try { finished = GodotObject.IsInstanceValid(ctl.levelControl) && ctl.levelControl.awardCreate; } catch { }
				if (failed || finished)
				{
					DeleteSave(_dailyLevelName);
					Log("已清除存档（" + (finished ? "已通关" : "已失败") + "，防刷奖励）。");
					Diag("cleared save after " + (finished ? "victory" : "fail") + " level=" + _dailyLevelName);
				}
			}
		}
		catch (Exception ex)
		{
			WarnOnce("frame", "每帧异常：" + ex.GetBaseException().Message);
		}
	}

	// ============================================================ 恢复武装（arm）

	/// <summary>
	/// 每帧推进"是否武装 hasProgress"的决策（v3.3.0）。
	/// 时序假设：只在 `!ctl.isGameRunning` 时武装（见 _armControlId 处注释）。
	/// </summary>
	private void TickRestoreArm(TowerDefenseControlNew ctl)
	{
		try
		{
			ulong id = ctl.GetInstanceId();

			// 换控制器 / 换关卡 ⇒ 重新开始一轮决策
			if (_armControlId != id || _armLevelName != _dailyLevelName)
			{
				_armControlId = id;
				_armLevelName = _dailyLevelName;
				_armDecided = false;
				_armRetryFrames = ArmRetryWindowFrames;
				_armRetryCooldown = 0;
				LogArmContext(_dailyLevelName, ctl);   // ★ D：无条件日志（进入关卡那一行）
			}

			// 已经开跑 ⇒ 武装窗口关闭。若还没结论，就是"错过了"。
			if (ctl.isGameRunning)
			{
				if (!_armDecided)
				{
					_armDecided = true;
					Warn("错过武装窗口，本次不恢复（存档保留）：" + _dailyLevelName);
					Diag("skip(错过武装窗口：isGameRunning 已为 true，hasProgress 不再生效) level=" + _dailyLevelName);
				}
				return;
			}

			if (_armDecided) { return; }

			if (_armRetryCooldown > 0) { _armRetryCooldown--; return; }

			ArmDecision decision = TryArmRestore(_dailyLevelName, ctl, out string detail);

			if (decision == ArmDecision.Arm)
			{
				_armDecided = true;
				ctl.hasProgress = true;
				Log("存档自检通过，已设置 hasProgress=true ⇒ 游戏将自动走原生恢复：" + _dailyLevelName);
				Diag("arm level=" + _dailyLevelName + " hasProgress=true");
				Notify("发现每日挑战存档，恢复中…");
				// ★ v3.4.0（A）：恢复成功后进入保护期，8 秒内不写主档
				BeginRestoreGrace();
				// ★ v3.4.0（D）：开始观察恢复结果，失败则取证
				BeginRestoreWatch();
				return;
			}

			if (decision == ArmDecision.SkipSilent)
			{
				_armDecided = true;
				Diag("skip(" + detail + ") level=" + _dailyLevelName);
				return;
			}

			if (decision == ArmDecision.SkipCorrupt)
			{
				_armDecided = true;
				DiagErr("skip(存档内容损坏，已通知玩家，原档保留) level=" + _dailyLevelName + " reason=" + detail);
				return;
			}

			// RetryLater —— 暂时性失败：不判定损坏、不提示，只在窗口内重试
			_armRetryFrames -= ArmRetryIntervalFrames;
			_armRetryCooldown = ArmRetryIntervalFrames;
			Log("恢复预演暂时失败，稍后重试（剩余窗口 " + Math.Max(_armRetryFrames, 0) + " 帧）：" + detail);
			Diag("retry(暂时性失败) level=" + _dailyLevelName + " reason=" + detail
				+ " remain=" + Math.Max(_armRetryFrames, 0));
			if (_armRetryFrames <= 0)
			{
				_armDecided = true;
				Warn("暂时性失败重试耗尽，本次不恢复（存档保留）：" + _dailyLevelName + " reason=" + detail);
				Diag("skip(暂时性失败重试耗尽，静默按新局走) level=" + _dailyLevelName + " reason=" + detail);
			}
		}
		catch (Exception ex)
		{
			// ★★★ v3.5.0 修复（玩家定位 + 要求）：
			//   旧实现在这里 `_armDecided = true` ⇒ **一次异常就永久放弃本次恢复**。
			//   而本次真凶 `DateTime.TryParseExact` 缺方法恰恰是在这条链上抛的，
			//   于是"再一次进关"永远走不到 arm ⇒ 玩家看到的"之后就读不了了"。
			//   ⇒ 改成与"暂时性失败"同策略：不判定损坏、不永久放弃，
			//     在窗口内继续重试（`_armRetryFrames` 耗尽才算放弃），并把异常信息带进日志。
			string msg = ex.GetBaseException().Message;
			DiagErr("arm tick 异常（可在窗口内重试）level=" + _dailyLevelName + " err=" + msg);
			_armRetryFrames -= ArmRetryIntervalFrames;
			_armRetryCooldown = ArmRetryIntervalFrames;
			if (_armRetryFrames <= 0)
			{
				_armDecided = true;
				Warn("恢复决策多次异常，本次不恢复（存档保留）：" + _dailyLevelName + " err=" + msg);
				Diag("skip(arm 异常重试耗尽，静默按新局走) level=" + _dailyLevelName + " err=" + msg);
			}
			else
			{
				Diag("retry(arm 异常) level=" + _dailyLevelName + " remain=" + Math.Max(_armRetryFrames, 0) + " err=" + msg);
			}
		}
	}

	/// <summary>★ D：进入每日挑战关卡那一行（无条件）：关卡名 + 用户脱敏名 + 主档路径 + 主档/备份是否存在。</summary>
	private void LogArmContext(string levelName, TowerDefenseControlNew ctl)
	{
		string primary = NativeProgressPath(levelName);
		string safe = "";
		bool hasUser = TryResolveUserSafe(out safe);
		bool hasPrimary = !string.IsNullOrEmpty(primary) && Godot.FileAccess.FileExists(primary);
		bool hasBackup = !string.IsNullOrEmpty(primary) && Godot.FileAccess.FileExists(NativeBackupPath(primary));
		bool hasVerified = !string.IsNullOrEmpty(primary) && IoExists(NativeVerifiedPath(primary));
		string ctlState = "?";
		try { ctlState = "running=" + ctl.isGameRunning + " fail=" + ctl.isGameFail + " hasProgress=" + ctl.hasProgress; } catch { }
		Diag("enter daily level=" + levelName
			+ " user=" + (hasUser ? safe : "(空)")
			+ " primary=" + (string.IsNullOrEmpty(primary) ? "(无)" : primary)
			+ " primaryExists=" + hasPrimary
			+ " backupExists=" + hasBackup
			+ " verifiedExists=" + hasVerified
			+ " pathLen=" + PathLen(primary)
			+ " " + ctlState
			+ " strictRestore=" + ReadBool(KStrictRestore, false));
	}

	// ============================================================ v3.4.0（A）恢复后保护期

	/// <summary>
	/// A：恢复保护期推进。
	/// 目的：arm 成功后的战场是"宿主边初始化边恢复"的产物，前几秒状态不完整；
	/// 此时把快照写回主档 ⇒ 主档被半成品覆盖 ⇒ 下次读坏（v3.3.0 的连环自毁链）。
	/// 规则：保护期内**不写主档**（TrySaveByName 里 checks），只写 `*.recover.tres` 旁路；
	/// 同时要求"真的开跑 + 连续稳定 N 个 tick"才提前结束保护期。
	/// </summary>
	private void TickRestoreGrace(TowerDefenseControlNew ctl)
	{
		if (!_restoreGraceActive) { return; }
		try
		{
			if (--_restoreGraceLeft > 0)
			{
				// 只有"确实在跑、没失败、关卡控件有效"的帧才算稳定 tick
				bool stable = false;
				try
				{
					stable = ctl.isGameRunning && !ctl.isGameFail
						&& GodotObject.IsInstanceValid(ctl.levelControl);
				}
				catch { }
				if (stable)
				{
					if (++_restoreStableTicks >= RestoreStableTicksNeeded && _restoreGraceLeft < RestoreGraceFrames - RestoreStableTicksNeeded)
					{
						_restoreGraceActive = false;
						_restoreGraceLeft = 0;
						Log("恢复保护期结束（已稳定 " + _restoreStableTicks + " tick）。");
						Diag("recover grace end(early, stable=" + _restoreStableTicks + " tick) level=" + _dailyLevelName);
					}
				}
				else
				{
					_restoreStableTicks = 0;   // 不稳定就重新数
				}
				return;
			}

			_restoreGraceActive = false;
			_restoreGraceLeft = 0;
			Log("恢复保护期结束（满 " + RestoreGraceFrames + " 帧）。");
			Diag("recover grace end(full " + RestoreGraceFrames + " frames) level=" + _dailyLevelName);
		}
		catch { _restoreGraceActive = false; _restoreGraceLeft = 0; }
	}

	/// <summary>开始保护期（arm 成功时调用）。</summary>
	private void BeginRestoreGrace()
	{
		_restoreGraceActive = true;
		_restoreGraceLeft = RestoreGraceFrames;
		_restoreStableTicks = 0;
		Log("进入恢复保护期 " + RestoreGraceFrames + " 帧内不写主档（只写 *.recover.tres）。");
		Diag("recover grace begin frames=" + RestoreGraceFrames + " level=" + _dailyLevelName);
	}

	// ============================================================ v3.4.0（D）恢复结果观察 / 失败取证

	/// <summary>arm 成功后开始观察恢复结果（窗口 ≈2 秒）。</summary>
	private void BeginRestoreWatch()
	{
		_restoreWatched = true;
		_restoreWatchFrames = RestoreWatchFrames;
		_restoreWatchExtend = 0;
		_restoreSnapCooldown = 0;
		try { _restoreWatchScene = SceneManager.CurrentScene ?? ""; } catch { _restoreWatchScene = ""; }
	}

	/// <summary>
	/// D：恢复结果观察。
	/// · 每 30 帧（受 enableLog 控制）记录一次恢复过程快照；
	/// · 若 isGameRunning 在窗口内变 true ⇒ 恢复成功，结束观察；
	/// · 若窗口耗尽仍未开跑、且场景已不是战斗场景（退回菜单）⇒ **无条件 Error 日志
	///   `restore failed (scene changed back)` + 把主档另存 `*.failed-<时间戳>.tres` 取证**。
	/// </summary>
	private void TickRestoreWatch(TowerDefenseControlNew ctl)
	{
		if (!_restoreWatched) { return; }
		try
		{
			bool running = false, fail = false, award = false;
			int hasProgressField = -1;
			try
			{
				running = ctl.isGameRunning;
				fail = ctl.isGameFail;
				award = GodotObject.IsInstanceValid(ctl.levelControl) && ctl.levelControl.awardCreate;
				hasProgressField = ctl.hasProgress ? 1 : 0;
			}
			catch { }

			// 恢复成功：立刻结束观察
			if (running)
			{
				_restoreWatched = false;
				Log("恢复观察结束：已开跑（isGameRunning=true）。");
				Diag("restore ok (isGameRunning=true) level=" + _dailyLevelName
					+ " after=" + (RestoreWatchFrames - _restoreWatchFrames) + "frames");
				return;
			}

			// 节流快照（受 enableLog 控制）
			if (--_restoreSnapCooldown <= 0)
			{
				_restoreSnapCooldown = RestoreSnapshotIntervalFrames;
				string scene = "";
				try { scene = SceneManager.CurrentScene ?? ""; } catch { }
				Log("恢复过程快照：hasProgress=" + hasProgressField
					+ " isGameRunning=" + running
					+ " isGameFail=" + fail
					+ " awardCreate=" + award
					+ " restoreGraceLeft=" + _restoreGraceLeft
					+ " scene=" + scene
					+ " (" + (RestoreWatchFrames - _restoreWatchFrames) + "frames)");
			}

			if (--_restoreWatchFrames > 0) { return; }

			// ★ 窗口耗尽仍未开跑。**不能只凭"没开跑"就判失败** ——
			//   宿主 GameInitEntered 先 await EnsureGameplayAtlasesReadyAsync（玩家日志里
			//   fullWallMs≈5 秒），加载慢时 2 秒窗口根本不够。任务 D.2 的判据是
			//   "仍未开跑 **且** 场景已切回菜单" 两个条件同时成立，这里严格照做：
			//   · 场景回到菜单类场景 ⇒ 宿主确实 RejectProgressLoad 并把玩家踢回去了 ⇒ 判失败；
			//   · 场景没回菜单 ⇒ 只是加载慢 ⇒ **延长观察窗口**（最多 RestoreWatchExtendMax 次），
			//     延长期间仍然只用 Log（受 enableLog）记录，不给玩家任何提示。
			string nowScene = "";
			try { nowScene = SceneManager.CurrentScene ?? ""; } catch { }
			bool sceneIsMenu = IsMenuScene(nowScene);
			bool sceneChangedBack = sceneIsMenu
				&& !string.Equals(nowScene, _restoreWatchScene, StringComparison.Ordinal);

			if (!sceneChangedBack && _restoreWatchExtend < RestoreWatchExtendMax)
			{
				_restoreWatchExtend++;
				_restoreWatchFrames = RestoreWatchFrames;
				Log("恢复观察窗口延长（第 " + _restoreWatchExtend + " 次，仍在加载）：scene=" + nowScene);
				Diag("restore watch extend#" + _restoreWatchExtend + " level=" + _dailyLevelName
					+ " scene=" + nowScene + " graceLeft=" + _restoreGraceLeft);
				return;
			}

			_restoreWatched = false;
			if (sceneChangedBack)
			{
				string reason = "scene changed back";
				DiagErr("restore failed (" + reason + ") level=" + _dailyLevelName
					+ " scene=" + nowScene + " armedScene=" + _restoreWatchScene
					+ " graceLeft=" + _restoreGraceLeft);
				// 恢复失败 ⇒ 取消保护期（本次没恢复成功；取证副本已先保存，
				// 且 v3.4.0 的 verified 会保证下次还有可回退的好档）
				_restoreGraceActive = false;
				_restoreGraceLeft = 0;
				PreserveFailedSnapshot(_dailyLevelName, reason);
				// ★ B.3 断言：恢复失败**绝不**删档（这里显式复查并打日志）
				string primary = NativeProgressPath(_dailyLevelName);
				if (!string.IsNullOrEmpty(primary))
				{
					bool stillThere = IoExists(primary);
					Diag("restore failed: save preserved? primaryExists=" + stillThere + " path=" + primary);
					if (!stillThere)
					{
						DiagErr("restore failed BUT primary is missing（异常：不应发生）path=" + primary);
					}
				}
				return;
			}

			// 观察窗口延长次数用尽、但场景没回菜单：**不判失败**（保存仍在、下次还能试），
			// 只留一条 Warn 供排障，不做 took-forensics、不弹窗。
			Warn("恢复观察超时但场景未回菜单，不判失败（存档保留）：" + _dailyLevelName + " scene=" + nowScene);
			Diag("restore watch timeout(no menu scene, not failed) level=" + _dailyLevelName
				+ " scene=" + nowScene + " extends=" + _restoreWatchExtend);
		}
		catch (Exception ex)
		{
			_restoreWatched = false;
			DiagErr("restore watch 异常：" + ex.GetBaseException().Message);
		}
	}

	/// <summary>场景名是否属于"菜单类"（宿主 RejectProgressLoad 的 OnClose 只会切到这几个）。</summary>
	private static bool IsMenuScene(string scene)
	{
		if (string.IsNullOrEmpty(scene)) { return false; }
		switch (scene)
		{
			case "MainMenu":
			case "LevelChoose":
			case "LevelEditorStage":
				return true;
			default:
				return false;
		}
	}

	/// <summary>
	/// D.2：恢复失败 ⇒ 把主档另存为 `*.failed-&lt;yyyyMMdd-HHmmss&gt;.tres` 留作取证（**不删原档**）。
	/// 只保留最近 5 份，避免目录无限增长。
	/// </summary>
	private void PreserveFailedSnapshot(string levelName, string reason)
	{
		try
		{
			string primary = NativeProgressPath(levelName);
			if (string.IsNullOrEmpty(primary)) { return; }
			if (!IoExists(primary))
			{
				Diag("failed-snapshot skip: primary missing path=" + primary);
				return;
			}
			string stamp = FailedSnapshotStamp();   // ★ v3.5.0：不再用 DateTime.Now.ToString(fmt)（BCL 裁剪风险）
			if (string.IsNullOrEmpty(stamp)) { stamp = "unknown"; }   // 引擎时间戳取不到时的兜底名
			string dst = primary.TrimSuffix(".tres") + ".failed-" + stamp + ".tres";
			if (!IoCopy(primary, dst, "failed-snapshot"))
			{
				DiagErr("failed-snapshot copy fail reason=" + reason + " dst=" + dst);
				return;
			}
			Diag("failed-snapshot ok reason=" + reason + " dst=" + dst + " size=" + FileSize(dst));
			PruneFailedSnapshots(levelName, 5);
		}
		catch (Exception ex)
		{
			DiagErr("failed-snapshot 异常：" + ex.GetBaseException().Message);
		}
	}

	/// <summary>只保留最近 N 份 `*.failed-*.tres` 取证档（按文件名里的时间戳排序）。</summary>
	private void PruneFailedSnapshots(string levelName, int keep)
	{
		try
		{
			string primary = NativeProgressPath(levelName);
			if (string.IsNullOrEmpty(primary)) { return; }
			string dirUser = primary.GetBaseDir();
			string dirAbs = IoPath(dirUser);
			if (!System.IO.Directory.Exists(dirAbs)) { return; }
			string stem = levelName + ".failed-";
			var list = new List<string>();
			foreach (string f in System.IO.Directory.GetFiles(dirAbs, stem + "*.tres"))
			{
				list.Add(f);
			}
			if (list.Count <= keep) { return; }
			list.Sort(StringComparer.Ordinal);   // 时间戳在文件名里 ⇒ 字典序即时间序
			int remove = list.Count - keep;
			for (int i = 0; i < remove; i++)
			{
				try
				{
					System.IO.File.Delete(list[i]);
					Log("取证档清理（保留最近 " + keep + " 份）：" + list[i]);
				}
				catch (Exception ex) { DiagErr("取证档清理失败：" + ex.GetBaseException().Message); }
			}
		}
		catch (Exception ex) { DiagErr("取证档清理异常：" + ex.GetBaseException().Message); }
	}

	/// <summary>
	/// ★ v3.2 核心（v3.3.0 扩展为分类返回）：决定是否武装 hasProgress。
	/// 返回 Arm ⇔ 宿主稍后**一定能**读到档（返回非 Arm 时绝不武装，宿主的
	/// RejectProgressLoad 弹窗就不会因"没有可用档/暂时不可读"而出现）。
	/// </summary>
	private ArmDecision TryArmRestore(string levelName, TowerDefenseControlNew ctl, out string detail)
	{
		detail = "";
		if (ctl == null || !GodotObject.IsInstanceValid(ctl)) { detail = "控制器不可用"; return ArmDecision.RetryLater; }

		string primary = NativeProgressPath(levelName);
		if (string.IsNullOrEmpty(primary))
		{
			// 用户名为空：宿主同样把它当作"不可存读"（PrepareProgressRecovery:1590）
			detail = "当前用户名为空";
			return ArmDecision.SkipSilent;
		}

		// ★ C.1：文件名必须与当前关卡名一致（现状由路径天然保证，这里显式校验并打日志）
		string fileStem = System.IO.Path.GetFileNameWithoutExtension(primary);
		if (!string.Equals(fileStem, levelName, StringComparison.Ordinal))
		{
			detail = "文件名与关卡名不一致 file=" + fileStem + " level=" + levelName;
			DiagErr(detail);
			return ArmDecision.SkipSilent;
		}

		bool hasPrimary = Godot.FileAccess.FileExists(primary);
		bool hasBackup = Godot.FileAccess.FileExists(NativeBackupPath(primary));
		if (!hasPrimary && !hasBackup)
		{
			// 情况①全新进入（从来没存过）／情况②存档已被死亡/通关清掉
			detail = "本关没有进度档（全新进入或已清档）";
			return ArmDecision.SkipSilent;
		}

		if (IsExpiredLevelName(levelName))
		{
			// 情况③跨天存档过期
			detail = "存档属于往日关卡（跨天过期），原档保留";
			return ArmDecision.SkipSilent;
		}

		// ★ 用它宿主自己的读取器预演一次：能读才武装（等价于宿主 1376 行那次调用）。
		bool loadable = false;
		string reason = null;
		try
		{
			loadable = GameSaveManager.Instance != null
				&& GodotObject.IsInstanceValid(GameSaveManager.Instance)
				&& GameSaveManager.Instance.TryGetLoadableLevelProgress(levelName, out _, out reason);
		}
		catch (Exception ex)
		{
			reason = "读取异常：" + ex.GetBaseException().Message;
		}

		// ★ D：宿主预演结果（无条件）
		Diag("preflight loadable=" + loadable + " reason=" + (reason ?? "(空)") + " level=" + levelName
			+ " hasPrimary=" + hasPrimary + " hasBackup=" + hasBackup);

		if (!loadable)
		{
			if (string.IsNullOrWhiteSpace(reason)) { reason = "未知原因"; }
			ReasonKind kind = ClassifyReason(reason);
			detail = reason;
			if (kind == ReasonKind.Transient || kind == ReasonKind.Unknown)
			{
				// 暂时性：不判损坏、不提示（可能是 currentControl 尚未就绪 / 用户尚未载入 /
				// _progressRecovery 竞争 / EnsureGameplayAtlasesReadyAsync 之前的时序）
				return ArmDecision.RetryLater;
			}
			if (kind == ReasonKind.NotFound)
			{
				// 文件在"检查存在"与"真正读取"之间被删掉（通关/失败清档的竞态）⇒ 静默新局
				detail = reason + "（读取时已不存在，视为已清档）";
				return ArmDecision.SkipSilent;
			}

			// ★ v3.4.0（B.2）：内容类失败 ⇒ 先试 verified 副本回退（每控制器只试一次）
			if (!_verifiedFallbackTried)
			{
				_verifiedFallbackTried = true;
				if (TryVerifiedFallback(levelName, primary, out string fallbackDetail))
				{
					// 回退成功：verified 已覆盖主档，重新走一次宿主预演
					bool loadable2 = false;
					string reason2 = null;
					try
					{
						loadable2 = GameSaveManager.Instance != null
							&& GodotObject.IsInstanceValid(GameSaveManager.Instance)
							&& GameSaveManager.Instance.TryGetLoadableLevelProgress(levelName, out _, out reason2);
					}
					catch (Exception ex) { reason2 = "读取异常：" + ex.GetBaseException().Message; }
					Diag("preflight(after verified fallback) loadable=" + loadable2
						+ " reason=" + (reason2 ?? "(空)") + " level=" + levelName);
					if (loadable2)
					{
						// 回退成功：可以 arm（保护期/观察照常）
						detail = "已从 verified 副本恢复";
						Diag("verified fallback ok, arm with verified copy level=" + levelName);
						Notify("主档损坏，已用最近一次校验通过的备份继续", 420);
						return ArmDecision.Arm;
					}
					detail = reason + "；verified 回退后仍不可读：" + (reason2 ?? "(空)");
				}
				else
				{
					detail = reason + "；" + fallbackDetail;
				}
			}

			// 内容类且 verified 也救不回来 ⇒ 提示一次，原档保留
			Warn("存档存在但无法读取，已跳过恢复（原档保留）：" + reason + " path=" + primary);
			NotifyCorrupt(primary, reason);
			return ArmDecision.SkipCorrupt;
		}

		// ★ C.2：strictRestore 保守开关 —— 恢复前额外校验档内 Mod 环境快照
		if (ReadBool(KStrictRestore, false))
		{
			if (!TryStrictSnapshotCheck(primary, out string strictReason))
			{
				detail = "strictRestore：" + strictReason;
				Warn("保守模式（strictRestore）：" + strictReason + "，本次不恢复。");
				Diag("skip(strictRestore 不一致) level=" + levelName + " reason=" + strictReason);
				Notify("存档的 Mod 环境与当前不一致，本次不恢复（原档保留）", 420);
				return ArmDecision.SkipSilent;
			}
			Log("strictRestore 校验通过。");
		}

		return ArmDecision.Arm;
	}

	/// <summary>
	/// strictRestore 校验：档内 modSnapshot 必须与当前 Mod 环境一致。
	/// 宿主把 modSnapshot 存在 TowerDefenseLevelSaveConfigCSharp.modSnapshot（:11），
	/// 写入来自 XWModEnvironmentService.WriteSnapshot（:65）；
	/// 校验用同文件 :293 CheckSnapshot（内部 CompareSnapshots）。
	/// </summary>
	private bool TryStrictSnapshotCheck(string primary, out string reason)
	{
		reason = "";
		TowerDefenseLevelSaveConfigCSharp probe = LoadProgressFile(primary) ?? LoadProgressFile(NativeBackupPath(primary));
		if (probe == null) { reason = "档无法读取，无法校验；"; return false; }
		var snapshot = probe.modSnapshot;
		if (snapshot == null || snapshot.Count == 0) { reason = "档内没有 Mod 环境快照；"; return false; }
		try
		{
			if (XWModEnvironmentService.CheckSnapshot(snapshot, out string diff))
			{
				return true;
			}
			reason = string.IsNullOrWhiteSpace(diff) ? "Mod 环境不一致；" : diff;
			return false;
		}
		catch (Exception ex)
		{
			reason = "Mod 环境校验异常：" + ex.GetBaseException().Message;
			return false;
		}
	}

	/// <summary>
	/// ★ v3.4.0（B.2）verified 回退：主档内容类失败时，用"最近一次自检通过的副本"覆盖主档。
	///
	/// 为什么敢覆盖主档：主档此刻已经**确认读不出来**（Content 类 reason：无法读取关卡进度资源 /
	/// 读取关卡进度失败），留着它只会让玩家永远"读不了"；而 verified 是最近一次
	/// `verify ok` 的字节级副本 ⇒ 覆盖是严格改善。原来的坏主档**不会丢**：
	/// 覆盖前先另存 `*.failed-&lt;时间戳&gt;.tres` 取证（见 PreserveFailedSnapshot 的同名思路）。
	/// </summary>
	private bool TryVerifiedFallback(string levelName, string primary, out string detail)
	{
		detail = "";
		try
		{
			string verified = NativeVerifiedPath(primary);
			if (!IoExists(verified))
			{
				detail = "没有 verified 副本可回退";
				return false;
			}
			// 先把坏主档留一份取证（不删）
			PreserveFailedSnapshot(levelName, "content-broken-before-verified-fallback");

			// verified 自身也必须能读，否则覆盖过去也没意义
			TowerDefenseLevelSaveConfigCSharp probe = LoadProgressFile(verified);
			if (probe == null)
			{
				detail = "verified 副本也读不出来";
				DiagErr("verified fallback abort: verified unreadable path=" + verified
					+ " len=" + PathLen(verified));
				return false;
			}

			if (!IoCopy(verified, primary, "verified-fallback"))
			{
				detail = "verified 复制回主档失败";
				return false;
			}
			// 再回读确认
			TowerDefenseLevelSaveConfigCSharp after = LoadProgressFile(primary);
			if (after == null)
			{
				detail = "verified 回退后主档仍读不出来";
				DiagErr("verified fallback: primary unreadable after copy path=" + primary);
				return false;
			}
			Diag("verified fallback ok from=" + verified + " to=" + primary
				+ " size=" + FileSize(primary) + " len=" + PathLen(primary));
			return true;
		}
		catch (Exception ex)
		{
			detail = "verified 回退异常：" + ex.GetBaseException().Message;
			DiagErr("verified fallback 异常：" + ex.GetBaseException().Message);
			return false;
		}
	}

	/// <summary>宿主同款读取器（GameSaveManager.TryReadProgressFile:1476-1494 的等价实现）。</summary>
	private static TowerDefenseLevelSaveConfigCSharp LoadProgressFile(string path)
	{
		try
		{
			if (string.IsNullOrEmpty(path) || !Godot.FileAccess.FileExists(path)) { return null; }
			return ResourceLoader.Load(path, "", ResourceLoader.CacheMode.Ignore) as TowerDefenseLevelSaveConfigCSharp;
		}
		catch (Exception ex)
		{
			DiagErr("load fail path=" + path + " err=" + ex.GetBaseException().Message);
			return null;
		}
	}

	/// <summary>
	/// 失败原因分类（v3.3.0 任务 B.1）。宿主可能给的 reason：
	/// 「关卡进度文件不存在。」「无法读取关卡进度资源。」「读取关卡进度失败：…」
	/// 「当前战斗控制器不可用。」「当前用户或关卡不可用。」（见 unpack :
	/// GameSaveManager.cs:1479/1487/1492、:1589-1590，TowerDefenseLevelSaveConfigCSharp.cs:55）
	/// </summary>
	private static ReasonKind ClassifyReason(string reason)
	{
		if (string.IsNullOrEmpty(reason)) { return ReasonKind.Unknown; }

		// ① 文件不存在（读取瞬间被清档的竞态）⇒ 静默
		if (reason.Contains("文件不存在")) { return ReasonKind.NotFound; }

		// ② 明确的内容类（反序列化失败 / 资源类型不对）
		if (reason.Contains("无法读取关卡进度资源")) { return ReasonKind.Content; }
		if (reason.Contains("读取关卡进度失败")) { return ReasonKind.Content; }
		if (reason.Contains("读取异常")) { return ReasonKind.Content; }

		// ③ 暂时性（环境尚未就绪）
		if (reason.Contains("不可用")) { return ReasonKind.Transient; }
		if (reason.Contains("未就绪")) { return ReasonKind.Transient; }
		if (reason.Contains("尚未")) { return ReasonKind.Transient; }
		if (reason.Contains("稍后")) { return ReasonKind.Transient; }
		if (reason.Contains("正在")) { return ReasonKind.Transient; }

		// ④ 未知 ⇒ 一律按暂时性处理（**绝不**因未知原因误报"存档损坏"）
		return ReasonKind.Unknown;
	}

	/// <summary>离开时是否保留存档（通关/失败则不保留）。</summary>
	private bool HasSaveKeepDecision(TowerDefenseControlNew ctl)
	{
		try
		{
			if (ctl != null && GodotObject.IsInstanceValid(ctl))
			{
				if (ctl.isGameFail) { DeleteSave(_dailyLevelName); return false; }
				bool finished = GodotObject.IsInstanceValid(ctl.levelControl) && ctl.levelControl.awardCreate;
				if (finished) { DeleteSave(_dailyLevelName); return false; }
			}
		}
		catch { }
		return true;
	}

	private void LeaveDaily()
	{
		if (_wasInDailyBattle) { Log("离开每日挑战：" + _dailyLevelName); }
		_wasInDailyBattle = false;
		_dailyLevelName = "";
		_saveTimer = 0;
		// 恢复决策状态随控制器一起失效（下次进关重新决策）
		_armControlId = 0;
		_armLevelName = "";
		_armDecided = false;
		_armRetryFrames = 0;
		_armRetryCooldown = 0;
		// ★ v3.4.0：保护期 / 观察窗口 / verified 回退标记也一起复位
		//   注意：**不在这里"补救"失败判定** —— 恢复失败要走 TickRestoreWatch 的取证分支
		//   （它会在控制器失效前触发）；这里只是兜底清状态，避免状态跨关卡泄漏。
		if (_restoreWatched)
		{
			DiagErr("restore failed (control lost before watch completed) level=" + _armLevelName);
			PreserveFailedSnapshot(_armLevelName, "control-lost");
		}
		_restoreGraceActive = false;
		_restoreGraceLeft = 0;
		_restoreStableTicks = 0;
		_restoreWatched = false;
		_restoreWatchFrames = 0;
		_restoreWatchExtend = 0;
		_restoreWatchScene = "";
		_verifiedFallbackTried = false;
	}

	private static bool IsDailyLevel(string levelName)
	{
		return !string.IsNullOrEmpty(levelName) && levelName.StartsWith("DailyLevel-", StringComparison.Ordinal);
	}

	// ============================================================ 保存

	private void TrySaveCurrent(bool quiet = false)
	{
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			TowerDefenseControlNew ctl = (mgr != null && GodotObject.IsInstanceValid(mgr)) ? mgr.currentControl : null;
			if (ctl != null && IsDailyLevel(ctl.levelConfig?.name)) { TrySaveByName(ctl.levelConfig.name, quiet); }
		}
		catch { }
	}

	/// <summary>
	/// 保存一次（v3.3.0 大修）：抓快照 → 备份旧档 → tmp+Move → **回读自检** → 失败重试/回滚。
	/// 关键步骤全部走 Diag（无条件），失败走 DiagErr（Error 级），不再依赖 enableLog。
	///
	/// ★ 任务 A.4 的 uid 查证结论（实测，不是猜）：**保留 tmp+Move，不需要改成"直写最终路径"**。
	///   1) Godot 4 的 ResourceSaver 只有在该 Resource 已持有有效 UID（即从 res:// 导入、
	///      在 ResourceUID 缓存里登记过的资源）时才会在 `[gd_resource ...]` 头部写
	///      `uid="uid://…"`；运行时 `new` 出来的 Resource 的 UID 是无效的，不会写。
	///   2) 本机实测反证：`user://Csharp/Progress/2/` 下**全部** .tres（宿主写的与老版本 Mod
	///      写的）`uid=` 与 `uid://` 出现次数都是 **0**；整个 `user://Csharp` 树里
	///      `grep uid://` 命中 0 个文件。
	///   3) 而且导出后的游戏没有编辑器侧的 UID 缓存：`user://.godot/uid_cache.bin` 不存在，
	///      `user://` 下 `.uid` 边车文件数量为 0。
	///   ⇒ "写 tmp 会让 header 的 uid 指向 tmp 路径"这个担心在本项目**不成立**。
	///   反过来，改成"直写最终路径"会把"写到一半崩溃"从"旧档完好"变成"坏档且无备份"，
	///   而同目录内 File.Move 走 MoveFileEx 是原子的 ⇒ **保留 tmp+Move，用回读自检兜底**。
	/// </summary>
	private void TrySaveByName(string levelName, bool quiet = false)
	{
		string path = "";
		try
		{
			GameSaveManager save = GameSaveManager.Instance;
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (save == null || mgr == null || !GodotObject.IsInstanceValid(mgr.currentControl))
			{
				if (!quiet) { Warn("保存跳过：存档管理器或战斗控制器不可用。"); }
				Log("save skip: manager/control unavailable");
				return;
			}
			TowerDefenseControlNew ctl = mgr.currentControl;
			if (!ctl.isGameRunning || ctl.isGameFail)
			{
				if (!quiet) { Log("保存跳过：游戏未在运行或已失败。"); }
				Log("save skip: not running or failed");
				return;
			}
			try
			{
				if (GodotObject.IsInstanceValid(ctl.levelControl) && ctl.levelControl.awardCreate)
				{
					if (!quiet) { Log("保存跳过：本关已通关。"); }
					Log("save skip: level completed");
					return;
				}
			}
			catch { }
			if (Global.IsMultiplayerMode) { if (!quiet) { Log("保存跳过：联机模式。"); } Diag("save skip: multiplayer"); return; }

			string reason;
			if (!ctl.CanCreateProgressSave(out reason))
			{
				Log("保存延后（不安全检查点）：" + reason);
				Diag("save defer(unsafe checkpoint) reason=" + reason);
				return;
			}

			path = NativeProgressPath(levelName);
			if (string.IsNullOrEmpty(path))
			{
				if (!quiet) { Warn("保存跳过：当前用户名为空。"); }
				Diag("save skip: empty user");
				return;
			}

			// ① 抓全量快照（★ 单独 try：这一步抛异常以前被外层 catch 静默吃掉，
			//    玩家日志里那 4 条「[Save] 保存Feature[Map]...」正是这一步已执行过、
			//    但后面落盘失败且无任何痕迹的场景）
			var snapshot = new TowerDefenseLevelSaveConfigCSharp();
			try
			{
				snapshot.Save();
			}
			catch (Exception snapEx)
			{
				DiagErr("save fail stage=snapshot err=" + snapEx.GetBaseException().Message + " path=" + path);
				Warn("保存异常（抓快照）：" + snapEx.GetBaseException().Message);
				return;
			}

			// ★ v3.4.0（A）：恢复保护期内**不写主档**。
			//   理由：刚恢复出来的战场是"宿主边初始化边恢复"的产物，前几秒组件状态不完整；
			//   此时把快照写回主档 ⇒ 主档被半成品覆盖 ⇒ 下次读坏（v3.3.0 的连环自毁链）。
			//   保护期内只写 *.recover.tres 旁路（不参与恢复、不更新 verified），
			//   玩家这次会话若崩溃也只损失"恢复后的这段进度"，而不会污染仅存的好档。
			bool holdPrimary = _restoreGraceActive;
			string target = holdPrimary ? NativeRecoverSidePath(path) : path;
			if (holdPrimary)
			{
				Log("save hold(recover grace) 保护期内不写主档，改写旁路：" + target + " graceLeft=" + _restoreGraceLeft);
			}

			// ② 目录（结果以前被忽略 ⇒ 现在检查并打印）
			Error dirErr = DirAccess.MakeDirRecursiveAbsolute(path.GetBaseDir());
			if (dirErr != Error.Ok && dirErr != Error.AlreadyExists)
			{
				DiagErr("save fail stage=mkdir err=" + dirErr + " path=" + path
					+ " dir=" + path.GetBaseDir() + " len=" + PathLen(path));
				return;
			}

			// ③ A.3：Move 之前把旧档复制成 .pre-relaxed.tres
			//    （与宿主 EnsureProgressBackup:1531-1542 语义一致：**仅当备份不存在时生成**，
			//     这样备份保留的是"最早那份好档"，宿主读主档失败时会自动回退到它）
			//    保护期内写的是旁路文件，不碰主档 ⇒ 也不需要备份主档。
			if (!holdPrimary) { EnsureBackupBeforeOverwrite(path); }

			// ④ tmp + 原子 Move（宿主 SaveResourceAtomically:484-556 的等价实现；
			//    窗口 500-509 会先删残留 tmp，我们以前漏了这一步，这里补上）
			string tmp = target.TrimSuffix(".tres") + ".tmp.tres";
			DeleteGlobalized(tmp);
			Error err = Error.Ok;
			try { err = ResourceSaver.Save(snapshot, tmp, ResourceSaver.SaverFlags.None); }
			catch (Exception saveEx)
			{
				DiagErr("save fail stage=saver(throw) err=" + saveEx.GetBaseException().Message + " path=" + tmp);
				return;
			}
			if (err != Error.Ok)
			{
				DiagErr("save fail stage=saver err=" + err + " path=" + tmp);
				Warn("保存失败：" + err + " path=" + tmp);
				DeleteGlobalized(tmp);
				return;
			}

			// ★ v3.4.0（C）：Move/Copy 全部改走长路径安全的 IoMove/IoCopy
			if (!IoMove(tmp, target, "save-move"))
			{
				if (!IoCopy(tmp, target, "save-copy"))
				{
					DeleteGlobalized(tmp);
					return;
				}
				DeleteGlobalized(tmp);
			}

			long size = FileSize(target);
			Diag("save ok path=" + target + " size=" + size + " len=" + PathLen(target)
				+ (holdPrimary ? " hold=recover-grace" : ""));

			// ⑤ A.2：写后**回读自检**（宿主同款读取器 + CanLoad）
			if (VerifySaved(target, out string verifyReason))
			{
				Diag("verify ok path=" + target);
				// ★ v3.4.0（B.1）：只有"写主档且自检通过"才更新 verified 副本。
				//   保护期内的旁路档是半成品，绝不能当 verified。
				if (!holdPrimary) { UpdateVerifiedCopy(path); }
				if (!quiet)
				{
					string msg = holdPrimary ? "每日挑战进度已保存（恢复保护期，暂存旁路）" : "每日挑战进度已保存";
					Log(msg + " → " + target);
					Notify(msg);
				}
				return;
			}

			DiagErr("verify fail reason=" + verifyReason + " path=" + target);

			// ⑥ 重试一次：**直接写最终路径**（绕过 tmp 环节，排除 Move 造成的损坏）
			Error err2 = Error.Ok;
			try { err2 = ResourceSaver.Save(snapshot, target, ResourceSaver.SaverFlags.None); }
			catch (Exception retryEx)
			{
				DiagErr("verify retry fail stage=saver(throw) err=" + retryEx.GetBaseException().Message + " path=" + target);
				err2 = Error.Failed;
			}
			if (err2 == Error.Ok && VerifySaved(target, out string retryReason))
			{
				Diag("verify ok(after direct-write retry) path=" + target + " size=" + FileSize(target));
				if (!holdPrimary) { UpdateVerifiedCopy(path); }
				return;
			}
			DiagErr("verify fail after retry err=" + err2
				+ " reason=" + (err2 == Error.Ok ? verifyReason : "(saver " + err2 + ")") + " path=" + target);

			// ⑦ 两次自检都失败 ⇒ 把旧档放回去（不覆盖好档），让玩家下次至少还有上一份可读的档
			//    保护期内失败的是旁路文件，主档根本没被动过 ⇒ 不动主档。
			if (!holdPrimary)
			{
				if (IoExists(NativeVerifiedPath(path)))
				{
					// v3.4.0：verified 是"最近一次自检通过"的档，比 pre-relaxed 更新，优先用它回退
					Diag("rollback: prefer verified copy path=" + NativeVerifiedPath(path));
					RestorePrimaryFromVerified(path);
				}
				else
				{
					RestorePrimaryFromBackup(path);
				}
			}
		}
		catch (Exception ex)
		{
			DiagErr("save fail stage=unknown err=" + ex.GetBaseException().Message + " path=" + path);
			Warn("保存异常：" + ex.GetBaseException().Message);
		}
	}

	/// <summary>
	/// A.3：Move 之前备份旧档 → `.pre-relaxed.tres`。
	/// 与宿主 GameSaveManager.EnsureProgressBackup:1531-1542 一致：**备份已存在就不动它**
	/// （备份语义是"最早那份还能读的好档"）。
	/// </summary>
	private void EnsureBackupBeforeOverwrite(string primary)
	{
		try
		{
			if (!IoExists(primary)) { return; }
			string backup = NativeBackupPath(primary);
			if (IoExists(backup))
			{
				Log("旧档备份已存在，保持不动：" + backup);
				return;
			}
			if (!IoCopy(primary, backup, "backup", overwrite: false))
			{
				return;   // IoCopy 内部已 DiagErr（含 stage=backup 与路径长度）
			}
			Diag("backup ok from=" + primary + " to=" + backup + " size=" + FileSize(backup)
				+ " len=" + PathLen(backup));
		}
		catch (Exception ex)
		{
			DiagErr("backup fail err=" + ex.GetBaseException().Message + " primary=" + primary);
		}
	}

	/// <summary>
	/// ★ v3.4.0（B.1）维护 verified 副本：仅在"写主档 + 回读自检通过"之后调用。
	/// 覆盖式，只保留最近一次自检通过的档 ⇒ 这是玩家手上"最新的确定可读档"。
	/// </summary>
	private void UpdateVerifiedCopy(string primary)
	{
		try
		{
			string verified = NativeVerifiedPath(primary);
			if (!IoExists(primary)) { return; }
			// 先写临时再 Move，避免"复制到一半崩"把 verified 也弄坏
			string vt = verified + ".tmp";
			IoDelete(vt, "verified-tmp");
			if (!IoCopy(primary, vt, "verified-write"))
			{
				return;
			}
			if (!IoMove(vt, verified, "verified-commit"))
			{
				IoDelete(vt, "verified-tmp");
				return;
			}
			// 回读确认 verified 自身可读（否则下次回退也没意义）
			if (LoadProgressFile(verified) == null)
			{
				DiagErr("verified copy unreadable after write path=" + verified + " len=" + PathLen(verified));
				return;
			}
			Diag("verified ok path=" + verified + " size=" + FileSize(verified) + " len=" + PathLen(verified));
		}
		catch (Exception ex)
		{
			DiagErr("verified 维护异常：" + ex.GetBaseException().Message);
		}
	}

	/// <summary>★ v3.4.0（B）自检两次都失败时，优先用 verified 把主档放回去（比 pre-relaxed 更新）。</summary>
	private void RestorePrimaryFromVerified(string primary)
	{
		try
		{
			string verified = NativeVerifiedPath(primary);
			if (!IoExists(verified))
			{
				DiagErr("rollback(verified) skip: verified missing path=" + verified);
				RestorePrimaryFromBackup(primary);
				return;
			}
			if (!IoCopy(verified, primary, "rollback-verified"))
			{
				return;
			}
			Diag("rollback ok restored primary from verified=" + verified
				+ " size=" + FileSize(primary) + " len=" + PathLen(primary));
			NotifyCorrupt(primary, "写入后回读失败，已回退到最近一次校验通过的副本");
		}
		catch (Exception ex)
		{
			DiagErr("rollback(verified) 异常：" + ex.GetBaseException().Message);
		}
	}

	/// <summary>回读自检：宿主同款读取器 + CanLoad（GameSaveManager.TryReadProgressFile 等价）。</summary>
	private bool VerifySaved(string path, out string reason)
	{
		reason = "";
		try
		{
			if (!Godot.FileAccess.FileExists(path)) { reason = "文件不存在"; return false; }
			TowerDefenseLevelSaveConfigCSharp loaded =
				ResourceLoader.Load(path, "", ResourceLoader.CacheMode.Ignore) as TowerDefenseLevelSaveConfigCSharp;
			if (loaded == null) { reason = "无法读取关卡进度资源。"; return false; }
			if (!loaded.CanLoad(out string canLoadReason))
			{
				reason = string.IsNullOrWhiteSpace(canLoadReason) ? "CanLoad 失败" : canLoadReason;
				return false;
			}
			// 内容粗检：至少要有 featureSave 或 processSave 之一，否则等于空档
			bool hasFeature = loaded.featureSave != null && loaded.featureSave.Count > 0;
			bool hasProcess = loaded.processSave != null && loaded.processSave.Count > 0;
			if (!hasFeature && !hasProcess)
			{
				reason = "档内没有 featureSave/processSave（空档）";
				return false;
			}
			return true;
		}
		catch (Exception ex)
		{
			reason = "读取关卡进度失败：" + ex.GetBaseException().Message;
			return false;
		}
	}

	/// <summary>自检两次都失败 ⇒ 用备份把主档放回去（宁可用旧档，也不留一个读不出来的新档）。</summary>
	private void RestorePrimaryFromBackup(string primary)
	{
		try
		{
			string backup = NativeBackupPath(primary);
			if (!IoExists(backup))
			{
				DiagErr("rollback skip: no backup available, primary left as-is path=" + primary);
				NotifyCorrupt(primary, "写入后回读失败，且没有可回退的备份");
				return;
			}
			if (!IoCopy(backup, primary, "rollback-backup"))
			{
				return;
			}
			Diag("rollback ok restored primary from=" + backup + " size=" + FileSize(primary));
			NotifyCorrupt(primary, "写入后回读失败，已回退到备份");
		}
		catch (Exception ex)
		{
			DiagErr("rollback fail err=" + ex.GetBaseException().Message + " primary=" + primary);
		}
	}

	/// <summary>
	/// ★ 原生进度路径（**逐字照抄** GameSaveManager.GetLevelProgressPath 的 identity==null 分支
	/// 与 _GetUserCurrentSafe，见 Core/GameSaveManager/GameSaveManager.cs:1259-1266、1322-1325）。
	/// 用户名为空时返回 ""（宿主同样视为"不可存/不可读"，此时一律不存不读）。
	/// </summary>
	private static string NativeProgressPath(string levelName)
	{
		if (!TryResolveUserSafe(out string safe)) { return ""; }
		if (string.IsNullOrEmpty(levelName)) { return ""; }
		return $"user://Csharp/Progress/{safe}/{levelName}.tres";
	}

	/// <summary>★ 宿主备份档名（照抄 GameSaveManager.ProgressBackupPath，见同文件 1471-1474）。</summary>
	private static string NativeBackupPath(string primary)
	{
		return primary.TrimSuffix(".tres") + ".pre-relaxed.tres";
	}

	/// <summary>
	/// ★ v3.4.0（B.1）verified 副本名：`X.verified.tres`（覆盖式，只保留最近一份）。
	/// 语义：**最近一次"写后回读自检通过"的档**，与宿主 `.pre-relaxed.tres`（最早那份好档）互补。
	/// </summary>
	private static string NativeVerifiedPath(string primary)
	{
		return primary.TrimSuffix(".tres") + ".verified.tres";
	}

	/// <summary>★ v3.4.0（A）恢复保护期内不写主档时，改为写这个旁路文件（不参与恢复）。</summary>
	private static string NativeRecoverSidePath(string primary)
	{
		return primary.TrimSuffix(".tres") + ".recover.tres";
	}

	/// <summary>
	/// 用户名脱敏：**必须与宿主 _safeCharRegex = new Regex("[^\\\\w\\\\-\\\\.]") 完全一致**
	/// （GameSaveManager.cs:104）。曾经用 [^\\w-]（少了 \\.），用户名含 "." 时本 Mod 写进
	/// 一个宿主永远不看的目录 ⇒ 玩家"明明有存档"却报"文件不存在"。
	/// </summary>
	private static readonly System.Text.RegularExpressions.Regex UserSafeRegex =
		new System.Text.RegularExpressions.Regex("[^\\w\\-\\.]", System.Text.RegularExpressions.RegexOptions.Compiled);

	private static bool TryResolveUserSafe(out string safe)
	{
		safe = "";
		string user = "";
		try { user = GameSaveManager.Instance.GetUserCurrent(); } catch { }
		if (string.IsNullOrWhiteSpace(user)) { return false; }
		safe = UserSafeRegex.Replace(user, "_");
		return true;
	}

	/// <summary>
	/// 判断关卡名里的日期（`DailyLevel-yyyy-MM-dd-<id>` 的第 12~21 字符）是不是**今天**。
	/// <para>返回 true = 是今天 / 解析不出来（保守按"不过期"处理，绝不误杀存档）。</para>
	///
	/// ★★★ v3.5.0 修复（玩家实测精确定位，本作 BCL 裁剪的又一例）：
	///   旧实现用 `DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
	///   DateTimeStyles.None, out parsed)`。本作运行时**裁掉了一批 BCL 方法** —— 注意
	///   "System.Private.CoreLib.dll 里能搜到 TryParseExact 字符串"**不能**当证据
	///   （元数据还在、实现被裁），真实调用抛：
	///       Method not found: Boolean System.DateTime.TryParseExact(...)
	///   而它抛在 `TickRestoreArm` 链上、被外层 catch 吞掉后立刻"本次恢复永久放弃"
	///   （`hasProgress` 没设）⇒ 玩家看到的"第一次能读、之后再也读不了"。
	///   同类旧账：`PropertyInfo.get_CanWrite`（DoubleTracking v1.4.5）、`Time.GetTicksUsec` 溢出。
	///
	/// ★ 现方案（**已由玩家在 0.31 发布版 / 0.32-alpha1 两套原生裁剪运行库上各跑 27 项断言验证**）：
	///   完全不碰任何"带格式串的日期解析 API"，只用：
	///     · `DateTime.Now` 的 `Year/Month/Day` **属性 getter**（实测未被裁；只读三个 int）
	///     · 纯字符/整数运算逐位解析关卡名里的年月日
	///     · 手写闰年与月长校验（含 400 年规则）
	///   解析失败（长度/分隔符/非数字/非法月日）⇒ 一律**不判过期**（交给读取器自检）。
	/// </summary>
	private static bool IsExpiredLevelName(string levelName)
	{
		int y = 0, m = 0, d = 0;
		try
		{
			// ★ 只用 getter，不用 ToString(格式串) / TryParseExact 等"带格式解析"的重载
			DateTime now = DateTime.Now;
			y = now.Year;
			m = now.Month;
			d = now.Day;
		}
		catch (Exception ex)
		{
			// 极端情况（连 DateTime.Now 都被裁）：退回引擎日期串；再失败就"不判过期"
			Diag("DateTime.Now 不可用，改用引擎日期（按不判过期处理）：" + ex.GetBaseException().Message);
			string today = TodayString();
			if (today.Length == 10
				&& int.TryParse(today.Substring(0, 4), out int ey)
				&& int.TryParse(today.Substring(5, 2), out int em)
				&& int.TryParse(today.Substring(8, 2), out int ed))
			{
				y = ey; m = em; d = ed;
			}
			else { return false; }
		}
		return IsExpiredLevelNameOnDate(levelName, y, m, d);
	}

	/// <summary>
	/// **可离线单测的纯函数**：给定"今天"的年月日，判断关卡名是否属于往日。
	/// 返回 true = 过期（不是今天）；false = 今天 / 解析失败（不判过期）。
	/// 依赖它的 QA 用反射直调本方法即可覆盖：同日/昨日/未来/闰年/非法日期等全部分支。
	/// </summary>
	private static bool IsExpiredLevelNameOnDate(string levelName, int todayYear, int todayMonth, int todayDay)
	{
		// "DailyLevel-" 占 0..10，日期占 11..20（yyyy-MM-dd），位置 15/18 必须是 '-'
		if (string.IsNullOrEmpty(levelName) || levelName.Length < 21) { return false; }
		if (levelName[15] != '-' || levelName[18] != '-') { return false; }

		int year = ReadDateDigits(levelName, 11, 4);
		int month = ReadDateDigits(levelName, 16, 2);
		int day = ReadDateDigits(levelName, 19, 2);
		if (year < 1 || month < 1 || month > 12 || day < 1) { return false; }

		// 手写闰年（含 400 年规则）与月长校验 —— 不做任何 DateTime 构造/解析
		bool leap = (year % 4 == 0) && (year % 100 != 0 || year % 400 == 0);
		int daysInMonth = month == 2 ? (leap ? 29 : 28)
			: (month == 4 || month == 6 || month == 9 || month == 11) ? 30 : 31;
		if (day > daysInMonth) { return false; }

		return year != todayYear || month != todayMonth || day != todayDay;
	}

	/// <summary>逐位取数字（不做字符串→数字转换，避免另触裁剪的 Parse 重载）。非法字符返回 -1。</summary>
	private static int ReadDateDigits(string text, int start, int length)
	{
		int result = 0;
		for (int i = start; i < start + length; i++)
		{
			if (i < 0 || i >= text.Length) { return -1; }
			int digit = text[i] - '0';
			if (digit < 0 || digit > 9) { return -1; }
			result = result * 10 + digit;
		}
		return result;
	}

	/// <summary>
	/// "今天"的本地日期串（yyyy-MM-dd），**只依赖 Godot 引擎**（`Time.GetDateStringFromSystem`，
	/// 已在 GodotSharp.dll 里确认存在，由 C++ 侧提供，不走 BCL）。
	/// 仅作为 `DateTime.Now` 不可用时的兜底；返回 "" 表示取不到（调用方按"不判过期"处理）。
	/// </summary>
	private static string TodayString()
	{
		try
		{
			string s = Time.GetDateStringFromSystem();   // 例："2026-10-28"
			if (!string.IsNullOrEmpty(s) && s.Length >= 10) { return s.Substring(0, 10); }
		}
		catch { }
		return "";
	}

	/// <summary>
	/// 取证档的时间戳（yyyyMMdd-HHmmss）。同样**只用引擎接口**：
	/// `Time.GetDateStringFromSystem()`（2026-10-28）+ `Time.GetTimeStringFromSystem()`（17:03:39）。
	/// 不用 `DateTime.Now.ToString("格式串")` —— 那属于"带格式解析"一族，本作已证实会裁剪同类重载
	/// （`TryParseExact` 就是被裁的），发布日期不该赌它。
	/// 取不到时返回 ""（调用方会用 "unknown" 兜底，不影响取证）。
	/// </summary>
	private static string FailedSnapshotStamp()
	{
		try
		{
			string d = Time.GetDateStringFromSystem();
			string t = Time.GetTimeStringFromSystem();
			if (string.IsNullOrEmpty(d) || string.IsNullOrEmpty(t)) { return ""; }
			string dd = d.Replace("-", "");
			string tt = t.Replace(":", "");
			if (dd.Length < 8 || tt.Length < 6) { return ""; }
			return dd.Substring(0, 8) + "-" + tt.Substring(0, 6);
		}
		catch { return ""; }
	}

	/// <summary>内容损坏/反序列化失败：提示要具体（保留原档 + 说清坏的是哪个文件）。</summary>
	private void NotifyCorrupt(string path, string reason)
	{
		Notify("存档损坏，未恢复（原文件已保留）\n文件：" + path + "\n原因：" + reason, 480);
	}

	/// <summary>
	/// 删除进度档（通关/失败防刷）。v3.2：精确删已知文件，不再用通配符扫目录。
	/// v3.4.0：新增删 `.verified.tres` / `.recover.tres`（否则清档后 verified 仍存在，
	/// 下一局可能被 verified 回退"复活"上一局进度，破坏防刷语义）；全部走长路径 IoDelete。
	/// </summary>
	private void DeleteSave(string levelName)
	{
		try
		{
			string path = NativeProgressPath(levelName);
			if (string.IsNullOrEmpty(path)) { return; }
			string[] targets =
			{
				path,                                    // 主进度档
				NativeBackupPath(path),                  // 宿主备份档 .pre-relaxed.tres
				NativeVerifiedPath(path),                // ★ v3.4.0 verified 副本
				NativeRecoverSidePath(path),             // ★ v3.4.0 保护期旁路档
				path.TrimSuffix(".tres") + ".tmp.tres"   // 未落地的临时档
			};
			foreach (string p in targets)
			{
				if (!IoExists(p)) { continue; }
				if (IoDelete(p, "clear-save"))
				{
					Log("已删除：" + p);
				}
			}
			Diag("clear-save done level=" + levelName);
		}
		catch (Exception ex) { Warn("删除存档失败：" + ex.GetBaseException().Message); }
	}

	// ============================================================ 小工具

	private static string Globalize(string userPath)
	{
		try { return ProjectSettings.GlobalizePath(userPath); } catch { return userPath; }
	}

	/// <summary>
	/// ★ v3.4.0（C）长路径加固：把 `user://…` 换成**可用于 .NET 文件 API 的绝对路径**。
	///
	/// 背景：那位玩家的用户名脱敏后 >100 字符，实际路径形如
	/// `C:\Users\xxx\AppData\Roaming\Godot\app_userdata\植物大战僵尸杂交版\Csharp\Progress\
	///  _rainbow_freq_1.0_…_爱猫人__wave___rainbow_\DailyLevel-2026-10-28-654.tres`
	/// 轻松超过 Windows 传统 MAX_PATH(260)。Godot 自己的 `FileAccess` 走引擎封装，行为与 .NET
	/// 不同（能读写）；但本 Mod 手写的 `System.IO.File.Move/Copy/Delete/FileInfo` 在 .NET 上
	/// **默认受 MAX_PATH 限制** ⇒ 会出现"ResourceSaver 写成功、Move 失败"这种半成功。
	///
	/// 加固方式：Windows 上对**绝对路径**加 `\\?\` 前缀（Long Path 语法，绕过 260 限制；
	/// 要求路径已是绝对路径且用反斜杠，这里同时把 `/` 归一化为 `\`）。
	/// 非 Windows 原样返回（Unix 没有这个限制）。
	/// 注意：`\\?\` 前缀只用于 .NET IO API，**不能**传给 Godot 的 FileAccess/ResourceLoader，
	/// 那两处继续用 original `user://` 路径。
	/// </summary>
	private static string IoPath(string userPath)
	{
		string abs = Globalize(userPath);
		return LongPath(abs);
	}

	/// <summary>纯绝对路径 → 长路径形式（见 IoPath 注释）。失败则原样返回。</summary>
	private static string LongPath(string abs)
	{
		try
		{
			if (string.IsNullOrEmpty(abs)) { return abs; }
			if (System.IO.Path.DirectorySeparatorChar != '\\') { return abs; }   // 非 Windows
			if (abs.StartsWith(@"\\?\", StringComparison.Ordinal)) { return abs; }
			string norm = abs.Replace('/', '\\');
			if (!System.IO.Path.IsPathRooted(norm)) { return abs; }
			// UNC（\\server\share）→ \\?\UNC\server\share
			if (norm.StartsWith(@"\\", StringComparison.Ordinal))
			{
				return @"\\?\UNC\" + norm.Substring(2);
			}
			return @"\\?\" + norm;
		}
		catch { return abs; }
	}

	/// <summary>路径长度（诊断用；返回 -1 表示无法计算）。</summary>
	private static int PathLen(string userPath)
	{
		try
		{
			string abs = Globalize(userPath);
			return string.IsNullOrEmpty(abs) ? -1 : abs.Length;
		}
		catch { return -1; }
	}

	private static bool IoExists(string userPath)
	{
		try
		{
			if (string.IsNullOrEmpty(userPath)) { return false; }
			// 先用 Godot 的 FileAccess 判存在（它对 user:// 与长路径最稳），
			// 再用 .NET 兜底（有些情况下 FileAccess 对绝对路径不生效）
			if (Godot.FileAccess.FileExists(userPath)) { return true; }
			return System.IO.File.Exists(IoPath(userPath));
		}
		catch { return false; }
	}

	private static bool IoDelete(string userPath, string stage = "delete")
	{
		try
		{
			string g = IoPath(userPath);
			if (!System.IO.File.Exists(g)) { return true; }
			System.IO.File.Delete(g);
			return true;
		}
		catch (Exception ex)
		{
			DiagErr("io fail stage=" + stage + "(delete) err=" + ex.GetBaseException().Message
				+ " path=" + userPath + " len=" + PathLen(userPath));
			return false;
		}
	}

	private static bool IoCopy(string src, string dst, string stage = "copy", bool overwrite = true)
	{
		try
		{
			string gs = IoPath(src);
			string gd = IoPath(dst);
			EnsureDir(dst, stage);
			System.IO.File.Copy(gs, gd, overwrite);
			return true;
		}
		catch (Exception ex)
		{
			DiagErr("io fail stage=" + stage + "(copy) err=" + ex.GetBaseException().Message
				+ " src=" + src + " len=" + PathLen(src)
				+ " dst=" + dst + " len=" + PathLen(dst));
			return false;
		}
	}

	private static bool IoMove(string src, string dst, string stage = "move", bool overwrite = true)
	{
		try
		{
			string gs = IoPath(src);
			string gd = IoPath(dst);
			EnsureDir(dst, stage);
			System.IO.File.Move(gs, gd, overwrite);
			return true;
		}
		catch (Exception ex)
		{
			DiagErr("io fail stage=" + stage + "(move) err=" + ex.GetBaseException().Message
				+ " src=" + src + " len=" + PathLen(src)
				+ " dst=" + dst + " len=" + PathLen(dst));
			return false;
		}
	}

	/// <summary>确保目标文件所在目录存在（用长路径形式创建）。</summary>
	private static void EnsureDir(string userFile, string stage)
	{
		try
		{
			string g = IoPath(userFile);
			string dir = System.IO.Path.GetDirectoryName(g);
			if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
			{
				System.IO.Directory.CreateDirectory(dir);
			}
		}
		catch (Exception ex)
		{
			DiagErr("io fail stage=" + stage + "(mkdir) err=" + ex.GetBaseException().Message
				+ " path=" + userFile + " len=" + PathLen(userFile));
		}
	}

	private static long FileSize(string userPath)
	{
		try
		{
			string g = IoPath(userPath);
			return System.IO.File.Exists(g) ? new System.IO.FileInfo(g).Length : -1;
		}
		catch { return -1; }
	}

	/// <summary>兼容旧调用名（内部已改为长路径安全的 IoDelete）。</summary>
	private static void DeleteGlobalized(string userPath)
	{
		IoDelete(userPath, "tmp-cleanup");
	}

	// ============================================================ 提示 / 配置 / 日志

	private const string KAutoLoadEnabled = "autoLoad";   // 复用配置键（bool）
	private const string KStrictRestore = "strictRestore";// ★ v3.3.0 保守开关（bool，默认 false）

	private Label _notifyLabel;
	private int _notifyLeft;

	private void Notify(string msg, int frames = 180)
	{
		if (!ReadBool(KNotify, true)) { return; }
		try
		{
			if (_tree == null || _tree.Root == null) { return; }
			if (_notifyLabel == null || !GodotObject.IsInstanceValid(_notifyLabel))
			{
				CanvasLayer layer = new CanvasLayer();
				layer.Name = "DailySaveNotify";
				layer.Layer = 130;
				Label l = new Label();
				l.Name = "Text";
				l.Position = new Vector2(24f, 150f);
				l.AddThemeFontSizeOverride("font_size", 18);
				l.AddThemeColorOverride("font_color", new Color(0.55f, 0.9f, 0.55f));
				l.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f));
				l.AddThemeConstantOverride("outline_size", 5);
				l.MouseFilter = Control.MouseFilterEnum.Ignore;
				layer.AddChild(l, false, Node.InternalMode.Disabled);
				_tree.Root.AddChild(layer, false, Node.InternalMode.Disabled);
				_notifyLabel = l;
			}
			_notifyLabel.Text = msg;
			_notifyLeft = frames > 0 ? frames : 180;
		}
		catch { }
	}

	private void TickNotify()
	{
		try
		{
			if (_notifyLabel == null || !GodotObject.IsInstanceValid(_notifyLabel)) { return; }
			if (_notifyLeft > 0)
			{
				if (--_notifyLeft == 0) { _notifyLabel.Text = ""; }
			}
		}
		catch { }
	}

	private bool ReadBool(string key, bool fallback)
	{
		if (_settings == null) { return fallback; }
		try
		{
			object v = _settings.Get<object>(key);
			if (v is bool b) { return b; }
			if (v is string s && bool.TryParse(s, out bool sb)) { return sb; }
			if (v is System.Text.Json.JsonElement je)
			{
				if (je.ValueKind == System.Text.Json.JsonValueKind.True) { return true; }
				if (je.ValueKind == System.Text.Json.JsonValueKind.False) { return false; }
			}
		}
		catch { }
		return fallback;
	}

	/// <summary>
	/// ★ D（v3.3.0 最重要的一条）：**关键路径诊断日志，不受 enableLog 门控，无条件输出**。
	/// 只用于"一次会话只会打几行"的关键节点（进入关卡 / 预演结果 / 写档 / 回读自检 / 最终决策），
	/// 不要往这里塞高频日志（每帧/每次保存的细节仍走 Log）。
	/// </summary>
	private static void Diag(string msg)
	{
		try { GD.Print(P + msg); } catch { }
	}

	/// <summary>关键路径的失败诊断（Error 级，同样不受 enableLog 门控）。</summary>
	private static void DiagErr(string msg)
	{
		try { GD.PrintErr(P + msg); } catch { }
	}

	private void Log(string msg)
	{
		// ★ v3.3.0：恢复成"由 enableLog 真正控制"（默认 false ⇒ 静默）。
		//   注意取反方向：!ReadBool(...) 为 true 时提前 return —— 开关关就不打。
		if (!ReadBool(KLog, false)) { return; }
		try { GD.Print(P + msg); } catch { }
	}

	private void Warn(string msg)
	{
		if (!ReadBool(KLog, false)) { return; }
		try { GD.PrintErr(P + msg); } catch { }
	}

	private void WarnOnce(string tag, string msg)
	{
		if (!ReadBool(KLog, false)) { return; }
		try { GD.PrintErr(P + "[" + tag + "] " + msg); } catch { }
	}
}
