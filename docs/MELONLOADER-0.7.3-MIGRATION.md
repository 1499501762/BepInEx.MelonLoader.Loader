# MelonLoader 0.7.3 → BepInEx 移植实施计划

> 状态：**全部完成**（核心移植、BepInEx 托管、宿主插件、构建打包、真实游戏完整验证）
> 日期：2026-08-09
> 背景：BepInEx 6.0.0-be.785 升级已完成并验证（见下方"已完成"章节）。MelonLoader 0.7.3 的完整移植已执行，本文档记录实施路径与**最终验证结果**。
> **最终结论：net6.0 (IL2CPP) 在真实游戏 Iron Nest 中完整验证通过——ML mod（CustomRecords、IronNestFCS 含开火）与 BepInEx 插件（Coop）全部正常工作。**

## 已完成（BepInEx 6.0.0-be.785）

- `BepInEx.Unity` → `BepInEx.Unity.Mono` 6.0.0-be.785（UnityMono/BepInEx6）
- `BepInEx.IL2CPP` → `BepInEx.Unity.IL2CPP` 6.0.0-be.785（IL2CPP，目标框架改为 net6.0）
- `BepInEx.Core` → 6.0.0-be.785（MelonLoader.csproj）
- HarmonyX 2.10.0 → 2.10.2（BepInEx.Core be.785 依赖要求）
- 插件版本 2.1.0 → 2.2.0
- 两个构建配置（UnityMono BepInEx6 / IL2CPP BepInEx6）全部通过

## 已完成（MelonLoader 0.7.3 移植）
### 阶段 1：0.7.3 核心项目建立
- `MelonLoader/` 源码整体替换为 v0.7.3（251 个 .cs；git 保留 0.5.7 历史可回退）
- `MelonLoader.csproj` 多目标 `net35;net6.0`，内联 v0.7.3 依赖版本变量
- 从 `MelonLoader.Bootstrap/` 链接 `ColorARGB.cs`、`SharedDelegates.cs`
- net35 与 net6.0 均编译通过（0 错误）
- `<Version>0.7.3</Version>` 使 `BuildInfo.Version` 报告 0.7.3
- 移除 `AppendTargetFrameworkToOutputPath=false`（多目标下会覆盖输出）

### 阶段 2：BepInEx 托管适配
- `BootstrapLibrary.cs`：属性 setter 改为 `internal set`（允许托管赋值）
- `BootstrapInterop.cs`：新增 `InitializeManaged(BootstrapLibrary)` 
- 新增 `MelonLoader/Hosting/BepInExHost.cs`：托管 BootstrapLibrary（日志→BepInEx ManualLogSource、配置→LoaderConfig、原生钩子→no-op）+ `Initialize(baseDir)` / `Start()` 入口
- `Core.cs` 4 处 BepInEx 适配：GetLoaderConfig 容错、net35 跳过 `MonoLibrary.Setup()`、跳过 `MonoCoreEntrypoint.Init()`、`Start()` 跳过启动画面直接 `PreSetup()`
- `MelonLoader.csproj` 添加 `BepInEx.Core` 引用（`IncludeAssets="compile"`，不随包发布）

### 阶段 3：宿主插件更新
- UnityMono `Plugin.cs`：调用 `BepInExHost.Initialize(MLLoader 绝对路径)` + `Start()`
- IL2CPP `Plugin.cs`：同上
- **BepInEx 5 支持已移除**（0.7.3 核心基于 BepInEx 6 API）
- IL2CPP 项目添加 `CopyLocalLockFileAssemblies=true`（net6.0 库默认不复制传递依赖，必须显式开启）

### 阶段 4：构建脚本
- `build/Build.cs`：`MLVersionName` → v0.7.3、下载 URL → v0.7.3、移除 BepInEx5 构建
- 更新依赖删除逻辑以匹配 v0.7.3 Dependencies 布局（`MonoBleedingEdgePatches`/`NetStandardPatches` 保留）
- 修复 `Mono*.dll` glob 误删 MonoMod/Mono.Cecil 的问题（改为只删 `*.pdb` + `*Harmony.dll`）
- 完整 Nuke 构建通过，产出：
  - `Output/MLLoader-UnityMono-BepInEx6-v0.7.3.zip`
  - `Output/MLLoader-IL2CPP-BepInEx6-v0.7.3.zip`

