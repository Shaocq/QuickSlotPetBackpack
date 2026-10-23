# 快捷栏可使用宠物背包（QuickSlotPetBackpack）

[![游戏](https://img.shields.io/badge/游戏-Escape%20from%20Duckov-blue)](https://store.steampowered.com/app/3167020/)
[![协议](https://img.shields.io/badge/协议-MIT-yellow)](LICENSE)
[![版本](https://img.shields.io/badge/版本-1.0.0-brightgreen)](CHANGELOG.md)

> 让快捷栏可以绑定宠物背包（含背包内容器）里的物品并直接使用；切换地图或读档后自动恢复绑定，物品仍留在宠物背包里。

| 项目 | 信息 |
| --- | --- |
| 游戏 | 逃离鸭科夫（Escape from Duckov，AppID `3167020`） |
| 框架 | `Duckov.Modding.ModBehaviour` |
| 前置 | HarmonyLib（工坊 `3589088839`），必须置于本 mod **之前**加载 |
| 创意工坊 | — |
| 当前版本 | 1.0.0 |
| 开源协议 | [MIT](LICENSE) |

## 安装

订阅前置 HarmonyLib（工坊 `3589088839`）并置于本 mod **之前**加载，然后把 `QuickSlotPetBackpack` 文件夹放入 `Duckov_Data\Mods\`。

> **`lib/0Harmony.dll` 不在仓库内。** 需要从源码编译时，请把创意工坊前置 HarmonyLib 目录下的 `0Harmony.dll` 复制到 `lib/` 下。


让快捷栏（按键 3~8）可以绑定**宠物背包及其内容器**里的物品，也支持**主背包容器内部**的物品；换地图、读档后自动恢复绑定；使用时物品仍留在原背包/容器里，用完放回原处。

## 前置

- 「HarmonyLib」（工坊 `3589088839`），请置于本 mod 之前加载。
- 本 mod **不内嵌** 0Harmony。

## 原版问题

- `ItemShortcut.IsItemValid` 只认主角色背包（`item.InInventory == MainInventory`），宠物背包物品会被判定无效。
- 拖拽绑定时 `ItemShortcutEditorEntry.OnDrop` 会把不在角色身上的物品塞回主背包。
- 存档 `ItemShortcut_Data` 只记录主背包格子序号，宠物背包物品写成 `-1`，读档时被跳过 → 绑定丢失。
- 用完后 `CA_UseItem.OnStop` 调 `PickupItem`，物品会被塞进主背包，主背包满了就掉在地上。

## 实现

| 文件 | 作用 |
| --- | --- |
| `src/ModBehaviour.cs` | 生命周期、Harmony 重试加载、宠物背包就绪后恢复绑定 |
| `src/Patches.cs` | 8 个 Harmony 补丁（见下） |
| `src/PetShortcutPersistence.cs` | 绑定持久化：主存储写游戏存档 `PetShortcutBindings_Data`，独立文件仅作兼容兜底 |
| `src/PetShortcutUtility.cs` | 宠物背包路径计算 / 解析、环境就绪判断、四位置同 ID 物品查找（主背包顶层 / 主背包容器 / 宠物背包格子 / 宠物背包容器）、槽位占用判断、UI 刷新通知 |
| `src/PetUseTracker.cs` | 记录使用中物品的原位置（宠物背包 / 主背包容器），用完后放回 |
| `src/PetSlotTracker.cs` | 记录哪些快捷栏槽位由扩展物品绑定及其 TypeID，供用完后自动重绑 |
| `src/HarmonyLoad.cs` | 从 AppDomain 找前置载入的 0Harmony |
| `src/SafeGuard.cs` | 异常隔离与日志限流 |
| `src/Diag.cs` | 独立的诊断日志（写 mod 目录下的 `QuickSlotPetBackpack.log`，游戏日志看不到时用） |

补丁：

1. `ItemShortcut.IsItemValid` 后置：宠物背包物品（非武器）以及主背包容器内部的物品（非武器）有效；使用中已脱离背包的物品也暂时有效，避免槽位被清空。
2. `ItemShortcutEditorEntry.OnDrop` 前缀：宠物背包物品直接绑定，不移动物品；保留原版的快捷栏格互换。
3. `ItemShortcut.Set` 后置：绑定宠物背包物品时记住槽位与 TypeID（自动重绑的依据）；换成主背包物品时清除标记，槽位完全交回原版。
4. `ItemShortcut.Get_Local` 前缀（**对齐原版语义：TypeID 为主，实例只是缓存**）：扩展槽位会记住绑定的**那一件**物品实例。
   - **不看耐久**：与原版 `IsItemValid` 一致，耐久为 0 的物品在被 `CA_UseItem.OnFinish` 销毁前仍然留在槽位里，
     不会提前换绑，避免出现「UI 显示 A、按键实际用了 B、扣的是 B 的耐久」；
   - 实例**还活着但已脱离背包**（宠物物品使用中会被 `UseItem` 移出背包，这是原版主背包物品没有的瞬态）时，
     保留**同一件**实例并通知 UI 刷新。只对「确实处于使用流程中」的物品生效（有使用记录 / 被角色持有 /
     有活动 Agent）；玩家把物品丢到地上或放进其它容器时按原版规则立即清空槽位；
   - 实例**被销毁 / 位置失效**时，与原版一致：只清空 `items[index]`，**保留 `itemTypes[index]` 待命**；
   - 然后按 **TypeID** 在四个位置找替代品补位，**绑定物品原来所在的位置优先**，
     其余按固定顺序搜索：
     1. 原位置（主背包顶层 / 主背包容器 / 宠物背包格子 / 宠物背包容器）；
     2. 主背包顶层的物品（与原版 `MainInventory.Find` 一致）；
     3. 主背包中容器内部的物品；
     4. 宠物背包格子里的物品；
     5. 宠物背包中容器内部的物品。

     找到就调用原版 `ItemShortcut.Set` 补位——原版 `Set_Local` 会**连坐清空其他同型号槽位**（本 mod 同步解除那些槽位的接管）。
     四处都没有时**保留 `itemTypes` 待命**（与原版一致，绝不置 -1）：以后背包里再出现同型号物品会自动绑上；
     UI 只在本轮确实清掉了实例时刷新一次，不会反复重刷。
   - **原版主背包顶层槽位**（不由本 mod 接管）也会被兜底：原版 `Get_Local` 只用
     `MainInventory.Find` 搜主背包顶层，找不到就返回 `null`。此时本 mod 的 Postfix 追加搜索
     **主背包容器 → 宠物背包格子 → 宠物背包容器**，找到就绑定（之后该槽位由本 mod 接管）。
     原版已经搜过主背包顶层，所以不会重复；找不到时保持原版结果，槽位进入待命。
5. `ItemShortcut.Save` 后置：把宠物背包绑定写入游戏存档（与原版 `ItemShortcut_Data` 同一存档文件）。
6. `CharacterMainControl.UseItem` 前缀：记录宠物背包物品原位置（可同时跟踪多个物品，不会被其他物品的使用打断），并刷新快捷栏槽位里记录的「最近有效时间」，保证使用期间不会被误判失效。
7. `CharacterMainControl.UseItem` 后置：使用动作没真正启动时（角色在跑动/交互/换动作），物品已被原方法移出背包且不会触发 `CA_UseItem.OnStop`，立即把它放回原格并刷新 UI。
8. `CharacterMainControl.PickupItem` 前缀：使用结束后放回宠物背包原格（使用跟踪记录丢失时用槽位里记住的原位置兜底；失败才走原版拾取/掉落）；物品已经在宠物背包/主背包容器里时直接拦截，避免被原版塞进主背包。放回成功后主动刷新快捷栏 UI。

## UI 同步

原版快捷栏只在 `ItemShortcut.OnSetItem`、主背包内容变化、堆叠数变化时刷新，宠物背包/容器里的变化它完全看不到。本 mod 在以下时机主动强制刷新：

- 绑定 / 改绑 / 解除绑定（`ItemShortcut.Set` 之后立即刷新，不用等下一帧）；
- 自动重绑 / 写回槽位 / 解除绑定；
- 使用动作启动成功、启动失败回位、使用结束放回原容器。

刷新方式是**双保险 + 定时兜底**：

1. 触发 `ItemShortcut.OnSetItem` —— 逐个调用订阅者并各自 `try/catch`，一个订阅者异常不会连累其它快捷栏格；
   注意该事件在游戏程序集里是 **public static event**，反射查询必须包含 `BindingFlags.Public`（只查 NonPublic 会静默失效）；
2. 直接反射调用所有 `ItemShortcutButton` 的私有 `Refresh()`，即使事件没订阅上也能同步 HUD 图标；
3. **每 0.2 秒对账一次**：只针对本 mod 接管的槽位，比较按钮缓存的 `TargetItem` 与实时 `ItemShortcut.Get`，
   不一致就调用按钮 `Refresh()`。原版 HUD 收不到的瞬态（宠物背包物品用完 → 自动换绑主背包物品等）
   错过一次事件也不会永久空白；**原版主背包槽位一次都不读**，不会干扰原版绑定。
4. **刷新期间发生的换绑会补刷一轮**：自动换绑常常是在按钮 `Refresh → Get` 的过程中被触发的，
   此时不能嵌套刷新（会把触发换绑的那个按钮停在空状态），改为记下请求、本轮结束后再刷一轮。
5. **任意位置之间移动物品**（宠物背包 / 宠物背包容器 / 主背包 / 主背包容器，以及**同类型容器之间**，
   例如两个同类存储容器互换物品）时，检测到绑定路径（背包实例 + 从根到物品的格子序号）变化就主动刷新。
   同类型容器互换时物品实例和 TypeID 都没变，按钮显示本身是对的，但物品已换到另一个容器，
   必须重新刷新，否则后续的使用 / 放回流程会沿用旧容器状态。定时对账会在 `ItemShortcut.Get`
   之前先比较位置签名，因此这类「实例不变、位置变了」的情况也会被覆盖。

这样物品使用过程中快捷栏不会变空，任意两个位置之间切换/顶替/用完后都会立即恢复显示（最迟 0.2 秒兜底）。
按钮列表会缓存，换关/换存档后自动重新收集，不额外产生 `FindObjectsOfType` 开销。

## 诊断日志

游戏自身的日志渠道会被其它 mod 的日志处理器接管/过滤，本 mod 的 `Debug.Log` 不一定能看到。
因此 mod 会在**自己的目录**下写 `QuickSlotPetBackpack.log`，记录绑定 / 写回 / 重绑 / 解绑 / 使用回位，
以及 UI 刷新时的异常堆栈（超过 512 KB 自动轮转为 `.log.1`）。排查问题时优先看这个文件。

## 与原版主背包的边界

- 原版 `ItemShortcut_Data` 既不读取也不写入；本 mod 只写自己的 `PetShortcutBindings_Data`。
- `IsItemValid` 补丁只在原版判定为「无效」时才放宽；主背包**顶层**物品的原版结果保持不变。
- 拖拽、使用、拾取补丁只接管「扩展物品」（宠物背包 / 主背包容器内），主背包顶层物品一律走原版逻辑。
  例外是下面「用完后补位」：原版找不到同型号时本 mod 会追加搜索，并在找到后接管该槽位。
- 恢复扩展绑定时：目标槽位已被原版恢复的主背包物品占用，或**别处有同型号的主背包物品**（原版 `Set_Local` 会清除同 TypeID 的其他槽位），就**跳过该绑定**，优先保留原版主背包设置。
- **用完后补位**：四个位置（主背包顶层 / 主背包容器 / 宠物背包格子 / 宠物背包容器）中任意一处绑定的物品
  用完后，都会在「**原位置优先** + 其余三个位置」里寻找同 ID 替代品，只建立绑定，
  不移动、不修改背包里的物品；**同型号冲突与原版完全一致**：由 `Set_Local` 连坐清空其他同型号槽位。
  找不到则保留 `itemTypes` 待命。
- 从主背包容器内的快捷栏使用物品时，用完后会放回原容器，不会被原版挪到主背包顶层。

## 存档方式（与原版主背包绑定一致）

原版 `ItemShortcut.SaveData` 只记录主背包**顶层**的格子序号，宠物背包（含容器）以及主背包容器内的物品都会被记成 `-1`，读档时被跳过。本 mod **按原版完全相同的方式**补上一份数据：

- **主存储：游戏存档本身**，键名 `PetShortcutBindings_Data`，和原版 `ItemShortcut_Data` 在同一个 `Save_N.sav` 里。
  - 存的值是 **JSON 字符串**（`{"bindings":[{"shortcutSlot":0,"path":"2/0"}]}`）：ES3 只需序列化 `string`，不依赖它对 mod 程序集里自定义类型的解析，跨版本最稳。
  - 保存：在原版 `ItemShortcut.Save` 之后、以及 `SavesSystem.OnCollectSaveData` 事件内调用 `SavesSystem.Save`；游戏随后调用 `SavesSystem.SaveFile()` 统一落盘。
  - 读取：在 `LevelManager.OnLevelInitialized` / `SavesSystem.OnSetFile` 之后、宠物背包加载完成时调用 `SavesSystem.Load`。
  - 因此它和角色数据、主背包快捷栏绑定**同生共死**：换地图、读档、回滚备份存档、云同步都会一起变化，不会出现「独立文件与存档对不上」。
- **兜底：`<mod目录>/pet_shortcut_bindings.json`**（mod 目录不可写时退到 `Application.persistentDataPath`）。
  - 仅在游戏存档里**还没有** `PetShortcutBindings_Data` 时读取（兼容旧版本数据），读到后会在下一次保存时自动迁移进游戏存档。
  - 游戏存档里的键存在时，以它为准（哪怕列表为空），避免回滚旧存档后误用独立文件里的新数据。
  - **JSON 由 mod 自己生成和解析，不使用 `UnityEngine.JsonUtility`**：实测 `JsonUtility` 无法序列化这几个嵌套在 `internal` 类里的数据类型，写出来的文件只有 `{}`。旧版本的 `{}` 文件会被安全地当成空数据读取。

记录的是路径，不依赖物品实例 ID：

- 宠物背包：`"2/0"` = 宠物背包第 2 格的容器内第 0 格（与旧版本格式完全一致）；
- 主背包容器：`"main:3/1"` = 主背包第 3 格的容器内第 1 格；
- 主背包顶层物品仍由原版 `ItemShortcut_Data` 保存，本 mod 不重复记录。

- 本关绑定尚未恢复完成时不会写数据，避免把旧数据覆盖成空；副本等 `LevelConfig.SavePet == false` 的关卡会跳过恢复与保存。
- 原版 `ItemShortcut.Save` 只在 `LevelConfig.SaveCharacter` 的关卡执行；订阅 `SavesSystem.OnCollectSaveData` 后，在 `SaveCharacter == false`（但 `SavePet == true`）的关卡也补写一次。

## 异常隔离

本 mod 的所有入口都做了异常兜底，**任何异常都不会抛回游戏或其他 mod 的调用栈**：

- 8 个 Harmony Prefix/Postfix 的方法体全部包在 `try/catch` 里，失败时退回原版逻辑（例如拖拽补丁出错就让原版处理，拾取补丁出错就让原版拾取）。
- Unity 生命周期（`OnEnable` / `OnDisable` / `Update` / `OnAfterSetup`）和游戏事件回调（`OnLevelInitialized` / `OnSetFile` / `OnCollectSaveData`）全部包裹；事件订阅/退订也逐个隔离，某一个失败不影响其他。
- 反射字段改为**首次使用时惰性获取**，避免静态构造抛异常连累整个补丁类初始化。
- 高频路径（`IsItemValid`、`TryGetPath`）内部吞掉异常，只按“不在宠物背包里”处理。
- 日志失败、文件读写失败、备份目录不可写等情况都不会向外抛。
- `SafeGuard` 统一记录异常（同类日志 30 秒限流，异常堆栈也受限流控制），便于排查但不刷屏。

## 构建

```bash
dotnet build 快捷栏可使用宠物背包/QuickSlotPetBackpack.csproj -c Release
```

产物（可直接放进 `Duckov_Data\Mods\`）：`bin/Release/Mod/QuickSlotPetBackpack/`。

## 更新日志

完整版本记录见 [CHANGELOG.md](CHANGELOG.md)。

## 许可证

本项目以 [MIT 协议](LICENSE) 开源，可自由使用、修改与再发布，保留版权声明即可。

## 致谢

- 游戏与 mod 框架：TeamSoda《逃离鸭科夫》
- Harmony 补丁框架：[Harmony](https://github.com/pardeike/Harmony)（MIT）

- 感谢所有在创意工坊反馈问题与建议的玩家
