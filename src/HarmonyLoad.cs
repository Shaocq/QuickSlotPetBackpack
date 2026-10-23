using System;
using System.Reflection;
using UnityEngine;

namespace QuickSlotPetBackpack
{
    /// <summary>
    /// 获取前置「HarmonyLib」（工坊 3589088839 / HarmonyLoadMod）提供的 0Harmony。
    ///
    /// 本 mod 不内嵌 0Harmony：前置启用后会把 0Harmony.dll 载入进程，
    /// 本 mod 以同名程序集引用绑定到它。前置未启用 / 尚未加载时返回 null，由 ModBehaviour 定时重试。
    /// </summary>
    internal static class HarmonyLoad
    {
        private static Assembly _harmonyAssembly;

        /// <summary>已找到的 Harmony 版本（未找到时为"未加载"），日志诊断用。</summary>
        public static string HarmonyVersion { get; private set; } = "未加载";

        /// <summary>返回前置已加载的 0Harmony；尚未加载则返回 null（不抛异常）。</summary>
        public static Assembly Load0Harmony()
        {
            if (_harmonyAssembly != null)
                return _harmonyAssembly;

            try
            {
                _harmonyAssembly = FindLoadedHarmony();
                if (_harmonyAssembly != null)
                {
                    HarmonyVersion = _harmonyAssembly.GetName().Version?.ToString() ?? "未知";
                    Debug.Log($"[快捷栏可使用宠物背包] 已找到前置提供的 0Harmony，版本 {HarmonyVersion}");
                }
            }
            catch (Exception e)
            {
                SafeGuard.Report("查找前置 Harmony 失败", e);
                _harmonyAssembly = null;
            }

            return _harmonyAssembly;
        }

        /// <summary>在 AppDomain 里找已经加载的 0Harmony（只接受 2.x）。</summary>
        private static Assembly FindLoadedHarmony()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (assembly.GetName().Name != "0Harmony")
                        continue;

                    Version version = assembly.GetName().Version;
                    if (version != null && version.Major >= 2)
                        return assembly;
                }
                catch
                {
                    // 个别程序集的 GetName 可能抛异常，跳过即可
                }
            }

            return null;
        }
    }
}
