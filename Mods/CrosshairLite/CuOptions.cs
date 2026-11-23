// 把设置项注册进游戏内「游戏」页（走 CUCoreLib 的 ModOptionsRegistry）
// 全部走反射，CUCoreLib 缺失或 API 变化时静默降级，不影响血条功能本身。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace CrosshairLite
{
    internal static class CuOptions
    {
        private static Type _defType;
        private static Type _registryType;
        private static Type _categoryType;
        private static Type _choiceType;
        private static bool _resolved;
        private static ManualLogSource _log;

        internal static bool Available { get; private set; }

        internal static void Init(ManualLogSource log)
        {
            _log = log;
            try
            {
                _defType = FindType("ModOptionDefinition");
                _registryType = FindType("ModOptionsRegistry");
                _categoryType = FindType("SettingCategory");
                _choiceType = FindType("ModDropdownChoice");

                if (_defType == null || _registryType == null || _categoryType == null)
                {
                    log.LogWarning("未找到 CUCoreLib 的设置接口，设置只能改配置文件");
                    return;
                }
                _resolved = true;
                Available = true;
            }
            catch (Exception e)
            {
                log.LogWarning("初始化游戏内设置失败: " + e.Message);
            }
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
                {
                    if (t != null && t.Name == name) return t;
                }
            }
            return null;
        }

        private static object Category => Enum.ToObject(_categoryType, 2);   // 2 = 游戏(Gameplay)

        private static MethodInfo FindFactory(string name, int paramCount)
        {
            foreach (var m in _defType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != name) continue;
                var ps = m.GetParameters();
                if (ps.Length != paramCount) continue;
                if (ps.Length > 3 && ps[3].ParameterType == _categoryType) return m;
            }
            return null;
        }

        private static bool Register(object option)
        {
            if (option == null) return false;
            var reg = _registryType.GetMethod("Register", BindingFlags.Public | BindingFlags.Static, null,
                                              new[] { _defType }, null);
            if (reg == null) return false;
            var ok = reg.Invoke(null, new[] { option });
            return ok is bool b && b;
        }

        internal static bool Bool(string id, string label, string desc, bool def, Action<bool> apply)
        {
            if (!_resolved) return false;
            try
            {
                var m = FindFactory("Bool", 6);
                if (m == null) return false;
                var opt = m.Invoke(null, new object[] { id, label, desc, Category, def, apply });
                return Register(opt);
            }
            catch (Exception e) { _log.LogWarning("注册开关项失败 " + id + ": " + e.Message); return false; }
        }

        internal static bool Float(string id, string label, string desc, float def, float min, float max, Action<float> apply)
        {
            if (!_resolved) return false;
            try
            {
                var m = FindFactory("Float", 9);
                if (m == null) return false;
                var opt = m.Invoke(null, new object[] { id, label, desc, Category, def, min, max, apply, null });
                return Register(opt);
            }
            catch (Exception e) { _log.LogWarning("注册数值项失败 " + id + ": " + e.Message); return false; }
        }

        internal static bool Dropdown(string id, string label, string desc, int defIndex, string[] values, string[] labels, Action<int> apply)
        {
            if (!_resolved || _choiceType == null) return false;
            try
            {
                var m = FindFactory("Dropdown", 7);
                if (m == null) return false;

                var arr = Array.CreateInstance(_choiceType, values.Length);
                var ctor = _choiceType.GetConstructor(new[] { typeof(string), typeof(string) });
                for (int i = 0; i < values.Length; i++)
                    arr.SetValue(ctor.Invoke(new object[] { values[i], labels[i] }), i);

                var opt = m.Invoke(null, new object[] { id, label, desc, Category, defIndex, arr, apply });
                return Register(opt);
            }
            catch (Exception e) { _log.LogWarning("注册下拉项失败 " + id + ": " + e.Message); return false; }
        }
    }
}
