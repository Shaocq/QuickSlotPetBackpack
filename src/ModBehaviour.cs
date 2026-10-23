using System;
using System.Collections;
using System.Reflection;
using Duckov;
using HarmonyLib;
using Saves;
using UnityEngine;

namespace QuickSlotPetBackpack
{
    /// <summary>
    /// 快捷栏可使用宠物背包。
    ///
    /// 原版快捷栏只认主背包里的物品，宠物背包（含容器）里的物品一旦被绑定：
    ///   - 存档时格子序号记为 -1，换地图 / 读档后绑定丢失；
    ///   - 拖进快捷栏时会被强行塞回主背包。
    ///
    /// 本 mod 做的事：
    ///   1. 放宽 ItemShortcut.IsItemValid，允许宠物背包里的非武器物品；
    ///   2. 拖拽绑定时不移动物品，仍留在宠物背包；
    ///   3. 像原版 ItemShortcut_Data 一样把绑定路径写进游戏存档（PetShortcutBindings_Data），
    ///      加载后自动恢复；mod 目录的 JSON 只作兼容兜底；
    ///   4. 使用结束后把物品放回宠物背包原位置，不塞进主背包、不掉落。
    ///
    /// 前置：必须启用「HarmonyLib」（工坊 3589088839 / HarmonyLoadMod）。
    /// </summary>
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        private const string HarmonyId = "com.quickslotpetbackpack";
        private const float RetryInterval = 2f;
        private const float PatchFailureBackoff = 10f;

        /// <summary>环境就绪后继续等待的时间，避开宠物背包异步加载的竞态。</summary>
        private const float ReadyDelay = 1f;

        /// <summary>等待宠物背包环境的最长时间，超时后本关不再尝试恢复。</summary>
        private const float RestoreTimeout = 60f;

        private static bool _patched;
        private static bool _patchAttempted;
        private static string _modDirectory = ".";

        private static bool _needsRestore;
        private static bool _restoreIncomplete;
        private static float _restoreStartTime;
        private static float _readySince = -1f;

        private Coroutine _retryCoroutine;
        private bool _warnedMissingHarmony;
        private bool _subscribed;

        /// <summary>mod 所在目录（数据文件就放在这里）。</summary>
        public static string ModDirectory => _modDirectory;

        /// <summary>本关绑定还没恢复完成时禁止写文件，避免把旧数据覆盖成空。</summary>
        public static bool SaveBlocked => _needsRestore || _restoreIncomplete;

        protected override void OnAfterSetup()
        {
            try
            {
                string directory = info.path;
                if (string.IsNullOrEmpty(directory))
                    directory = Path_ofExecutingAssembly();
                _modDirectory = directory;

                Debug.Log($"[快捷栏可使用宠物背包] 数据目录：{_modDirectory}");
                Diag.Log($"mod 已初始化，数据目录：{_modDirectory}");
            }
            catch (Exception e)
            {
                SafeGuard.Report("初始化 mod 目录失败", e);
            }
        }

        private static string Path_ofExecutingAssembly()
        {
            try
            {
                string location = Assembly.GetExecutingAssembly().Location;
                return string.IsNullOrEmpty(location)
                    ? "."
                    : System.IO.Path.GetDirectoryName(location) ?? ".";
            }
            catch
            {
                return ".";
            }
        }

        // Unity 生命周期消息：ModBehaviour 继承 MonoBehaviour，必须保留为实例方法
#pragma warning disable CA1822
        private void OnEnable()
        {
            try
            {
                Subscribe();
                RequestRestore("mod 启用");
                TryPatch();
            }
            catch (Exception e)
            {
                SafeGuard.Report("mod 启用失败", e);
            }
        }

        private void OnDisable()
        {
            try
            {
                if (_retryCoroutine != null)
                {
                    StopCoroutine(_retryCoroutine);
                    _retryCoroutine = null;
                }

                Unsubscribe();
                _warnedMissingHarmony = false;
                _needsRestore = false;
                _restoreIncomplete = false;
                PetUseTracker.Reset();
                PetSlotTracker.Reset();
                Unpatch();
            }
            catch (Exception e)
            {
                SafeGuard.Report("mod 禁用时清理失败", e);
            }
        }

        private void Update()
        {
            try
            {
                PollRestore();

                // 兜底对账：宠物背包 / 主背包容器物品切换后，原版 HUD 可能
                // 收不到事件而一直显示空白；这里定时把本 mod 接管的槽位同步一次
                PetShortcutUtility.ReconcileShortcutButtons();
            }
            catch (Exception e)
            {
                SafeGuard.Report("恢复宠物背包快捷栏绑定时出错", e);
                _needsRestore = false;
                _restoreIncomplete = true;
            }
        }

