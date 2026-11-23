// Health Bar Lite v1.4.2
//
// 规则：
//   - 默认只显示「已受伤」的实体（打过才亮）
//   - AlwaysShowTypes 中列出的类型（敌对建筑/陷阱等）无视受伤状态始终显示
//   - 首次扫描会把场景里实体的具体类型名与组件组合写入日志，便于精调
using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace HealthBarLite
{
    [BepInPlugin("com.local.healthbarlite", "Health Bar Lite", "1.4.2")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> Range;
        internal static ConfigEntry<bool> OnlyDamaged;
        internal static ConfigEntry<bool> ShowNumbers;
        internal static ConfigEntry<float> RefreshSec;
        internal static ConfigEntry<KeyboardShortcut> ToggleKey;
        internal static ConfigEntry<string> Exclude;
        internal static ConfigEntry<float> Opacity;
        internal static ConfigEntry<bool> TrackPlayer;
        internal static ConfigEntry<bool> PlayerBars;
        internal static ConfigEntry<int> HpSource;
        internal static ConfigEntry<string> AlwaysShow;
        internal static BepInEx.Logging.ManualLogSource Log;
        internal static bool _drawnPlayer;

        private void Awake()
        {
            Log = Logger;
            Enabled     = Config.Bind("General", "Enabled", true, "显示血条（H 键切换）");
            Range       = Config.Bind("General", "MaxDistance", 28f, "只显示该距离（格）内");
            OnlyDamaged = Config.Bind("General", "OnlyWhenDamaged", true, "只显示受过伤的实体（打过才亮血条）");
            AlwaysShow  = Config.Bind("General", "AlwaysShowTypes", "Turret,TurretBase,Trap,Mine,Bomb,Hostile",
                                      "无视受伤状态、始终显示血条的类型名（逗号分隔，匹配具体类名或物体名）");
            Exclude     = Config.Bind("General", "ExcludeComponents", "",
                                      "跳过含这些组件的实体（逗号分隔），如 SurvivorNote");
            HpSource    = Config.Bind("Player", "HealthSource", 0, "0=脑部健康（决定生死），1=血容量百分比");
            PlayerBars  = Config.Bind("Player", "ShowPlayerBars", true, "在玩家头顶显示 血量/饥饿/口渴 三条");
            TrackPlayer = Config.Bind("Debug", "TrackPlayerValues", true, "记录玩家数值变化（用于定位饥饿/口渴等字段，找完可关）");
            Opacity     = Config.Bind("Display", "Opacity", 0.5f, "血条不透明度（0.2~1.0，越小越不挡视野）");
            ShowNumbers = Config.Bind("Display", "ShowNumbers", true, "显示 当前/最大 数值");
            RefreshSec  = Config.Bind("Performance", "RefreshSeconds", 0.1f, "扫描间隔（秒）");
            ToggleKey   = Config.Bind("General", "ToggleKey", new KeyboardShortcut(KeyCode.H), "开关热键");

            gameObject.AddComponent<Scanner>();
            RegisterInGameOptions();
            gameObject.AddComponent<Hud>();
            Log.LogInfo("Health Bar Lite v1.4.2 已加载（H 键开关）");
        }

        /// <summary>把设置注册进游戏内「游戏」页</summary>
        private void RegisterInGameOptions()
        {
            try
            {
                CuOptions.Init(Logger);
                if (!CuOptions.Available) return;

                CuOptions.Bool("healthbarlite.enabled", "显示血条",
                    "总开关。关闭后所有血条隐藏（H 键同样可切换）。", Enabled.Value,
                    v => { Enabled.Value = v; });

                CuOptions.Bool("healthbarlite.player", "玩家状态条",
                    "在玩家脚下显示 脑健康/饱食/水合 三条。", PlayerBars.Value,
                    v => { PlayerBars.Value = v; });

                CuOptions.Dropdown("healthbarlite.hpsource", "玩家血量来源",
                    "决定玩家血条读取哪个数值。脑部健康是决定生死的指标。",
                    HpSource.Value, new[] { "brain", "blood" }, new[] { "脑部健康", "血容量" },
                    v => { HpSource.Value = v; Scanner.ResetHpMember(); });

                CuOptions.Float("healthbarlite.opacity", "血条不透明度",
                    "越小越不挡视野。", Opacity.Value, 0.1f, 1f,
                    v => { Opacity.Value = v; });

                CuOptions.Float("healthbarlite.distance", "显示距离",
                    "只显示该距离（格）内的实体血条。", Range.Value, 5f, 60f,
                    v => { Range.Value = v; });

                CuOptions.Float("healthbarlite.refresh", "扫描间隔（秒）",
                    "越小越灵敏，越大越省性能。", RefreshSec.Value, 0.05f, 0.5f,
                    v => { RefreshSec.Value = v; });

                CuOptions.Bool("healthbarlite.damagedonly", "只显示受伤的实体",
                    "开启后建筑/物体要被打过才显示血条。", OnlyDamaged.Value,
                    v => { OnlyDamaged.Value = v; });

                CuOptions.Bool("healthbarlite.numbers", "显示数值",
                    "血条上方显示 当前/最大。", ShowNumbers.Value,
                    v => { ShowNumbers.Value = v; });

                Logger.LogInfo("游戏内设置已注册到「游戏」页");
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
                    Log.LogInfo("血条显示: " + (Enabled.Value ? "开" : "关"));
                }
            }
            catch { }
        }
    }

    internal class Entry
    {
        public Transform tr;
        public float cur;
        public float max;
    }

    internal class Scanner : MonoBehaviour
    {
        internal static readonly List<Entry> Items = new List<Entry>();

        private Type _type;
        private MemberInfo _mCur;
        private MemberInfo _mMax;
        private bool _ready;
        private bool _gaveUp;
        private bool _dumped;
        private float _timer;
        private float _pruneTimer;

        private readonly Dictionary<int, float> _observedMax = new Dictionary<int, float>();
        private Transform _pRoot;
        private Dictionary<string, float> _snap;
        private float _trackTimer;
        private Component _body;
        private Type _bodyType;
        private MemberInfo _mHunger, _mThirst, _mHp;
        internal static float PHunger, PThirst, PHp;
        internal static Transform PRoot;
        internal static SpriteRenderer PSprite;
        private readonly Dictionary<int, bool> _excludeCache = new Dictionary<int, bool>();

        private void Start() { Resolve(); }

        private void Resolve()
        {
            if (_ready || _gaveUp) return;

            Type t = FindType("BuildingEntity");
            if (t == null)
            {
                if (Time.frameCount > 900)
                {
                    Plugin.Log.LogError("找不到类型 BuildingEntity，血条不工作");
                    _gaveUp = true;
                }
                return;
            }

            _type = t;
            _mCur = FindNumberMember(t, "health", "hp", "currenthealth");
            _mMax = FindNumberMember(t, "maxhealth", "healthmax", "maxhp", "hpmax");

            if (_mCur == null)
            {
                Plugin.Log.LogError("BuildingEntity 上找不到 health，血条不工作");
                _gaveUp = true;
                return;
            }
            if (_mMax == null)
                Plugin.Log.LogWarning("没有 maxHealth，改用「首次出现血量」作为上限");

            _ready = true;
            Plugin.Log.LogInfo("血条就绪: " + t.FullName + " | 当前=" + Describe(_mCur) +
                               " | 上限=" + (_mMax == null ? "(降级)" : Describe(_mMax)));
        }

        private static string Describe(MemberInfo m)
        {
            if (m is PropertyInfo p) return "属性 " + p.Name;
            if (m is FieldInfo f) return "字段 " + f.Name + "(" + f.DeclaringType.Name + ")";
            return m == null ? "null" : m.Name;
        }

        private static Type FindType(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch { continue; }
                if (types == null) continue;
                for (int i = 0; i < types.Length; i++)
                {
                    var x = types[i];
                    if (x != null && x.Name == name) return x;
                }
            }
            return null;
        }

        private static MemberInfo FindNumberMember(Type t, params string[] names)
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            for (Type cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                foreach (var n in names)
                {
                    var p = cur.GetProperty(n, F);
                    if (p != null && IsNumber(p.PropertyType)) return p;
                    var f = cur.GetField(n, F);
                    if (f != null && IsNumber(f.FieldType)) return f;
                }
            }
            return null;
        }

        /// <summary>切换血量来源后调用，强制重新解析成员</summary>
        internal static void ResetHpMember()
        {
            var scanner = FindObjectOfType<Scanner>();
            if (scanner != null) scanner._mHp = null;
        }

        /// <summary>调用 CUCoreLib 的 CUCoreUtils.TryGetBody(ref Body)，失败返回 null</summary>
        private static Component TryGetBodyViaCUCore()
        {
            try
            {
                var t = FindType("CUCoreUtils");
                if (t == null) return null;
                var m = t.GetMethod("TryGetBody", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (m == null) return null;
                var args = new object[] { null };
                bool ok = (bool)m.Invoke(null, args);
                if (!ok) return null;
                return args[0] as Component;
            }
            catch { return null; }
        }

        private static MemberInfo Pick(Type t, BindingFlags F, params string[] names)
        {
            foreach (var n in names)
            {
                var f = t.GetField(n, F);
                if (f != null && IsNumber(f.FieldType)) return f;
                var p = t.GetProperty(n, F);
                if (p != null && IsNumber(p.PropertyType) && p.GetIndexParameters().Length == 0) return p;
            }
            return null;
        }

        private static bool IsNumber(Type t)
        {
            return t == typeof(float) || t == typeof(int) || t == typeof(double) || t == typeof(long);
        }

        private static float Read(MemberInfo m, object o)
        {
            try
            {
                object v = null;
                if (m is PropertyInfo p) v = p.GetValue(o);
                else if (m is FieldInfo f) v = f.GetValue(o);
                return v == null ? float.NaN : Convert.ToSingle(v);
            }
            catch { }
            return float.NaN;
        }

        private static bool MatchesList(string csv, string name)
        {
            if (string.IsNullOrEmpty(csv) || string.IsNullOrEmpty(name)) return false;
            foreach (var part in csv.Split(','))
            {
                var p = part.Trim();
                if (p.Length > 0 && string.Equals(p, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private bool IsExcluded(Component c)
        {
            string cfg = Plugin.Exclude.Value;
            if (string.IsNullOrEmpty(cfg)) return false;

            int id = c.GetInstanceID();
            if (_excludeCache.TryGetValue(id, out var v)) return v;

            bool skip = false;
            try
            {
                foreach (var comp in c.gameObject.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    if (cfg.IndexOf(comp.GetType().Name, StringComparison.OrdinalIgnoreCase) >= 0) { skip = true; break; }
                }
            }
            catch { }

            if (_excludeCache.Count > 2000) _excludeCache.Clear();
            _excludeCache[id] = skip;
            return skip;
        }

        private void Update()
        {
            if (!_ready) { Resolve(); return; }

            _timer -= Time.deltaTime;
            if (_timer > 0f) return;
            _timer = Mathf.Max(0.1f, Plugin.RefreshSec.Value);

            TrackPlayerValues();

            Items.Clear();
            if (!Plugin.Enabled.Value) return;

            Camera cam = Camera.main;
            if (cam == null) return;

            UnityEngine.Object[] found;
            try { found = FindObjectsOfType(_type); }
            catch (Exception e)
            {
                Plugin.Log.LogError("FindObjectsOfType 失败: " + e.Message);
                _gaveUp = true;
                return;
            }
            if (found == null || found.Length == 0) return;

            if (!_dumped) { _dumped = true; DumpTypes(found); }

            Vector3 cp = cam.transform.position;
            float r2 = Plugin.Range.Value * Plugin.Range.Value;

            for (int i = 0; i < found.Length; i++)
            {
                var c = found[i] as Component;
                if (c == null) continue;

                Vector3 p = c.transform.position;
                if ((p - cp).sqrMagnitude > r2) continue;
                if (IsExcluded(c)) continue;

                float cur = Read(_mCur, c);
                if (float.IsNaN(cur) || cur < 0f) continue;

                float max;
                if (_mMax != null)
                {
                    max = Read(_mMax, c);
                    if (float.IsNaN(max) || max <= 0f) continue;
                }
                else
                {
                    int id = c.GetInstanceID();
                    if (_observedMax.TryGetValue(id, out var known))
                    {
                        if (cur > known) { known = cur; _observedMax[id] = known; }
                        max = known;
                    }
                    else
                    {
                        max = cur;
                        _observedMax[id] = max;
                    }
                    if (max <= 0f) continue;
                }

                bool damaged = cur < max - 0.01f;
                string typeName = c.GetType().Name;
                bool always = MatchesList(Plugin.AlwaysShow.Value, typeName) ||
                              MatchesList(Plugin.AlwaysShow.Value, c.gameObject.name);

                if (!always && Plugin.OnlyDamaged.Value && !damaged) continue;

                Items.Add(new Entry { tr = c.transform, cur = cur, max = max });
            }

            _pruneTimer += Plugin.RefreshSec.Value;
            if (_pruneTimer > 60f) { _pruneTimer = 0f; if (_observedMax.Count > 800) _observedMax.Clear(); }
        }

        /// <summary>每 2 秒给玩家层级做数值快照，把变化打印出来（用于定位 hunger/thirst/brainHealth 等字段）</summary>
        private void TrackPlayerValues()
        {
            // 快照（重负载）仍然 2 秒一次；数值本身每次调用都刷新
            _trackTimer -= Time.deltaTime;
            bool doSnap = Plugin.TrackPlayer.Value && _trackTimer <= 0f;
            if (doSnap) _trackTimer = 2f;

            // 校验缓存：换场景/重生后旧 Transform 会被销毁
            if (_pRoot != null && !_pRoot) { _pRoot = null; _body = null; _mHunger = _mThirst = _mHp = null; }
            if (_pRoot == null) { _body = null; _mHunger = _mThirst = _mHp = null; }

            if (_pRoot == null)
            {
                try
                {
                    var go = GameObject.FindGameObjectWithTag("Player");
                    if (go != null) _pRoot = go.transform.root;
                }
                catch { }

                // 退回：按类型名找 Body 组件，取离相机最近的那个（即玩家）
                if (_pRoot == null && _bodyType != null)
                {
                    var bodies = FindObjectsOfType(_bodyType);
                    Camera cam0 = Camera.main;
                    Vector3 c0 = cam0 != null ? cam0.transform.position : Vector3.zero;
                    float best = float.MaxValue;
                    foreach (var o in bodies)
                    {
                        var c = o as Component;
                        if (c == null) continue;
                        float d = (c.transform.position - c0).sqrMagnitude;
                        if (d < best) { best = d; _pRoot = c.transform.root; }
                    }
                }
                if (_pRoot == null) return;
            }

            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var now = doSnap ? new Dictionary<string, float>(512) : null;

            if (doSnap)
            try
            {
                foreach (var comp in _pRoot.GetComponentsInChildren<Component>(true))
                {
                    if (comp == null) continue;
                    var ct = comp.GetType();
                    var tn = ct.Name;
                    if (tn == "Transform" || tn == "SpriteRenderer" || tn == "Rigidbody2D" ||
                        tn == "Animator" || tn == "AudioSource" || tn.Contains("Collider2D") ||
                        tn == "Light2D" || tn == "ParticleSystem") continue;

                    foreach (var p in ct.GetProperties(F))
                    {
                        if (!IsNumber(p.PropertyType) || p.GetIndexParameters().Length > 0) continue;
                        try { now[tn + "." + p.Name] = Convert.ToSingle(p.GetValue(comp)); } catch { }
                    }
                    foreach (var f in ct.GetFields(F))
                    {
                        if (!IsNumber(f.FieldType)) continue;
                        try { now[tn + "." + f.Name] = Convert.ToSingle(f.GetValue(comp)); } catch { }
                    }
                    if (now.Count > 900) break;
                }
            }
            catch { now = null; }

            // 解析玩家 Body 组件上的三条数值
            if (_body != null && !_body) { _body = null; _mHunger = _mThirst = _mHp = null; }
            if (_body == null && _bodyType == null) _bodyType = FindType("Body");
            if (_body == null)
            {
                // 首选 CUCoreLib 官方接口（原 mod 用的就是这个）
                _body = TryGetBodyViaCUCore();
                if (_body == null && _pRoot != null)
                {
                    foreach (var comp in _pRoot.GetComponentsInChildren<Component>(true))
                    {
                        if (comp != null && comp.GetType().Name == "Body") { _body = comp; break; }
                    }
                }
                if (_body != null)
                {
                    var bt = _body.GetType();
                    const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                    // 优先脑健康（wiki: 唯一决定生死的指标），其次血量百分比，最后血容量
                    _mHp = Plugin.HpSource.Value == 1
                        ? Pick(bt, BF, "bloodVolumePercentage", "bloodVolume")
                        : Pick(bt, BF, "brainHealth", "brainHealthPercentage", "brainIntegrity", "bloodVolumePercentage");
                    // 饱食度 / 水合度：wiki 里叫 Saturation / Hydration
                    _mHunger = Pick(bt, BF, "saturation", "hunger");
                    _mThirst = Pick(bt, BF, "hydration", "thirst");
                    Plugin.Log.LogInfo("玩家状态条字段: hunger=" + (_mHunger == null ? "缺失" : _mHunger.Name) +
                                       ", thirst=" + (_mThirst == null ? "缺失" : _mThirst.Name) +
                                       ", hp=" + (_mHp == null ? "缺失" : _mHp.Name) +
                                       " | 当前值 hp=" + (_mHp == null ? "-" : Read(_mHp, _body).ToString("F1")) +
                                       " hunger=" + (_mHunger == null ? "-" : Read(_mHunger, _body).ToString("F1")) +
                                       " thirst=" + (_mThirst == null ? "-" : Read(_mThirst, _body).ToString("F1")));
                }
            }
            if (_body != null && _mHunger == null && _mThirst == null && _mHp == null)
            {
                var bt0 = _body.GetType();
                const BindingFlags BF0 = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                _mHp = Plugin.HpSource.Value == 1
                    ? Pick(bt0, BF0, "bloodVolumePercentage", "bloodVolume")
                    : Pick(bt0, BF0, "brainHealth", "brainHealthPercentage", "brainVolumePercentage");
                _mHunger = Pick(bt0, BF0, "hunger", "saturation");
                _mThirst = Pick(bt0, BF0, "thirst", "hydration");
                Plugin.Log.LogInfo("玩家字段(重解析): hp=" + (_mHp == null ? "无" : _mHp.Name) +
                                   " hunger=" + (_mHunger == null ? "无" : _mHunger.Name) +
                                   " thirst=" + (_mThirst == null ? "无" : _mThirst.Name));
            }

            if (_body == null)
            {
                PHp = PHunger = PThirst = float.NaN;
                PRoot = null;
            }
            else
            {
                PRoot = _body.transform;
                // 找角色本体精灵（面积最大的那个），用于精确定位到脚下
                if (PSprite == null || !PSprite)
                {
                    PSprite = null;
                    float bestArea = 0f;
                    foreach (var sr in _pRoot.GetComponentsInChildren<SpriteRenderer>(true))
                    {
                        if (sr == null || !sr.enabled) continue;
                        var b = sr.bounds;
                        float area = b.size.x * b.size.y;
                        if (area > bestArea) { bestArea = area; PSprite = sr; }
                    }
                }
                PHunger = _mHunger == null ? float.NaN : Read(_mHunger, _body);
                PThirst = _mThirst == null ? float.NaN : Read(_mThirst, _body);
                PHp     = _mHp == null ? float.NaN : Read(_mHp, _body);
            }

            if (doSnap && now != null && _snap != null)
            {
                var diffs = new List<string>();
                foreach (var kv in now)
                {
                    float old;
                    if (_snap.TryGetValue(kv.Key, out old) && Mathf.Abs(old - kv.Value) > 0.05f)
                        diffs.Add(kv.Key + " " + old.ToString("F1") + "→" + kv.Value.ToString("F1"));
                }
                if (diffs.Count > 0)
                {
                    var line = "[TRACK] " + string.Join(", ", diffs.ToArray());
                    if (line.Length > 1000) line = line.Substring(0, 1000) + "...";
                    Plugin.Log.LogInfo(line);
                }
            }
            if (doSnap && now != null) _snap = now;
        }

        /// <summary>一次性普查：组件名统计 + 样本物体名 + 玩家对象字段</summary>
        private void DumpTypes(UnityEngine.Object[] found)
        {
            try
            {
                var compCount = new Dictionary<string, int>();
                var names = new List<string>();
                for (int i = 0; i < found.Length; i++)
                {
                    var c = found[i] as Component;
                    if (c == null) continue;
                    if (names.Count < 15) names.Add(c.gameObject.name);
                    foreach (var comp in c.gameObject.GetComponents<Component>())
                    {
                        if (comp == null) continue;
                        var n = comp.GetType().Name;
                        compCount.TryGetValue(n, out var k);
                        compCount[n] = k + 1;
                    }
                }

                var parts = new List<string>();
                foreach (var kv in compCount) parts.Add(kv.Key + "×" + kv.Value);
                parts.Sort();
                var line = "[DIAG] 组件普查: " + string.Join(", ", parts.ToArray());
                if (line.Length > 1800) line = line.Substring(0, 1800) + "...";
                Plugin.Log.LogInfo(line);

                Plugin.Log.LogInfo("[DIAG] 样本物体名: " + string.Join(" | ", names.ToArray()));

                // 高血量实体专项：找出不可选中但血很厚的结构物
                var big = new List<string>();
                for (int i = 0; i < found.Length; i++)
                {
                    var c = found[i] as Component;
                    if (c == null) continue;
                    float hp = Read(_mCur, c);
                    if (float.IsNaN(hp) || hp < 1000f) continue;
                    var cs = new List<string>();
                    foreach (var comp in c.gameObject.GetComponents<Component>())
                        if (comp != null) cs.Add(comp.GetType().Name);
                    big.Add(c.gameObject.name + " [hp=" + hp.ToString("F0") + "] <" + string.Join("+", cs.ToArray()) + ">");
                    if (big.Count >= 6) break;
                }
                if (big.Count > 0)
                    Plugin.Log.LogInfo("[DIAG] 高血量实体: " + string.Join(" || ", big.ToArray()));
                else
                    Plugin.Log.LogInfo("[DIAG] 高血量实体: 无");

                DumpPlayer();
            }
            catch (Exception e) { Plugin.Log.LogWarning("[DIAG] 失败: " + e.Message); }
        }

        /// <summary>找玩家对象，打印其组件上所有数值成员（用于实现玩家状态条）</summary>
        private void DumpPlayer()
        {
            try
            {
                Component best = null;

                // 1) 优先用 Player 标签
                try
                {
                    var go = GameObject.FindGameObjectWithTag("Player");
                    if (go != null) best = go.transform;
                }
                catch { }

                // 2) 退回：类型名以 Player 开头
                if (best == null)
                {
                    var all = FindObjectsOfType<MonoBehaviour>();
                    for (int i = 0; i < all.Length; i++)
                    {
                        var mb = all[i];
                        if (mb == null) continue;
                        var tn = mb.GetType().Name;
                        if (tn.StartsWith("Player", StringComparison.OrdinalIgnoreCase)) { best = mb; break; }
                    }
                }

                if (best == null) { Plugin.Log.LogWarning("[DIAG] 没找到玩家候选组件"); return; }

                var root = best.transform.root;
                Plugin.Log.LogInfo("[DIAG] 玩家候选: " + best.GetType().Name + " @ " + best.gameObject.name +
                                   " | 根对象: " + root.name);

                // 遍历整棵层级，按组件类型各取一个样本打印数值成员
                var seenTypes = new HashSet<string>();
                var comps = root.GetComponentsInChildren<Component>(true);
                int dumped = 0;
                foreach (var comp in comps)
                {
                    if (comp == null) continue;
                    var ct = comp.GetType();
                    if (!seenTypes.Add(ct.Name)) continue;
                    if (dumped++ >= 6) break;

                    var list2 = new List<string>();
                    const BindingFlags FF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                    foreach (var p2 in ct.GetProperties(FF))
                        if (IsNumber(p2.PropertyType) && p2.GetIndexParameters().Length == 0) list2.Add(p2.Name);
                    foreach (var f2 in ct.GetFields(FF))
                        if (IsNumber(f2.FieldType)) list2.Add(f2.Name);
                    var tline = "[DIAG] 层级组件 " + ct.Name + ": " + string.Join(",", list2.ToArray());
                    if (tline.Length > 700) tline = tline.Substring(0, 700) + "...";
                    Plugin.Log.LogInfo(tline);
                }

                const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                for (int i = 0; i < 6 && i < best.gameObject.transform.childCount + 1; i++) { }

                var members = new List<string>();
                foreach (var comp in best.gameObject.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    var ct = comp.GetType();
                    foreach (var p in ct.GetProperties(F))
                    {
                        if (!IsNumber(p.PropertyType) || p.GetIndexParameters().Length > 0) continue;
                        members.Add(p.Name + ":" + p.PropertyType.Name + "@" + ct.Name);
                    }
                    foreach (var f in ct.GetFields(F))
                    {
                        if (!IsNumber(f.FieldType)) continue;
                        members.Add(f.Name + ":" + f.FieldType.Name + "@" + ct.Name);
                    }
                }

                var mm = "[DIAG] 玩家数值成员: " + string.Join(", ", members.ToArray());
                if (mm.Length > 1800) mm = mm.Substring(0, 1800) + "...";
                Plugin.Log.LogInfo(mm);
            }
            catch (Exception e) { Plugin.Log.LogWarning("[DIAG] 玩家普查失败: " + e.Message); }
        }

    }

    internal class Hud : MonoBehaviour
    {
        private GUIStyle _style;

        /// <summary>
        /// normalMax（100）对应「满格宽度」；超出部分用 overCol 向右延伸，最多延伸到 overMax 对应的比例。
        /// 例如水合 125 / 100 → 满格 + 额外 25% 宽度。
        /// </summary>
        private void DrawOverfill(float cx, float cy, float w, float h, float value, float normalMax, float overMax, Color col, Color overCol, float op)
        {
            if (overMax < normalMax) overMax = normalMax;

            float ratio = value / Mathf.Max(1f, normalMax);
            float cap = overMax / Mathf.Max(1f, normalMax);
            ratio = Mathf.Clamp(ratio, 0f, cap);

            // 背景 = 满值(100)的基准宽度
            GUI.color = new Color(0f, 0f, 0f, 0.65f * op);
            GUI.DrawTexture(new Rect(cx - w * 0.5f - 1f, cy - h * 0.5f - 1f, w + 2f, h + 2f), Texture2D.whiteTexture);

            float normalPart = Mathf.Min(ratio, 1f);
            var c1 = col; c1.a = op;
            GUI.color = c1;
            GUI.DrawTexture(new Rect(cx - w * 0.5f, cy - h * 0.5f, w * normalPart, h), Texture2D.whiteTexture);

            if (ratio > 1f)
            {
                var c2 = overCol; c2.a = op;
                GUI.color = c2;
                GUI.DrawTexture(new Rect(cx - w * 0.5f + w, cy - h * 0.5f, w * (ratio - 1f), h), Texture2D.whiteTexture);
            }
        }

        private void DrawStat(float cx, float cy, float w, float h, float pct, Color col, float op)
        {
            pct = Mathf.Clamp01(pct);
            GUI.color = new Color(0f, 0f, 0f, 0.65f * op);
            GUI.DrawTexture(new Rect(cx - w * 0.5f - 1f, cy - h * 0.5f - 1f, w + 2f, h + 2f), Texture2D.whiteTexture);
            var c2 = col; c2.a = op;
            GUI.color = c2;
            GUI.DrawTexture(new Rect(cx - w * 0.5f, cy - h * 0.5f, w * pct, h), Texture2D.whiteTexture);
        }

        private void OnGUI()
        {
            try
            {
                if (!Plugin.Enabled.Value) return;
                if (Event.current == null || Event.current.type != EventType.Repaint) return;

                Camera cam = Camera.main;
                if (cam == null) return;

                if (_style == null)
                {
                    _style = new GUIStyle(GUI.skin.label)
                    {
                        fontSize = 15,
                        alignment = TextAnchor.MiddleCenter,
                        richText = false
                    };
                }

                // 玩家三条：完全照抄原 mod 的定位与布局
                if (Plugin.PlayerBars.Value && Scanner.PRoot != null && !float.IsNaN(Scanner.PHp))
                {
                    float op2 = Mathf.Clamp(Plugin.Opacity.Value, 0.1f, 1f);
                    Vector3 anchor = Scanner.PRoot.position + Vector3.up * -3.2f;
                    Vector3 pp = cam.WorldToScreenPoint(anchor);
                    if (pp.z > 0f)
                    {
                        float ui = Mathf.Max(1f, Screen.height / 1080f);
                        float bw = 90f * ui, bh = 5f * ui, gap = 2f * ui;
                        float bx = pp.x;
                        float top = Screen.height - pp.y;

                        if (!Plugin._drawnPlayer)
                        {
                            Plugin._drawnPlayer = true;
                            Plugin.Log.LogInfo("玩家条已绘制 @屏幕(" + bx.ToString("F0") + "," + top.ToString("F0") + ") hp=" + Scanner.PHp.ToString("F1"));
                        }

                        // 三条自上而下
                        DrawOverfill(bx, top, bw, bh,
                            Scanner.PHp, 100f, 100f,
                            new Color(0.95f, 0.55f, 0.66f), new Color(0.95f, 0.55f, 0.66f), op2);

                        if (!float.IsNaN(Scanner.PHunger))
                            DrawOverfill(bx, top + (bh + gap), bw, bh,
                                Scanner.PHunger, 100f, 125f,
                                new Color(1f, 0.78f, 0.55f), new Color(1f, 0.88f, 0.72f), op2);

                        if (!float.IsNaN(Scanner.PThirst))
                            DrawOverfill(bx, top + (bh + gap) * 2f, bw, bh,
                                Scanner.PThirst, 100f, 250f,
                                new Color(0.55f, 0.75f, 0.95f), new Color(0.72f, 0.85f, 0.98f), op2);
                    }
                }

                var list = Scanner.Items;
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    if (e.tr == null) continue;

                    Vector3 sp = cam.WorldToScreenPoint(e.tr.position + Vector3.up * 0.9f);
                    if (sp.z < 0f) continue;

                    float x = sp.x;
                    float y = Screen.height - sp.y;
                    float ui = Mathf.Max(1f, Screen.height / 1080f);
                    float w = 96f * ui, h = 5f * ui;
                    float pct = Mathf.Clamp01(e.cur / e.max);

                    float op = Mathf.Clamp(Plugin.Opacity.Value, 0.1f, 1f);
                    GUI.color = new Color(0f, 0f, 0f, 0.65f * op);
                    GUI.DrawTexture(new Rect(x - w * 0.5f - 1f, y - h * 0.5f - 1f, w + 2f, h + 2f), Texture2D.whiteTexture);

                    var fill = Color.Lerp(Color.red, new Color(0.2f, 0.9f, 0.3f), pct);
                    fill.a = op;
                    GUI.color = fill;
                    GUI.DrawTexture(new Rect(x - w * 0.5f, y - h * 0.5f, w * pct, h), Texture2D.whiteTexture);

                    if (Plugin.ShowNumbers.Value)
                    {
                        GUI.color = new Color(1f, 1f, 1f, op);
                        GUI.Label(new Rect(x - 70f, y - h * 0.5f - 24f, 140f, 22f),
                                  Mathf.CeilToInt(e.cur) + "/" + Mathf.CeilToInt(e.max), _style);
                    }
                }

                GUI.color = Color.white;
            }
            catch { }
        }
    }
}
