# MelonLoader 0.7.3 → BepInEx 移植实施计划

> 状态：**阶段 1-4 已完成**（核心移植、BepInEx 托管、宿主插件、构建打包；完整构建通过并产出 zip）
> 日期：2026-08-08
> 背景：BepInEx 6.0.0-be.785 升级已完成并验证（见下方"已完成"章节）。MelonLoader 0.7.3 的完整移植已执行，本文档记录实施路径与**剩余运行时验证项**。

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

### ✅ net6.0 (IL2CPP) 已通过真实游戏验证（2026-08-09，v2.2.1）

游戏：Iron Nest: Heavy Turret Simulator（Il2Cpp，Unity 6000.3.9f1，BepInEx 6.0.0-be.785）

二次运行（`realrun.log`，v2.2.1）确认：
- ✅ `InternalsVisibleTo` 修复生效，`FieldAccessException` / `MethodAccessException` 完全消失
- ✅ SupportModule（Il2Cpp.dll）加载成功，SceneHandler 场景事件正常（无 override 失败）
- ✅ 共存 BepInEx 插件的 Harmony 补丁正常：IronNest Coop 加载成功，**65 个方法补丁成功**
- ✅ 二次启动时 Il2CppAssemblyGenerator 命中缓存（"Assembly is up to date. No Generation Needed."）
- ✅ 游戏正常运行（Mods/Plugins 加载机制正常，当前 0 个 ML mod）

### ⚠️ net35 (UnityMono) 尚未验证
- `SupportModule.Setup()`（Mono.dll）、`MelonFolderHandler`、Harmony patch 的 Mono 路径待 Unity Mono 游戏测试
- 理论风险较低（net35 是 0.7.3 的成熟路径），但未实测

### 其他已知限制
- **原生钩子**：`NativeHookAttach/Detach` 目前为 no-op，依赖原生钩子的 ML mod 将无法工作
- **bHaptics**：`bHapticsManager.Connect` 初始化时调用，未实测
- **退出流程**：`Core.Quit()` 相关的干净退出未验证

## 参考源码位置

- v0.7.3 源码：`C:\Users\zhizh\AppData\Local\Temp\ml073\repo2`
- BepInEx 6 插件模板（be.785 时代标准引用方式）：`C:\Users\zhizh\AppData\Local\Temp\ml073\templates`
- 迁移前 0.5.7 适配源码：git 历史 `HEAD` 之前的 `MelonLoader/` 目录