        private void Subscribe()
        {
            if (_subscribed)
                return;

            // 逐个订阅：某个事件不可用时不能让后面的订阅也失败
            try
            {
                LevelManager.OnLevelInitialized += OnLevelInitialized;
            }
            catch (Exception e)
            {
                SafeGuard.Report("订阅关卡初始化事件失败", e);
            }

            try
            {
                SavesSystem.OnSetFile += OnSetSaveFile;
            }
            catch (Exception e)
            {
                SafeGuard.Report("订阅切换存档事件失败", e);
            }

            try
            {
                SavesSystem.OnCollectSaveData += OnCollectSaveData;
            }
            catch (Exception e)
            {
                SafeGuard.Report("订阅收集存档事件失败", e);
            }

            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed)
                return;

            // 逐个退订：任何一个失败都不能影响其他事件的清理
            try
            {
                LevelManager.OnLevelInitialized -= OnLevelInitialized;
            }
            catch (Exception e)
            {
                SafeGuard.Report("退订关卡初始化事件失败", e);
            }

            try
            {
                SavesSystem.OnSetFile -= OnSetSaveFile;
            }
            catch (Exception e)
            {
                SafeGuard.Report("退订切换存档事件失败", e);
            }

            try
            {
                SavesSystem.OnCollectSaveData -= OnCollectSaveData;
            }
            catch (Exception e)
            {
                SafeGuard.Report("退订收集存档事件失败", e);
            }

            _subscribed = false;
        }
