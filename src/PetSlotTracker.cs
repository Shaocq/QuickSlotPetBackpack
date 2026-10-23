using System;
using System.Collections.Generic;
using ItemStatsSystem;
using UnityEngine;

namespace QuickSlotPetBackpack
{
    /// <summary>
    /// 记录「哪些快捷栏槽位由本 mod 绑定到了扩展物品」（宠物背包 / 主背包容器内），
    /// 以及绑定物品的实例、TypeID 和它原来所在的背包位置。
    ///
    /// 用途：
    ///   1. 绑定实例**被销毁**后，按 TypeID 找回替代品补位（对齐原版：不看耐久，耐久为 0
    ///      但在 <c>CA_UseItem.OnFinish</c> 销毁前仍然保留）；
    ///   2. 只要**原来那件物品还活着**（哪怕正在使用、暂时脱离背包），就绝不换绑；
    ///   3. 使用记录丢失时（例如别的 mod 改写了 UseItem），用这里记住的原位置把物品放回去；
    ///   4. 绑定 / 解绑 / 用完回位后主动刷新快捷栏 UI。
    ///
    /// 本 mod 只接管宠物背包 / 主背包容器里的绑定；原版主背包顶层槽位的绑定
    /// 仍然由原版维护，但在原版找不到同型号物品时，本 mod 会追加搜索
    /// 「主背包容器 → 宠物背包格子 → 宠物背包容器」（见 <c>ItemShortcutGetLocalPatch</c>）。
    /// </summary>
    internal static class PetSlotTracker
    {
        /// <summary>找不到替代品后，同一槽位在这个时间内不重复扫描背包。</summary>
        private const float SearchCooldown = 0.5f;

        /// <summary>
        /// 物品脱离背包后，在这段时间内仍视为「正在使用 / 刚使用完」。
        /// 物品一旦被丢弃或放进其它容器，超过这段时间就不再保留绑定。
        /// 使用中的物品另有 <see cref="IsInUse"/> 的强信号，不依赖这个计时；
        /// 这里只是给「拿在手上但 Agent 尚未建立」等瞬态留足余量。
        /// </summary>
        private const float DetachedGraceSeconds = 120f;

        private sealed class Binding
        {
            /// <summary>绑定物品的 TypeID（用完后的重绑依据）。</summary>
            public int TypeID;

            /// <summary>绑定时的物品实例。只要它还没被销毁，就优先使用它。</summary>
            public Item Item;

            /// <summary>物品原来所在的背包（宠物背包 / 宠物背包容器 / 主背包容器）。</summary>
            public Inventory Origin;

            /// <summary>物品原来所在背包的格子序号。</summary>
            public int OriginIndex;

            /// <summary>物品原来的绑定路径（宠物背包 "2/0"、主背包容器 "main:3/1"）。</summary>
            public string Path;

            /// <summary>最后一次确认物品仍在原背包里的时间。</summary>
            public float LastValidTime;
        }

        /// <summary>槽位 → 绑定信息。</summary>
        private static readonly Dictionary<int, Binding> Bindings = new Dictionary<int, Binding>();

        /// <summary>槽位 → 上次扫描背包的时间。</summary>
        private static readonly Dictionary<int, float> LastSearchTime = new Dictionary<int, float>();

        private static float Now
        {
            get
            {
                try
                {
                    return Time.realtimeSinceStartup;
                }
                catch
                {
                    // 时间接口异常时返回 0：退化成「只认物品实例、不看时间」
                    return 0f;
                }
            }
        }

        /// <summary>把槽位标记为扩展绑定，并记住物品实例、TypeID 和原位置。</summary>
        public static void Remember(int index, Item item)
        {
            try
            {
                if (index < 0 || !IsAlive(item))
                    return;

                int typeID = item.TypeID;
                if (typeID < 0)
                    return;

                Binding binding;
                if (!Bindings.TryGetValue(index, out binding))
                {
                    binding = new Binding();
                    Bindings[index] = binding;
                }

                binding.TypeID = typeID;
                binding.Item = item;
                binding.LastValidTime = Now;

                // 物品已经脱离背包（使用中又被重新 Set 一次）时不要覆盖原位置记录，
                // 否则用完就找不到该放回哪个格子了
                if (item.InInventory != null)
                {
                    binding.Origin = item.InInventory;
                    binding.OriginIndex = binding.Origin.GetIndex(item);

                    if (PetShortcutUtility.TryGetBindingPath(item, out string path))
                        binding.Path = path;
                    else
                        binding.Path = null;
                }
            }
            catch (Exception e)
            {
                SafeGuard.Report("记录扩展快捷栏槽位失败", e);
            }
        }

