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
			Log("初始化完成（v3：写原生路径 + hasProgress 钩子，恢复走游戏原生链）。");
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
			if (true)    /* ★ 用户要求：日志默认关闭（原：!ReadBool(KEnabled, true)） */ { _wasInDailyBattle = false; return; }

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

			// ★ 恢复钩子：控制器出现后、状态机 SendStateEvent 前的最早一帧，
			//   若有本关存档 ⇒ hasProgress = true，游戏自己走原生恢复链。
			if (ReadBool(KAutoLoadEnabled, true) && _armedList != ctl.GetInstanceId())
			{
				_armedList = ctl.GetInstanceId();
				if (HasSave(_dailyLevelName))
				{
					ctl.hasProgress = true;
					Log("发现存档，已设置 hasProgress=true ⇒ 游戏将自动走原生恢复：" + _dailyLevelName);
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
			string path = SavePath(levelName);
			DirAccess.MakeDirRecursiveAbsolute(path.GetBaseDir());
			string tmp = path.TrimSuffix(".tres") + ".tmp.tres";
			Error err = ResourceSaver.Save(snapshot, tmp, ResourceSaver.SaverFlags.None);
			if (err == Error.Ok)
			{
				string dst = ProjectSettings.GlobalizePath(path);
				string tmpG = ProjectSettings.GlobalizePath(tmp);
				if (System.IO.File.Exists(dst)) { System.IO.File.Delete(dst); }
				System.IO.File.Move(tmpG, dst);
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

	/// <summary>★ 原生进度路径（照抄 GameSaveManager.GetLevelProgressPath 的 identity==null 分支）。</summary>
	private static string SavePath(string levelName)
	{
		string user = "default";
		try { user = GameSaveManager.Instance.GetUserCurrent(); } catch { }
		if (string.IsNullOrWhiteSpace(user)) { user = "default"; }
		string safe = System.Text.RegularExpressions.Regex.Replace(user, "[^\\w-]", "_");
		return $"user://Csharp/Progress/{safe}/{levelName}.tres";
	}

	private bool HasSave(string levelName)
	{
		return Godot.FileAccess.FileExists(SavePath(levelName));
	}

	/// <summary>删除进度档（通关/失败防刷）。</summary>
	private void DeleteSave(string levelName)
	{
		try
		{
			string path = SavePath(levelName);
			if (Godot.FileAccess.FileExists(path))
			{
				string g = ProjectSettings.GlobalizePath(path);
				if (System.IO.File.Exists(g)) { System.IO.File.Delete(g); }
				// 原生备份文件也一并删（.tres 备份命名见 ProgressBackupPath）
				string dir = System.IO.Path.GetDirectoryName(g);
				string baseName = System.IO.Path.GetFileNameWithoutExtension(g);
				if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
				{
					foreach (string f in System.IO.Directory.GetFiles(dir, baseName + "*"))
					{
						try { System.IO.File.Delete(f); } catch { }
					}
				}
			}
		}
		catch (Exception ex) { Warn("删除存档失败：" + ex.Message); }
	}

	// ============================================================ 提示 / 配置 / 日志

	private const string KAutoLoadEnabled = "autoLoad";   // 复用配置键（bool）

	private Label _notifyLabel;
	private int _notifyLeft;

	private void Notify(string msg)
	{
		if (true)    /* ★ 用户要求：日志默认关闭（原：!ReadBool(KNotify, true)） */ { return; }
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
			_notifyLeft = 180;
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
