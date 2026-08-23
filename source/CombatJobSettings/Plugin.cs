using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace CombatJobSettings;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "Combat Job Settings V0.1.0";
    private const string CommandName = "/cjs";
    private bool windowOpen = false;
    private readonly WindowSystem windowSystem = new("CombatJobSettings");
    private readonly MainCjsWindow mainWindow;
    private readonly MiniCjsWindow miniWindow;
    private readonly Configuration config;
    private string status = "待機中";
    private string storagePathEdit = string.Empty;
    private string namedProfileNameEdit = string.Empty;
    private int namedProfileIndex = 0;
    private DefaultSettingsSnapshot? defaultSnapshot;
    private string requestedTab = string.Empty;
    private string currentMainTab = "概要";
    private string activeSettingsName = "未適用";
    internal float MiniBackgroundOpacity => Math.Clamp(config.MiniBackgroundOpacity, 0.05f, 1.00f);
    internal float MiniTitleOpacity => Math.Clamp(config.MiniTitleOpacity, 0.05f, 1.00f);
    private readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
        IncludeFields = true
    };

    // Wrath provides an official getter; this is the first true live-read path.
    private readonly ICallGateSubscriber<bool> wrathGetAutoRotationState;
    private bool wrathLiveAvailable;
    private bool wrathLiveAuto;
    private bool bossModLiveAvailable;
    private bool bmrLiveAvailable;
    private bool bmrAiLiveAvailable;
    private bool bmrFollowActiveBossModule;
    private bool rsrLiveAvailable;
    private bool rsrEngageLiveAvailable;
    private string rsrEngageMemberName = string.Empty;
    private string[] rsrEngageChoices = Array.Empty<string>();
    private string rsrLiveState = "取得不可";
    private bool wrathTargetLiveAvailable;
    private DateTime lastLivePoll = DateTime.MinValue;

    private static readonly (string Internal, string Japanese)[] RsrTargets =
    {
        ("Big", "大型の敵（Big）"),
        ("Small", "小型の敵（Small）"),
        ("HighHP", "現在HPが高い敵（HighHP）"),
        ("LowHP", "現在HPが低い敵（LowHP）"),
        ("HighHPPercent", "HP割合が高い敵（HighHPPercent）"),
        ("LowHPPercent", "HP割合が低い敵（LowHPPercent）"),
        ("HighMaxHp", "最大HPが高い敵（HighMaxHp）"),
        ("LowMaxHP", "最大HPが低い敵（LowMaxHP）"),
        ("Nearest", "最も近い敵（Nearest）"),
        ("Farthest", "最も遠い敵（Farthest）"),
    };

    private static readonly (string Internal, string Japanese)[] WrathTargets =
    {
        ("manual", "手動ターゲット（Manual）"),
        ("highest_max", "最大HPが最も高い敵（Highest Max）"),
        ("lowest_max", "最大HPが最も低い敵（Lowest Max）"),
        ("highest_current", "現在HPが最も高い敵（Highest Current）"),
        ("lowest_current", "現在HPが最も低い敵（Lowest Current）"),
        ("tank_target", "タンクが狙っている敵（Tank Target）"),
        ("nearest", "最も近い敵（Nearest）"),
        ("furthest", "最も遠い敵（Furthest）"),
    };

    private static readonly (string Internal, string Japanese)[] WrathHealerTargets =
    {
        ("manual", "手動ターゲット（Manual）"),
        ("highest_current", "現在HPが最も高い対象（Highest Current）"),
        ("lowest_current", "現在HPが最も低い対象（Lowest Current）"),
    };

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Service>();
        config = (pluginInterface.GetPluginConfig() as Configuration) ?? new Configuration();
        // Preserve an explicitly customized Mini background value; migrate only the former 70% default.
        if (config.Version < 11)
        {
            if (Math.Abs(config.MiniBackgroundOpacity - 0.70f) < 0.001f)
                config.MiniBackgroundOpacity = 0.50f;
            config.Version = 11;
            config.Save();
        }
        // CJS is intentionally hidden on game/plugin startup. Open it only when the user requests it.
        // Do not keep forcing the saved Mini flag here; the Window object owns the live open/close state.
        config.MiniWindowEnabled = false;
        InitializeStorage();
        LoadExternalProfiles();
        LoadDefaultSnapshot();
        if (defaultSnapshot == null && config.AutoApplyOnJobChange)
        {
            config.AutoApplyOnJobChange = false;
            config.Save();
        }
        storagePathEdit = config.StoragePath;
        wrathGetAutoRotationState = pluginInterface.GetIpcSubscriber<bool>("WrathCombo.GetAutoRotationState");

        mainWindow = new MainCjsWindow(this);
        miniWindow = new MiniCjsWindow(this);
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(miniWindow);
        miniWindow.IsOpen = false;

        Service.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Combat Job Settings V0.1.0 を開きます。"
        });
        Service.PluginInterface.UiBuilder.Draw += Draw;
        Service.PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        Service.ClientState.ClassJobChanged += OnClassJobChanged;
    }

    public void Dispose()
    {
        Service.ClientState.ClassJobChanged -= OnClassJobChanged;
        Service.PluginInterface.UiBuilder.Draw -= Draw;
        Service.PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        Service.CommandManager.RemoveHandler(CommandName);
        windowSystem.RemoveAllWindows();
    }

    private void OnCommand(string command, string args) { windowOpen = true; mainWindow.IsOpen = true; }
    private void OpenConfigUi() { windowOpen = true; mainWindow.IsOpen = true; }

    private uint CurrentJobId()
    {
        try { return Service.PlayerState.IsLoaded ? Service.PlayerState.ClassJob.RowId : 0; }
        catch { return 0; }
    }

    private void OnClassJobChanged(uint jobId)
    {
        if (!config.AutoApplyOnJobChange)
            return;

        var resolved = TryResolveSavedProfile(jobId);
        if (resolved.Profile != null)
            ApplyProfile(resolved.Profile, $"JOB変更 / {resolved.Source}");
        else
            status = $"{JobName(jobId)}：このJOBには設定がありません";
    }

    private void PollLiveValues()
    {
        if ((DateTime.UtcNow - lastLivePoll).TotalMilliseconds < 250)
            return;
        lastLivePoll = DateTime.UtcNow;

        try
        {
            wrathLiveAuto = wrathGetAutoRotationState.InvokeFunc();
            wrathLiveAvailable = true;
        }
        catch
        {
            wrathLiveAvailable = false;
        }

        // BossMod: read the same live AIConfig object its own UI uses.
        TryReadBossModCurrentValues(config.Editor, false);

        // BossMod Reborn: read the same live AIConfig object its own UI uses.
        // Do not save here; this is the live view, not a profile write.
        TryReadBmrCurrentValues(config.Editor, false);

        // RSR / Wrath: mirror the actual settings held by the running plugins.
        // These are live-view updates only; no profile is saved here.
        TryReadRsrCurrentValues(config.Editor);
        TryReadWrathCurrentValues(config.Editor);
    }

    private void Draw()
    {
        PollLiveValues();

        mainWindow.IsOpen = windowOpen;
        var activeTitle = FormatMiniActiveSettingsName(activeSettingsName);
        mainWindow.WindowName = $"Combat Job Settings V0.1.0 ｜ 適用：{activeTitle}###cjs_main";
        miniWindow.WindowName = $"CJS Mini ｜ 適用：{activeTitle}###cjs_mini";

        windowSystem.Draw();

        windowOpen = mainWindow.IsOpen;
        if (config.MiniWindowEnabled != miniWindow.IsOpen)
        {
            config.MiniWindowEnabled = miniWindow.IsOpen;
            config.Save();
        }
    }

    internal void DrawMainContent()
    {
        var job = CurrentJobId();
        var role = RoleKey(job);
        ImGui.TextUnformatted($"現在JOB：{JobName(job)}");
        ImGui.SameLine();
        ImGui.TextDisabled($"ロール：{RoleName(role)} / ID {job}");

        bool auto = config.AutoApplyOnJobChange;
        ImGui.BeginDisabled(defaultSnapshot == null);
        if (ImGui.Checkbox("JOB変更時に自動適用", ref auto))
        {
            config.AutoApplyOnJobChange = auto;
            config.Save();
            SaveSettingsInfo();
        }
        ImGui.EndDisabled();
        if (defaultSnapshot == null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("※デフォルト設定を保存するまで使用できません");
        }

        ImGui.TextDisabled($"適用：{FormatMiniActiveSettingsName(activeSettingsName)}");
        ImGui.Separator();

        if (ImGui.BeginTabBar("mainTabs"))
        {
            DrawMainTab("概要", () => DrawOverviewTab(config.Editor));
            DrawMainTab("BM", () => DrawBossModTab(config.Editor));
            DrawMainTab("BMR", () => DrawBmrTab(config.Editor));
            DrawMainTab("RSR", () => DrawRsrTab(config.Editor));
            DrawMainTab("Wrath", () => DrawWrathTab(config.Editor));
            DrawMainTab("JOB・ロール設定", () => DrawProfiles(job, role));
            DrawMainTab("設定", DrawSettingsTab);
            ImGui.EndTabBar();
        }
    }

    private void DrawMainTab(string name, Action draw)
    {
        var flags = requestedTab == name ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        if (ImGui.BeginTabItem(name, flags))
        {
            currentMainTab = name;
            if (requestedTab == name)
                requestedTab = string.Empty;
            draw();
            ImGui.EndTabItem();
        }
    }

    private void OpenMainTab(string name)
    {
        // Mini tab buttons are true toggles:
        // - same tab while CJS is open -> close CJS
        // - another tab (or while closed) -> open/switch to that tab
        var mainIsOpen = windowOpen || mainWindow.IsOpen;
        if (mainIsOpen && string.Equals(currentMainTab, name, StringComparison.Ordinal))
        {
            requestedTab = string.Empty;
            windowOpen = false;
            mainWindow.IsOpen = false;
            return;
        }

        requestedTab = name;
        windowOpen = true;
        mainWindow.IsOpen = true;
    }

    internal void DrawMiniContent()
    {
        var job = CurrentJobId();
        var role = RoleKey(job);
        var p = config.Editor;

        if (config.MiniShowJobRole)
            ImGui.TextUnformatted($"JOB：{JobName(job)} / ロール：{RoleName(role)}");

        if (config.MiniShowBossMod || config.MiniShowBmr)
        {
            if (config.MiniShowBossMod)
                DrawMiniBossMod(p);
            if (config.MiniShowBossMod && config.MiniShowBmr)
                ImGui.SameLine();
            if (config.MiniShowBmr)
                DrawMiniBmr(p);
        }
        if (config.MiniShowRsr)
            DrawMiniRsr(p);
        if (config.MiniShowWrath)
            DrawMiniWrath(p);

        if (config.MiniShowJobApply || config.MiniShowRoleApply || config.MiniShowDefaultApply || config.MiniShowNamedProfile)
        {
            ImGui.Separator();
            ImGui.TextUnformatted("保存済み設定を適用");

            if (config.MiniShowJobApply)
            {
                if (config.JobProfiles.TryGetValue(job, out var jp))
                {
                    if (ApplyButton($"{JobName(job)} 適用##mini_j"))
                        ApplyProfile(jp, $"手動 / {JobName(job)}設定");
                }
                else
                {
                    ImGui.BeginDisabled(); ImGui.Button($"{JobName(job)} 設定なし##mini_j_none"); ImGui.EndDisabled();
                }
            }

            if (config.MiniShowRoleApply)
            {
                if (config.RoleProfiles.TryGetValue(role, out var rp))
                {
                    if (ApplyButton($"{RoleName(role)} 適用##mini_r"))
                        ApplyProfile(rp, $"手動 / {RoleName(role)}設定");
                }
                else
                {
                    ImGui.BeginDisabled(); ImGui.Button($"{RoleName(role)} 設定なし##mini_r_none"); ImGui.EndDisabled();
                }
            }

            if (config.MiniShowDefaultApply)
            {
                ImGui.BeginDisabled(defaultSnapshot == null);
                if (ApplyButton("デフォルト適用##mini_default"))
                    RestoreDefaultSettings();
                ImGui.EndDisabled();
            }

            if (config.MiniShowNamedProfile)
            {
                var names = config.NamedProfiles.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                if (names.Length > 0)
                {
                    namedProfileIndex = Math.Clamp(namedProfileIndex, 0, names.Length - 1);
                    ImGui.SetNextItemWidth(170f);
                    ImGui.Combo("##mini_named", ref namedProfileIndex, names, names.Length);
                    ImGui.SameLine();
                    if (ApplyButton("適用##mini_named_apply"))
                    {
                        var name = names[namedProfileIndex];
                        ApplyProfile(config.NamedProfiles[name], $"名前付きプリセット / {name}");
                    }
                }
            }
        }

        if (config.MiniShowTabButtons)
        {
            ImGui.Separator();
            ImGui.TextUnformatted("CJS各タブ");
            foreach (var tab in new[] { "概要", "BM", "BMR", "RSR", "Wrath", "JOB・ロール設定", "設定" })
            {
                if (ImGui.SmallButton($"{tab}##mini_tab_{tab}"))
                    OpenMainTab(tab);
                ImGui.SameLine();
            }
            ImGui.NewLine();
        }

        if (config.MiniShowOriginalPluginButtons)
        {
            ImGui.TextUnformatted("本家プラグイン");
            DrawMiniOriginalButton("BM", "/vbm", bossModLiveAvailable, "bm");
            ImGui.SameLine();
            DrawMiniOriginalButton("BMR", "/bmr", bmrLiveAvailable || bmrAiLiveAvailable, "bmr");
            ImGui.SameLine();
            DrawMiniOriginalButton("RSR", "/rsr", rsrLiveAvailable, "rsr");
            ImGui.SameLine();
            DrawMiniOriginalButton("Wrath", "/wrath", wrathLiveAvailable || wrathTargetLiveAvailable, "wrath");
        }

        ImGui.Separator();
        if (ImGui.Button(windowOpen ? "CombatJobSettingsを閉じる##mini_open_main" : "CombatJobSettingsを開く##mini_open_main"))
        {
            windowOpen = !windowOpen;
            mainWindow.IsOpen = windowOpen;
        }
    }

    private void DrawMiniBossMod(CombatProfile p)
    {
        bool v = p.BossModAiEnabled;
        ImGui.BeginDisabled(!bossModLiveAvailable);
        if (ImGui.Checkbox("BM AI##mini_bm", ref v))
        {
            p.BossModAiEnabled = v;
            Send($"/vbm ai {(v ? "on" : "off")}");
            MarkManualChange();
        }
        ImGui.EndDisabled();
    }

    private void DrawMiniBmr(CombatProfile p)
    {
        bool v = p.BmrAiEnabled;
        ImGui.BeginDisabled(!(bmrAiLiveAvailable || bmrLiveAvailable));
        if (ImGui.Checkbox("BMR AI##mini_bmr", ref v))
        {
            p.BmrAiEnabled = v;
            Send($"/bmrai {(v ? "on" : "off")}");
            MarkManualChange();
        }
        ImGui.EndDisabled();
    }

    private void DrawMiniRsr(CombatProfile p)
    {
        var mode = rsrLiveAvailable ? rsrLiveState : p.RsrOperatingMode;
        var off = IsRsrMode(mode, "Off");
        var auto = IsRsrMode(mode, "Auto");
        var manual = IsRsrMode(mode, "Manual");
        ImGui.TextUnformatted("RSR"); ImGui.SameLine();
        ImGui.BeginDisabled(!rsrLiveAvailable);
        if (ImGui.RadioButton("停止##mini_rsr_off", off) && !off) { p.RsrOperatingMode = "Off"; p.RsrAutoEnabled = false; Send("/rotation off"); MarkManualChange(); }
        ImGui.SameLine();
        if (ImGui.RadioButton("自動##mini_rsr_auto", auto) && !auto) { p.RsrOperatingMode = "Auto"; p.RsrAutoEnabled = true; Send("/rotation auto on"); MarkManualChange(); }
        ImGui.SameLine();
        if (ImGui.RadioButton("手動##mini_rsr_manual", manual) && !manual) { p.RsrOperatingMode = "Manual"; p.RsrAutoEnabled = false; Send("/rotation manual"); MarkManualChange(); }
        ImGui.EndDisabled();
    }

    private void DrawMiniWrath(CombatProfile p)
    {
        bool v = wrathLiveAvailable ? wrathLiveAuto : p.WrathAutoRotation;
        ImGui.BeginDisabled(!wrathLiveAvailable);
        if (ImGui.Checkbox("Wrath Auto##mini_wrath", ref v))
        {
            p.WrathAutoRotation = v;
            Send($"/wrath auto {(v ? "on" : "off")}");
            MarkManualChange();
        }
        ImGui.EndDisabled();
    }

    private void DrawMiniOriginalButton(string label, string command, bool available, string id, string? assemblyName = null)
    {
        ImGui.BeginDisabled(!available);
        if (ImGui.SmallButton($"{label}##mini_original_{id}"))
        {
            if (assemblyName == null || !TryToggleExternalSettingsWindow(assemblyName))
                Send(command);
        }
        ImGui.EndDisabled();
    }

    private void MarkManualChange()
    {
        activeSettingsName = "手動変更";
        config.Save();
    }


    private void DrawOverviewTab(CombatProfile p)
    {
        ImGui.TextUnformatted("主要動作");
        ImGui.TextDisabled("各プラグインの現在状態をリアルタイム表示し、ここから即時切替できます。");
        DrawBossModAiControl(p);
        DrawBmrAiControl(p);
        DrawRsrModeControl(p);
        DrawWrathAutoControl(p);

        ImGui.Separator();
        ImGui.Separator();
        ImGui.BeginDisabled(defaultSnapshot == null);
        if (ImGui.Button("デフォルト設定へ戻す"))
            RestoreDefaultSettings();
        ImGui.EndDisabled();
        if (defaultSnapshot == null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("※設定タブで先に現在の実設定をデフォルト保存してください");
        }
        else
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"保存日時：{defaultSnapshot.SavedAt}");
        }
    }

    private void DrawBossModAiControl(CombatProfile p)
    {
        bool boss = p.BossModAiEnabled;
        ImGui.BeginDisabled(!bossModLiveAvailable);
        if (ImGui.Checkbox("BossMod AI##overview_bm_ai", ref boss))
        {
            p.BossModAiEnabled = boss;
            Send($"/vbm ai {(boss ? "on" : "off")}");
            config.Save();
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled(bossModLiveAvailable ? "[実値]" : "[未導入 / 未ロード]");
    }

    private void DrawBmrAiControl(CombatProfile p)
    {
        bool bmr = p.BmrAiEnabled;
        ImGui.BeginDisabled(!(bmrAiLiveAvailable || bmrLiveAvailable));
        if (ImGui.Checkbox("BossMod Reborn AI##overview_bmr_ai", ref bmr))
        {
            p.BmrAiEnabled = bmr;
            Send($"/bmrai {(bmr ? "on" : "off")}");
            config.Save();
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled(bmrAiLiveAvailable ? "[実値]" : "[未導入 / 未ロード]");
    }

    private void DrawRsrModeControl(CombatProfile p)
    {
        ImGui.TextUnformatted("RSR");
        ImGui.SameLine();
        var rsrMode = rsrLiveAvailable ? rsrLiveState : p.RsrOperatingMode;
        var isOff = IsRsrMode(rsrMode, "Off");
        var isAuto = IsRsrMode(rsrMode, "Auto");
        var isManual = IsRsrMode(rsrMode, "Manual");

        ImGui.BeginDisabled(!rsrLiveAvailable);
        if (ImGui.RadioButton("停止##overview_rsr_off", isOff) && !isOff)
        {
            p.RsrOperatingMode = "Off"; p.RsrAutoEnabled = false; Send("/rotation off"); config.Save();
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("自動##overview_rsr_auto", isAuto) && !isAuto)
        {
            // /rotation Auto はAuto中に再実行するとTargetingTypeが順送りされるため、
            // 既にAutoならコマンドを送らない。Off/Manual -> Auto の時だけ送る。
            p.RsrOperatingMode = "Auto"; p.RsrAutoEnabled = true; Send("/rotation auto on"); config.Save();
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("手動##overview_rsr_manual", isManual) && !isManual)
        {
            p.RsrOperatingMode = "Manual"; p.RsrAutoEnabled = false; Send("/rotation manual"); config.Save();
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled(rsrLiveAvailable ? $"[実値: {rsrLiveState}]" : "[未導入 / 未ロード]");
    }

    private static bool IsRsrMode(string? raw, string mode)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return raw.Trim().StartsWith(mode, StringComparison.OrdinalIgnoreCase);
    }

    private void DrawWrathAutoControl(CombatProfile p)
    {
        bool wrath = wrathLiveAvailable ? wrathLiveAuto : p.WrathAutoRotation;
        ImGui.BeginDisabled(!wrathLiveAvailable);
        if (ImGui.Checkbox("Wrath 自動ローテーション##overview_wrath_auto", ref wrath))
        {
            p.WrathAutoRotation = wrath;
            Send($"/wrath auto {(wrath ? "on" : "off")}");
            config.Save();
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled(wrathLiveAvailable ? "[実値]" : "[未導入 / 未ロード]");
    }

    private void DrawBossModTab(CombatProfile p)
    {
        ImGui.TextUnformatted("BossMod");
        bool boss = p.BossModAiEnabled;
        if (ImGui.Checkbox("AI##bm_ai", ref boss))
        {
            p.BossModAiEnabled = boss;
            Send($"/vbm ai {(boss ? "on" : "off")}");
            config.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled(bossModLiveAvailable ? "[リアルタイム取得中]" : "[実値取得不可]");
        DrawOriginalPluginButton("BossMod設定を開く", "/vbm", bossModLiveAvailable);
    }

    private void DrawBmrTab(CombatProfile p)
    {
        ImGui.TextUnformatted("BossMod Reborn");
        bool bmr = p.BmrAiEnabled;
        if (ImGui.Checkbox("AI##bmr_ai", ref bmr))
        {
            p.BmrAiEnabled = bmr;
            Send($"/bmrai {(bmr ? "on" : "off")}");
            config.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled(bmrAiLiveAvailable ? "[リアルタイム取得中]" : "[AI状態取得不可]");

        bool followTarget = p.BmrFollowTarget;
        if (ImGui.Checkbox("攻撃対象との距離制御を有効にする（Follow target）", ref followTarget))
        {
            p.BmrFollowTarget = followTarget;
            Send($"/bmrai followtarget {(followTarget ? "on" : "off")}");
            config.Save();
        }
        if (!p.BmrFollowTarget)
            ImGui.TextDisabled("※ OFF中は下の最大距離・最小距離を攻撃対象への距離制御に使用しません。");

        float max = p.BmrMaxDistanceTarget;
        ImGui.SetNextItemWidth(78f);
        if (ImGui.InputFloat("##bmr_max_distance_tab", ref max, 0.1f, 0.1f, "%.1f"))
        {
            p.BmrMaxDistanceTarget = Math.Clamp(max, 0f, 50f);
            Send($"/bmrai MAXDISTANCETARGET {p.BmrMaxDistanceTarget.ToString(CultureInfo.InvariantCulture)}");
            config.Save();
        }
        ImGui.SameLine(); ImGui.TextUnformatted("ターゲットとの最大距離（Max distance to target）");

        float min = p.BmrMinDistance;
        ImGui.SetNextItemWidth(78f);
        if (ImGui.InputFloat("##bmr_min_distance_tab", ref min, 0.1f, 0.1f, "%.1f"))
        {
            p.BmrMinDistance = Math.Clamp(min, 0f, 50f);
            Send($"/bmrai MINDISTANCE {p.BmrMinDistance.ToString(CultureInfo.InvariantCulture)}");
            config.Save();
        }
        ImGui.SameLine(); ImGui.TextUnformatted("ヒットボックスからの最小距離（Minimum distance to hitbox）");
        ImGui.TextDisabled(bmrLiveAvailable ? "リアルタイム取得中" : "実値取得不可");
        DrawOriginalPluginButton("BossMod Reborn設定を開く", "/bmr", bmrLiveAvailable || bmrAiLiveAvailable);
    }

    private void DrawRsrTab(CombatProfile p)
    {
        ImGui.TextUnformatted("動作モード");
        DrawRsrModeControl(p);
        ImGui.Separator();

        ImGui.TextUnformatted("攻撃対象候補（Engage settings）");
        if (rsrEngageLiveAvailable && rsrEngageChoices.Length > 0)
        {
            var engageIndex = Array.FindIndex(rsrEngageChoices, x => x.Equals(p.RsrEngageSetting, StringComparison.OrdinalIgnoreCase));
            if (engageIndex < 0) engageIndex = 0;
            var engageLabels = rsrEngageChoices.Select(RsrEngageDisplayName).ToArray();
            ImGui.SetNextItemWidth(360f);
            if (ImGui.Combo("##rsr_engage_setting", ref engageIndex, engageLabels, engageLabels.Length))
            {
                p.RsrEngageSetting = rsrEngageChoices[engageIndex];
                if (TrySetRsrEngageSetting(p.RsrEngageSetting))
                {
                    config.Save();
                    status = $"RSR交戦対象を変更：{RsrEngageDisplayName(p.RsrEngageSetting)}";
                }
                else
                    status = "RSR交戦対象の変更に失敗しました";
            }
        }
        else
        {
            ImGui.TextDisabled("実値取得不可");
        }

        ImGui.Separator();
        ImGui.TextUnformatted("敵の優先条件（Hostile target selection condition）");
        ImGui.TextDisabled("複数選択可。変更するとRSRへ即時反映します。");
        foreach (var (internalName, japanese) in RsrTargets)
        {
            bool selected = p.RsrTargetingTypes.Contains(internalName, StringComparer.OrdinalIgnoreCase);
            if (ImGui.Checkbox($"{japanese}##rsr_tab_{internalName}", ref selected))
            {
                if (selected)
                {
                    if (!p.RsrTargetingTypes.Contains(internalName, StringComparer.OrdinalIgnoreCase)) p.RsrTargetingTypes.Add(internalName);
                }
                else p.RsrTargetingTypes.RemoveAll(x => x.Equals(internalName, StringComparison.OrdinalIgnoreCase));
                ApplyRsrTargets(p); config.Save();
            }
        }
        ImGui.TextDisabled(rsrLiveAvailable ? "リアルタイム取得中" : "実値取得不可");
        DrawOriginalPluginButton("RSR設定を開く", "/rsr", rsrLiveAvailable);
    }

    private void DrawWrathTab(CombatProfile p)
    {
        DrawWrathAutoControl(p);
        ImGui.TextDisabled(wrathTargetLiveAvailable ? "設定値をリアルタイム取得中" : "設定値の実値取得不可");

        if (ImGui.TreeNodeEx("攻撃設定", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var idx = Array.FindIndex(WrathTargets, x => x.Internal.Equals(p.WrathDamageTargetMode, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) idx = 0;
            var labels = WrathTargets.Select(x => x.Japanese).ToArray();
            ImGui.SetNextItemWidth(220f);
            if (ImGui.Combo("##wrath_damage_target_tab", ref idx, labels, labels.Length))
            {
                p.WrathDamageTargetMode = WrathTargets[idx].Internal;
                Send($"/wrath auto target damage {p.WrathDamageTargetMode}"); config.Save();
            }
            ImGui.SameLine(); ImGui.TextUnformatted("攻撃対象モード");
            DrawNullableInt("AoE攻撃開始に必要な対象数", ref p.WrathDpsAoeTargets, 1, 20, "DPSAoETargets");
            DrawFloat("最大対象距離", ref p.WrathMaxDistance, 0.5f, 0f, 50f, "MaxDistance");
            DrawWrathBool("ボス戦では最大対象距離を無視", ref p.WrathIgnoreRangeInBoss, "IgnoreRangeInBoss");
            DrawWrathBool("FATE対象を優先", ref p.WrathFatePriority, "FATEPriority");
            DrawWrathBool("クエスト対象を優先", ref p.WrathQuestPriority, "QuestPriority");
            DrawWrathBool("非戦闘中の対象を優先", ref p.WrathPreferNonCombat, "PreferNonCombat");
            DrawWrathBool("すでに戦闘中の対象のみ攻撃", ref p.WrathOnlyAttackInCombat, "OnlyAttackInCombat");
            DrawWrathBool("常にハードターゲットを設定", ref p.WrathDpsAlwaysHardTarget, "DPSAlwaysHardTarget");
            ImGui.TreePop();
        }
        if (ImGui.TreeNodeEx("回復設定", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var hidx = Array.FindIndex(WrathHealerTargets, x => x.Internal.Equals(p.WrathHealerTargetMode, StringComparison.OrdinalIgnoreCase));
            if (hidx < 0) hidx = 0;
            var hlabels = WrathHealerTargets.Select(x => x.Japanese).ToArray();
            ImGui.SetNextItemWidth(220f);
            if (ImGui.Combo("##wrath_healer_target_tab", ref hidx, hlabels, hlabels.Length))
            {
                p.WrathHealerTargetMode = WrathHealerTargets[hidx].Internal;
                Send($"/wrath auto target healer {p.WrathHealerTargetMode}"); config.Save();
            }
            ImGui.SameLine(); ImGui.TextUnformatted("回復対象モード");
            DrawWrathInt("単体回復 HP閾値", ref p.WrathSingleTargetHpp, 0, 100, "SingleTargetHPP");
            DrawWrathInt("単体回復 HP閾値・継続回復あり", ref p.WrathSingleTargetRegenHpp, 0, 100, "SingleTargetRegenHPP");
            DrawWrathInt("単体回復 HP閾値・深謀遠慮あり", ref p.WrathSingleTargetExcogHpp, 0, 100, "SingleTargetExcogHPP");
            DrawWrathInt("AoE回復 HP閾値", ref p.WrathAoeTargetHpp, 0, 100, "AoETargetHPP");
            DrawWrathBool("シールド分をHP判定に含める", ref p.WrathIncludeShields, "IncludeShields");
            DrawNullableInt("AoE回復開始に必要な対象数", ref p.WrathAoeHealTargetCount, 1, 8, "AoEHealTargetCount", true);
            DrawWrathInt("条件成立後に回復開始するまでの遅延・秒", ref p.WrathHealDelay, 0, 10, "HealDelay");
            DrawWrathBool("自動蘇生", ref p.WrathAutoRez, "AutoRez");
            if (p.WrathAutoRez)
            {
                ImGui.Indent();
                DrawWrathBool("パーティ外メンバーにも適用", ref p.WrathAutoRezOutOfParty, "AutoRezOutOfParty");
                DrawWrathBool("迅速魔を必須にする", ref p.WrathAutoRezRequireSwift, "AutoRezRequireSwift");
                DrawWrathBool("SMN / RDMにも適用", ref p.WrathAutoRezDpsJobs, "AutoRezDPSJobs");
                DrawWrathBool("DPS蘇生はヒーラー対象のみ", ref p.WrathAutoRezDpsJobsHealersOnly, "AutoRezDPSJobsHealersOnly");
                ImGui.Unindent();
            }
            DrawWrathBool("自動エスナ", ref p.WrathAutoCleanse, "AutoCleanse");
            DrawWrathBool("SGE カルディアを自動管理", ref p.WrathManageKardia, "ManageKardia");
            if (p.WrathManageKardia)
            {
                ImGui.Indent(); DrawWrathBool("カルディアはタンクのみに制限", ref p.WrathKardiaTanksOnly, "KardiaTanksOnly"); ImGui.Unindent();
            }
            DrawWrathBool("戦闘前に継続回復／バリアを付与", ref p.WrathPreEmptiveHot, "PreEmptiveHoT");
            DrawWrathBool("友好的NPCも回復対象にする", ref p.WrathIncludeNpcs, "IncludeNPCs");
            DrawWrathBool("常にハードターゲットを設定", ref p.WrathHealerAlwaysHardTarget, "HealerAlwaysHardTarget");
            DrawWrathBool("検知した全体攻撃に対応", ref p.WrathHandleRaidwides, "HandleRaidwides");
            DrawWrathBool("検知したタンク強攻撃に対応", ref p.WrathHandleTankbusters, "HandleTankbusters");
            ImGui.TreePop();
        }
        DrawOriginalPluginButton("Wrath設定を開く", "/wrath autosettings", wrathLiveAvailable || wrathTargetLiveAvailable);
    }

    private void DrawOriginalPluginButton(string label, string command, bool available, string? assemblyName = null)
    {
        ImGui.Separator();
        ImGui.BeginDisabled(!available);
        if (ImGui.Button(label))
        {
            if (assemblyName == null || !TryToggleExternalSettingsWindow(assemblyName))
                Send(command);
        }
        ImGui.EndDisabled();
        if (!available)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("未導入 / 未ロード");
        }
    }

    private static bool TryToggleExternalSettingsWindow(string assemblyName)
    {
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
            if (asm == null)
                return false;

            object? bestWindow = null;
            PropertyInfo? bestIsOpen = null;
            var bestScore = int.MinValue;

            foreach (var type in asm.GetTypes())
            {
                var typeName = type.Name;
                var typeScore = 0;
                if (typeName.Contains("Config", StringComparison.OrdinalIgnoreCase)) typeScore += 8;
                if (typeName.Contains("Setting", StringComparison.OrdinalIgnoreCase)) typeScore += 8;
                if (typeName.Contains("Window", StringComparison.OrdinalIgnoreCase)) typeScore += 4;
                if (typeName.Contains("AI", StringComparison.OrdinalIgnoreCase)) typeScore -= 3;

                foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    object? obj;
                    try { obj = field.GetValue(null); } catch { continue; }
                    if (obj == null) continue;
                    var prop = obj.GetType().GetProperty("IsOpen", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (prop?.PropertyType != typeof(bool) || !prop.CanRead || !prop.CanWrite) continue;
                    var score = typeScore;
                    if (field.Name.Contains("Config", StringComparison.OrdinalIgnoreCase)) score += 10;
                    if (field.Name.Contains("Setting", StringComparison.OrdinalIgnoreCase)) score += 10;
                    if (field.Name.Contains("Window", StringComparison.OrdinalIgnoreCase)) score += 5;
                    if (score > bestScore) { bestScore = score; bestWindow = obj; bestIsOpen = prop; }
                }

                foreach (var propStatic in type.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (!propStatic.CanRead || propStatic.GetIndexParameters().Length != 0) continue;
                    object? obj;
                    try { obj = propStatic.GetValue(null); } catch { continue; }
                    if (obj == null) continue;
                    var prop = obj.GetType().GetProperty("IsOpen", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (prop?.PropertyType != typeof(bool) || !prop.CanRead || !prop.CanWrite) continue;
                    var score = typeScore;
                    if (propStatic.Name.Contains("Config", StringComparison.OrdinalIgnoreCase)) score += 10;
                    if (propStatic.Name.Contains("Setting", StringComparison.OrdinalIgnoreCase)) score += 10;
                    if (propStatic.Name.Contains("Window", StringComparison.OrdinalIgnoreCase)) score += 5;
                    if (score > bestScore) { bestScore = score; bestWindow = obj; bestIsOpen = prop; }
                }
            }

            if (bestWindow == null || bestIsOpen == null || bestScore < 4)
                return false;

            var isOpen = (bool)(bestIsOpen.GetValue(bestWindow) ?? false);
            bestIsOpen.SetValue(bestWindow, !isOpen);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void DrawLiveEditor(CombatProfile p)
    {
        ImGui.TextUnformatted("戦闘機能 ON / OFF（Combat features）");
        ImGui.TextDisabled("押した時点で各プラグインへ即時送信します。プラグイン自体のロード解除ではありません。");

        bool boss = p.BossModAiEnabled;
        if (ImGui.Checkbox("BossMod AI（BossMod AI）", ref boss))
        {
            p.BossModAiEnabled = boss;
            Send($"/vbm ai {(boss ? "on" : "off")}");
            config.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled(bossModLiveAvailable ? "[実値を取得中]" : "[実値取得不可]");

        bool bmr = p.BmrAiEnabled;
        if (ImGui.Checkbox("BossMod Reborn AI（BossMod Reborn AI）", ref bmr))
        {
            p.BmrAiEnabled = bmr;
            Send($"/bmrai {(bmr ? "on" : "off")}");
            config.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled(bmrAiLiveAvailable ? "[実値]" : "[AI状態取得不可]");

        ImGui.TextUnformatted("RSR 動作モード");
        ImGui.SameLine();
        var rsrMode = rsrLiveAvailable ? rsrLiveState : p.RsrOperatingMode;
        if (ImGui.RadioButton("停止##rsr_mode_off", rsrMode.Equals("Off", StringComparison.OrdinalIgnoreCase)))
        {
            p.RsrOperatingMode = "Off";
            p.RsrAutoEnabled = false;
            Send("/rotation off");
            config.Save();
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("自動##rsr_mode_auto", rsrMode.Equals("Auto", StringComparison.OrdinalIgnoreCase)))
        {
            p.RsrOperatingMode = "Auto";
            p.RsrAutoEnabled = true;
            Send("/rotation auto on");
            config.Save();
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("手動##rsr_mode_manual", rsrMode.Equals("Manual", StringComparison.OrdinalIgnoreCase)))
        {
            p.RsrOperatingMode = "Manual";
            p.RsrAutoEnabled = false;
            Send("/rotation manual");
            config.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled(rsrLiveAvailable ? $"[実値: {rsrLiveState}]" : "[実値取得不可]");

        bool wrath = wrathLiveAvailable ? wrathLiveAuto : p.WrathAutoRotation;
        if (ImGui.Checkbox("Wrath 自動ローテーション（Auto Rotation）", ref wrath))
        {
            p.WrathAutoRotation = wrath;
            Send($"/wrath auto {(wrath ? "on" : "off")}");
            config.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled(wrathLiveAvailable ? "[実値を取得中]" : "[実値取得不可]");

        ImGui.Separator();
        ImGui.TextUnformatted("RSR - 敵の優先条件（Hostile target selection condition）");
        ImGui.SameLine();
        ImGui.TextDisabled(rsrLiveAvailable ? "[リアルタイム取得中]" : "[実値取得不可]");
        ImGui.TextDisabled("変更するとRSRへその場で反映します。複数選択可。");
        foreach (var (internalName, japanese) in RsrTargets)
        {
            bool selected = p.RsrTargetingTypes.Contains(internalName, StringComparer.OrdinalIgnoreCase);
            if (ImGui.Checkbox($"{japanese}##rsr_{internalName}", ref selected))
            {
                if (selected)
                {
                    if (!p.RsrTargetingTypes.Contains(internalName, StringComparer.OrdinalIgnoreCase))
                        p.RsrTargetingTypes.Add(internalName);
                }
                else
                {
                    p.RsrTargetingTypes.RemoveAll(x => x.Equals(internalName, StringComparison.OrdinalIgnoreCase));
                }
                ApplyRsrTargets(p);
                config.Save();
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("BossMod Reborn - 移動・距離（Automovement）");
        ImGui.SameLine();
        ImGui.TextDisabled(bmrLiveAvailable ? "[リアルタイム取得中]" : "[実値取得不可]");

        bool followTarget = p.BmrFollowTarget;
        if (ImGui.Checkbox("攻撃対象との距離制御を有効にする（Follow target）", ref followTarget))
        {
            p.BmrFollowTarget = followTarget;
            Send($"/bmrai followtarget {(followTarget ? "on" : "off")}");
            config.Save();
        }
        if (!p.BmrFollowTarget)
            ImGui.TextDisabled("※ OFF中は下の最大距離・最小距離を攻撃対象への距離制御に使用しません。");

        float max = p.BmrMaxDistanceTarget;
        ImGui.SetNextItemWidth(78f);
        if (ImGui.InputFloat("##bmr_max_distance", ref max, 0.1f, 1f, "%.1f"))
        {
            p.BmrMaxDistanceTarget = Math.Clamp(max, 0f, 50f);
            Send($"/bmrai MAXDISTANCETARGET {p.BmrMaxDistanceTarget.ToString(CultureInfo.InvariantCulture)}");
            config.Save();
        }
        ImGui.SameLine();
        ImGui.TextUnformatted("ターゲットとの最大距離（Max distance to target）");

        float min = p.BmrMinDistance;
        ImGui.SetNextItemWidth(78f);
        if (ImGui.InputFloat("##bmr_min_distance", ref min, 0.1f, 1f, "%.1f"))
        {
            p.BmrMinDistance = Math.Clamp(min, 0f, 50f);
            Send($"/bmrai MINDISTANCE {p.BmrMinDistance.ToString(CultureInfo.InvariantCulture)}");
            config.Save();
        }
        ImGui.SameLine();
        ImGui.TextUnformatted("ヒットボックスからの最小距離（Minimum distance to hitbox）");

        ImGui.Separator();
        ImGui.TextUnformatted("Wrath");
        ImGui.SameLine();
        ImGui.TextDisabled(wrathTargetLiveAvailable ? "[リアルタイム取得中]" : "[実値取得不可]");

        if (ImGui.TreeNodeEx("攻撃設定", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var idx = Array.FindIndex(WrathTargets, x => x.Internal.Equals(p.WrathDamageTargetMode, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) idx = 0;
            var labels = WrathTargets.Select(x => x.Japanese).ToArray();
            ImGui.SetNextItemWidth(220f);
            if (ImGui.Combo("##wrath_damage_target", ref idx, labels, labels.Length))
            {
                p.WrathDamageTargetMode = WrathTargets[idx].Internal;
                Send($"/wrath auto target damage {p.WrathDamageTargetMode}");
                config.Save();
            }
            ImGui.SameLine();
            ImGui.TextUnformatted("攻撃対象モード");

            DrawNullableInt("AoE攻撃開始に必要な対象数", ref p.WrathDpsAoeTargets, 1, 20, "DPSAoETargets");
            DrawFloat("最大対象距離", ref p.WrathMaxDistance, 0.5f, 0f, 50f, "MaxDistance");
            DrawWrathBool("ボス戦では最大対象距離を無視", ref p.WrathIgnoreRangeInBoss, "IgnoreRangeInBoss");
            DrawWrathBool("FATE対象を優先", ref p.WrathFatePriority, "FATEPriority");
            DrawWrathBool("クエスト対象を優先", ref p.WrathQuestPriority, "QuestPriority");
            DrawWrathBool("非戦闘中の対象を優先", ref p.WrathPreferNonCombat, "PreferNonCombat");
            DrawWrathBool("すでに戦闘中の対象のみ攻撃", ref p.WrathOnlyAttackInCombat, "OnlyAttackInCombat");
            DrawWrathBool("常にハードターゲットを設定", ref p.WrathDpsAlwaysHardTarget, "DPSAlwaysHardTarget");
            ImGui.TreePop();
        }

        if (ImGui.TreeNodeEx("回復設定", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var hidx = Array.FindIndex(WrathHealerTargets, x => x.Internal.Equals(p.WrathHealerTargetMode, StringComparison.OrdinalIgnoreCase));
            if (hidx < 0) hidx = 0;
            var hlabels = WrathHealerTargets.Select(x => x.Japanese).ToArray();
            ImGui.SetNextItemWidth(220f);
            if (ImGui.Combo("##wrath_healer_target", ref hidx, hlabels, hlabels.Length))
            {
                p.WrathHealerTargetMode = WrathHealerTargets[hidx].Internal;
                Send($"/wrath auto target healer {p.WrathHealerTargetMode}");
                config.Save();
            }
            ImGui.SameLine();
            ImGui.TextUnformatted("回復対象モード");

            DrawWrathInt("単体回復 HP閾値", ref p.WrathSingleTargetHpp, 0, 100, "SingleTargetHPP");
            DrawWrathInt("単体回復 HP閾値・継続回復あり", ref p.WrathSingleTargetRegenHpp, 0, 100, "SingleTargetRegenHPP");
            DrawWrathInt("単体回復 HP閾値・深謀遠慮あり", ref p.WrathSingleTargetExcogHpp, 0, 100, "SingleTargetExcogHPP");
            DrawWrathInt("AoE回復 HP閾値", ref p.WrathAoeTargetHpp, 0, 100, "AoETargetHPP");
            DrawWrathBool("シールド分をHP判定に含める", ref p.WrathIncludeShields, "IncludeShields");
            DrawNullableInt("AoE回復開始に必要な対象数", ref p.WrathAoeHealTargetCount, 1, 8, "AoEHealTargetCount", true);
            DrawWrathInt("条件成立後に回復開始するまでの遅延・秒", ref p.WrathHealDelay, 0, 10, "HealDelay");

            DrawWrathBool("自動蘇生", ref p.WrathAutoRez, "AutoRez");
            if (p.WrathAutoRez)
            {
                ImGui.Indent();
                DrawWrathBool("パーティ外メンバーにも適用", ref p.WrathAutoRezOutOfParty, "AutoRezOutOfParty");
                DrawWrathBool("迅速魔を必須にする", ref p.WrathAutoRezRequireSwift, "AutoRezRequireSwift");
                DrawWrathBool("SMN / RDMにも適用", ref p.WrathAutoRezDpsJobs, "AutoRezDPSJobs");
                DrawWrathBool("DPS蘇生はヒーラー対象のみ", ref p.WrathAutoRezDpsJobsHealersOnly, "AutoRezDPSJobsHealersOnly");
                ImGui.Unindent();
            }
            DrawWrathBool("自動エスナ", ref p.WrathAutoCleanse, "AutoCleanse");
            DrawWrathBool("SGE カルディアを自動管理", ref p.WrathManageKardia, "ManageKardia");
            if (p.WrathManageKardia)
            {
                ImGui.Indent();
                DrawWrathBool("カルディアはタンクのみに制限", ref p.WrathKardiaTanksOnly, "KardiaTanksOnly");
                ImGui.Unindent();
            }
            DrawWrathBool("戦闘前に継続回復／バリアを付与", ref p.WrathPreEmptiveHot, "PreEmptiveHoT");
            DrawWrathBool("友好的NPCも回復対象にする", ref p.WrathIncludeNpcs, "IncludeNPCs");
            DrawWrathBool("常にハードターゲットを設定", ref p.WrathHealerAlwaysHardTarget, "HealerAlwaysHardTarget");
            DrawWrathBool("検知した全体攻撃に対応", ref p.WrathHandleRaidwides, "HandleRaidwides");
            DrawWrathBool("検知したタンク強攻撃に対応", ref p.WrathHandleTankbusters, "HandleTankbusters");
            ImGui.TreePop();
        }

        ImGui.Separator();
        ImGui.TextDisabled("設定は各プラグインの現在値を取得し、変更時は本体へ反映します。");
    }

    private void DrawWrathBool(string label, ref bool value, string member)
    {
        bool v = value;
        if (ImGui.Checkbox($"{label}##wrath_{member}", ref v))
        {
            value = v;
            WriteWrathSetting(member, v);
            config.Save();
        }
    }

    private void DrawWrathInt(string label, ref int value, int min, int max, string member)
    {
        int v = value;
        ImGui.SetNextItemWidth(70f);
        if (ImGui.InputInt($"##wrath_{member}", ref v, 1, 5))
        {
            value = Math.Clamp(v, min, max);
            WriteWrathSetting(member, value);
            config.Save();
        }
        ImGui.SameLine();
        ImGui.TextUnformatted(label);
    }

    private void DrawFloat(string label, ref float value, float step, float min, float max, string member)
    {
        float v = value;
        ImGui.SetNextItemWidth(78f);
        if (ImGui.InputFloat($"##wrath_{member}", ref v, step, 1f, "%.1f"))
        {
            value = Math.Clamp(v, min, max);
            WriteWrathSetting(member, value);
            config.Save();
        }
        ImGui.SameLine();
        ImGui.TextUnformatted(label);
    }

    private void DrawNullableInt(string label, ref int? value, int min, int max, string member, bool healer = false)
    {
        bool enabled = value.HasValue;
        if (ImGui.Checkbox($"##wrath_{member}_enabled", ref enabled))
        {
            value = enabled ? Math.Clamp(value ?? min, min, max) : null;
            WriteWrathSetting(member, value, healer);
            config.Save();
        }
        ImGui.SameLine();
        int v = value ?? min;
        ImGui.BeginDisabled(!enabled);
        ImGui.SetNextItemWidth(60f);
        if (ImGui.InputInt($"##wrath_{member}", ref v, 1, 5))
        {
            value = Math.Clamp(v, min, max);
            WriteWrathSetting(member, value, healer);
            config.Save();
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextUnformatted(label);
    }

    private static bool SaveButton(string label)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.15f, 0.38f, 0.68f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.20f, 0.48f, 0.82f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.12f, 0.31f, 0.58f, 1.0f));
        var clicked = ImGui.Button(label);
        ImGui.PopStyleColor(3);
        return clicked;
    }

    private static bool ApplyButton(string label)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.18f, 0.52f, 0.28f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.23f, 0.64f, 0.34f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.14f, 0.43f, 0.23f, 1.0f));
        var clicked = ImGui.Button(label);
        ImGui.PopStyleColor(3);
        return clicked;
    }

    private void DrawProfiles(uint job, string role)
    {
        var resolved = TryResolveSavedProfile(job);
        ImGui.TextUnformatted($"現在JOB：{JobName(job)}　ロール：{RoleName(role)}");
        ImGui.TextUnformatted($"自動適用時の候補：{(resolved.Profile != null ? resolved.Source : "設定なし")}");
        ImGui.Spacing();

        ImGui.TextUnformatted("JOB設定");
        if (SaveButton($"{JobName(job)} 保存"))
        {
            config.JobProfiles[job] = config.Editor.Clone();
            config.Save();
            SaveProfilesExternal();
            status = $"{JobName(job)}設定へ保存しました";
        }
        ImGui.SameLine();
        if (config.JobProfiles.TryGetValue(job, out var jobProfile))
        {
            if (ApplyButton($"{JobName(job)} 適用"))
                ApplyProfile(jobProfile, $"手動 / {JobName(job)}設定");
        }
        else
        {
            ImGui.BeginDisabled();
            ImGui.Button($"{JobName(job)} 設定なし");
            ImGui.EndDisabled();
        }

        ImGui.TextUnformatted("ロール設定");
        if (SaveButton($"{RoleName(role)} 保存"))
        {
            config.RoleProfiles[role] = config.Editor.Clone();
            config.Save();
            SaveProfilesExternal();
            status = $"{RoleName(role)}設定へ保存しました";
        }
        ImGui.SameLine();
        if (config.RoleProfiles.TryGetValue(role, out var roleProfile))
        {
            if (ApplyButton($"{RoleName(role)} 適用"))
                ApplyProfile(roleProfile, $"手動 / {RoleName(role)}設定");
        }
        else
        {
            ImGui.BeginDisabled();
            ImGui.Button($"{RoleName(role)} 設定なし");
            ImGui.EndDisabled();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("現在JOBへ優先設定を適用");
        if (resolved.Profile != null)
        {
            var applyLabel = resolved.Kind == "J"
                ? $"{JobName(job)}設定を適用"
                : resolved.Kind == "R"
                    ? $"{RoleName(role)}設定を適用"
                    : "デフォルト設定を適用";
            if (ApplyButton(applyLabel))
                ApplyProfile(resolved.Profile, $"手動 / {resolved.Source}");
            ImGui.SameLine();
            ImGui.TextDisabled($"適用元：{resolved.Source}");
        }
        else
        {
            ImGui.BeginDisabled();
            ImGui.Button("適用できる設定がありません");
            ImGui.EndDisabled();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("他のロールへ保存");
        foreach (var key in new[] { "tank", "healer", "melee", "physranged", "caster" })
        {
            if (SaveButton($"{RoleName(key)} 保存##{key}"))
            {
                config.RoleProfiles[key] = config.Editor.Clone();
                config.Save();
                SaveProfilesExternal();
                status = $"{RoleName(key)}設定へ保存しました";
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("名前付きプリセット");
        ImGui.TextDisabled("JOB・ロールに関係なく、現在の実設定を好きな名前で保存して手動適用できます。");

        ImGui.SetNextItemWidth(220f);
        ImGui.InputText("##named_profile_name", ref namedProfileNameEdit, 80);
        ImGui.SameLine();
        if (SaveButton("現在設定を保存"))
        {
            var name = namedProfileNameEdit.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                status = "プリセット名を入力してください";
            }
            else
            {
                config.NamedProfiles[name] = config.Editor.Clone();
                namedProfileNameEdit = string.Empty;
                namedProfileIndex = Math.Max(0, config.NamedProfiles.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList().FindIndex(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)));
                config.Save();
                SaveProfilesExternal();
                status = $"名前付きプリセット『{name}』を保存しました";
            }
        }

        var presetNames = config.NamedProfiles.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        if (presetNames.Length > 0)
        {
            namedProfileIndex = Math.Clamp(namedProfileIndex, 0, presetNames.Length - 1);
            ImGui.SetNextItemWidth(240f);
            ImGui.Combo("##named_profile_select", ref namedProfileIndex, presetNames, presetNames.Length);
            ImGui.SameLine();
            if (ApplyButton("適用"))
            {
                var name = presetNames[namedProfileIndex];
                ApplyProfile(config.NamedProfiles[name], $"名前付きプリセット / {name}");
            }
            ImGui.SameLine();
            if (SaveButton("上書き保存"))
            {
                var name = presetNames[namedProfileIndex];
                config.NamedProfiles[name] = config.Editor.Clone();
                config.Save();
                SaveProfilesExternal();
                status = $"名前付きプリセット『{name}』を上書き保存しました";
            }
            ImGui.SameLine();
            if (ImGui.Button("削除"))
            {
                var name = presetNames[namedProfileIndex];
                config.NamedProfiles.Remove(name);
                namedProfileIndex = 0;
                config.Save();
                SaveProfilesExternal();
                status = $"名前付きプリセット『{name}』を削除しました";
            }
        }
        else
        {
            ImGui.TextDisabled("保存済みプリセットはありません");
        }

        ImGui.TextDisabled("適用優先順位：JOB設定 > ロール設定 > 保存済みデフォルト（名前付きプリセットは手動適用）");
    }

    private void DrawSettingsTab()
    {
        ImGui.TextUnformatted("保存場所");
        ImGui.TextDisabled("JOB・ロール設定、名前付きプリセット、復元用デフォルトを、このフォルダーへJSONで保存します。");
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("##storage_path", ref storagePathEdit, 512);

        if (ImGui.Button("保存場所を適用"))
        {
            ApplyStoragePath(storagePathEdit);
        }
        ImGui.SameLine();
        if (ImGui.Button("保存場所を開く"))
        {
            OpenStorageFolder();
        }
        ImGui.SameLine();
        if (ImGui.Button("標準保存先へ戻す"))
        {
            storagePathEdit = DefaultStoragePath();
            ApplyStoragePath(storagePathEdit);
        }

        ImGui.TextDisabled($"現在の保存先：{config.StoragePath}");
        ImGui.TextDisabled("保存ファイル：Settings.json / DefaultSettings.json / JobProfiles.json / RoleProfiles.json / NamedProfiles.json");

        ImGui.Separator();
        ImGui.TextUnformatted("復元用デフォルト");
        ImGui.TextDisabled("JOB別設定を試す前の『本来の設定』を保存します。デフォルト保存では各戦闘プラグインを書き換えません。");

        if (defaultSnapshot == null)
        {
            ImGui.TextUnformatted("デフォルト設定：未保存");
        }
        else
        {
            ImGui.TextUnformatted($"デフォルト設定：保存済み {defaultSnapshot.SavedAt}");
            ImGui.TextDisabled($"BMR: {CapturedText(defaultSnapshot.BmrCaptured)} / RSR: {CapturedText(defaultSnapshot.RsrCaptured)} / Wrath: {CapturedText(defaultSnapshot.WrathCaptured)} / BossMod: {CapturedText(defaultSnapshot.BossModCaptured)}");
        }

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.62f, 0.16f, 0.16f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.76f, 0.20f, 0.20f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.50f, 0.12f, 0.12f, 1.0f));
        var saveDefaultClicked = ImGui.Button("現在の実設定をデフォルトとして保存");
        ImGui.PopStyleColor(3);
        if (saveDefaultClicked)
        {
            CaptureDefaultSettings();
        }

        if (defaultSnapshot != null && !defaultSnapshot.BossModCaptured)
            ImGui.TextDisabled("※ BossModの実値を取得できなかったため、このデフォルトにはBossMod設定が含まれていません。");

        ImGui.Separator();
        ImGui.TextUnformatted("ウィンドウ操作");
        ImGui.TextDisabled("固定・クリック透過・透明度は、各ウィンドウ右上の標準メニューから設定できます。");

        ImGui.Separator();
        ImGui.TextUnformatted("簡易ウィンドウ");
        var miniOpen = miniWindow.IsOpen;
        if (ImGui.Checkbox("簡易ウィンドウを表示##cfg_MiniWindowEnabled", ref miniOpen))
        {
            miniWindow.IsOpen = miniOpen;
            config.MiniWindowEnabled = miniOpen;
            config.Save();
        }
        ImGui.TextDisabled("ゲーム／プラグイン起動時はCJS本体・Miniとも自動表示しません。表示する項目を選択できます。");

        var miniBgOpacityPercent = Math.Clamp(config.MiniBackgroundOpacity * 100f, 5f, 100f);
        ImGui.SetNextItemWidth(180f);
        if (ImGui.SliderFloat("Mini背景の濃さ##MiniBackgroundOpacity", ref miniBgOpacityPercent, 5f, 100f, "%.0f%%", ImGuiSliderFlags.None))
        {
            config.MiniBackgroundOpacity = miniBgOpacityPercent / 100f;
            config.Save();
        }
        var miniTitleOpacityPercent = Math.Clamp(config.MiniTitleOpacity * 100f, 5f, 100f);
        ImGui.SetNextItemWidth(180f);
        if (ImGui.SliderFloat("Miniタイトルバーの濃さ##MiniTitleOpacity", ref miniTitleOpacityPercent, 5f, 100f, "%.0f%%", ImGuiSliderFlags.None))
        {
            config.MiniTitleOpacity = miniTitleOpacityPercent / 100f;
            config.Save();
        }
        ImGui.TextDisabled("本文背景とタイトルバーを別々に調整します。文字・ボタン・チェック・タイトル文字は薄くしません。");
        DrawConfigBool("現在JOB・ロール", nameof(Configuration.MiniShowJobRole), config.MiniShowJobRole, v => config.MiniShowJobRole = v);
        DrawConfigBool("BM AI ON/OFF", nameof(Configuration.MiniShowBossMod), config.MiniShowBossMod, v => config.MiniShowBossMod = v);
        DrawConfigBool("BMR AI ON/OFF", nameof(Configuration.MiniShowBmr), config.MiniShowBmr, v => config.MiniShowBmr = v);
        DrawConfigBool("RSR 停止／自動／手動", nameof(Configuration.MiniShowRsr), config.MiniShowRsr, v => config.MiniShowRsr = v);
        DrawConfigBool("Wrath 自動ローテーション", nameof(Configuration.MiniShowWrath), config.MiniShowWrath, v => config.MiniShowWrath = v);
        DrawConfigBool("JOB設定 適用ボタン", nameof(Configuration.MiniShowJobApply), config.MiniShowJobApply, v => config.MiniShowJobApply = v);
        DrawConfigBool("ロール設定 適用ボタン", nameof(Configuration.MiniShowRoleApply), config.MiniShowRoleApply, v => config.MiniShowRoleApply = v);
        DrawConfigBool("デフォルト 適用ボタン", nameof(Configuration.MiniShowDefaultApply), config.MiniShowDefaultApply, v => config.MiniShowDefaultApply = v);
        DrawConfigBool("名前付きプリセット", nameof(Configuration.MiniShowNamedProfile), config.MiniShowNamedProfile, v => config.MiniShowNamedProfile = v);
        DrawConfigBool("CJS各タブを開くボタン", nameof(Configuration.MiniShowTabButtons), config.MiniShowTabButtons, v => config.MiniShowTabButtons = v);
        DrawConfigBool("本家プラグインを開くボタン", nameof(Configuration.MiniShowOriginalPluginButtons), config.MiniShowOriginalPluginButtons, v => config.MiniShowOriginalPluginButtons = v);
    }

    private void DrawConfigBool(string label, string id, bool value, Action<bool> setter)
    {
        var v = value;
        if (ImGui.Checkbox($"{label}##cfg_{id}", ref v))
        {
            setter(v);
            config.Save();
        }
    }

    private static string CapturedText(bool captured) => captured ? "保存済み" : "対象外";

    private string DefaultStoragePath()
    {
        try
        {
            var dll = typeof(Plugin).Assembly.Location;
            var dir = Path.GetDirectoryName(dll);
            if (!string.IsNullOrWhiteSpace(dir))
                return Path.Combine(dir, "Config");
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CombatJobSettings");
    }

    private void InitializeStorage()
    {
        if (string.IsNullOrWhiteSpace(config.StoragePath))
        {
            config.StoragePath = DefaultStoragePath();
            config.Save();
        }
        try { Directory.CreateDirectory(config.StoragePath); }
        catch (Exception ex) { Service.Log.Warning(ex, "Could not create CombatJobSettings storage directory"); }
    }

    private void ApplyStoragePath(string path)
    {
        try
        {
            path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (string.IsNullOrWhiteSpace(path))
            {
                status = "保存場所を入力してください";
                return;
            }
            Directory.CreateDirectory(path);
            config.StoragePath = Path.GetFullPath(path);
            storagePathEdit = config.StoragePath;
            config.Save();
            SaveAllExternalData();
            status = $"保存場所を変更しました：{config.StoragePath}";
        }
        catch (Exception ex)
        {
            status = $"保存場所の変更に失敗：{ex.Message}";
            Service.Log.Warning(ex, "Failed to change CombatJobSettings storage path");
        }
    }

    private void OpenStorageFolder()
    {
        try
        {
            Directory.CreateDirectory(config.StoragePath);
            Process.Start(new ProcessStartInfo { FileName = config.StoragePath, UseShellExecute = true });
            status = "保存場所を開きました";
        }
        catch (Exception ex)
        {
            status = $"保存場所を開けません：{ex.Message}";
        }
    }

    private string StorageFile(string fileName) => Path.Combine(config.StoragePath, fileName);

    private void SaveJson<T>(string fileName, T value)
    {
        Directory.CreateDirectory(config.StoragePath);
        var path = StorageFile(fileName);
        var temp = path + ".tmp";
        var node = JsonSerializer.SerializeToNode(value, jsonOptions);
        var decorated = DecorateJsonForHumans(fileName, node);
        File.WriteAllText(temp, decorated.ToJsonString(jsonOptions));
        File.Move(temp, path, true);
    }

    private JsonNode DecorateJsonForHumans(string fileName, JsonNode? node)
    {
        var root = new JsonObject
        {
            ["_comment"] = fileName switch
            {
                "Settings.json" => "CombatJobSettings本体の保存先・自動適用などの基本設定です。",
                "DefaultSettings.json" => "復元用デフォルト設定です。JOB設定・ロール設定がない場合にもこの設定へ戻します。",
                "JobProfiles.json" => "JOBごとの保存設定です。JOB設定はロール設定より優先されます。",
                "RoleProfiles.json" => "ロールごとの保存設定です。JOB個別設定がない場合に使用します。",
                "NamedProfiles.json" => "ユーザーが自由な名前で保存した手動適用用プリセットです。JOB・ロールとは独立しています。",
                _ => "CombatJobSettingsの設定ファイルです。"
            },
            ["_savedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["_version"] = "V0.1.0"
        };

        if (node is JsonObject obj)
        {
            foreach (var kv in obj.ToList())
            {
                var child = kv.Value?.DeepClone();
                if (child is JsonObject childObj)
                {
                    string? comment = null;
                    if (fileName == "JobProfiles.json" && uint.TryParse(kv.Key, out var jobId))
                        comment = $"{JobName(jobId)} JOB設定";
                    else if (fileName == "RoleProfiles.json")
                        comment = $"{RoleName(kv.Key)} ロール設定";
                    else if (fileName == "NamedProfiles.json")
                        comment = $"名前付きプリセット『{kv.Key}』";
                    if (comment != null)
                    {
                        var commented = new JsonObject { ["_comment"] = comment };
                        foreach (var childKv in childObj.ToList())
                            commented[childKv.Key] = childKv.Value?.DeepClone();
                        child = commented;
                    }
                }
                root[kv.Key] = child;
            }
        }
        else
        {
            root["data"] = node?.DeepClone();
        }
        return root;
    }

    private T? LoadJson<T>(string fileName)
    {
        try
        {
            var path = StorageFile(fileName);
            if (!File.Exists(path)) return default;
            var text = File.ReadAllText(path);
            var node = JsonNode.Parse(text);
            if (node is JsonObject root)
            {
                root.Remove("_comment");
                root.Remove("_savedAt");
                root.Remove("_version");
                foreach (var kv in root.ToList())
                {
                    if (kv.Value is JsonObject child)
                        child.Remove("_comment");
                }
                return root.Deserialize<T>(jsonOptions);
            }
            return JsonSerializer.Deserialize<T>(text, jsonOptions);
        }
        catch (Exception ex)
        {
            Service.Log.Warning(ex, $"Failed to load {fileName}");
            return default;
        }
    }

    private void SaveSettingsInfo()
    {
        try
        {
            SaveJson("Settings.json", new ExternalSettingsInfo
            {
                StoragePath = config.StoragePath,
                AutoApplyOnJobChange = config.AutoApplyOnJobChange,
                UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            });
        }
        catch (Exception ex) { Service.Log.Warning(ex, "Failed to save Settings.json"); }
    }

    private void SaveProfilesExternal()
    {
        try
        {
            SaveJson("JobProfiles.json", config.JobProfiles);
            SaveJson("RoleProfiles.json", config.RoleProfiles);
            SaveJson("NamedProfiles.json", config.NamedProfiles);
            SaveSettingsInfo();
        }
        catch (Exception ex) { Service.Log.Warning(ex, "Failed to save external profiles"); }
    }

    private void SaveAllExternalData()
    {
        SaveProfilesExternal();
        if (defaultSnapshot != null)
        {
            try { SaveJson("DefaultSettings.json", defaultSnapshot); }
            catch (Exception ex) { Service.Log.Warning(ex, "Failed to save default snapshot"); }
        }
    }

    private void LoadExternalProfiles()
    {
        try
        {
            var jobs = LoadJson<Dictionary<uint, CombatProfile>>("JobProfiles.json");
            var roles = LoadJson<Dictionary<string, CombatProfile>>("RoleProfiles.json");
            var named = LoadJson<Dictionary<string, CombatProfile>>("NamedProfiles.json");
            if (jobs != null) config.JobProfiles = jobs;
            if (roles != null) config.RoleProfiles = roles;
            if (named != null) config.NamedProfiles = new Dictionary<string, CombatProfile>(named, StringComparer.OrdinalIgnoreCase);
            if (jobs == null && roles == null && named == null)
                SaveProfilesExternal();
        }
        catch (Exception ex) { Service.Log.Warning(ex, "Failed to load external profiles"); }
    }

    private void LoadDefaultSnapshot()
    {
        defaultSnapshot = LoadJson<DefaultSettingsSnapshot>("DefaultSettings.json");
    }

    private void CaptureDefaultSettings()
    {
        var snapshotProfile = config.Editor.Clone();
        var bossMod = TryReadBossModCurrentValues(snapshotProfile, false);
        var bmr = TryReadBmrCurrentValues(snapshotProfile, false);
        var rsr = TryReadRsrCurrentValues(snapshotProfile);
        var wrath = TryReadWrathCurrentValues(snapshotProfile);

        if (wrathLiveAvailable)
            snapshotProfile.WrathAutoRotation = wrathLiveAuto;

        defaultSnapshot = new DefaultSettingsSnapshot
        {
            Profile = snapshotProfile,
            BossModCaptured = bossMod,
            BmrCaptured = bmr && bmrAiLiveAvailable,
            RsrCaptured = rsr,
            WrathCaptured = wrath && wrathLiveAvailable,
            SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };

        try
        {
            SaveJson("DefaultSettings.json", defaultSnapshot);
            SaveSettingsInfo();
            status = $"現在の実設定をデフォルト保存しました：{defaultSnapshot.SavedAt}";
        }
        catch (Exception ex)
        {
            status = $"デフォルト保存失敗：{ex.Message}";
            Service.Log.Warning(ex, "Failed to save default settings");
        }
    }

    private void RestoreDefaultSettings()
    {
        if (defaultSnapshot == null) return;
        var p = defaultSnapshot.Profile;
        try
        {
            var current = config.Editor.Clone();
            var bossRead = defaultSnapshot.BossModCaptured && TryReadBossModCurrentValues(current, false);
            var bmrRead = defaultSnapshot.BmrCaptured && TryReadBmrCurrentValues(current, false);
            var rsrRead = defaultSnapshot.RsrCaptured && TryReadRsrCurrentValues(current);
            var wrathRead = defaultSnapshot.WrathCaptured && TryReadWrathCurrentValues(current);
            if (defaultSnapshot.WrathCaptured && wrathLiveAvailable)
                current.WrathAutoRotation = wrathLiveAuto;

            var changeCount = 0;

            if (bossRead && bossModLiveAvailable && current.BossModAiEnabled != p.BossModAiEnabled)
            {
                Send($"/vbm ai {(p.BossModAiEnabled ? "on" : "off")}", false);
                changeCount++;
            }

            if (bmrRead && (bmrLiveAvailable || bmrAiLiveAvailable))
            {
                if (bmrAiLiveAvailable && current.BmrAiEnabled != p.BmrAiEnabled)
                {
                    Send($"/bmrai {(p.BmrAiEnabled ? "on" : "off")}", false);
                    changeCount++;
                }
                if (bmrLiveAvailable && current.BmrFollowTarget != p.BmrFollowTarget)
                {
                    Send($"/bmrai followtarget {(p.BmrFollowTarget ? "on" : "off")}", false);
                    changeCount++;
                }
                if (bmrLiveAvailable && !SameFloat(current.BmrMaxDistanceTarget, p.BmrMaxDistanceTarget))
                {
                    Send($"/bmrai MAXDISTANCETARGET {p.BmrMaxDistanceTarget.ToString(CultureInfo.InvariantCulture)}", false);
                    changeCount++;
                }
                if (bmrLiveAvailable && !SameFloat(current.BmrMinDistance, p.BmrMinDistance))
                {
                    Send($"/bmrai MINDISTANCE {p.BmrMinDistance.ToString(CultureInfo.InvariantCulture)}", false);
                    changeCount++;
                }
            }

            if (rsrRead && rsrLiveAvailable)
            {
                if (!IsRsrMode(rsrLiveState, p.RsrOperatingMode))
                {
                    Send(RsrModeCommand(p.RsrOperatingMode, p.RsrAutoEnabled), false);
                    changeCount++;
                }
                if (rsrEngageLiveAvailable && !string.IsNullOrWhiteSpace(p.RsrEngageSetting) &&
                    !string.Equals(current.RsrEngageSetting, p.RsrEngageSetting, StringComparison.OrdinalIgnoreCase))
                {
                    if (TrySetRsrEngageSetting(p.RsrEngageSetting))
                        changeCount++;
                }
                if (!SameRsrTargets(current.RsrTargetingTypes, p.RsrTargetingTypes))
                {
                    ApplyRsrTargets(p);
                    changeCount++;
                }
            }

            if (wrathRead && (wrathLiveAvailable || wrathTargetLiveAvailable))
            {
                if (wrathLiveAvailable && current.WrathAutoRotation != p.WrathAutoRotation)
                {
                    Send($"/wrath auto {(p.WrathAutoRotation ? "on" : "off")}", false);
                    changeCount++;
                }
                changeCount += ApplyWrathSettingsDifferential(current, p);
            }

            config.Editor = p.Clone();
            config.Save();
            activeSettingsName = "保存済みデフォルト";
            status = changeCount == 0
                ? "保存済みデフォルトを確認しました（変更なし）"
                : $"保存済みデフォルトへ戻しました（{changeCount}項目変更）";
        }
        catch (Exception ex)
        {
            status = $"デフォルト復元エラー：{ex.Message}";
            Service.Log.Error(ex, "Failed to restore default settings");
        }
    }

    private bool TryReadRsrCurrentValues(CombatProfile p)
    {
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetType("RotationSolver.Commands.RSCommands", false) != null);
            if (asm == null)
            {
                rsrLiveAvailable = false;
                rsrEngageLiveAvailable = false;
                rsrLiveState = "未起動";
                return false;
            }

            var serviceType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("RotationSolver.Basic.Service", false))
                .FirstOrDefault(t => t != null);
            var rsCommandsType = asm.GetType("RotationSolver.Commands.RSCommands", false);
            var rsrConfig = serviceType != null ? GetStaticMember(serviceType, "Config") : null;

            var readSomething = false;
            if (rsCommandsType != null)
            {
                var rawState = GetStaticMember(rsCommandsType, "_stateString")?.ToString();
                if (!string.IsNullOrWhiteSpace(rawState))
                {
                    rsrLiveState = rawState;
                    p.RsrOperatingMode = IsRsrMode(rawState, "Auto") ? "Auto"
                        : IsRsrMode(rawState, "Manual") ? "Manual"
                        : IsRsrMode(rawState, "Off") ? "Off"
                        : rawState;
                    p.RsrAutoEnabled = IsRsrMode(rawState, "Auto");
                    readSomething = true;
                }
            }

            if (rsrConfig != null)
            {
                if (TryReadRsrEngageSetting(rsrConfig, p))
                    readSomething = true;

                var rawTargets = GetInstanceMember(rsrConfig, "TargetingTypes");
                if (rawTargets is System.Collections.IEnumerable enumerable && rawTargets is not string)
                {
                    var liveTargets = new List<string>();
                    foreach (var item in enumerable)
                    {
                        var name = item?.ToString();
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        var canonical = CanonicalRsrTarget(name);
                        if (canonical != null && !liveTargets.Contains(canonical, StringComparer.OrdinalIgnoreCase))
                            liveTargets.Add(canonical);
                    }

                    // An empty collection is a valid setting (no extra target conditions selected).
                    p.RsrTargetingTypes = liveTargets;
                    readSomething = true;
                }
            }

            rsrLiveAvailable = readSomething;
            return readSomething;
        }
        catch (Exception ex)
        {
            rsrLiveAvailable = false;
            rsrEngageLiveAvailable = false;
            rsrLiveState = "取得エラー";
            Service.Log.Debug(ex, "Failed to read RotationSolverReborn current values");
            return false;
        }
    }

    private bool TryReadWrathCurrentValues(CombatProfile p)
    {
        try
        {
            if (!TryGetWrathObjects(out var configuration, out var rotationConfig, out var dps, out var healer))
            {
                wrathTargetLiveAvailable = false;
                return false;
            }

            var damageMode = GetInstanceMember(rotationConfig, "DPSRotationMode");
            var healerMode = GetInstanceMember(rotationConfig, "HealerRotationMode");
            var damageCanonical = damageMode == null ? null : CanonicalWrathTarget(damageMode.ToString() ?? string.Empty);
            var healerCanonical = healerMode == null ? null : CanonicalWrathHealerTarget(healerMode.ToString() ?? string.Empty);
            if (damageCanonical != null) p.WrathDamageTargetMode = damageCanonical;
            if (healerCanonical != null) p.WrathHealerTargetMode = healerCanonical;

            ReadInto(dps, "DPSAoETargets", ref p.WrathDpsAoeTargets);
            ReadInto(dps, "MaxDistance", ref p.WrathMaxDistance);
            ReadInto(dps, "IgnoreRangeInBoss", ref p.WrathIgnoreRangeInBoss);
            ReadInto(dps, "FATEPriority", ref p.WrathFatePriority);
            ReadInto(dps, "QuestPriority", ref p.WrathQuestPriority);
            ReadInto(dps, "PreferNonCombat", ref p.WrathPreferNonCombat);
            ReadInto(dps, "OnlyAttackInCombat", ref p.WrathOnlyAttackInCombat);
            ReadInto(dps, "DPSAlwaysHardTarget", ref p.WrathDpsAlwaysHardTarget);

            ReadInto(healer, "SingleTargetHPP", ref p.WrathSingleTargetHpp);
            ReadInto(healer, "SingleTargetRegenHPP", ref p.WrathSingleTargetRegenHpp);
            ReadInto(healer, "SingleTargetExcogHPP", ref p.WrathSingleTargetExcogHpp);
            ReadInto(healer, "AoETargetHPP", ref p.WrathAoeTargetHpp);
            ReadInto(healer, "IncludeShields", ref p.WrathIncludeShields);
            ReadInto(healer, "AoEHealTargetCount", ref p.WrathAoeHealTargetCount);
            ReadInto(healer, "HealDelay", ref p.WrathHealDelay);
            ReadInto(healer, "AutoRez", ref p.WrathAutoRez);
            ReadInto(healer, "AutoRezOutOfParty", ref p.WrathAutoRezOutOfParty);
            ReadInto(healer, "AutoRezRequireSwift", ref p.WrathAutoRezRequireSwift);
            ReadInto(healer, "AutoRezDPSJobs", ref p.WrathAutoRezDpsJobs);
            ReadInto(healer, "AutoRezDPSJobsHealersOnly", ref p.WrathAutoRezDpsJobsHealersOnly);
            ReadInto(healer, "AutoCleanse", ref p.WrathAutoCleanse);
            ReadInto(healer, "ManageKardia", ref p.WrathManageKardia);
            ReadInto(healer, "KardiaTanksOnly", ref p.WrathKardiaTanksOnly);
            ReadInto(healer, "PreEmptiveHoT", ref p.WrathPreEmptiveHot);
            ReadInto(healer, "IncludeNPCs", ref p.WrathIncludeNpcs);
            ReadInto(healer, "HealerAlwaysHardTarget", ref p.WrathHealerAlwaysHardTarget);
            ReadInto(healer, "HandleRaidwides", ref p.WrathHandleRaidwides);
            ReadInto(healer, "HandleTankbusters", ref p.WrathHandleTankbusters);

            wrathTargetLiveAvailable = damageCanonical != null || healerCanonical != null;
            return true;
        }
        catch (Exception ex)
        {
            wrathTargetLiveAvailable = false;
            Service.Log.Debug(ex, "Failed to read Wrath settings");
            return false;
        }
    }

    private static string? CanonicalWrathHealerTarget(string value)
    {
        var normalized = NormalizeToken(value);
        foreach (var (internalName, _) in WrathHealerTargets)
            if (NormalizeToken(internalName) == normalized)
                return internalName;
        return null;
    }

    private static void ReadInto<T>(object instance, string name, ref T target)
    {
        var raw = GetInstanceMember(instance, name);
        if (raw is T typed)
        {
            target = typed;
            return;
        }
        if (raw == null) return;
        try
        {
            target = (T)Convert.ChangeType(raw, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture);
        }
        catch { }
    }

    private bool TryGetWrathObjects(out object configuration, out object rotationConfig, out object dps, out object healer)
    {
        configuration = rotationConfig = dps = healer = null!;
        var asm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetType("WrathCombo.WrathCombo", false) != null || a.GetType("WrathCombo.Commands", false) != null);
        if (asm == null) return false;
        var serviceType = asm.GetType("WrathCombo.Services.Service", false);
        configuration = serviceType != null ? GetStaticMember(serviceType, "Configuration")! : null!;
        if (configuration == null) return false;
        rotationConfig = GetInstanceMember(configuration, "RotationConfig")!;
        if (rotationConfig == null) return false;
        dps = GetInstanceMember(rotationConfig, "DPSSettings")!;
        healer = GetInstanceMember(rotationConfig, "HealerSettings")!;
        return dps != null && healer != null;
    }

    private bool WriteWrathSetting(string member, object? value, bool healerSection = false)
    {
        try
        {
            if (!TryGetWrathObjects(out var configuration, out var rotationConfig, out var dps, out var healer))
            {
                status = $"Wrath反映失敗：{member}（本体未取得）";
                return false;
            }
            var target = healerSection || IsWrathHealerMember(member) ? healer : dps;
            if (!SetInstanceMember(target, member, value))
            {
                status = $"Wrath反映失敗：{member}（項目未検出）";
                return false;
            }
            InvokeNoArg(configuration, "Save");
            status = $"Wrathへ即時反映：{member}";
            return true;
        }
        catch (Exception ex)
        {
            status = $"Wrath反映エラー：{member}";
            Service.Log.Warning(ex, "Failed to write Wrath setting {Member}", member);
            return false;
        }
    }

    private static bool IsWrathHealerMember(string name) => name is
        "SingleTargetHPP" or "SingleTargetRegenHPP" or "SingleTargetExcogHPP" or "AoETargetHPP" or
        "IncludeShields" or "AoEHealTargetCount" or "HealDelay" or "AutoRez" or "AutoRezOutOfParty" or
        "AutoRezRequireSwift" or "AutoRezDPSJobs" or "AutoRezDPSJobsHealersOnly" or "AutoCleanse" or
        "ManageKardia" or "KardiaTanksOnly" or "PreEmptiveHoT" or "IncludeNPCs" or
        "HealerAlwaysHardTarget" or "HandleRaidwides" or "HandleTankbusters";

    private static string? CanonicalRsrTarget(string value)
    {
        var normalized = NormalizeToken(value);
        foreach (var (internalName, _) in RsrTargets)
            if (NormalizeToken(internalName) == normalized)
                return internalName;
        return null;
    }

    private static string? CanonicalWrathTarget(string value)
    {
        var normalized = NormalizeToken(value);
        foreach (var (internalName, _) in WrathTargets)
            if (NormalizeToken(internalName) == normalized)
                return internalName;
        return null;
    }

    private static string NormalizeToken(string value) =>
        new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private bool TryReadBossModCurrentValues(CombatProfile p, bool updateStatus)
    {
        try
        {
            // Original BossMod (VBM) has BossMod.AI.AIWindow. BMR uses AIManagementWindow.
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetType("BossMod.AI.AIWindow", false) != null);
            if (asm == null)
            {
                bossModLiveAvailable = false;
                if (updateStatus) status = "BossMod取得失敗：BossModが見つかりません";
                return false;
            }

            var serviceType = asm.GetType("BossMod.Service", false);
            var aiConfigType = asm.GetType("BossMod.AI.AIConfig", false);
            if (serviceType == null || aiConfigType == null)
            {
                bossModLiveAvailable = false;
                if (updateStatus) status = "BossMod取得失敗：AIConfigの型が見つかりません";
                return false;
            }

            // BossMod itself uses Service.Config.Get<AIConfig>() in AIWindow.
            var configService = GetStaticMember(serviceType, "Config");
            if (configService == null)
            {
                bossModLiveAvailable = false;
                if (updateStatus) status = "BossMod取得失敗：Configサービスへアクセスできません";
                return false;
            }

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var getMethod = configService.GetType().GetMethods(flags)
                .FirstOrDefault(m => m.Name == "Get" && m.IsGenericMethodDefinition &&
                                     m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 0);
            if (getMethod == null)
            {
                bossModLiveAvailable = false;
                if (updateStatus) status = "BossMod取得失敗：Config.Get<T>()が見つかりません";
                return false;
            }

            var bossConfig = getMethod.MakeGenericMethod(aiConfigType).Invoke(configService, null);
            if (bossConfig == null || !TryReadMember(bossConfig, "Enabled", out bool enabled))
            {
                bossModLiveAvailable = false;
                if (updateStatus) status = "BossMod取得失敗：AI Enable状態を取得できません";
                return false;
            }

            p.BossModAiEnabled = enabled;
            bossModLiveAvailable = true;
            if (updateStatus)
                status = $"BossMod実値取得：AI {(enabled ? "ON" : "OFF")}";
            return true;
        }
        catch (Exception ex)
        {
            bossModLiveAvailable = false;
            if (updateStatus) status = $"BossMod取得エラー：{ex.GetType().Name}";
            Service.Log.Warning(ex, "Failed to read BossMod current values");
            return false;
        }
    }

    private bool TryReadBmrCurrentValues(CombatProfile p, bool updateStatus)
    {
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetType("BossMod.AI.AIManagementWindow", false) != null);
            if (asm == null)
            {
                bmrLiveAvailable = false;
                bmrAiLiveAvailable = false;
                if (updateStatus) status = "BMR取得失敗：BossModRebornが見つかりません";
                return false;
            }

            var windowType = asm.GetType("BossMod.AI.AIManagementWindow", false);
            var configField = windowType?.GetField("_config", BindingFlags.Static | BindingFlags.NonPublic);
            var bmrConfig = configField?.GetValue(null);
            if (bmrConfig == null)
            {
                bmrLiveAvailable = false;
                if (updateStatus) status = "BMR取得失敗：AIConfigへアクセスできません";
                return false;
            }

            if (!TryReadMember(bmrConfig, "FollowDuringCombat", out bool followCombat) ||
                !TryReadMember(bmrConfig, "FollowDuringActiveBossModule", out bool followActiveBossModule) ||
                !TryReadMember(bmrConfig, "FollowTarget", out bool followTarget) ||
                !TryReadMember(bmrConfig, "MaxDistanceToTarget", out float maxDistance) ||
                !TryReadMember(bmrConfig, "MinDistance", out float minDistance))
            {
                bmrLiveAvailable = false;
                if (updateStatus) status = "BMR取得失敗：設定項目の構造が想定と異なります";
                return false;
            }

            p.BmrFollowCombat = followCombat;
            p.BmrFollowTarget = followTarget;
            p.BmrMaxDistanceTarget = maxDistance;
            p.BmrMinDistance = minDistance;
            bmrFollowActiveBossModule = followActiveBossModule;
            bmrLiveAvailable = true;

            // Current BMR exposes the active AI manager through AIManager.Instance.
            // AIManagementWindow._manager is an instance field, so reading it as static
            // returns nothing on current builds. Mirror BMR's own ON/OFF check instead.
            bmrAiLiveAvailable = false;
            var managerType = asm.GetType("BossMod.AI.AIManager", false);
            object? manager = null;
            if (managerType != null)
            {
                manager = managerType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null)
                    ?? managerType.GetField("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
            }
            if (manager != null)
            {
                var beh = GetInstanceMember(manager, "Beh");
                p.BmrAiEnabled = beh != null;
                bmrAiLiveAvailable = true;
            }

            if (updateStatus)
                status = $"BMR実値取得：AI {(p.BmrAiEnabled ? "ON" : "OFF")} / 最大 {maxDistance:0.###} / 最小 {minDistance:0.###}";
            return true;
        }
        catch (Exception ex)
        {
            bmrLiveAvailable = false;
            bmrAiLiveAvailable = false;
            if (updateStatus) status = $"BMR取得エラー：{ex.GetType().Name}";
            Service.Log.Warning(ex, "Failed to read BossModReborn current values");
            return false;
        }
    }

    private static bool TryReadMember<T>(object instance, string name, out T value)
    {
        value = default!;
        var raw = GetInstanceMember(instance, name);
        if (raw is T typed)
        {
            value = typed;
            return true;
        }

        try
        {
            if (raw != null)
            {
                value = (T)Convert.ChangeType(raw, typeof(T), CultureInfo.InvariantCulture);
                return true;
            }
        }
        catch
        {
            // ignored; caller will show a clear read failure
        }
        return false;
    }

    private static object? GetStaticMember(Type type, string name)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        return type.GetProperty(name, flags)?.GetValue(null) ?? type.GetField(name, flags)?.GetValue(null);
    }

    private static object? GetInstanceMember(object instance, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = instance.GetType();
        return type.GetProperty(name, flags)?.GetValue(instance) ?? type.GetField(name, flags)?.GetValue(instance);
    }

    private static bool SetInstanceMember(object instance, string name, object? value)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = instance.GetType();
        var prop = type.GetProperty(name, flags);
        if (prop != null && prop.CanWrite)
        {
            prop.SetValue(instance, ConvertForMember(value, prop.PropertyType));
            return true;
        }
        var field = type.GetField(name, flags);
        if (field != null)
        {
            field.SetValue(instance, ConvertForMember(value, field.FieldType));
            return true;
        }
        return false;
    }

    private static object? ConvertForMember(object? value, Type targetType)
    {
        if (value == null) return null;
        var underlying = Nullable.GetUnderlyingType(targetType);
        if (underlying != null)
            return Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);
        if (targetType.IsEnum && value is string s)
            return Enum.Parse(targetType, s, true);
        if (targetType.IsInstanceOfType(value)) return value;
        return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
    }

    private static void InvokeNoArg(object instance, string methodName)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        instance.GetType().GetMethod(methodName, flags, binder: null, types: Type.EmptyTypes, modifiers: null)?.Invoke(instance, null);
    }

    private bool TryReadRsrEngageSetting(object rsrConfig, CombatProfile p)
    {
        try
        {
            // RSR source: Configs._hostileType has [JobConfig].
            // JobConfigGenerator creates the public per-job property "HostileType".
            // Reading the backing field would only return the default value, not the live current-job value.
            const string memberName = "HostileType";
            var type = rsrConfig.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var prop = type.GetProperty(memberName, flags);
            if (prop == null || !prop.CanRead)
            {
                rsrEngageLiveAvailable = false;
                return false;
            }

            var raw = prop.GetValue(rsrConfig);
            if (raw == null)
            {
                rsrEngageLiveAvailable = false;
                return false;
            }

            rsrEngageMemberName = memberName;
            var valueType = prop.PropertyType;
            rsrEngageChoices = valueType.IsEnum
                ? Enum.GetNames(valueType)
                : Array.Empty<string>();

            p.RsrEngageSetting = raw.ToString() ?? string.Empty;
            rsrEngageLiveAvailable = rsrEngageChoices.Length > 0;
            return rsrEngageLiveAvailable;
        }
        catch (Exception ex)
        {
            rsrEngageLiveAvailable = false;
            Service.Log.Debug(ex, "Failed to read RSR HostileType");
            return false;
        }
    }

    private bool TrySetRsrEngageSetting(string value)
    {
        try
        {
            var serviceType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("RotationSolver.Basic.Service", false))
                .FirstOrDefault(t => t != null);
            var rsrConfig = serviceType != null ? GetStaticMember(serviceType, "Config") : null;
            if (rsrConfig == null) return false;

            const string memberName = "HostileType";
            if (!SetInstanceMember(rsrConfig, memberName, value))
                return false;

            rsrEngageMemberName = memberName;
            InvokeNoArg(rsrConfig, "Save");
            TryReadRsrEngageSetting(rsrConfig, config.Editor);
            return true;
        }
        catch (Exception ex)
        {
            Service.Log.Debug(ex, "Failed to set RSR HostileType");
            return false;
        }
    }

    private static string RsrEngageDisplayName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "未取得";

        return raw switch
        {
            "AllTargetsCanAttack" => "射程内の全敵を対象（タンク／AutoDuty）",
            "TargetsHaveTarget" => "交戦済みの敵のみ（非タンク）",
            "AllTargetsWhenSoloInDuty" => "コンテンツ内ソロ時は全敵、それ以外は交戦済みのみ",
            "AllTargetsWhenSolo" => "ソロ時は全敵、PT時は交戦済みのみ",
            "SoloDeepDungeonSmart" => "DDソロ：非戦闘時は最寄り1体、戦闘中は交戦済みのみ",
            _ => raw,
        };
    }

    private void ApplyRsrTargets(CombatProfile p)
    {
        Send("/rotation Settings TargetingTypes removeall");
        foreach (var t in p.RsrTargetingTypes.Distinct(StringComparer.OrdinalIgnoreCase))
            Send($"/rotation Settings TargetingTypes add {t}");
    }

    private static bool SameRsrTargets(IEnumerable<string> a, IEnumerable<string> b)
    {
        var aa = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
        var bb = new HashSet<string>(b, StringComparer.OrdinalIgnoreCase);
        return aa.SetEquals(bb);
    }

    private static bool SameFloat(float a, float b) => MathF.Abs(a - b) < 0.001f;

    private (CombatProfile? Profile, string Source, string Kind) TryResolveSavedProfile(uint job)
    {
        if (config.JobProfiles.TryGetValue(job, out var jp))
            return (jp, $"{JobName(job)}設定", "J");
        var role = RoleKey(job);
        if (config.RoleProfiles.TryGetValue(role, out var rp))
            return (rp, $"{RoleName(role)}設定", "R");
        if (defaultSnapshot != null)
            return (defaultSnapshot.Profile, "保存済みデフォルト", "D");
        return (null, "デフォルト未保存", "");
    }

    private void ApplyProfile(CombatProfile p, string reason)
    {
        try
        {
            // 適用直前に各本体の実値を読み直し、差分がある項目だけ変更する。
            // 同じロール設定のままJOB変更した場合など、不要なコマンド送信・保存を発生させない。
            var current = config.Editor.Clone();
            var bossRead = TryReadBossModCurrentValues(current, false);
            var bmrRead = TryReadBmrCurrentValues(current, false);
            var rsrRead = TryReadRsrCurrentValues(current);
            var wrathRead = TryReadWrathCurrentValues(current);
            if (wrathLiveAvailable)
                current.WrathAutoRotation = wrathLiveAuto;

            var changeCount = 0;

            // 未導入・未ロードのプラグインにはコマンドも設定書き込みも行わない。
            if (bossRead && bossModLiveAvailable && current.BossModAiEnabled != p.BossModAiEnabled)
            {
                Send($"/vbm ai {(p.BossModAiEnabled ? "on" : "off")}", false);
                changeCount++;
            }

            if (bmrRead && (bmrLiveAvailable || bmrAiLiveAvailable))
            {
                if (bmrAiLiveAvailable && current.BmrAiEnabled != p.BmrAiEnabled)
                {
                    Send($"/bmrai {(p.BmrAiEnabled ? "on" : "off")}", false);
                    changeCount++;
                }
                if (bmrLiveAvailable && current.BmrFollowTarget != p.BmrFollowTarget)
                {
                    Send($"/bmrai followtarget {(p.BmrFollowTarget ? "on" : "off")}", false);
                    changeCount++;
                }
                if (bmrLiveAvailable && !SameFloat(current.BmrMaxDistanceTarget, p.BmrMaxDistanceTarget))
                {
                    Send($"/bmrai MAXDISTANCETARGET {p.BmrMaxDistanceTarget.ToString(CultureInfo.InvariantCulture)}", false);
                    changeCount++;
                }
                if (bmrLiveAvailable && !SameFloat(current.BmrMinDistance, p.BmrMinDistance))
                {
                    Send($"/bmrai MINDISTANCE {p.BmrMinDistance.ToString(CultureInfo.InvariantCulture)}", false);
                    changeCount++;
                }
            }

            if (rsrRead && rsrLiveAvailable)
            {
                var wantMode = p.RsrOperatingMode;
                // Auto中にAutoコマンドを再送するとTargetingTypeが順送りされるため、同一モードなら送らない。
                if (!IsRsrMode(rsrLiveState, wantMode))
                {
                    Send(RsrModeCommand(wantMode, p.RsrAutoEnabled), false);
                    changeCount++;
                }

                if (rsrEngageLiveAvailable && !string.IsNullOrWhiteSpace(p.RsrEngageSetting) &&
                    !string.Equals(current.RsrEngageSetting, p.RsrEngageSetting, StringComparison.OrdinalIgnoreCase))
                {
                    if (TrySetRsrEngageSetting(p.RsrEngageSetting))
                        changeCount++;
                }

                if (!SameRsrTargets(current.RsrTargetingTypes, p.RsrTargetingTypes))
                {
                    ApplyRsrTargets(p);
                    changeCount++;
                }
            }

            if (wrathRead && (wrathLiveAvailable || wrathTargetLiveAvailable))
            {
                if (wrathLiveAvailable && current.WrathAutoRotation != p.WrathAutoRotation)
                {
                    Send($"/wrath auto {(p.WrathAutoRotation ? "on" : "off")}", false);
                    changeCount++;
                }

                // Wrathの対象モード＋詳細設定は直接設定オブジェクトへ差分反映し、Saveは最後に1回だけ。
                changeCount += ApplyWrathSettingsDifferential(current, p);
            }

            // Keep the current editor synchronized with the profile just applied.
            config.Editor = p.Clone();
            config.Save();
            SetActiveSettingsSource(reason);
            status = changeCount == 0
                ? $"適用：{FormatMiniActiveSettingsName(activeSettingsName)}（変更なし）"
                : $"適用：{FormatMiniActiveSettingsName(activeSettingsName)}（{changeCount}項目変更）";
        }
        catch (Exception ex)
        {
            status = $"適用エラー：{ex.Message}";
            Service.Log.Error(ex, "CombatJobSettings apply failed");
        }
    }

    private void SetActiveSettingsSource(string reason)
    {
        activeSettingsName = reason.Replace("手動 / ", string.Empty).Replace("JOB変更 / ", string.Empty);
    }

    private int ApplyWrathSettingsDifferential(CombatProfile current, CombatProfile wanted)
    {
        if (!TryGetWrathObjects(out var configuration, out var rotationConfig, out var dps, out var healer))
            return 0;

        var changed = 0;

        bool SetIfDifferent(object target, string member, object? currentValue, object? wantedValue)
        {
            if (ValuesEquivalent(currentValue, wantedValue))
                return false;
            if (!SetInstanceMember(target, member, wantedValue))
                return false;
            changed++;
            return true;
        }

        SetIfDifferent(rotationConfig, "DPSRotationMode", current.WrathDamageTargetMode, wanted.WrathDamageTargetMode);
        SetIfDifferent(rotationConfig, "HealerRotationMode", current.WrathHealerTargetMode, wanted.WrathHealerTargetMode);

        SetIfDifferent(dps, "DPSAoETargets", current.WrathDpsAoeTargets, wanted.WrathDpsAoeTargets);
        SetIfDifferent(dps, "MaxDistance", current.WrathMaxDistance, wanted.WrathMaxDistance);
        SetIfDifferent(dps, "IgnoreRangeInBoss", current.WrathIgnoreRangeInBoss, wanted.WrathIgnoreRangeInBoss);
        SetIfDifferent(dps, "FATEPriority", current.WrathFatePriority, wanted.WrathFatePriority);
        SetIfDifferent(dps, "QuestPriority", current.WrathQuestPriority, wanted.WrathQuestPriority);
        SetIfDifferent(dps, "PreferNonCombat", current.WrathPreferNonCombat, wanted.WrathPreferNonCombat);
        SetIfDifferent(dps, "OnlyAttackInCombat", current.WrathOnlyAttackInCombat, wanted.WrathOnlyAttackInCombat);
        SetIfDifferent(dps, "DPSAlwaysHardTarget", current.WrathDpsAlwaysHardTarget, wanted.WrathDpsAlwaysHardTarget);

        SetIfDifferent(healer, "SingleTargetHPP", current.WrathSingleTargetHpp, wanted.WrathSingleTargetHpp);
        SetIfDifferent(healer, "SingleTargetRegenHPP", current.WrathSingleTargetRegenHpp, wanted.WrathSingleTargetRegenHpp);
        SetIfDifferent(healer, "SingleTargetExcogHPP", current.WrathSingleTargetExcogHpp, wanted.WrathSingleTargetExcogHpp);
        SetIfDifferent(healer, "AoETargetHPP", current.WrathAoeTargetHpp, wanted.WrathAoeTargetHpp);
        SetIfDifferent(healer, "IncludeShields", current.WrathIncludeShields, wanted.WrathIncludeShields);
        SetIfDifferent(healer, "AoEHealTargetCount", current.WrathAoeHealTargetCount, wanted.WrathAoeHealTargetCount);
        SetIfDifferent(healer, "HealDelay", current.WrathHealDelay, wanted.WrathHealDelay);
        SetIfDifferent(healer, "AutoRez", current.WrathAutoRez, wanted.WrathAutoRez);
        SetIfDifferent(healer, "AutoRezOutOfParty", current.WrathAutoRezOutOfParty, wanted.WrathAutoRezOutOfParty);
        SetIfDifferent(healer, "AutoRezRequireSwift", current.WrathAutoRezRequireSwift, wanted.WrathAutoRezRequireSwift);
        SetIfDifferent(healer, "AutoRezDPSJobs", current.WrathAutoRezDpsJobs, wanted.WrathAutoRezDpsJobs);
        SetIfDifferent(healer, "AutoRezDPSJobsHealersOnly", current.WrathAutoRezDpsJobsHealersOnly, wanted.WrathAutoRezDpsJobsHealersOnly);
        SetIfDifferent(healer, "AutoCleanse", current.WrathAutoCleanse, wanted.WrathAutoCleanse);
        SetIfDifferent(healer, "ManageKardia", current.WrathManageKardia, wanted.WrathManageKardia);
        SetIfDifferent(healer, "KardiaTanksOnly", current.WrathKardiaTanksOnly, wanted.WrathKardiaTanksOnly);
        SetIfDifferent(healer, "PreEmptiveHoT", current.WrathPreEmptiveHot, wanted.WrathPreEmptiveHot);
        SetIfDifferent(healer, "IncludeNPCs", current.WrathIncludeNpcs, wanted.WrathIncludeNpcs);
        SetIfDifferent(healer, "HealerAlwaysHardTarget", current.WrathHealerAlwaysHardTarget, wanted.WrathHealerAlwaysHardTarget);
        SetIfDifferent(healer, "HandleRaidwides", current.WrathHandleRaidwides, wanted.WrathHandleRaidwides);
        SetIfDifferent(healer, "HandleTankbusters", current.WrathHandleTankbusters, wanted.WrathHandleTankbusters);

        if (changed > 0)
            InvokeNoArg(configuration, "Save");

        return changed;
    }

    private static bool ValuesEquivalent(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        if (a is float af && b is float bf) return SameFloat(af, bf);
        if (a is double ad && b is double bd) return Math.Abs(ad - bd) < 0.001;
        return Equals(a, b);
    }

    private void ApplyWrathExtendedSettings(CombatProfile p)
    {
        var current = config.Editor.Clone();
        TryReadWrathCurrentValues(current);
        ApplyWrathSettingsDifferential(current, p);
    }

    private static string RsrModeCommand(string? mode, bool legacyAuto)
    {
        if (IsRsrMode(mode, "Manual")) return "/rotation manual";
        if (IsRsrMode(mode, "Auto")) return "/rotation auto on";
        if (IsRsrMode(mode, "Off")) return "/rotation off";
        return legacyAuto ? "/rotation auto on" : "/rotation off";
    }

    private void Send(string cmd, bool updateStatus = true)
    {
        try
        {
            var ok = Service.CommandManager.ProcessCommand(cmd);
            if (updateStatus)
                status = ok ? $"即時反映：{cmd}" : $"コマンド未検出：{cmd}";
            Service.Log.Information("CombatJobSettings command: {Command} / Result={Result}", cmd, ok);
        }
        catch (Exception ex)
        {
            if (updateStatus) status = $"送信エラー：{cmd}";
            Service.Log.Warning(ex, "CombatJobSettings command failed: {Command}", cmd);
        }
    }

    private static string RoleKey(uint id) => id switch
    {
        19 or 21 or 32 or 37 => "tank",
        24 or 28 or 33 or 40 => "healer",
        20 or 22 or 30 or 34 or 39 or 41 => "melee",
        23 or 31 or 38 => "physranged",
        25 or 27 or 35 or 42 => "caster",
        _ => "other"
    };

    private static string RoleName(string key) => key switch
    {
        "tank" => "タンク",
        "healer" => "ヒーラー",
        "melee" => "近接DPS",
        "physranged" => "遠隔物理DPS",
        "caster" => "魔法DPS",
        _ => "その他"
    };


    private static string FormatMiniActiveSettingsName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "未適用";

        return value;
    }

    private static string JobName(uint id) => id switch
    {
        19 => "ナイト", 20 => "モンク", 21 => "戦士", 22 => "竜騎士",
        23 => "吟遊詩人", 24 => "白魔道士", 25 => "黒魔道士", 27 => "召喚士",
        28 => "学者", 30 => "忍者", 31 => "機工士", 32 => "暗黒騎士",
        33 => "占星術師", 34 => "侍", 35 => "赤魔道士", 37 => "ガンブレイカー",
        38 => "踊り子", 39 => "リーパー", 40 => "賢者", 41 => "ヴァイパー",
        42 => "ピクトマンサー", _ => id == 0 ? "未取得" : $"Job {id}"
    };
}