### 阶段 5：真实运行验证（Iron Nest: Heavy Turret Simulator，Il2Cpp，BepInEx 6.0.0-be.785）
验证结果（`realrun.log`）：
- ✅ MelonLoader 0.7.3 加载成功（`MelonLoader v0.7.3 Open-Beta`，Runtime Type: net6）
- ✅ `Core::BasePath = <游戏目录>/MLLoader`（MLLoader 目录正确）
- ✅ Il2CppAssemblyGenerator 完整运行（Cpp2IL 下载/执行、Interop 程序集生成成功）
- ✅ Support Module `Il2Cpp.dll` 加载成功
- ✅ BepInEx 链式加载器随后正常加载其他插件

**修复的运行时错误（v2.2.1）**：
- ❌→✅ `FieldAccessException`：`MelonLoader.Support.SceneHandler.Init` 访问 `Core.HarmonyInstance`
- ❌→✅ `MethodAccessException`：`MelonLoader.Support.MelonDetourProvider+MelonDetour.Apply()` 访问 `CoreClrDelegateFixer.GetFixedPointerForDelegate`
- 根因：官方 SupportModule（Il2Cpp.dll 等）需要访问 MelonLoader 的 internal 成员，但迁移时遗漏了 `InternalsVisibleTo` 声明
- 修复：在 `MelonLoader.csproj` 恢复上游的 7 个 `InternalsVisibleTo`（MelonLoader.NativeHost / Il2CppAssemblyGenerator / Il2CppUnityTls / Il2Cpp / Mono / MelonStartScreen / EOS）

## 关键架构差异（0.5.7 vs 0.7.3）

| 维度 | 0.5.7（旧） | 0.7.3（新） |
|------|--------------|--------------|
| 项目结构 | 单一 net35 项目 | `MelonLoader`（net35;net6.0）多目标 |
| 启动方式 | `MLCore.Initialize(Config, false)` | `BepInExHost.Initialize(baseDir)` / `Start()` |
| 配置 | BepInEx ConfigFile | `LoaderConfig`（BaseDirectory 指向 MLLoader） |
| 原生宿主 | 无（自包含） | 依赖 BootstrapInterop → 已用托管实现替换 |

## 剩余工作（运行时验证状态）

### ✅ net6.0 (IL2CPP) 已通过真实游戏完整验证（2026-08-09，v2.3.0）

游戏：Iron Nest: Heavy Turret Simulator（Il2Cpp，Unity 6000.3.9f1，BepInEx 6.0.0-be.785）

**最终验证通过的 mod（真实游戏内）**：
- ✅ **IronNestFCS.CustomRecords v1.0.1**：自定义记录盘（Erika.mp3）在游戏内创建、可拖拽交互
- ✅ **IronNestFCS v1.0.5**：完整 Fire Control System，任务中绑定成功（`[FCS] Initialize: success`），**T1..T4 目标按钮开火正常**
- ✅ **IronNest Coop 2.1.0**（BepInEx 插件）：65 方法 Harmony 补丁正常，与 ML mod 共存
- ✅ F9 热重载 Logic 正常

**为让 ML mod 在 BepInEx 6 下真正工作，依次解决的关键问题（v2.2.3 → v2.3.2）**：

1. **v2.2.3 — 游戏循环事件由插件侧驱动**
   官方 SupportModule 在 BepInEx 托管下不交付游戏循环事件（SM_Component 的 GameObject 非根对象，`DontDestroyOnLoad` 警告）。改为宿主插件创建 MonoBehaviour 驱动：
   - IL2CPP：`GameLoopDriver`（`IL2CPPChainloader.AddUnityComponent(typeof(...))` 注册，参数less构造器 + `ClassInjector` 可行）
   - UnityMono：`BaseUnityPlugin` 自身即 MonoBehaviour
   - `UnityEngine.Modules` 必须 `ExcludeAssets="runtime" PrivateAssets="all"`（否则 DLL 拷入插件目录与 interop 冲突）

2. **v2.2.4 — 场景事件不能直接订阅**
   Il2Cpp 下 `SceneManager.sceneLoaded += ...` 抛 `MissingMethodException`（interop `UnityAction<Scene,LoadSceneMode>` 缺 `(Object,IntPtr)` ctor）。改用轮询检测场景。

