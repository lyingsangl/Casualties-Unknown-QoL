// MiniMapLite v1.1.1 —— 轻量小地图
//
// 相比原 WorldMap：
//   · 只在「玩家跨过一格」或「缩放变化」时重建，最快 5Hz
//   · 颜色查表 O(1)
//   · 战争迷雾只按「探索半径」记录，能看出走过的轨迹
//   · 支持鼠标拖动摆放位置
using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace MiniMapLite
{
    [BepInPlugin("com.local.minimaplite", "MiniMap Lite", "1.1.1")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<KeyboardShortcut> ToggleKey;
        internal static ConfigEntry<int> SizePx;
        internal static ConfigEntry<int> ViewCells;
        internal static ConfigEntry<float> Margin;
        internal static ConfigEntry<bool> FogOfWar;
        internal static ConfigEntry<int> ExploreRadius;
        internal static ConfigEntry<float> Alpha;
        internal static ConfigEntry<bool> DrawPlayer;
        internal static ConfigEntry<float> PosX;
        internal static ConfigEntry<float> PosY;
        internal static BepInEx.Logging.ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            Enabled    = Config.Bind("General", "Enabled", true, "显示小地图（默认 N 键切换）");
            ToggleKey  = Config.Bind("General", "ToggleKey", new KeyboardShortcut(KeyCode.N), "开关热键");
            FogOfWar   = Config.Bind("General", "FogOfWar", true, "只显示探索过的地方");
            ExploreRadius = Config.Bind("General", "ExploreRadius", 9, "每走一步记录的探索半径（格），越小迷雾越明显");

            SizePx     = Config.Bind("Display", "SizePixels", 190, "小地图边长（1080p 基准，自动按分辨率缩放）");
            ViewCells  = Config.Bind("Display", "ViewCells", 55, "地图覆盖的格数（单边）");
            Margin     = Config.Bind("Display", "Margin", 16f, "初始距右上角的边距");
            Alpha      = Config.Bind("Display", "Opacity", 0.85f, "不透明度");
            DrawPlayer = Config.Bind("Display", "ShowPlayerMarker", true, "中心显示玩家点");

            PosX = Config.Bind("Display", "PositionX", -1f, "左上角 X（-1 = 自动贴在右上角）");
            PosY = Config.Bind("Display", "PositionY", -1f, "左上角 Y（-1 = 自动贴在右上角）");

            gameObject.AddComponent<MapRenderer>();
            Log.LogInfo("MiniMap Lite v1.1.1 已加载（N 开关 / 滚轮缩放 / 拖动移动）");
        }

        private void Update()
        {
            try
            {
                if (ToggleKey.Value.IsDown())
                {
                    Enabled.Value = !Enabled.Value;
                    Log.LogInfo("小地图: " + (Enabled.Value ? "开" : "关"));
                }
            }
            catch { }
        }
    }

    internal class MapRenderer : MonoBehaviour
    {
        private Type _wgType;
        private FieldInfo _fWorld, _fWidth, _fHeight;
        private MethodInfo _mToBlock, _mGetBlock;
        private Component _world;

        private Texture2D _tex;
        private Color32[] _pixels;
        private int _texSize = -1, _cellsWidth = -1, _cellPx;

        private Vector2Int _lastPlayerBlock = new Vector2Int(int.MinValue, int.MinValue);
        private float _rebuildTimer;
        private float _zoomMul = 1f;

        private readonly Dictionary<ushort, Color32> _colorCache = new Dictionary<ushort, Color32>();
        private readonly HashSet<long> _explored = new HashSet<long>();

        private static readonly Color32 Unknown = new Color32(10, 10, 14, 255);

        private Vector2 _pos = new Vector2(-1f, -1f);
        private bool _dragging;
        private Vector2 _grabOffset;
        private Rect _rect;

        private void Start() { Resolve(); }

        private void Resolve()
        {
            if (_wgType != null) return;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch { continue; }
                if (types == null) continue;
                foreach (var t in types)
                    if (t != null && t.Name == "WorldGeneration") { _wgType = t; break; }
                if (_wgType != null) break;
            }
            if (_wgType == null) return;

            const BindingFlags S = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            const BindingFlags I = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            _fWorld = _wgType.GetField("world", S);
            _fWidth = _wgType.GetField("width", I);
            _fHeight = _wgType.GetField("height", I);
            _mToBlock = _wgType.GetMethod("WorldToBlockPos", I, null, new[] { typeof(Vector2) }, null);
            _mGetBlock = _wgType.GetMethod("GetBlock", I, null, new[] { typeof(Vector2Int) }, null);

            if (_fWorld == null || _mGetBlock == null || _mToBlock == null)
            {
                Plugin.Log.LogError("WorldGeneration 接口不匹配，小地图不可用");
                enabled = false;
                return;
            }
            Plugin.Log.LogInfo("小地图接口就绪");
        }

        private bool EnsureWorld()
        {
            if (_world != null && _world) return true;
            _world = _fWorld?.GetValue(null) as Component;
            return _world != null;
        }

        private Vector2Int PlayerBlock()
        {
            Vector3 pos;
            try
            {
                var go = GameObject.FindGameObjectWithTag("Player");
                if (go == null) return _lastPlayerBlock;
                pos = go.transform.position;
            }
            catch { return _lastPlayerBlock; }

            return (Vector2Int)_mToBlock.Invoke(_world, new object[] { new Vector2(pos.x, pos.y) });
        }

        private ushort GetBlock(int x, int y)
        {
            try { return (ushort)_mGetBlock.Invoke(_world, new object[] { new Vector2Int(x, y) }); }
            catch { return 0; }
        }

        private Color32 ColorOf(ushort b)
        {
            if (_colorCache.TryGetValue(b, out var c)) return c;
            switch (b)
            {
                case 0:  c = new Color32(30, 30, 40, 255); break;
                case 1:  c = new Color32(110, 110, 110, 255); break;
                case 2:  c = new Color32(140, 120, 90, 255); break;
                case 3:  c = new Color32(100, 100, 130, 255); break;
                case 4:  c = new Color32(95, 85, 75, 255); break;
                case 5:  c = new Color32(160, 160, 160, 255); break;
                case 6:  c = new Color32(190, 190, 210, 255); break;
                case 7:  c = new Color32(210, 230, 255, 150); break;
                case 11: c = new Color32(150, 80, 30, 255); break;
                case 12: c = new Color32(220, 200, 150, 255); break;
                case 16: c = new Color32(110, 75, 40, 255); break;
                case 23: c = new Color32(70, 130, 50, 255); break;
                default: c = new Color32(140, 140, 140, 255); break;
            }
            _colorCache[b] = c;
            return c;
        }

        private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;

        /// <summary>只在玩家跨格/缩放变化时重建，5Hz 上限；迷雾单独按探索半径记录</summary>
        private void Update()
        {
            if (!Plugin.Enabled.Value) return;
            if (_wgType == null) { Resolve(); return; }
            if (!EnsureWorld()) return;

            Vector2Int pb = PlayerBlock();
            if (pb.x == int.MinValue) return;

            _rebuildTimer -= Time.deltaTime;
            bool moved = pb.x != _lastPlayerBlock.x || pb.y != _lastPlayerBlock.y;
            if (!moved && _rebuildTimer > 0f) return;

            _lastPlayerBlock = pb;
            _rebuildTimer = 0.2f;
            Rebuild(pb);
        }

        private void Rebuild(Vector2Int center)
        {
            int cells = Mathf.Clamp(Mathf.RoundToInt(Plugin.ViewCells.Value / _zoomMul), 9, 241);
            int cellPx = Mathf.Max(1, Mathf.RoundToInt(240f / cells));
            int size = cells * cellPx;

            if (_tex == null || _texSize != size || _cellsWidth != cells)
            {
                if (_tex != null) Destroy(_tex);
                _tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                };
                _pixels = new Color32[size * size];
                _texSize = size;
                _cellsWidth = cells;
                _cellPx = cellPx;
            }

            int half = cells / 2;
            bool fog = Plugin.FogOfWar.Value;

            // 逐格记录探索范围（半径内全部点亮，不再跳格，避免出现斑点）
            if (fog)
            {
                int r = Mathf.Clamp(Plugin.ExploreRadius.Value, 1, 40);
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                        _explored.Add(Key(center.x + dx, center.y + dy));
            }

            for (int cy = 0; cy < cells; cy++)
            {
                for (int cx = 0; cx < cells; cx++)
                {
                    int wx = center.x + (cx - half);
                    int wy = center.y + (cy - half);

                    Color32 col = (fog && !_explored.Contains(Key(wx, wy)))
                        ? Unknown
                        : ColorOf(GetBlock(wx, wy));

                    int px0 = cx * cellPx;
                    int py0 = cy * cellPx;
                    for (int j = 0; j < cellPx; j++)
                    {
                        int row = (py0 + j) * size + px0;
                        for (int i = 0; i < cellPx; i++) _pixels[row + i] = col;
                    }
                }
            }

            _tex.SetPixels32(_pixels);
            _tex.Apply(false, false);
        }

        private void OnGUI()
        {
            try
            {
                if (!Plugin.Enabled.Value || _tex == null) return;

                float ui = Mathf.Max(1f, Screen.height / 1080f);
                float side = Plugin.SizePx.Value * ui;
                float margin = Plugin.Margin.Value * ui;

                // 首次或配置为 -1 时贴在右上角
                if (_pos.x < 0f || _pos.y < 0f)
                {
                    float cfgX = Plugin.PosX.Value, cfgY = Plugin.PosY.Value;
                    _pos = (cfgX >= 0f && cfgY >= 0f)
                        ? new Vector2(cfgX * ui, cfgY * ui)
                        : new Vector2(Screen.width - side - margin, margin);
                }

                var rect = new Rect(_pos.x, _pos.y, side, side);
                _rect = rect;

                // 拖动
                var e = Event.current;
                if (e != null)
                {
                    if (e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition))
                    {
                        _dragging = true;
                        _grabOffset = e.mousePosition - _pos;
                        e.Use();
                    }
                    else if (_dragging && e.type == EventType.MouseDrag)
                    {
                        _pos = e.mousePosition - _grabOffset;
                        _pos.x = Mathf.Clamp(_pos.x, 0f, Screen.width - side);
                        _pos.y = Mathf.Clamp(_pos.y, 0f, Screen.height - side);
                        rect = new Rect(_pos.x, _pos.y, side, side);
                        _rect = rect;
                        e.Use();
                    }
                    else if (_dragging && e.type == EventType.MouseUp)
                    {
                        _dragging = false;
                        Plugin.PosX.Value = _pos.x / ui;
                        Plugin.PosY.Value = _pos.y / ui;
                        e.Use();
                    }
                }

                // 底衬
                GUI.color = new Color(0f, 0f, 0f, 0.5f * Plugin.Alpha.Value);
                GUI.DrawTexture(new Rect(rect.x - 2, rect.y - 2, rect.width + 4, rect.height + 4), Texture2D.whiteTexture);

                // 地图本体
                GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(Plugin.Alpha.Value));
                GUI.DrawTexture(rect, _tex);

                // 玩家点
                if (Plugin.DrawPlayer.Value)
                {
                    float cx = rect.x + rect.width * 0.5f;
                    float cy = rect.y + rect.height * 0.5f;
                    float d = Mathf.Max(4f, 5f * ui);
                    GUI.color = new Color(1f, 0.3f, 0.35f, 1f);
                    GUI.DrawTexture(new Rect(cx - d * 0.5f, cy - d * 0.5f, d, d), Texture2D.whiteTexture);
                }

                GUI.color = Color.white;
            }
            catch { }
        }

        /// <summary>滚轮缩放：仅当光标位于小地图内</summary>
        private void LateUpdate()
        {
            try
            {
                if (!Plugin.Enabled.Value) return;
                if (_rect.width <= 0f) return;

                var mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
                if (!_rect.Contains(mouse)) return;

                float scroll = Input.mouseScrollDelta.y;
                if (Mathf.Abs(scroll) < 0.01f) return;

                _zoomMul = Mathf.Clamp(_zoomMul * (scroll > 0f ? 0.85f : 1.18f), 0.4f, 2.5f);
                _lastPlayerBlock = new Vector2Int(int.MinValue, int.MinValue);   // 强制重建
            }
            catch { }
        }
    }
}
