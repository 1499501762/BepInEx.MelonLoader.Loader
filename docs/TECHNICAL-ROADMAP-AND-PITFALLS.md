# 技术路线与难点总结

> 项目：`BepInEx.MelonLoader.Loader` — 让 **MelonLoader 0.7.3** 的 mods 跑在 **BepInEx 6** 游戏环境里
> 验证游戏：Iron Nest Heavy Turret Simulator（IL2CPP）
> 最后更新：2026-08-16（当前 release：v2.3.8）

---

## 1. 项目定位与铁律

- **做什么**：BepInEx 6 插件托管 MelonLoader 运行时，使原本面向 MelonLoader 的 mods 无需改动即可加载运行。
- **铁律（Approach A）**：**绝不 rewrite mods**。mods 必须是 verbatim 原样加载。所有兼容性工作都放在加载器侧（宿主 + interop 别名）完成。
- **双运行时**：`IL2CPP`（net6.0，BepInEx.Unity.IL2CPP）与 `UnityMono`（net35，BepInEx.Unity.Mono）各一个宿主插件。

## 2. 技术栈

| 组件 | 版本/选型 | 用途 |
|---|---|---|
| BepInEx | 6.0.0-be.785 | 游戏托管框架（IL2CPP 用 Cpp2IL 生成 interop） |
| MelonLoader | 0.7.3（`net35;net6.0` 多目标） | 被托管的 mod 运行时 |
| dnlib | 4.5.0 | 读写 interop 程序集（唯一能正确处理的库） |
| HarmonyX | 2.10.2 | BepInEx.Core be.785 要求 |
| Nuke | `build.ps1` / `build/Build.cs` | Clean + DownloadDependencies + Compile + 打包 zip |
| Win32 Console API | `GetStdHandle` / `SetConsoleTextAttribute` / `WriteConsoleW` | 绕过 BepInEx 日志捕获的彩色输出 |

## 3. 核心架构

```
游戏 (IL2CPP)
 └─ BepInEx 6 preloader
     ├─ [Patcher] BepInEx.MelonLoader.Loader.Patcher  ← interop 别名注入（构造函数阶段）
     └─ BepInEx 插件
         └─ BepInEx.MelonLoader.Loader.IL2CPP / .UnityMono  ← 托管 MelonLoader
             ├─ BepInExHost：Initialize / Start / 游戏循环事件
             ├─ 加载时打印 early-access 警告 banner
             └─ Core.Initialize() → MelonLoader 运行时 → 加载 MLLoader\Mods\*.dll
```

- **interop 别名**：BepInEx 的 interop 用原始命名空间（`Assembly-CSharp`、`Unity.TextMeshPro`），而 MelonLoader mods 引用带前缀类型（`Il2Cpp.LookAtTarget`、`Il2CppTMPro.TMP_Text`）。AliasInjector 在 `BepInEx/interop/` 里为每个非引擎程序集的顶层类型生成 `Il2Cpp*` 前缀别名。
- **指纹跳过**：`BepInEx/interop/.melonloader-aliased` 记录目录指纹（name/size/time）；未变化则跳过（启动快），BepInEx 重新生成 interop 则自动全量重生成。
- **引擎类型绝不别名**：`UnityEngine.*` / `Unity.*` / `System.*` 命名空间类型不生成别名（MelonLoader 从不给它们加前缀，BepInEx interop 已提供），避免破坏引用。

## 4. 技术路线演进（版本史）

| 版本 | 方案 | 结果 |
|---|---|---|
| v2.3.6 | **进程内（plugin）全量别名** | ❌ 不可行：BepInEx 在插件运行前已把全部 interop 加载并内存映射锁定，64 个写回全部 Access denied |
| v2.3.7 | **Preloader Patcher 的 `Initialize()`** 改写 interop | ❌ 仍失败：BepInEx IL2CPP preloader 时序里 `PatchAndLoad()`（调 Initialize）在 `LoadAssemblyDirectories`（Cecil 锁定 interop）之后 → 44 个 Access denied → 0 Mods loaded |
| v2.3.8 | **Preloader Patcher 的构造函数** 改写 interop | ✅ 真实验证通过：别名本次启动即生效，mods 零错误加载 |
| v2.3.8+ | 加载时西瓜配色中英双语 early-access 警告 banner | ✅ 按显示宽度对齐 |

### BepInEx 6 IL2CPP preloader 关键时序（v2.3.8 根因）