3. **v2.2.5 — interop 命名空间约定冲突（核心根因）**
   ML 的 Il2CppInterop 给所有游戏类型加 `Il2Cpp.` 前缀（`Il2Cpp.RecordItem`），BepInEx 6 用原始命名空间（`RecordItem`）。两套 interop 程序集标识相同（`Assembly-CSharp, 0.0.0.0`）无法共存。ML mod 引用 `Il2Cpp.*` → `TypeLoadException`。
   修复：新增 `Il2CppInteropModRewriter`，用 Mono.Cecil 在加载前剥掉游戏类型引用的 `Il2Cpp` 前缀（**6 个字符**），使 mod 绑定到 BepInEx interop。Coop 等 BepInEx 插件不受影响。

4. **v2.2.6 — 场景检测必须枚举所有已加载场景**
   游戏用 Additive 加载主菜单/任务场景，只查 `GetActiveScene()` 会漏掉。改枚举 `SceneManager.sceneCount` + `GetSceneAt`。此后 CustomRecords 在每个场景正确建盘。

5. **v2.2.7 — 目录级就地改写**
   有的 mod 自己热加载 Logic DLL（IronNestFCS.Logic.dll，按 F9 重载），绕过 MelonAssembly 加载时改写 → 同样 TypeLoadException。在 `Core.Initialize()` 前 `RewriteAllOnDisk` 扫描改写整个 MLLoader 目录（含 UserData）。幂等。

6. **v2.2.8 — 推迟 mod 加载到场景就绪**
   插件 `Load()` 里同步 `Core.Start()` 时初始场景未加载，mod 初始化查场景对象失败（IronNestFCS 找不到 "Player Turret Piece"）。改为驱动第一帧 Update 再 `Start()`（`HasStarted` 幂等保护）。

7. **v2.2.9 — MelonCoroutines 协程队列推进**
   `MelonCoroutines._queue` 从未被消费（SupportModule 协程 runner 失效）→ 所有 `WaitForSeconds` 永不完成。新增 `ProcessQueue` 每帧推进（反射读 `WaitForSeconds.m_Seconds`）。此后 IronNestFCS 进任务后自动重绑成功（`Initialize: success`）。

8. **v2.3.0 — 嵌套 IEnumerator 协程支持**
   IronNestFCS 开火管线 `yield return CoroutineLock.Acquire()` / `GunSystem.LoadBullet()/SelectPowder()/LoadPowder()/SetElevation()/WaitFire()/WaitBackToIdle()` 全是**嵌套 IEnumerator**（返回 IEnumerator 的方法）。旧实现跳过嵌套协程 → 装弹/装药/调仰角/等开火全没执行 → **T1 不开火**。改为**栈式**执行：`current is IEnumerator` 时压栈跑完子协程再继续外层。此后 T1 开火正常。

9. **v2.3.1 — 退出流程事件交付**
   SupportModule 的 Quit 钩子在 BepInEx 托管下不触发 → mod 收不到 `OnApplicationQuit`/`OnApplicationDefiniteQuit`，`Core.Quit()` 清理（存偏好/解绑 Harmony/断开 bHaptics）不执行。在宿主驱动的 `OnApplicationQuit()` 中调用 `BepInExHost.InvokeOnApplicationQuit()` + `InvokeOnApplicationDefiniteQuit()`（→ `Core.Quit()`）。

10. **v2.3.2 — Harmony 注解 blob 改写**
    `[HarmonyPatch(typeof(Il2Cpp.EntityLocation), "Method")]` 里的 `typeof(...)` 被编译器编码进 **CustomAttribute blob**（不是元数据 TypeRef），`GetTypeReferences()` 看不到 → Harmony 读注解抛 `TypeLoadException: Could not load type 'Il2Cpp.EntityLocation' from assembly 'Assembly-CSharp'` → patch 全部失效 → mod 初始化中断（cfg 不生成）。修复：`RewriteModule` 遍历每个类型/方法/字段/属性/事件的 `CustomAttributes`，改写构造参数、命名参数里的 `typeof(游戏类型)` 类型引用（递归处理数组）；`RewriteTypeReference` 兼容 Mono.Cecil fallback 解码的 `ModuleReference` scope。类级与方法级注解均已覆盖（合成端到端验证：真实 `MelonLoader.dll` 反射调用 `RewriteIfNeeded`，`Il2Cpp.EntityLocation` → `EntityLocation`）。

