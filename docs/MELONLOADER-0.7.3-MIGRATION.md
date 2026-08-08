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

## 关键架构差异（0.5.7 vs 0.7.3）

| 维度 | 0.5.7（旧） | 0.7.3（新） |
|------|--------------|--------------|
| 项目结构 | 单一 net35 项目 | `MelonLoader`（net35;net6.0）多目标 |
| 启动方式 | `MLCore.Initialize(Config, false)` | `BepInExHost.Initialize(baseDir)` / `Start()` |
| 配置 | BepInEx ConfigFile | `LoaderConfig`（BaseDirectory 指向 MLLoader） |
| 原生宿主 | 无（自包含） | 依赖 BootstrapInterop → 已用托管实现替换 |

## 剩余工作（需要真实游戏运行时验证）

> ⚠️ 编译与打包已完成，但**运行时行为未经真实游戏验证**，以下为高风险点：

1. **net35 (UnityMono) 运行时**：
   - `SupportModule.Setup()` 需加载 Dependencies/SupportModules/Mono.dll（原生支持模块），在 BepInEx 环境下的兼容性未验证
   - `MelonFolderHandler` 扫描、Harmony patch、mod 加载流程需实际游戏测试
2. **net6.0 (IL2CPP) 运行时**：
   - 0.7.3 的 net6 分支依赖 Il2CppInterop（1.5.1），与 BepInEx 自身 Il2Cpp 集成可能有冲突/重叠
   - `Il2CppAssemblyGenerator`（PreSetup）在 BepInEx 环境下可能重复生成或冲突
   - `RegisterTypeInIl2Cpp` / `RegisterTypeInIl2CppWithInterfaces` 与 BepInEx 的注册流程交互未验证
3. **原生钩子**：`NativeHookAttach/Detach` 目前为 no-op，依赖原生钩子的 mod 将无法工作
4. **bHaptics**：`bHapticsManager.Connect` 在初始化时调用，需游戏环境验证
5. **退出流程**：`Core.Quit()` 相关的干净退出未验证

## 参考源码位置

- v0.7.3 源码：`C:\Users\zhizh\AppData\Local\Temp\ml073\repo2`
- BepInEx 6 插件模板（be.785 时代标准引用方式）：`C:\Users\zhizh\AppData\Local\Temp\ml073\templates`
- 迁移前 0.5.7 适配源码：git 历史 `HEAD` 之前的 `MelonLoader/` 目录
