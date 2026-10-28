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
			Log("初始化完成（v3.3.0：写后回读自检 + .pre-relaxed 备份；读档区分暂时性失败并重试；关键路径无条件日志）。");
			Diag("mod loaded v3.3.0");
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
			DiagErr("arm tick 异常：" + ex.GetBaseException().Message);
			_armDecided = true;
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
		string ctlState = "?";
		try { ctlState = "running=" + ctl.isGameRunning + " fail=" + ctl.isGameFail + " hasProgress=" + ctl.hasProgress; } catch { }
		Diag("enter daily level=" + levelName
			+ " user=" + (hasUser ? safe : "(空)")
			+ " primary=" + (string.IsNullOrEmpty(primary) ? "(无)" : primary)
			+ " primaryExists=" + hasPrimary
			+ " backupExists=" + hasBackup
			+ " " + ctlState
			+ " strictRestore=" + ReadBool(KStrictRestore, false));
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
			// 内容类：明确损坏 ⇒ 提示一次，原档保留
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

			// ② 目录（结果以前被忽略 ⇒ 现在检查并打印）
			Error dirErr = DirAccess.MakeDirRecursiveAbsolute(path.GetBaseDir());
			if (dirErr != Error.Ok && dirErr != Error.AlreadyExists)
			{
				DiagErr("save fail stage=mkdir err=" + dirErr + " path=" + path + " dir=" + path.GetBaseDir());
				return;
			}

			// ③ A.3：Move 之前把旧档复制成 .pre-relaxed.tres
			//    （与宿主 EnsureProgressBackup:1531-1542 语义一致：**仅当备份不存在时生成**，
			//     这样备份保留的是"最早那份好档"，宿主读主档失败时会自动回退到它）
			EnsureBackupBeforeOverwrite(path);

			// ④ tmp + 原子 Move（宿主 SaveResourceAtomically:484-556 的等价实现；
			//    窗口 500-509 会先删残留 tmp，我们以前漏了这一步，这里补上）
			string tmp = path.TrimSuffix(".tres") + ".tmp.tres";
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

			try
			{
				System.IO.File.Move(Globalize(tmp), Globalize(path), true);
			}
			catch (Exception moveEx)
			{
				DiagErr("save fail stage=move err=" + moveEx.GetBaseException().Message + " from=" + tmp + " to=" + path);
				try
				{
					System.IO.File.Copy(Globalize(tmp), Globalize(path), true);
					DeleteGlobalized(tmp);
				}
				catch (Exception copyEx)
				{
					DiagErr("save fail stage=copy err=" + copyEx.GetBaseException().Message + " to=" + path);
					return;
				}
			}

			long size = FileSize(path);
			Diag("save ok path=" + path + " size=" + size);

			// ⑤ A.2：写后**回读自检**（宿主同款读取器 + CanLoad）
			if (VerifySaved(path, out string verifyReason))
			{
				Diag("verify ok path=" + path);
				if (!quiet)
				{
					string msg = "每日挑战进度已保存";
					Log(msg + " → " + path);
					Notify(msg);
				}
				return;
			}

			DiagErr("verify fail reason=" + verifyReason + " path=" + path);

			// ⑥ 重试一次：**直接写最终路径**（绕过 tmp 环节，排除 Move 造成的损坏）
			Error err2 = Error.Ok;
			try { err2 = ResourceSaver.Save(snapshot, path, ResourceSaver.SaverFlags.None); }
			catch (Exception retryEx)
			{
				DiagErr("verify retry fail stage=saver(throw) err=" + retryEx.GetBaseException().Message + " path=" + path);
				err2 = Error.Failed;
			}
			if (err2 == Error.Ok && VerifySaved(path, out string retryReason))
			{
				Diag("verify ok(after direct-write retry) path=" + path + " size=" + FileSize(path));
				return;
			}
			DiagErr("verify fail after retry err=" + err2
				+ " reason=" + (err2 == Error.Ok ? verifyReason : "(saver " + err2 + ")") + " path=" + path);

			// ⑦ 两次自检都失败 ⇒ 把旧档放回去（不覆盖好档），让玩家下次至少还有上一份可读的档
			RestorePrimaryFromBackup(path);
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
			string srcG = Globalize(primary);
			if (!System.IO.File.Exists(srcG)) { return; }
			string backup = NativeBackupPath(primary);
			string dstG = Globalize(backup);
			if (System.IO.File.Exists(dstG))
			{
				Log("旧档备份已存在，保持不动：" + backup);
				return;
			}
			System.IO.File.Copy(srcG, dstG, false);
			Diag("backup ok from=" + primary + " to=" + backup + " size=" + FileSize(backup));
		}
		catch (Exception ex)
		{
			DiagErr("backup fail err=" + ex.GetBaseException().Message + " primary=" + primary);
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
			if (!System.IO.File.Exists(Globalize(backup)))
			{
				DiagErr("rollback skip: no backup available, primary left as-is path=" + primary);
				NotifyCorrupt(primary, "写入后回读失败，且没有可回退的备份");
				return;
			}
			System.IO.File.Copy(Globalize(backup), Globalize(primary), true);
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
	/// 关卡名里带日期（DailyLevel-&lt;yyyy-MM-dd&gt;-&lt;id&gt;）⇒ 与"今天"不同即视为跨天过期。
	/// 过期档不恢复、也不删除（原档保留），静默按正常新局走。
	/// 日期解析失败 ⇒ 一律不判过期（交给读取器自检，避免误杀）。
	/// </summary>
	private static bool IsExpiredLevelName(string levelName)
	{
		const int prefixLen = 11;   // "DailyLevel-".Length
		if (string.IsNullOrEmpty(levelName) || levelName.Length < prefixLen + 10) { return false; }
		string date = levelName.Substring(prefixLen, 10);
		if (!DateTime.TryParseExact(date, "yyyy-MM-dd",
				System.Globalization.CultureInfo.InvariantCulture,
				System.Globalization.DateTimeStyles.None, out DateTime parsed))
		{
			return false;   // 名字里没有可解析的日期 ⇒ 不判过期，交给读取器自检
		}
		return parsed.Date != DateTime.Now.Date;
	}

	/// <summary>内容损坏/反序列化失败：提示要具体（保留原档 + 说清坏的是哪个文件）。</summary>
	private void NotifyCorrupt(string path, string reason)
	{
		Notify("存档损坏，未恢复（原文件已保留）\n文件：" + path + "\n原因：" + reason, 480);
	}

	/// <summary>删除进度档（通关/失败防刷）。v3.2：改为精确删三个已知文件，不再用通配符扫目录。</summary>
	private void DeleteSave(string levelName)
	{
		try
		{
			string path = NativeProgressPath(levelName);
			if (string.IsNullOrEmpty(path)) { return; }
			string[] targets =
			{
				path,                                   // 主进度档
				NativeBackupPath(path),                 // 宿主备份档 .pre-relaxed.tres
				path.TrimSuffix(".tres") + ".tmp.tres"  // 未落地的临时档
			};
			foreach (string p in targets)
			{
				try
				{
					string g = ProjectSettings.GlobalizePath(p);
					if (System.IO.File.Exists(g)) { System.IO.File.Delete(g); }
				}
				catch (Exception ex) { Warn("删除存档失败（" + p + "）：" + ex.GetBaseException().Message); }
			}
		}
		catch (Exception ex) { Warn("删除存档失败：" + ex.GetBaseException().Message); }
	}

	// ============================================================ 小工具

	private static string Globalize(string userPath)
	{
		try { return ProjectSettings.GlobalizePath(userPath); } catch { return userPath; }
	}

	private static void DeleteGlobalized(string userPath)
	{
		try
		{
			string g = Globalize(userPath);
			if (System.IO.File.Exists(g)) { System.IO.File.Delete(g); }
		}
		catch { }
	}

	private static long FileSize(string userPath)
	{
		try
		{
			string g = Globalize(userPath);
			return System.IO.File.Exists(g) ? new System.IO.FileInfo(g).Length : -1;
		}
		catch { return -1; }
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
