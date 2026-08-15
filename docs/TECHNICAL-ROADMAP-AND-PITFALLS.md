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

## 7. 后续优化路线（候选，尚未实施）

> 均符合 Approach A 铁律。以下为可行性评估，供排期参考。

### 7.1 类型转发替代类型复制（消除重复类型）— ⚠️ 不可行（但有托管层替代方案）
用户建议：用 `TypeForwardedToAttribute` 把 `Il2Cpp.X` 转发到原始类型，避免 `Assembly.GetTypes()` 出现重复类型。
**评估**：`TypeForwardedTo` 要求**跨程序集**转发且**类型全名一致**；本场景是**同程序集内改名**（`LookAtTarget` → `Il2Cpp.LookAtTarget`，全名不同），CLR 不支持，此路线不成立。当前"复制 TypeDef + 跨模块 TypeRef"是实现同程序集改名的必要手段。真实风险可控：mod 视角只引用 `Il2Cpp.*` 前缀类型，正常业务 mod 不会遍历找无前缀类型；极端扫描类 mod（按命名空间/特性全量扫描）需另行评估。

**替代方案（托管层反射钩子）— ⚠️ 已实施并**实测否决**（2026-08-16）**：不改 interop 别名注入，只在 MelonLoader 托管层用 Harmony patch `Assembly.GetTypes()` / `GetExportedTypes()` / `GetType(string)`：
- 对 **interop 程序集**过滤掉无前缀原始类型，只返回 `Il2Cpp.*` 前缀别名版本 → mod 的托管反射视角与原生 MLL 完全一致，看不到重复类型；
- `GetType(string)` 做双向兼容，两种命名空间都能查到。
**优点**：行为对齐度高、成本低、不碰 interop 原生层。**缺点**：属于托管层"障眼法"，绕过硬反射 API 直读元数据仍可见重复，但对 99% mod 足够。
**风险点（需真实验证）**：
1. 作用域必须严格限定在"含别名类型的 interop 程序集"，避免影响 BepInEx/MLL 自身的反射逻辑；
2. **Il2CppInterop.Runtime 是否会在托管侧遍历 interop 的 `GetTypes()` 做类型注册**——若会，过滤可能破坏原始类型初始化（本项目铁律：理论必须实测）；
3. 需缓存过滤结果（interop 类型数千，避免每次调用 O(n) 过滤 + 分配）。

**实测结论（否决，全部回退）**：已完整实施并真机验证——Harmony postfix patch `Assembly.GetTypes/GetExportedTypes` + 程序集级 `[MelonLoaderAliasedInterop]` 标记（AliasGenerator 写回时打标）+ 按命名空间过滤（保留引擎/系统 + `Il2Cpp*`）+ 缓存。关键证据：**BepInEx 6 的 42 个 aliased interop 程序集 `GetTypes()` 全部抛 `ReflectionTypeLoadException`**（BepInEx interop 存在无法解析的类型引用）→ **postfix 在原方法抛异常时不执行，过滤对 interop 永远不生效**。同时证明"重复类型"问题实际不存在——`GetTypes()` 从不成功返回，mod 拿不到重复列表。Harmony postfix 实际只拦截到对**自身/非 interop** 程序集的 GetTypes 调用（如 `IronNestFCS.CustomRecords` 扫自己），本就无需过滤。结论：反射钩子无法解决该问题（postfix 路径被 GetTypes 异常阻断；prefix 完全接管枚举需复杂处理且 Type 解析不可靠），已全部回退，代码库保持干净。**经验：BepInEx 6 interop 的托管反射 `GetTypes()` 本身不可用（抛异常），任何依赖枚举 interop 类型的托管层方案都先验证这一点。**

### 7.2 补全 `NativeHookAttach` 兼容层 — ✅ 可行
对接 BepInEx 底层 detour 接口（DetourProvider），封装与 MLL 签名一致的 `MelonUtils.NativeHookAttach/Detach`。覆盖 99% 使用该 API 的 mod；hook 链顺序与原生 MLL 无法完全一致（已知限制）。

### 7.3 托管实现 `Il2CppICallInjector` — ✅ 可行（已有基础）
代码库已有 `MelonLoader/Fixes/Il2CppInterop/Il2CppICallInjector.cs`。进一步对接 BepInEx IL2CPP 的 ICall 解析钩子，让依赖 icall 劫持的 mod 可工作。

### 7.4 补全环境模拟 — ✅ 可行（低风险）
复刻 MLL 的环境变量、AppContext 开关、目录结构、日志格式、配置文件路径，让 mod 读取运行环境时无感知差异。

### 7.5 mod 兼容性自检 — ✅ 已实施（2026-08-16）
启动时（mods 加载前）用 **dnlib 静态扫描** mod 程序集的 IL 调用，识别对桥接下未实现/空操作 API（`MelonUtils.NativeHookAttach/Detach`、`Imports.Hook/Unhook`）的引用，打印英文 `[CompatScan]` Warning 警告（注明依赖原生 MLL 能力、并引导反馈到 **BepInEx MelonLoader Loader 分叉版** `https://github.com/1499501762/BepInEx.MelonLoader.Loader` 而非 mod 作者），从源头减少无效 issue。
- 实现：`MelonLoader/Hosting/ModCompatScanner.cs`（net6.0/IL2CPP），挂在 `Core.Start` 的 `LoadMelons` 之前；dnlib 元数据只读，不加载、不改写 mod（Approach A 安全），扫描后 `Dispose` 释放文件锁。
- 关键选型：**dnlib 静态扫描而非运行时反射**——因为 BepInEx 6 interop 的托管 `GetTypes()` 抛 `ReflectionTypeLoadException`（见 7.1 实测），运行时枚举 interop 不可靠；dnlib 直接读 IL operand 的 `MemberRef`/`MethodSpec`，可靠且零加载副作用。
- 真实验证：正常扫描 2 个 mod 程序集 0 警告；注入调用 `NativeHookAttach` 的测试 dll 后正确打出 `[Warning]` 级 `[CompatScan]` 警告（含 API 全名 + 原因 + 引导文案），删除测试 dll 后恢复 0 警告；游戏零错误。

**结论**：7.2–7.4 待排期实施；7.1 因 `TypeForwardedTo` 语义限制不可行（反射钩子替代也已实测否决），保持现有复制方案；7.5 已完成。

## 8. 相关文件

- `BepInEx.MelonLoader.Loader.Patcher/Patcher.cs` — preloader patcher（构造函数触发别名注入）
- `MelonLoader/Hosting/Il2CppInteropAliasInjector.cs` — interop 别名生成器
- `MelonLoader/Hosting/BepInExHost.cs` — 宿主初始化 + early-access banner
- `build/Build.cs` — 构建与打包（patcher + dnlib 部署到 `BepInEx/patchers/`）
- `docs/MELONLOADER-0.7.3-MIGRATION.md` — 迁移实施细节（分版本记录）