```
Il2CppInteropManager.Initialize()          # 生成 interop 文件、启动运行时
AddPatchersFromDirectory(...)              # Activator.CreateInstance → patcher 构造函数在此执行
LoadAssemblyDirectories(interop)           # Mono.Cecil ReadAssembly（InMemory=false）锁定所有 interop 文件
PatchAndLoad()                             # 调用 patcher.Initialize() → interop 已被锁 = Access denied
```

结论：**interop 改写必须在 patcher 构造函数中完成**（早于 Cecil 锁），且 BepInEx 后续的 Cecil 会读取我们改写后的别名文件并加载，别名本次启动即生效。

## 5. 遇到的难点与解决

### 5.1 类型命名空间差异（核心难点）
MelonLoader mods 引用 `Il2Cpp.*` / `Il2CppTMPro.*` 前缀类型，BepInEx interop 是原始命名空间。受 Approach A 约束不能改 mods → 给 interop 全量生成别名，覆盖 `Il2Cpp.*`、`Il2CppTMPro.*`、`Il2CppFMOD.*` 等前缀族。

### 5.2 BepInEx Cecil 文件锁（时序坑）
`LoadAssemblyDirectories` 用 Mono.Cecil `ReadAssembly`（InMemory=false）锁定所有 interop 文件。`Initialize()` 里写回必然失败。→ 移入构造函数。

### 5.3 dnlib 写回文件锁
`ModuleDefMD.Load` 内存映射会锁文件。写回前必须 `ModuleDefMD.Dispose()` 释放句柄再 `File.Move` 覆盖，否则 Access denied。

### 5.4 彩色输出被 BepInEx 捕获褪色
`Console.WriteLine` 会被 BepInEx 的 `Console.SetOut` 捕获、颜色丢失。→ 用 Win32 `GetStdHandle(STD_OUTPUT_HANDLE=-11)` + `SetConsoleTextAttribute` + `WriteConsoleW` 直写原生控制台，实现真正的粉色文字 + 绿色外框（西瓜配色）。

### 5.5 中英混排框对齐
中文全角字符在控制台占 2 列，而 `PadRight` 按字符数（1 列）计算 → 框错位。→ `DisplayWidth`（字符 > 0x7E 计 2 列）+ `RenderBoxLine` 按显示宽度填充空格。布局为**英文块在前、中文块在后**，左右 `|` 严格对齐到同一列。

### 5.6 引擎类型混淆风险
若给 `UnityEngine.*` / `Unity.*` / `System.*` 也加别名会破坏 BepInEx 对引擎类型的引用。→ 显式排除引擎命名空间与 `__Generated.dll`。

### 5.7 "0 Mods loaded" 假象
曾被 Steam 更新清空 `MLLoader\Mods` 目录，误以为加载器失效。→ 排查时先确认 Mods 目录非空。

### 5.8 别名覆盖的隐性缺口
跨模块别名引用需写成 `TypeRef`（而非解析成同模块 TypeDef）；未处理的游戏类型需现场补别名（保证 `TMP_Text.get_font()` 返回 `Il2CppTMPro.TMP_FontAsset`）；`Il2CppClassPointerStore<Il2Cpp.X>` 的泛型参数也要映射（保证 `GetComponentInChildren<Il2Cpp.X>()` 命中同一初始化槽位）。

## 6. 经验教训

1. **BepInEx 6 中"加载前改写 interop"必须用 Patcher 构造函数**（早于 Cecil 锁），`Initialize()` 太晚。
2. **写回前先 `Dispose` 内存映射**，否则文件被锁。
3. **全量别名比按需别名可靠**——覆盖所有前缀族和隐性引用路径。
4. **引擎类型绝不别名**。
5. **每次功能完成必须真实游戏实测再发布**——理论可行不等于实际可行（v2.3.6 / v2.3.7 都是实测才暴露的）。
6. 彩色输出走原生 Win32 console，别依赖被重定向的 `Console`。

## 7. 相关文件

- `BepInEx.MelonLoader.Loader.Patcher/Patcher.cs` — preloader patcher（构造函数触发别名注入）
- `MelonLoader/Hosting/Il2CppInteropAliasInjector.cs` — interop 别名生成器
- `MelonLoader/Hosting/BepInExHost.cs` — 宿主初始化 + early-access banner
- `build/Build.cs` — 构建与打包（patcher + dnlib 部署到 `BepInEx/patchers/`）
- `docs/MELONLOADER-0.7.3-MIGRATION.md` — 迁移实施细节（分版本记录）
