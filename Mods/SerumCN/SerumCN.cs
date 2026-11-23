// SerumCN —— 把 ReGrowthSerums（再生血清）的物品名与描述替换成中文
//
// 该 mod 把英文文案硬编码在 DLL 里，没有语言文件，所以只能在运行时改注册表：
//   CUCoreLib.ItemRegistry.RegisteredItems  (Dictionary<string, CustomItemInfo>)
// 注册完成后把 fullName / description 覆盖成中文即可，不改原 DLL。
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using UnityEngine;

namespace SerumCN
{
    [BepInPlugin("com.local.serumcn", "Serum CN", "1.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        private static BepInEx.Logging.ManualLogSource _log;
        private static readonly HashSet<string> Applied = new HashSet<string>();

        // 物品 ID → (名称, 描述)
        private static readonly Dictionary<string, string[]> Table = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["dragoon_103.eyegrowthserum"] = new[]
            {
                "眼部再生血清",
                "涂抹于缺失的眼窝，刺激脑组织再生出眼部组织。\n<color=orange>警告：在神经元再生症发作期间请勿使用。</color>"
            },
            ["dragoon_103.limbgrowthserum"] = new[]
            {
                "肢体再生血清",
                "一种实验性药物，可将现有组织迅速转化为新的干细胞生长。\n涂抹于相邻肢体缺失的身体部位即可再生。"
            },
            ["dragoon_103.jawgrowthserum"] = new[]
            {
                "面容重塑血清",
                "涂抹于缺失的面部，注入干细胞组织以再生下颌骨与组织。\n<color=orange>警告：再生过程中伴随剧痛。</color>"
            },
        };

        private void Awake()
        {
            _log = Logger;
            LoadExternal();
            StartCoroutine(ApplyLoop());
            Logger.LogInfo("Serum CN v1.1.0 已加载，将在注册完成后替换再生血清文案");
        }

        private IEnumerator ApplyLoop()
        {
            // 物品可能在插件加载后若干帧才注册完成，轮询一段时间
            for (int i = 0; i < 600; i++)
            {
                try { Apply(); } catch { }
                yield return new WaitForSeconds(1f);
            }
        }

        /// <summary>读取插件目录下的 translations.txt（每行：物品ID | 名称 | 描述）</summary>
        private void LoadExternal()
        {
            try
            {
                string dir = Path.GetDirectoryName(Info.Location);
                string file = Path.Combine(dir ?? ".", "translations.txt");
                if (!File.Exists(file)) return;

                int n = 0;
                foreach (var raw in File.ReadAllLines(file))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var parts = line.Split('|');
                    if (parts.Length < 2) continue;

                    var id = parts[0].Trim();
                    var name = parts[1].Trim();
                    var desc = parts.Length > 2 ? parts[2].Trim().Replace("\\n", "\n") : null;
                    if (id.Length == 0 || name.Length == 0) continue;

                    string oldDesc = "";
                    if (Table.TryGetValue(id, out var old)) oldDesc = old[1];
                    Table[id] = new[] { name, desc ?? oldDesc };
                    n++;
                }
                if (n > 0) _log.LogInfo("已从 translations.txt 载入 " + n + " 条翻译");
            }
            catch (Exception e) { _log.LogWarning("读取 translations.txt 失败: " + e.Message); }
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
                foreach (var t in types)
                    if (t != null && t.Name == name) return t;
            }
            return null;
        }

        private static void Apply()
        {
            if (Applied.Count >= Table.Count) return;

            var t = FindType("ItemRegistry");
            if (t == null) return;

            const BindingFlags S = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            var f = t.GetField("RegisteredItems", S);
            var dict = f?.GetValue(null) as IDictionary;
            if (dict == null) return;

            foreach (DictionaryEntry kv in dict)
            {
                var id = kv.Key as string;
                if (id == null || kv.Value == null) continue;
                if (!Table.TryGetValue(id, out var text)) continue;

                bool a = SetString(kv.Value, "fullName", text[0]);
                bool b = SetString(kv.Value, "description", text[1]);
                if (a && b && Applied.Add(id))
                    _log.LogInfo("已汉化: " + id + " → " + text[0]);
            }
        }

        private static bool SetString(object obj, string member, string value)
        {
            for (Type cur = obj.GetType(); cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

                var f = cur.GetField(member, F);
                if (f != null && f.FieldType == typeof(string)) { f.SetValue(obj, value); return true; }

                var p = cur.GetProperty(member, F);
                if (p != null && p.PropertyType == typeof(string) && p.CanWrite) { p.SetValue(obj, value); return true; }
            }
            return false;
        }
    }
}
