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
	/// <summary>已设过 hasProgress 的控制器 instanceId（防重复设）。</summary>
	private ulong _armedList;

	public void Initialize(XWModRuntimeContext context)
	{
		SponsorQrRoot = context.PackageRoot;
		try
		{
			_context = context;
			try { _settings = context.Settings; }
			catch (Exception ex) { Warn("配置不可用：" + ex.GetBaseException().Message); }
			Log("初始化完成（v3.2：原生路径写在宿主同一位置；只有宿主读取器自检通过才武装恢复）。");
		}
		catch (Exception ex) { try { GD.PrintErr(P + "Initialize 异常（已吞）：" + ex.Message); } catch { } }
	}

	public void OnAllModsLoaded()
	{
		try
		{
			if (_started) { return; }
			_tree = Engine.GetMainLoop() as SceneTree;
			if (_tree == null) { Warn("拿不到 SceneTree，本 Mod 不生效。"); return; }
			_tick = Callable.From(new Action(OnFrame));
			_tree.Connect("process_frame", _tick);
			Subscribe();
			_started = true;
			Log("已挂载 process_frame。");
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
		finally { _started = false; }
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

			// ★ 恢复钩子（v3.2）：控制器出现后、状态机 SendStateEvent 前的最早一帧做一次决策。
			//   只有 TryArmRestore 确认「宿主稍后一定读得到」时才武装 hasProgress；
			//   「没档 / 已清档 / 跨天过期」一律静默按正常新局走，只有内容损坏才提示。
			if (ReadBool(KAutoLoadEnabled, true) && _armedList != ctl.GetInstanceId() && !ctl.isGameRunning)
			{
				_armedList = ctl.GetInstanceId();
				if (TryArmRestore(_dailyLevelName, ctl))
				{
					ctl.hasProgress = true;
					Log("存档自检通过，已设置 hasProgress=true ⇒ 游戏将自动走原生恢复：" + _dailyLevelName);
					Notify("发现每日挑战存档，恢复中…");
				}
			}

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
				}
			}
		}
		catch (Exception ex)
		{
			WarnOnce("frame", "每帧异常：" + ex.GetBaseException().Message);
		}
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

	private void TrySaveByName(string levelName, bool quiet = false)
	{
		try
		{
			GameSaveManager save = GameSaveManager.Instance;
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (save == null || mgr == null || !GodotObject.IsInstanceValid(mgr.currentControl))
			{
				if (!quiet) { Warn("保存跳过：存档管理器或战斗控制器不可用。"); }
				return;
			}
			TowerDefenseControlNew ctl = mgr.currentControl;
			if (!ctl.isGameRunning || ctl.isGameFail) { if (!quiet) { Log("保存跳过：游戏未在运行或已失败。"); } return; }
			try
			{
				if (GodotObject.IsInstanceValid(ctl.levelControl) && ctl.levelControl.awardCreate) { if (!quiet) { Log("保存跳过：本关已通关。"); } return; }
			}
			catch { }
			if (Global.IsMultiplayerMode) { if (!quiet) { Log("保存跳过：联机模式。"); } return; }

			string reason;
			if (!ctl.CanCreateProgressSave(out reason)) { Log("保存延后（不安全检查点）：" + reason); return; }

			// ★ 全量快照 → 原子写【原生路径】（SaveResourceAtomically 是 internal，手写等价逻辑）
			var snapshot = new TowerDefenseLevelSaveConfigCSharp();
			snapshot.Save();
			string path = NativeProgressPath(levelName);
			if (string.IsNullOrEmpty(path)) { if (!quiet) { Warn("保存跳过：当前用户名为空。"); } return; }
			DirAccess.MakeDirRecursiveAbsolute(path.GetBaseDir());
			string tmp = path.TrimSuffix(".tres") + ".tmp.tres";
			Error err = ResourceSaver.Save(snapshot, tmp, ResourceSaver.SaverFlags.None);
			if (err == Error.Ok)
			{
				string dst = ProjectSettings.GlobalizePath(path);
				string tmpG = ProjectSettings.GlobalizePath(tmp);
				// ★ v3.2：改成"覆盖式 Move"，**不再"先删旧档再移动"** —— 旧写法一旦 Move 抛异常
				//   （杀软/云同步占用、磁盘满），旧档已被删、新档没落地 ⇒ 存档凭空消失，
				//   而下一关进来就正好撞上"文件不存在"。这里失败也只会留下旧档。
				try
				{
					System.IO.File.Move(tmpG, dst, true);
				}
				catch (Exception moveEx)
				{
					Warn("保存落盘失败（Move 覆盖）：" + moveEx.GetBaseException().Message);
					try { System.IO.File.Copy(tmpG, dst, true); System.IO.File.Delete(tmpG); }
					catch (Exception copyEx) { Warn("保存落盘失败（Copy 兜底）：" + copyEx.GetBaseException().Message); return; }
				}
				if (!quiet)
				{
					string msg = "每日挑战进度已保存";
					Log(msg + " → " + path);
					Notify(msg);
				}
			}
			else
			{
				Warn("保存失败：" + err + " path=" + path);
			}
		}
		catch (Exception ex) { Warn("保存异常：" + ex.GetBaseException().Message); }
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
	/// 用户名脱敏：**必须与宿主 _safeCharRegex = new Regex("[^\\w\\-\\.]") 完全一致**
	/// （GameSaveManager.cs:104）。曾经用 [^\w-]（少了 \.），用户名含 "." 时本 Mod 写进
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
			return false;   // 名字里没有可解析的日期 ⇒ 不判过期，交给下面的读取器自检
		}
		return parsed.Date != DateTime.Now.Date;
	}

	/// <summary>
	/// ★ v3.2 核心：决定是否武装 hasProgress。返回 true ⇔ 宿主稍后**一定能**读到档。
	/// 返回 false 时绝不武装（这样宿主的 RejectProgressLoad 弹窗永远不会因"没有可用档"而出现）。
	/// </summary>
	private bool TryArmRestore(string levelName, TowerDefenseControlNew ctl)
	{
		if (ctl == null || !GodotObject.IsInstanceValid(ctl)) { return false; }

		string primary = NativeProgressPath(levelName);
		if (string.IsNullOrEmpty(primary))
		{
			Log("不武装：当前用户名为空（宿主同样把它当作不可存读）⇒ 静默按正常新局走。");
			return false;
		}

		bool hasPrimary = Godot.FileAccess.FileExists(primary);
		bool hasBackup = Godot.FileAccess.FileExists(NativeBackupPath(primary));
		if (!hasPrimary && !hasBackup)
		{
			// 情况①全新进入（从来没存过）／情况②存档已被死亡/通关清掉
			Log("不武装：本关没有进度档（全新进入或已清档）⇒ 静默按正常新局走。");
			return false;
		}

		if (IsExpiredLevelName(levelName))
		{
			// 情况③跨天存档过期
			Log("不武装：存档属于往日关卡（跨天过期）⇒ 静默按正常新局走，原档保留：" + primary);
			return false;
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
		if (loadable) { return true; }

		if (string.IsNullOrWhiteSpace(reason)) { reason = "未知原因"; }
		Warn("存档存在但无法读取，已跳过恢复（原档保留）：" + reason + " path=" + primary);
		NotifyCorrupt(primary, reason);
		return false;
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

	// ============================================================ 提示 / 配置 / 日志

	private const string KAutoLoadEnabled = "autoLoad";   // 复用配置键（bool）

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

	private void Log(string msg)
	{
		if (true)    /* ★ 用户要求：日志默认关闭（原：!ReadBool(KLog, false)） */ { return; }
		try { GD.Print(P + msg); } catch { }
	}

	private void Warn(string msg)
	{
		if (true)    /* ★ 用户要求：日志默认关闭（原：!ReadBool(KLog, false)） */ { return; }
		// ★ 2026-10-14：该输出原先绕过日志开关，现统一受 enableLog 控制
		try { GD.PrintErr(P + msg); } catch { }
	}

	private void WarnOnce(string tag, string msg)
	{
		if (true)    /* ★ 用户要求：日志默认关闭（原：!ReadBool(KLog, false)） */ { return; }
		// ★ 2026-10-14：该输出原先绕过日志开关，现统一受 enableLog 控制
		try { GD.PrintErr(P + msg); } catch { }
	}
}