#pragma warning restore CA1822

        private void OnLevelInitialized()
        {
            try
            {
                RequestRestore("关卡初始化");
            }
            catch (Exception e)
            {
                // 游戏事件回调里绝不能把异常抛出去
                SafeGuard.Report("处理关卡初始化事件失败", e);
            }
        }

        private void OnSetSaveFile()
        {
            try
            {
                PetUseTracker.Reset();
                RequestRestore("切换存档");
            }
            catch (Exception e)
            {
                SafeGuard.Report("处理切换存档事件失败", e);
            }
        }

        /// <summary>
        /// 原版 ItemShortcut.Save 只在 LevelConfig.SaveCharacter 的关卡里执行，
        /// 有些关卡（副本等）不保存角色但仍会保存宠物背包。这里补上那部分，
        /// 避免在这些关卡里设置的绑定丢失。
        /// Save 内部自带「恢复完成前不写」「SavePet 为假不写」的保护。
        /// </summary>
        private void OnCollectSaveData()
        {
            try
            {
                if (!LevelConfig.SaveCharacter)
                    PetShortcutPersistence.Save();
            }
            catch (Exception e)
            {
                SafeGuard.Report("收集存档数据时保存宠物背包快捷栏绑定失败", e);
            }
        }

        /// <summary>标记「本关需要恢复绑定」，由 Update 轮询到宠物背包就绪后执行。</summary>
        private static void RequestRestore(string reason)
        {
            // 状态标记放在 try 外：无论日志/时间接口是否出问题，恢复流程都要被标记上
            _needsRestore = true;
            _restoreIncomplete = false;
            _readySince = -1f;

            // 换关卡 / 换存档后快捷栏是新建的，旧槽位标记全部作废，
            // 由随后的 Apply() 按当前存档重建
            PetSlotTracker.Reset();

            // HUD 按钮也会随场景重建，清掉缓存
            PetShortcutUtility.InvalidateShortcutButtonCache();

            try
            {
                _restoreStartTime = Time.realtimeSinceStartup;
                Debug.Log($"[快捷栏可使用宠物背包] 将在宠物背包就绪后恢复快捷栏绑定（{reason}）");
            }
            catch (Exception e)
            {
                SafeGuard.Report("请求恢复快捷栏绑定时写日志失败", e);
            }
        }

        /// <summary>
        /// 宠物背包是异步加载的：必须等 LevelInited、PetProxy 实例存在且 Inventory 不在 Loading，
        /// 并稳定保持一小段时间后再恢复，否则会把「空背包」当成最终状态。
        /// </summary>
        private static void PollRestore()
        {
            if (!_needsRestore)
                return;

            if (!_patched)
            {
                // 没有补丁时 Set 必然失败，不能当成「已恢复」，否则会清空绑定文件
                return;
            }

            // 本关不参与宠物背包存档（如副本）：保持等待，不算超时
            if (!LevelConfig.SavePet)
            {
                _readySince = -1f;
                return;
            }

            if (!PetShortcutUtility.IsEnvironmentReady())
            {
                _readySince = -1f;

                if (Time.realtimeSinceStartup - _restoreStartTime > RestoreTimeout)
                {
                    _needsRestore = false;
                    _restoreIncomplete = true;

                    SafeGuard.Report(
                        $"等待宠物背包超过 {RestoreTimeout:0} 秒，本关跳过快捷栏绑定恢复；旧的绑定文件会保留");
                }

                return;
            }

            if (_readySince < 0f)
            {
                _readySince = Time.realtimeSinceStartup;
                return;
            }

            if (Time.realtimeSinceStartup - _readySince < ReadyDelay)
                return;

            int applied = PetShortcutPersistence.Apply();
            _needsRestore = false;
            _restoreIncomplete = false;

            Diag.Log(
                $"恢复绑定完成：存档中 {PetShortcutPersistence.SavedCount} 条，成功恢复 {applied} 条（补丁已应用={_patched}）");

            if (applied > 0)
            {
                Debug.Log($"[快捷栏可使用宠物背包] 已恢复 {applied} 个扩展快捷栏绑定（宠物背包 / 主背包容器）");
            }
            else if (PetShortcutPersistence.SavedCount > 0)
            {
                Debug.LogWarning(
                    $"[快捷栏可使用宠物背包] 存档中有 {PetShortcutPersistence.SavedCount} 个绑定，但物品已不在原位置，未能恢复");
            }
        }

        /// <summary>0Harmony 就绪就立刻打补丁；没就绪就开重试协程。</summary>
        private void TryPatch()
        {
            if (_patched)
                return;

            if (HarmonyLoad.Load0Harmony() != null)
            {
                ApplyPatches();
                if (_patched)
                    return;
            }

            if (_retryCoroutine == null)
                _retryCoroutine = StartCoroutine(WaitForHarmony());
        }

        /// <summary>
        /// 定时检查前置是否已经把 0Harmony 载入进程。
        /// 本方法刻意不引用 HarmonyLib 的类型：找到之后再调用 ApplyPatches，
        /// 避免程序集尚未存在时 JIT 解析失败。
        /// </summary>
        private IEnumerator WaitForHarmony()
        {
            float wait = RetryInterval;

            while (!_patched)
            {
                yield return new WaitForSecondsRealtime(wait);
                wait = RetryInterval;

                try
                {
                    if (HarmonyLoad.Load0Harmony() != null)
                    {
                        ApplyPatches();
                        if (_patched)
                            break;

                        // 补丁失败时 ApplyPatches 已经报告过原因，继续重试即可，
                        // 拉长间隔避免日志刷屏
                        wait = PatchFailureBackoff;
                    }
                    else if (!_warnedMissingHarmony)
                    {
                        _warnedMissingHarmony = true;
                        SafeGuard.Report(
                            "尚未找到 0Harmony，请启用前置「HarmonyLib」（工坊 3589088839）并置于本 mod 之前");
                    }
                }
                catch (Exception e)
                {
                    SafeGuard.Report("等待 Harmony 前置失败", e);
                }
            }

            _retryCoroutine = null;
        }

        /// <summary>引用 HarmonyLib 的代码集中在这里，必须在 0Harmony 加载后调用。</summary>
        private static void ApplyPatches()
        {
            try
            {
                // 从这一刻起可能已经动过补丁，禁用时必须尝试清理
                _patchAttempted = true;

                Harmony harmony = new Harmony(HarmonyId);

                // 先清掉上一次可能残留或只应用了一半的补丁，避免重复叠加
                harmony.UnpatchAll(HarmonyId);
                harmony.PatchAll(typeof(ModBehaviour).Assembly);

                _patched = true;
                Debug.Log($"[快捷栏可使用宠物背包] Harmony 补丁已应用（Harmony {HarmonyLoad.HarmonyVersion}）");
                Diag.Log($"Harmony 补丁已应用（Harmony {HarmonyLoad.HarmonyVersion}）");
            }
            catch (Exception e)
            {
                SafeGuard.Report("应用 Harmony 补丁失败", e);
            }
        }

        private static void Unpatch()
        {
            if (!_patchAttempted)
                return;

            try
            {
                new Harmony(HarmonyId).UnpatchAll(HarmonyId);
                _patchAttempted = false;
            }
            catch (Exception e)
            {
                SafeGuard.Report("移除 Harmony 补丁失败", e);
            }
            finally
            {
                _patched = false;
            }
        }
    }
}