        /// <summary>槽位不再由本 mod 接管（换成了主背包顶层物品，或切关重建）。</summary>
        public static void Forget(int index)
        {
            try
            {
                Bindings.Remove(index);
                LastSearchTime.Remove(index);
            }
            catch (Exception e)
            {
                SafeGuard.Report("清理扩展快捷栏槽位失败", e);
            }
        }

        /// <summary>该槽位是否由本 mod 接管（宠物背包 / 主背包容器内的绑定）。</summary>
        public static bool IsTracked(int index)
        {
            try
            {
                return Bindings.ContainsKey(index);
            }
            catch
            {
                return false;
            }
        }

        public static bool TryGetType(int index, out int typeID)
        {
            try
            {
                if (Bindings.TryGetValue(index, out Binding binding)
                    && binding != null
                    && binding.TypeID >= 0)
                {
                    typeID = binding.TypeID;
                    return true;
                }
            }
            catch (Exception e)
            {
                SafeGuard.Report("读取扩展快捷栏槽位失败", e);
            }

            typeID = -1;
            return false;
        }

        /// <summary>
        /// 取出槽位绑定物品**原来所在的位置类型**（主背包顶层 / 主背包容器 /
        /// 宠物背包格子 / 宠物背包容器），用于「本身位置优先」的补位搜索。
        /// 绑定记录里没有路径信息时返回 false。
        /// </summary>
        public static bool TryGetLocationKind(int index, out PetShortcutUtility.ItemLocation kind)
        {
            kind = PetShortcutUtility.ItemLocation.Unknown;

            try
            {
                if (!Bindings.TryGetValue(index, out Binding binding) || binding == null)
                    return false;

                string path = binding.Path;
                if (!string.IsNullOrEmpty(path))
                {
                    if (path.StartsWith(PetShortcutUtility.MainPathPrefix, StringComparison.Ordinal))
                        kind = PetShortcutUtility.ItemLocation.MainContainer;
                    else if (path.IndexOf('/') >= 0)
                        kind = PetShortcutUtility.ItemLocation.PetContainer;
                    else
                        kind = PetShortcutUtility.ItemLocation.PetTopLevel;

                    return true;
                }

                // 没有路径时退而用背包实例判断顶层格子
                Inventory origin = binding.Origin;
                if (origin == null)
                    return false;

                if (origin == PetShortcutUtility.GetPetInventory())
                {
                    kind = PetShortcutUtility.ItemLocation.PetTopLevel;
                    return true;
                }

                if (origin == PetShortcutUtility.GetMainInventory())
                {
                    kind = PetShortcutUtility.ItemLocation.MainTopLevel;
                    return true;
                }

                // 其它背包实例只可能是某个容器的内部，但无法判断属于哪一侧：
                // 交给调用方按固定顺序搜索
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>取出槽位记录里绑定的物品实例；物品已被销毁时返回 null。</summary>
        public static bool TryGetItem(int index, out Item item)
        {
            item = null;

            try
            {
                if (!Bindings.TryGetValue(index, out Binding binding) || binding == null)
                    return false;

                item = binding.Item;
                return IsAlive(item);
            }
            catch (Exception e)
            {
                SafeGuard.Report("读取扩展快捷栏绑定物品失败", e);
                return false;
            }
        }

        /// <summary>
        /// 物品是否是某个扩展槽位里记录的那件实例（会被 Unity 的销毁语义影响：
        /// 已销毁的物品返回 false）。
        /// </summary>
        public static bool IsRememberedItem(Item item)
        {
            try
            {
                return IsAlive(item) && Find(item) != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 物品正处于「使用中」的强信号：
        ///   - 原版 UseItem 把它 Detach 后挂到角色身上；
        ///   - 使用动作已经建立了手持 Agent；
        ///   - 本 mod 的使用跟踪记录里还有它。
        /// 这几种状态下快捷栏必须保留它，不能清空、更不能换绑。
        /// </summary>
        public static bool IsInUse(Item item)
        {
            try
            {
                if (!IsAlive(item))
                    return false;

                if (PetShortcutUtility.IsHeldByMainCharacter(item))
                    return true;

                // ChangeHoldItem 会为使用中的物品建立手持 Agent
                if (item.AgentUtilities != null && item.AgentUtilities.ActiveAgent != null)
                    return true;

                return PetUseTracker.IsTracking(item);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 物品已经脱离背包，但还处在「使用中 / 刚用完」的窗口里。
        /// 这种状态下快捷栏必须继续保留它，不能清空、更不能换绑到别的同型号物品。
        /// </summary>
        public static bool IsDetachedWithinGrace(Item item)
        {
            try
            {
                if (!IsAlive(item))
                    return false;

                // 只有本 mod 记录过的绑定物品才享有豁免，原版武器等一律不受影响
                Binding binding = Find(item);
                if (binding == null)
                    return false;

                // 注意：这里**不看耐久**。对齐原版——耐久为 0 的物品在被
                // CA_UseItem.OnFinish 销毁之前仍然保留在槽位里，不能提前换绑，
                // 否则会出现「UI 显示 A、按键实际用了 B」的错位。

                // 使用中的强信号：不依赖计时，避免长时间使用或计时刷新失败导致误换绑
                if (IsInUse(item))
                    return true;

                // 还在某个背包里（宠物背包 / 主背包容器由 IsExtraBindableItem 负责）
                if (item.InInventory != null || item.ParentObject != null)
                    return false;

                // 没有任何未结束的使用记录：物品是被丢到地上 / 移出背包的，
                // 不能借用「脱离宽限期」继续显示。原版对这种物品会立即清空槽位，
                // 本 mod 必须保持一致，否则快捷栏会长时间显示一件已不在背包里的物品。
                if (!PetUseTracker.HasPending(item))
                    return false;

                return Now - binding.LastValidTime <= DetachedGraceSeconds;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 物品仍在原背包里时刷新记录（位置 / 路径 / 最近有效时间）。
        /// 返回 true 表示物品位置发生了变化（在四个位置之间移动了），
        /// 调用方应刷新一次快捷栏 UI。
        /// </summary>
        public static bool Observe(int index, Item item)
        {
            try
            {
                if (!Bindings.TryGetValue(index, out Binding binding) || binding == null)
                    return false;

                if (!IsAlive(item) || item.InInventory == null)
                    return false;

                string newPath = PetShortcutUtility.TryGetBindingPath(item, out string path) ? path : null;
                bool moved = binding.Origin != item.InInventory || binding.Path != newPath;

                binding.TypeID = item.TypeID;
                binding.Item = item;
                binding.Origin = item.InInventory;
                binding.OriginIndex = binding.Origin.GetIndex(item);
                binding.LastValidTime = Now;
                binding.Path = newPath;

                return moved;
            }
            catch (Exception e)
            {
                SafeGuard.Report("刷新扩展快捷栏绑定位置失败", e);
                return false;
            }
        }

        /// <summary>
        /// 绑定物品当前所在的位置是否已经和记录不一致（只检查、不更新记录）。
        /// 用于定时对账时发现「同类型容器之间」的移动：
        /// 两个同类容器互换时物品实例没变、TypeID 没变，UI 侧完全收不到变化，
        /// 只能靠位置签名（背包实例 + 路径）判断。
        /// </summary>
        public static bool HasLocationChanged(int index)
        {
            try
            {
                if (!Bindings.TryGetValue(index, out Binding binding) || binding == null)
                    return false;

                Item item = binding.Item;
                if (!IsAlive(item) || item.InInventory == null)
                    return false;

                string newPath = PetShortcutUtility.TryGetBindingPath(item, out string path) ? path : null;

                // 背包实例相同、路径也相同才算没动：容器只有同型号也照样能区分，
                // 因为路径是从根背包一路下来的格子序号
                return binding.Origin != item.InInventory || binding.Path != newPath;
            }
            catch
            {
                // 高频路径，出错只当作没变化
                return false;
            }
        }

        /// <summary>
        /// 开始使用物品前调用：物品还在背包里，刷新「最近有效时间」与路径。
        /// 即使后续的使用跟踪没有生效，也能保证使用期间不会被当成失效。
        /// </summary>
        public static void Touch(Item item)
        {
            try
            {
                if (!IsAlive(item) || item.InInventory == null)
                    return;

                Binding binding = Find(item);
                if (binding == null)
                    return;

                binding.Origin = item.InInventory;
                binding.OriginIndex = binding.Origin.GetIndex(item);
                binding.LastValidTime = Now;

                if (PetShortcutUtility.TryGetBindingPath(item, out string path))
                    binding.Path = path;
            }
            catch
            {
                // 高频路径，出错只当作刷新失败
            }
        }

        /// <summary>
        /// 取出物品绑定的原背包位置，用于「使用记录丢失时把物品放回去」。
        /// </summary>
        public static bool TryGetOrigin(Item item, out Inventory inventory, out int index)
        {
            inventory = null;
            index = -1;

            try
            {
                Binding binding = Find(item);
                if (binding == null || !IsAlive(item))
                    return false;

                inventory = binding.Origin;
                index = binding.OriginIndex;
                return inventory != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 物品正在使用（已脱离背包）时，取记录下来的原路径，供存档使用。
        /// </summary>
        public static string GetPathFor(Item item)
        {
            try
            {
                Binding binding = Find(item);
                return binding?.Path;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 把物品放回槽位绑定记录里的原格子（使用跟踪丢失时的兜底）。
        /// 返回 true 表示已经处理完，调用方应跳过原版拾取。
        /// </summary>
        public static bool TryReturnToOrigin(Item item)
        {
            try
            {
                if (!IsAlive(item))
                    return false;

                // 已经回到任意背包里：不再移动，交回原版判断
                if (item.InInventory != null)
                    return false;

                if (!TryGetOrigin(item, out Inventory origin, out int index))
                    return false;

                if (item.ParentObject != null)
                    item.Detach();

                item.AgentUtilities.ReleaseActiveAgent();

                if (index >= 0
                    && index < origin.Capacity
                    && origin.GetItemAt(index) == null
                    && origin.AddAt(item, index))
                {
                    return true;
                }

                if (origin.AddAndMerge(item))
                    return true;

                if (origin.AddItem(item))
                    return true;
            }
            catch (Exception e)
            {
                SafeGuard.Report("把扩展绑定物品放回原位置失败", e);
            }

            return false;
        }

        /// <summary>通知所有绑定了这件物品的槽位刷新 UI。</summary>
        public static void NotifySlotsForItem(Item item)
        {
            try
            {
                if (item == null)
                    return;

                foreach (KeyValuePair<int, Binding> pair in Bindings)
                {
                    Binding binding = pair.Value;
                    if (binding == null || binding.Item == null)
                        continue;

                    if (binding.Item == item)
                        PetShortcutUtility.NotifySlotChanged(pair.Key);
                }
            }
            catch (Exception e)
            {
                SafeGuard.Report("刷新扩展快捷栏显示失败", e);
            }
        }

        /// <summary>
        /// 是否需要执行一次「在宠物背包 / 主背包里找同 ID 替代品」的扫描。
        /// 物品刚用完时第一次调用立即返回 true；失败后 0.5 秒内不再重复扫描，
        /// 避免 UI 每帧刷新导致反复遍历背包。
        /// </summary>
        public static bool TryEnterSearch(int index)
        {
            try
            {
                float now = Now;
                if (LastSearchTime.TryGetValue(index, out float last) && now - last < SearchCooldown)
                    return false;

                LastSearchTime[index] = now;
                return true;
            }
            catch
            {
                // 时间接口异常时宁可正常扫描一次
                return true;
            }
        }

        /// <summary>切地图 / 读档 / 禁用 mod：快捷栏会重建，旧记录全部作废。</summary>
        public static void Reset()
        {
            try
            {
                Bindings.Clear();
                LastSearchTime.Clear();
            }
            catch (Exception e)
            {
                SafeGuard.Report("重置扩展快捷栏槽位记录失败", e);
            }
        }

        /// <summary>物品在 Unity 语义下是否仍然存在且没有被标记销毁。</summary>
        private static bool IsAlive(Item item)
        {
            try
            {
                return item != null && !item.IsBeingDestroyed;
            }
            catch
            {
                return false;
            }
        }

        private static Binding Find(Item item)
        {
            try
            {
                if (!IsAlive(item))
                    return null;

                foreach (KeyValuePair<int, Binding> pair in Bindings)
                {
                    Binding binding = pair.Value;
                    if (binding != null && binding.Item == item)
                        return binding;
                }
            }
            catch
            {
                // 查找失败只当作没有记录，不影响游戏
            }

            return null;
        }
    }
}
