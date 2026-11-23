// CrosshairLite v1.4.0 —— 精简准星 + 游戏中隐藏系统鼠标
//
// 目标：
//   1. 游戏进行中隐藏操作系统的箭头光标（避免和准星叠在一起）
//   2. 关掉游戏自带的粗准星，换成 1 像素白点
// 首次运行会把场景里疑似准星的物体打到日志，便于精确点名。
using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace CrosshairLite
{
    [BepInPlugin("com.local.crosshairlite", "Crosshair Lite", "1.4.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> HideSystemCursor;
        internal static ConfigEntry<int> DotSize;
        internal static ConfigEntry<int> CrossThickness;
        internal static ConfigEntry<int> CrossLength;
        internal static ConfigEntry<int> CrossGap;
        internal static ConfigEntry<int> CenterDot;
        internal static ConfigEntry<float> DotAlpha;
        internal static ConfigEntry<KeyboardShortcut> ToggleKey;
        internal static ConfigEntry<string> HideNames;
        internal static ConfigEntry<bool> Diag;
        internal static BepInEx.Logging.ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            Enabled = Config.Bind("General", "Enabled", true, "启用精简准星（F8 切换）");
            ToggleKey = Config.Bind("General", "ToggleKey", new KeyboardShortcut(KeyCode.F8), "开关热键");
            HideSystemCursor = Config.Bind("General", "HideSystemCursor", true, "游戏内隐藏系统鼠标箭头");
            HideNames = Config.Bind("General", "HideObjectNames", "crosshair,gunCrosshair,reticle",
                "要隐藏的原版准星物体名（逗号分隔，模糊匹配）");
            CrossThickness = Config.Bind("Display", "CrossThickness", 3, "准星臂粗（1080p 基准像素）");
            CrossLength    = Config.Bind("Display", "CrossLength", 8, "单臂长度（1080p 基准像素）");
            CrossGap       = Config.Bind("Display", "CrossGap", 5, "中心留空半径（1080p 基准像素）");
            CenterDot      = Config.Bind("Display", "CenterDot", 3, "中心点大小（0 = 不显示）");
            DotSize = Config.Bind("Display", "DotPixels", 3, "（旧参数，已不使用）");
            DotAlpha = Config.Bind("Display", "DotOpacity", 0.75f, "准星不透明度");
            Diag = Config.Bind("Debug", "DumpObjects", true, "首次进游戏时把疑似准星物体写进日志");

            gameObject.AddComponent<Reticle>();
            RegisterInGameOptions();
            Log.LogInfo("Crosshair Lite v1.4.0 已加载（F8 开关）");
        }

        /// <summary>把准星参数注册进游戏内「游戏」页（走 CUCoreLib，反射调用）</summary>
        private void RegisterInGameOptions()
        {
            try
            {
                CuOptions.Init(Logger);
                if (!CuOptions.Available) return;

                CuOptions.Bool("crosshairlite.enabled", "精简准星",
                    "接管枪械准星，画成细十字（F8 亦可切换）。", Enabled.Value,
                    v => { Enabled.Value = v; });

                CuOptions.Bool("crosshairlite.hidecursor", "持枪时隐藏鼠标光标",
                    "仅在持枪且准星可见时隐藏系统箭头，避免遮挡。", HideSystemCursor.Value,
                    v => { HideSystemCursor.Value = v; });

                CuOptions.Float("crosshairlite.thickness", "准星粗细",
                    "四段臂的粗细（1080p 基准像素）。", CrossThickness.Value, 1f, 12f,
                    v => { CrossThickness.Value = Mathf.RoundToInt(v); });

                CuOptions.Float("crosshairlite.length", "准星臂长",
                    "单段臂的长度（1080p 基准像素）。", CrossLength.Value, 1f, 30f,
                    v => { CrossLength.Value = Mathf.RoundToInt(v); });

                CuOptions.Float("crosshairlite.gap", "中心留空",
                    "中心点到四段臂之间的空隙。", CrossGap.Value, 0f, 20f,
                    v => { CrossGap.Value = Mathf.RoundToInt(v); });

                CuOptions.Float("crosshairlite.dot", "中心点大小",
                    "0 表示不显示中心点。", CenterDot.Value, 0f, 12f,
                    v => { CenterDot.Value = Mathf.RoundToInt(v); });

                CuOptions.Float("crosshairlite.opacity", "准星透明度",
                    "越小越不遮挡。", DotAlpha.Value, 0.1f, 1f,
                    v => { DotAlpha.Value = v; });

                Logger.LogInfo("准星设置已注册到「游戏」页");
            }
            catch (Exception e)
            {
                Logger.LogWarning("注册游戏内设置失败: " + e.Message);
            }
        }

        private void Update()
        {
            try
            {
                if (ToggleKey.Value.IsDown())
                {
                    Enabled.Value = !Enabled.Value;
                    Log.LogInfo("准星: " + (Enabled.Value ? "开" : "关"));
                }
            }
            catch { }
        }
    }

    internal class Reticle : MonoBehaviour
    {
        private readonly List<Component> _hidden = new List<Component>();
        private float _scanTimer;
        private bool _dumped;
        private bool _cursorHidden;
        private bool _crosshairActive;
        private RectTransform _rt;
        private readonly List<UnityEngine.UI.Graphic> _graphics = new List<UnityEngine.UI.Graphic>();

        private void Update()
        {
            try
            {
                if (!Plugin.Enabled.Value)
                {
                    RestoreHidden();
                    ShowCursor();
                    return;
                }

                // 每帧强制关闭原版准星的 Graphic，避免它被游戏刷新后露出
                if (_crosshairActive && _graphics.Count > 0)
                {
                    for (int i = 0; i < _graphics.Count; i++)
                    {
                        var g = _graphics[i];
                        if (g != null && g.enabled) g.enabled = false;
                    }
                }

                _scanTimer -= Time.deltaTime;
                if (_scanTimer > 0f)
                {
                    ApplyCursorRule();
                    return;
                }
                _scanTimer = 0.4f;

                ScanAndHide();
                ApplyCursorRule();
            }
            catch { }
        }

        private void ApplyCursorRule()
        {
            // 只有在“手持枪械、游戏准星存在”时才接管光标；其余时间完全不动
            bool want = Plugin.HideSystemCursor.Value && _crosshairActive && Time.timeScale > 0.01f;
            if (want && !_cursorHidden) { Cursor.visible = false; _cursorHidden = true; }
            else if (!want && _cursorHidden) { Cursor.visible = true; _cursorHidden = false; }
        }

        private void ShowCursor()
        {
            if (_cursorHidden) { Cursor.visible = true; _cursorHidden = false; }
        }

        private static bool NameMatches(string objName, string csv)
        {
            if (string.IsNullOrEmpty(objName) || string.IsNullOrEmpty(csv)) return false;
            var low = objName.ToLowerInvariant();
            foreach (var part in csv.Split(','))
            {
                var p = part.Trim().ToLowerInvariant();
                if (p.Length > 0 && low.Contains(p)) return true;
            }
            return false;
        }

        private static Type _ownerType;
        private static FieldInfo _fCross;

        /// <summary>反射定位 gunCrosshair 字段：找到持有它的组件类型后缓存</summary>
        private void ResolveCrosshairField()
        {
            if (_ownerType != null) return;

            var all = FindObjectsOfType<MonoBehaviour>();
            for (int i = 0; i < all.Length; i++)
            {
                var mb = all[i];
                if (mb == null) continue;
                var t = mb.GetType();
                const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var f = t.GetField("gunCrosshair", F);
                if (f != null)
                {
                    _ownerType = t;
                    _fCross = f;
                    Plugin.Log.LogInfo("[DIAG] 准星字段定位成功: " + t.FullName + ".gunCrosshair (" + f.FieldType.Name + ")");
                    return;
                }
            }
        }

        private void ScanAndHide()
        {
            ResolveCrosshairField();

            // 方案 A：反射字段（最可靠）
            if (_ownerType != null && _fCross != null)
            {
                var owners = FindObjectsOfType(_ownerType);
                bool any = false;
                for (int i = 0; i < owners.Length; i++)
                {
                    var o = owners[i];
                    if (o == null) continue;

                    object v;
                    try { v = _fCross.GetValue(o); } catch { continue; }
                    if (v == null) continue;

                    Component comp = v as Component;
                    GameObject go = v as GameObject;
                    if (comp != null) go = comp.gameObject;
                    if (go == null) continue;

                    _rt = comp as RectTransform;
                    if (_rt == null && comp != null) _rt = comp.GetComponent<RectTransform>();

                    // 只有对象真的显示时才接管（避免空手时也藏鼠标）
                    if (!go.activeInHierarchy) continue;
                    any = true;

                    // 缓存所有 Graphic，供每帧强制抑制
                    if (_graphics.Count == 0)
                    {
                        foreach (var g in go.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
                            if (g != null) _graphics.Add(g);
                    }
                    foreach (var g in _graphics)
                    {
                        if (g != null && g.enabled) { g.enabled = false; if (!_hidden.Contains(g)) _hidden.Add(g); }
                    }
                    foreach (var c in go.GetComponents<Component>())
                    {
                        if (c is SpriteRenderer sr && sr.enabled) { sr.enabled = false; _hidden.Add(sr); }
                        else if (c is Renderer rr && rr.enabled) { rr.enabled = false; _hidden.Add(rr); }
                    }
                }

                _crosshairActive = any;

                if (Plugin.Diag.Value && !_dumped && any)
                {
                    _dumped = true;
                    Plugin.Log.LogInfo("[DIAG] 已通过字段接管枪械准星（owner=" + _ownerType.Name + "）");
                }
                if (!any) RestoreHidden();
                return;
            }

            var all = FindObjectsOfType<Transform>();
            var found = new List<string>();

            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i];
                if (t == null) continue;
                if (!NameMatches(t.gameObject.name, Plugin.HideNames.Value)) continue;

                found.Add(t.gameObject.name + " <" + Describe(t.gameObject) + ">");

                foreach (var comp in t.GetComponents<Component>())
                {
                    if (comp is SpriteRenderer sr && sr.enabled) { sr.enabled = false; _hidden.Add(sr); }
                    else if (comp is Renderer r && r.enabled) { r.enabled = false; _hidden.Add(r); }
                }
            }

            _crosshairActive = found.Count > 0;

            if (!_crosshairActive)
            {
                // 目标消失（收枪/切场景）：恢复它的渲染
                RestoreHidden();
            }

            if (Plugin.Diag.Value && !_dumped && found.Count > 0)
            {
                _dumped = true;
                Plugin.Log.LogInfo("[DIAG] 隐藏的准星物体: " + string.Join(" | ", found.ToArray()));
            }
        }

        private static string Describe(GameObject go)
        {
            var names = new List<string>();
            foreach (var c in go.GetComponents<Component>())
                if (c != null) names.Add(c.GetType().Name);
            return string.Join("+", names.ToArray());
        }

        private void RestoreHidden()
        {
            for (int i = 0; i < _hidden.Count; i++)
            {
                var c = _hidden[i];
                if (c == null) continue;
                if (c is UnityEngine.UI.Graphic g) g.enabled = true;
                else if (c is SpriteRenderer sr) sr.enabled = true;
                else if (c is Renderer r) r.enabled = true;
            }
            _hidden.Clear();
        }

        private void OnGUI()
        {
            try
            {
                if (!Plugin.Enabled.Value) return;
                if (!_crosshairActive) return;          // 没拿枪就不画

                float ui = Mathf.Max(1f, Screen.height / 1080f);
                float t  = Mathf.Max(1f, Plugin.CrossThickness.Value * ui);   // 臂粗
                float len = Mathf.Max(1f, Plugin.CrossLength.Value * ui);      // 单臂长
                float gap = Mathf.Max(0f, Plugin.CrossGap.Value * ui);         // 中心留空
                float x, y;
                if (_rt != null)
                {
                    Vector3 sp = _rt.position;
                    x = sp.x;
                    y = Screen.height - sp.y;
                }
                else
                {
                    x = Input.mousePosition.x;
                    y = Screen.height - Input.mousePosition.y;
                }
                float a = Mathf.Clamp01(Plugin.DotAlpha.Value);

                GUI.color = new Color(1f, 1f, 1f, a);

                // 上
                GUI.DrawTexture(new Rect(x - t * 0.5f, y - gap - len, t, len), Texture2D.whiteTexture);
                // 下
                GUI.DrawTexture(new Rect(x - t * 0.5f, y + gap, t, len), Texture2D.whiteTexture);
                // 左
                GUI.DrawTexture(new Rect(x - gap - len, y - t * 0.5f, len, t), Texture2D.whiteTexture);
                // 右
                GUI.DrawTexture(new Rect(x + gap, y - t * 0.5f, len, t), Texture2D.whiteTexture);

                // 中心点
                int cd = Plugin.CenterDot.Value;
                if (cd > 0)
                {
                    float d = cd * ui;
                    GUI.DrawTexture(new Rect(x - d * 0.5f, y - d * 0.5f, d, d), Texture2D.whiteTexture);
                }

                GUI.color = Color.white;
            }
            catch { }
        }

        private void OnDisable() { RestoreHidden(); ShowCursor(); }
    }
}