11. **v2.3.3 — 发布 zip 全新部署与 Il2Cpp.* mod 的 interop 兼容（根因，约束：不改写 mod）**
    `v2.3.3` 采用 **Approach A**（mod 原样加载、**绝不改写 mod**），前提是运行时 interop（`BepInEx\interop\Assembly-CSharp.dll`）**自带 `Il2Cpp.*` 别名类型**。验证环境靠**手动**运行外部 `DnlibAliasGen` 工具把别名 interop 覆盖进 `BepInEx\interop\` 才通过；但**发布 zip（build.ps1 产物）不含 interop，也没有生成别名的自动化步骤** → 全新部署时 BepInEx 6 preloader 用 Cpp2IL 生成**原始命名空间** interop，原始 MelonLoader mod（Harmony `typeof(Il2Cpp.X)` / 直接 TypeRef 引用 `Il2Cpp.*`）解析失败 → `TypeLoadException: Could not load type 'Il2Cpp...' from assembly 'Assembly-CSharp'` → mod 失效。
    **已确认事实**：
    - 发布 zip 的 `MelonLoader.dll`（插件目录）与验证环境 SHA 完全一致（`1ECF15E9`，含 v2.3.3 DetourProvider 修复）。
    - `MLLoader\MelonLoader\Dependencies`（含 `SupportModules\Il2Cpp.dll`）与官方下载一致，`SupportModule` hash `SAME=True`。
    - 发布 zip 的 `MLLoader\MelonLoader\` 只有 `Dependencies`；验证环境多出的 `Il2CppAssemblies\`（MelonLoader 生成器输出）与 `MelonLoader.dll`（旧残留）在 BepInEx 托管下**不是**核心加载路径（核心从插件目录加载），官方 zip 的 `net6/net35/net472` 同样非必需。
    - BepInEx 6 interop 在 preloader 阶段生成并经 `LoadAssemblyDirectories` 加载进默认 ALC，`PreloadInteropAssemblies` 在插件加载前用 `Assembly.Load(name)` 复用已加载程序集 → **patcher / 插件都无法在 interop 预加载前改写 interop 文件实现一次启动生效**。
    **约束与方向**：用户明确**禁止以 rewrite 改写原本的 mod**。合法治本方向是把 `Il2Cpp.*` 别名生成整合进 loader（改 interop、不改 mod）——`BepInExHost.Initialize` 时检测 `BepInEx\interop\Assembly-CSharp.dll` 是否含 `Il2Cpp.*` 别名，若无则用 dnlib 注入别名（幂等），首次运行修复后需重启一次游戏使预加载使用别名版。

### ⚠️ net35 (UnityMono) 尚未实测
- `SupportModule.Setup()`（Mono.dll）、`MelonFolderHandler`、Harmony patch 的 Mono 路径待 Unity Mono 游戏测试
- 理论风险较低（net35 是 0.7.3 的成熟路径），驱动逻辑与 IL2CPP 版一致，但未实测

### 其他已知限制
- **退出流程（v2.3.1 已修复）**：SupportModule 的 Quit 钩子在 BepInEx 托管下不触发 → mod 收不到 `OnApplicationQuit`/`OnApplicationDefiniteQuit`，`Core.Quit()` 清理不执行。已在宿主驱动的 `OnApplicationQuit()` 中调用 `BepInExHost.InvokeOnApplicationQuit()` + `InvokeOnApplicationDefiniteQuit()`（→ `Core.Quit()` 保存偏好/解绑 Harmony/断开 bHaptics）。
- **原生钩子（保持 no-op）**：`NativeHookAttach/Detach` 仍为 no-op。核心里唯一内部使用者是 `Il2CppICallInjector`（hook `il2cpp_resolve_icall`，可选 fix，已 try/catch 静默失败，游戏不受影响）；公共 API `MelonUtils.NativeHookAttach` 仅极少数 mod 使用。实现真实 detour（MinHook 或自写 x64 detour）风险高收益低，遇到确实需要的 mod 再评估。
- **bHaptics（无问题）**：`bHapticsManager.Connect` 是 bHapticsLib 的后台连接，无设备时优雅失败，游戏运行正常；仅未接设备实测。
- **net35 (UnityMono)**：驱动逻辑与 IL2CPP 版一致，未实测。

## 参考源码位置

- v0.7.3 源码：`C:\Users\zhizh\AppData\Local\Temp\ml073\repo2`
- BepInEx 6 插件模板（be.785 时代标准引用方式）：`C:\Users\zhizh\AppData\Local\Temp\ml073\templates`
- 迁移前 0.5.7 适配源码：git 历史 `HEAD` 之前的 `MelonLoader/` 目录
