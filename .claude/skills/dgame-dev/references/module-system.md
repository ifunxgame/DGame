# DGame 模块系统架构

> **适用场景**：理解框架有哪些模块、模块如何注册与销毁、能否运行时启停（热插拔）、新模块该落在哪一套机制里 | **关联文档**：[modules.md](modules.md)（模块 API 速查）、[architecture.md](architecture.md)（分层与程序集）
>
> **核实基准**：Unity 6000.3.10f1，分支 `Unity6000.3`。2026-09-02 逐条核实，2026-09-03 修复其中 4 项（见第七章）——**本文行号为修复后的**。若与当前源码不符，以源码为准并回头修正本文。

---

## 核心结论

DGame **不是一套模块系统，而是三套并存的注册机制**，热插拔能力完全不同：

| 机制 | 位置 | 层 | 创建方式 | 单个卸载 |
|------|------|-----|----------|:--------:|
| `ModuleSystem` | `GameUnity/Assets/DGame/Runtime/Core/ModuleSystem/` | 框架（不热更） | 反射惰性创建 | ❌ 仅整体 `Destroy()` |
| `SingletonSystem` | `GameUnity/Assets/Scripts/HotFix/GameLogic/Module/SingletonSystem/` | 热更 | `Instance` 惰性 `new` | ✅ `DestroySingleton()` |
| `DataCenterSys` | `GameUnity/Assets/Scripts/HotFix/GameLogic/DataCenter/` | 热更 | Roslyn 源生成器 | ❌ 仅 Register |

**框架层不支持任何形式的模块热插拔。** 要做可动态启停的子系统，走热更层 `Singleton<T>` + `IUpdate`，那是现有基础设施里唯一能卸载的。

---

## 一、ModuleSystem（框架底座）

`ModuleSystem.cs:9` 是 `public static class`，非单例、非 MonoBehaviour，全文 253 行。

### 容器与轮询

`ModuleSystem.cs:15-18`：

| 字段 | 类型 | 用途 |
|------|------|------|
| `m_moduleMaps` | `Dictionary<Type, Module>` | 类型 → 实例（初始容量 `DEFAULT_MODULE_COUNT` = 16，`:14`） |
| `m_modules` | `LinkedList<Module>` | 全部模块，按 `Priority` **降序**插入 |
| `m_updateModules` | `LinkedList<Module>` | 仅实现 `IUpdateModule` 的模块 |
| `m_updateExecuteList` | `List<IUpdateModule>` | 实际轮询列表，脏标记重建（`:42-46`、`:54-62`） |

驱动来自 `RootModule.cs:261-265`（`MonoBehaviour`）→ `ModuleSystem.Update(GameTime.DeltaTime, GameTime.UnscaledDeltaTime)`。

### 获取模块：反射 + 命名约定

```csharp
GameModule.ResourceModule                      // 业务层标准写法
ModuleSystem.GetModule<IResourceModule>()      // 框架层 / 未进门面的模块
```

- 泛型参数**必须是接口**，否则抛 `DGameException`（`:72-75`）。
- 类型名由接口名反推（`:89`）：`$"{type.Namespace}.{type.Name.Substring(1)}, {type.Assembly.GetName().Name}"` → `Type.GetType`（`:91`）。
  即约定 **`IXxxModule` → 同命名空间、同程序集的 `XxxModule`**。
- 惰性创建（`:106`、`:132-134`）：`Activator.CreateInstance(moduleType) as Module`。首次反射成功后会以接口类型建立别名索引（`:97-102`），后续直接命中 `:77-80` 的快路径。
- 销毁后不再创建：`m_isDestroyed` 守卫（`:82`、`:113`）命中时打 `DLogger.Warning` 并返回 null。

> **落位约束**：新增框架模块必须遵守 `IXxxModule` / `XxxModule` 同命名空间同程序集的命名约定，否则 `Type.GetType` 反推失败。

### 基类

`Module.cs` 全文只有两个类型：

```csharp
public interface IUpdateModule                          // :3-11
{
    void Update(float elapseSeconds, float realElapseSeconds);
}

public abstract class Module                            // :16
{
    public virtual int Priority => 0;                   // :22 高者先轮询、后销毁
    public abstract void OnCreate();                    // :27
    public abstract void OnDestroy();                   // :32
}
```

**没有** `Enable` / `Disable` / `IsEnabled` / `Shutdown` / `Dispose`，未实现 `IDisposable`。这是"不支持热插拔"的根本原因。

### 框架层模块完整清单（12 个）

全在 `GameUnity/Assets/DGame/Runtime/Module/` 下：

| 模块 | 接口 | Priority | IUpdateModule | 文件:行 |
|------|------|:--------:|:-------------:|---------|
| `ObjectPoolModule` | `IObjectPoolModule` | 6 | ✅ | `ObjectPoolModule/ObjectPoolModule.cs:6`（Priority `:16`） |
| `ResourceModule` | `IResourceModule` | 4 | ❌ | `ResourceModule/ResourceModule.cs:11`（Priority `:61`） |
| `FsmModule` | `IFsmModule` | 1 | ✅ | `FsmModule/FsmModule.cs:7`（Priority `:12`） |
| `AnimModule` | `IAnimModule` | 1 | ✅ | `AnimModule/AnimModule.cs:6`（Priority `:13`） |
| `MonoDriver` | `IMonoDriver` | 0 | ❌ | `MonoDriver/MonoDriver.cs:8` |
| `AudioModule` | `IAudioModule` | 0 | ✅ | `AudioModule/AudioModule.cs:10` |
| `SceneModule` | `ISceneModule` | 0 | ❌ | `SceneModule/SceneModule.cs:10` |
| `GameTimerModule` | `IGameTimerModule` | 0 | ✅ | `GameTimer/GameTimerModule.cs:6` |
| `GameObjectPoolModule` | `IGameObjectPoolModule` | 0 | ✅ | `GameObjectPoolModule/GameObjectPoolModule.cs:9` |
| `SensitiveWordModule` | `ISensitiveWordModule` | 0 | ❌ | `SensitiveWordModule/SensitiveWordModule.cs:8` |
| `DebuggerModule` | `IDebuggerModule` | -1 | ✅ | `DebuggerModule/DebuggerModule.cs:3`（Priority `:11`） |
| `ProcedureModule` | `IProcedureModule` | -2 | ❌ | `ProcedureModule/ProcedureModule.cs:5`（Priority `:20`） |

`SensitiveWordModule` 是唯一 `public` 的实现类，其余多为 `internal sealed` —— 业务层只能通过接口访问。

### 热更层也注册进 ModuleSystem 的模块（2 个）

靠 `Type.GetType(ns.Name, assemblyName)` 反射跨程序集拿到：

| 模块 | 接口 | Priority | IUpdateModule | 文件:行 |
|------|------|:--------:|:-------------:|---------|
| `LocalizationModule` | `ILocalizationModule` | 0 | ❌ | `HotFix/GameLogic/Module/LocalizationModule/LocalizationModule.cs:5` |
| `InputModule` | `IInputModule` | 0 | ✅ | `HotFix/GameLogic/Module/InputModule/InputModule.cs:7`（整文件包在 `#if ENABLE_INPUT_SYSTEM` 内） |

### 预热点

`GameUnity/Assets/DGame.AOT/GameEntry.cs:9-11` 在 `Awake` 主动预热三个：`IMonoDriver`、`IResourceModule`、`IFsmModule`，随后 `ProcedureSettings.StartProcedure()`（`:12`）。**其余模块全靠首次访问触发创建。**

### 销毁

```csharp
ModuleSystem.Destroy()      // ModuleSystem.cs:234-251
```

- 从 `m_modules.Last` **反向**遍历（低优先级先销毁）调 `OnDestroy()`（`:237-242`）。
- 清空全部 4 个容器（`:244-247`）。
- 额外调 `MemoryPool.ClearAll()`（`:248`）与 `Utility.Marshal.FreeCachedHGlobal()`（`:249`）。
- 末尾置 `m_isDestroyed = true`（`:250`），此后不再创建任何模块。

**唯一调用点**：`RootModule.cs:277-284` 的 `OnDestroy()`，被 `#if !UNITY_EDITOR` 包住。

---

## 二、GameModule 门面

**全库只有一个 `GameModule`**，在热更层：`GameUnity/Assets/Scripts/HotFix/GameLogic/GameModule.cs:6`。
`DGame.Runtime` 下**不存在** `GameModule`。

全部 `static` 属性，懒加载 + 静态字段缓存：

| 属性 | 类型 | 来源 | 行 |
|------|------|------|-----|
| `RootModule` | `DGame.RootModule` | `UnityUtil.FindObjectOfType<RootModule>()` | `:15-20` |
| `FsmModule` | `IFsmModule` | ModuleSystem | `:27` |
| `SensitiveWordModule` | `ISensitiveWordModule` | ModuleSystem | `:35` |
| `AnimModule` | `IAnimModule` | ModuleSystem | `:43` |
| `ResourceModule` | `IResourceModule` | ModuleSystem | `:51` |
| `AudioModule` | `IAudioModule` | ModuleSystem | `:59` |
| `SceneModule` | `ISceneModule` | ModuleSystem | `:67` |
| `GameTimerModule` | `IGameTimerModule` | ModuleSystem | `:75` |
| `Input` | `GameLogic.IInputModule` | ModuleSystem，`#if ENABLE_INPUT_SYSTEM` | `:78-86` |
| `LocalizationModule` | `ILocalizationModule` | ModuleSystem | `:93` |
| `GameObjectPool` | `IGameObjectPoolModule` | ModuleSystem | `:101` |
| `UIModule` | `GameLogic.UIModule` | **`UIModule.Instance`（SingletonSystem）** | `:109` |
| `RedDotModule` | `GameLogic.RedDotModule` | **`RedDotModule.Instance`（SingletonSystem）** | `:117` |
| `GuideModule` | `GameLogic.GuideMgr` | **`GuideMgr.Instance`（SingletonSystem）** | `:125` |

`GameModule.Destroy()`（`:141-160`）**只把静态缓存字段置 null**，不销毁任何模块实例。

### 未进门面的 Runtime 模块

`IObjectPoolModule`、`IProcedureModule`、`IDebuggerModule`、`IMonoDriver` 四个只能直接 `ModuleSystem.GetModule<T>()`。
现有调用点：`RootModule.cs:163,165`、`ProcedureSettings.cs:24,65`、`DebuggerDriver.cs:376`、`UnityUtil.cs:329`、`SingletonSystem.cs:256,281`。

---

## 三、SingletonSystem（热更层，唯一支持运行时卸载）

目录：`GameUnity/Assets/Scripts/HotFix/GameLogic/Module/SingletonSystem/`

### 接口与基类

`ISingleton.cs`：

```csharp
public interface ISingleton { void Active(); void Destroy(); }   // :3-14
public interface IUpdate              { ... }                    // :16
public interface IFixedUpdate         { ... }                    // :24
public interface ILateUpdate          { ... }                    // :32
public interface IDrawGizmos          { ... }                    // :40
public interface IDrawGizmosSelected  { ... }                    // :48
```

`Singleton.cs:5`：`public abstract class Singleton<T> : ISingleton where T : Singleton<T>, new()`

- `Instance` getter（`:12-24`）：`new T()` → `OnInit()` → `SingletonSystem.Register(m_instance)`。惰性、按需。
- 构造函数用 `StackTrace` 校验是否经 `Instance` 创建（编辑器下报错，`:33-42`）——**不要手动 `new`**。
- `Destroy()`（`:59-68`）→ `OnDestroy()` + `SingletonSystem.DestroySingleton` + 置 null。

`MonoSingleton.cs:6`：`public class MonoSingleton<T> : MonoBehaviour`，`Instance` 里查/建 GameObject 再 `SingletonSystem.Register(go, m_instance)`（`:47`），`DontDestroyOnLoad`。
**当前无任何子类**。

### 注册表

`SingletonSystem.cs:11` `public static class SingletonSystem`

- 容器（`:14-26`）：`List<ISingleton>`、`List<IUpdate>`、`List<IFixedUpdate>`、`List<ILateUpdate>`、编辑器下两个 Gizmos 列表、`Dictionary<string, GameObject> m_gameObjects`。
- 注册：`Register(ISingleton)`（`:32`）、`Register(GameObject, object)`（`:44`）→ `RegisterLifeCycle`（`:58`）按接口分流挂到各生命周期列表。
- **运行时单个卸载**：`DestroySingleton(ISingleton)`（`:94`）、`DestroySingleton(GameObject, object)`（`:108`）→ `DestroyLifeCycle`（`:119-147`）从各列表 `Remove`。
  **这是全项目唯一具备"运行时移除单个成员"能力的注册表。**
- 整体：`Destroy()`（`:152-189`）、`Restart()`（`:218-227`，`Destroy()` + `SceneManager.LoadScene(0)`）。
- `GetSingleton(string)`（`:229`）是 `internal`，按 `ToString()` 线性查找。
- **驱动**：`CheckInit()`（`:245-268`）从 **Runtime 的 ModuleSystem** 取 `IMonoDriver`（`:256`），挂 `AddUpdateListener` / `AddFixedUpdateListener` / `AddLateUpdateListener`；`DeInit()`（`:270-293`）反注册。
  **这是两套系统唯一的耦合点** —— SingletonSystem 寄生在 Runtime 的 `MonoDriver` 上。

### 实现类清单

路径均相对 `GameUnity/Assets/Scripts/HotFix/GameLogic/`：

| 类 | 文件:行 | 额外接口 |
|----|---------|----------|
| `UIModule` | `Module/UIModule/UIModule.cs:14`（`sealed partial`） | `IUpdate` |
| `RedDotModule` | `Module/RedDotModule/RedDotModule.cs:9`（`sealed`） | — |
| `GuideMgr` | `Module/GuideModule/GuideMgr.cs:10` | — |
| `FrameSpriteMgr` | `Module/FrameAnimModule/FrameSpriteMgr.cs:12`（`sealed`） | — |
| `TextConfigMgr` | `Module/TextModule/TextConfigMgr.cs:6` | — |
| `DataCenterSys` | `DataCenter/DataCenterSys.cs:8`（`partial`） | `IUpdate` |
| `GameClient` | `DataCenter/GameClient.cs:47`（`sealed`） | — |
| `ClientSaveDataMgr` | `DataCenter/ClientSaveData/ClientSaveDataMgr.cs:9` | — |
| `SoundConfigMgr` | `ConfigMgr/SoundConfigMgr.cs:6` | — |
| `ModelConfigMgr` | `ConfigMgr/ModelConfigMgr.cs:5` | — |
| `GuideConfigMgr` | `ConfigMgr/GuideConfigMgr.cs:9` | — |
| `ClientGM` | `UI/GMPanel/ClientGM.cs:13` | `IUpdate` |

---

## 四、DataCenterSys / DataCenterModule（第三套，数据中心专用）

- `IDataCenterModule`（`DataCenter/DataCenterModule.cs:6`），生命周期 `OnInit` / `OnRoleLogin` / `OnRoleLogout` / `OnUpdate` / `OnMainPlayerMapChange`（`:11-31`）。
- `public abstract class DataCenterModule<T> : IDataCenterModule where T : new()`（`:38`），`Instance` 简单懒加载（`:45`），全部虚方法空实现。
- 宿主 `DataCenterSys`（`DataCenter/DataCenterSys.cs:8`）持 `List<IDataCenterModule> m_dataCenterModuleList`（`:24`），`partial void InitModule()`（`:30`）由 **Roslyn 源生成器**填充。
- 生成器源码：`Tools/Generata Tools/SourceGenerator/DataCenterModuleGenerator/Generator/DataCenterModuleGenerator.cs`
  —— 扫描继承 `DataCenterModule` 的类（`:115-118`），生成 `DataCenterModule_Gen.g.cs`（`:54`），内含 `InitModule()` 逐个 `RegisterModule(Xxx.Instance)`（`:83`）与 `RegisterModule(IDataCenterModule)`（`:93-102`，去重 + `OnInit()` + `Add`）。
  编译期挂载的 DLL：`DataCenter/DataCenterModuleGenerator.dll`。
- **只有 Register，没有 Unregister。** `ClearClientData()`（`:91-101`）只是遍历调 `OnRoleLogout()`。
- **当前无任何 `DataCenterModule<T>` 子类**，`moduleTypes.Count > 0` 短路（`DataCenterModuleGenerator.cs:51`）→ 不产出任何代码，`InitModule()` 是无实现的 partial（调用点被编译期擦除）。这是**正确行为**，不是故障。
- 宿主 `DataCenterSys` 由 `GameStart.StartGame()`（`GameStart.cs:96-103`）的 `_ = DataCenterSys.Instance;` 激活 —— 这一行**不能删**，删了整套机制会静默失效（见隐患 4）。

### 新增一个 DataCenterModule 的约束

生成器是**纯语法字符串匹配**（`DataCenterModuleGenerator.cs:115-118` 的 `ToString().StartsWith("DataCenterModule")`），不做符号比较。所以：

| 必须 | 原因 |
|------|------|
| 放在 `GameLogic` asmdef 范围内 | 生成器就近挂载到 `GameLogic.asmdef` |
| 用**块作用域** `namespace GameLogic { }` | 文件作用域 `namespace GameLogic;` 是 `FileScopedNamespaceDeclarationSyntax`，`:34` 的 `OfType<NamespaceDeclarationSyntax>()` 匹配不到 → 整个文件被跳过 |
| 命名空间**精确等于** `GameLogic` | `:37` 是精确串比较，`GameLogic.XXX` 不匹配 |
| 基类写**不带命名空间限定**的 `DataCenterModule<Foo>` | 写成 `GameLogic.DataCenterModule<Foo>` 不命中 `StartsWith` |

---

## 五、三套的关系与关停顺序

```
ModuleSystem (DGame.Runtime, 跨 AOT/热更)  ← 唯一底座
    ├── 12 个框架模块
    ├── LocalizationModule / InputModule   ← 热更程序集，靠反射注册进来
    └── IMonoDriver
            ↑ 借用 Unity 生命周期
        SingletonSystem (GameLogic, 纯热更)
            └── DataCenterSys      ← 由 GameStart.StartGame() 激活
                    └── IDataCenterModule × N（当前 0 个）

GameModule 门面把三者混在一起对外：
    前 11 个属性 → ModuleSystem
    UIModule / RedDotModule / GuideModule → SingletonSystem
```

**关停顺序**（`GameStart.cs:105-111` 的 `OnDestroy`，经 `UnityUtil.AddDestroyListener`（`:32`）挂载）：

```
SingletonSystem.Destroy()
    → UIImageEffect.ClearCache()
        → GameModule.Destroy()
```

这里不调 `ModuleSystem.Destroy()` —— 框架层的销毁由 `RootModule.OnDestroy()` 负责（`RootModule.cs:277-284`，仅 `#if !UNITY_EDITOR`）。两条链路互相独立，销毁顺序由 Unity 决定，详见隐患 2。

---

## 六、热插拔能力：框架层不支持

逐关键词全库 `.cs` 核实：

| 关键词 | 结果 |
|--------|------|
| `Plugin` / `IPlugin` / `HotPlug` / 热插拔 | 零命中 |
| `AddModule` / `RemoveModule` / `UnloadModule` / `UnregisterModule` | 零命中 |
| `Shutdown` | 零命中 |
| `Module` 基类的 `Enable`/`Disable`/`IsEnabled`/`Active` | 不存在（`Module.cs:16-33` 全文仅 3 个成员） |

`ModuleSystem` 无按模块启停：轮询列表 `m_updateExecuteList` 只在 `m_isExecuteListDirty` 时整体重建（`:42-46`），脏标记只在 `RegisterUpdateModule` 里置 true（`:224`），**没有任何路径能把已创建的模块从轮询中摘出去**。`m_isDestroyed` 守卫只能整体停摆，不是模块级开关。

### 最接近"运行时启停"的三处（都不是模块级）

| 能力 | 位置 | 粒度 |
|------|------|------|
| `SingletonSystem.DestroySingleton(...)` | `SingletonSystem.cs:94 / :108`，移除逻辑 `:119-147` | **单例级，真正的运行时卸载** |
| `DebuggerModule.ActiveWindow { get; set; }` | `DebuggerModule.cs:8`，`Update` 早退 `:56-64`；`RegisterDebuggerWindow` `:13` / `UnRegisterDebuggerWindow` `:29` | 模块**内部**开关 + 调试窗口级增删 |
| `IMonoDriver.Add/RemoveXxxListener` | `MonoDriver/IMonoDriver.cs`，用例 `UnityUtil.cs:304,314`、`SingletonSystem.cs:258-265,283-291` | 回调级增删 |

子系统级"卸载"也有，但只针对模块内部资源、不是模块本身：
`ObjectPoolModule.DestroyObjectPool(...)`（`ObjectPoolModule.cs:575 / :594 / :619`）、`GameObjectPoolModule.DestroyPool(string)`（`:333`）、`DestroyAllPool(bool)`（`:341`）。

### 新增模块该选哪一套

| 需求 | 落位 |
|------|------|
| 常驻整个生命周期的框架能力（资源、音频、场景、计时） | `DGame/Runtime/Module/`，遵守 `IXxxModule`/`XxxModule` 命名约定 |
| **需要运行时启停 / 按玩法动态装卸** | `HotFix/GameLogic/Module/`，继承 `Singleton<T>`，按需实现 `IUpdate`，用 `DestroySingleton` 卸载 |
| 角色级数据缓存 + 登录登出生命周期 | `HotFix/GameLogic/DataCenter/`，继承 `DataCenterModule<T>`，源生成器自动注册 |

---

## 七、隐患修复记录

> 2026-09-02 核实发现 4 项，2026-09-03 修复。修复后 `ModuleSystem.cs` / `MonoDriver.cs` / `MainMonoBehaviour.cs` 的行号已变，本文行号均为**修复后**的。

### 已修复

#### 1. `MonoDriver` 销毁回调时序（🔴 曾影响 Release 包）

`MonoDriver.OnDestroy()`（`MonoDriver.cs:35-43`）调 `m_monoBehaviour?.Destroy()`（`:37`），后者原先直接把 `OnDestroyEvent = null`。而热更层唯一的清理入口 `GameStart.OnDestroy`（`GameStart.cs:105-111`）正挂在这个事件上（`GameStart.cs:32`）。

`ModuleSystem.Destroy()` 一旦先于 `[MonoDriver]` GameObject 销毁执行，`SingletonSystem.Destroy()` / `UIImageEffect.ClearCache()` / `GameModule.Destroy()` **全部被静默跳过**。Unity 不保证两个 DDOL 对象的 `OnDestroy` 顺序，所以这是顺序竞态，非必现。

**修法**：`MainMonoBehaviour.OnDestroy()`（`:34-40`）和 `Destroy()`（`:137-152`）都改为「取出 → 置空 → 触发」，两条路径共用同一个 `OnDestroyEvent`，先到者执行、后到者拿到 null，任何顺序下恰好一次。

> ⚠️ 注意 `?.` 对已销毁的 `UnityEngine.Object` 走 CLR 真 null 检查、**不走 Unity 的 `==` 重载**。所以不能只在清空前加一行 `Invoke()` —— GameObject 先销毁时 `m_monoBehaviour?.Destroy()` 仍会执行，会触发第二次。

#### 2. `RemoveXxxListener` 在销毁期复活驱动

7 个 `RemoveXxxListener` 原先都无条件先调 `_MakeDriver()`（`MonoDriver.cs:17-28`），而 `OnDestroy()` 已把 `m_monoDriver = null`（`:42`）→ 销毁期调用会 `new GameObject("[MonoDriver]")` + `DontDestroyOnLoad`（`:24-27`），泄漏并触发 Unity 的 `Did you spawn new GameObjects from OnDestroy?`。

实际触发路径：`SingletonSystem.DeInit()`（`SingletonSystem.cs:283-285`）。

**修法**：Remove 系列去掉 `_MakeDriver()`（移除监听不需要驱动）；**Add 系列保留**。

#### 3. `GetModule` 每次都走反射 + 销毁后可复活模块

两条写 `m_moduleMaps` 的路径 key 不一致：`CreateModule` 用具体类型（`:141`），`RegisterModule<T>` 用接口类型（`:168`），而 `GetModule<T>()` 的快路径按接口查（`:77-80`）。`RegisterModule<T>` 全项目无调用点 → 接口 key 永不存在 → 快路径是死分支，每次都付字符串拼接（`:89`）+ `Type.GetType`（`:91`）。

且 `Destroy()` 原先只 `Clear()` 不置标志，之后任何 `GetModule` 都会 `CreateModule` → `OnCreate()`，而 `MonoDriver.OnCreate()` 和 `GameObjectPoolModule.OnCreate()`（`GameObjectPoolModule.cs:31-40`）都会新建 DDOL GameObject。

**修法**：
- 反射成功后以接口类型建立别名索引（`:97-102`），后续命中快路径。
- 新增 `m_isDestroyed`（`:20`）/ `IsDestroyed`（`:26`），守卫加在 `Update`（`:37`）、`GetModule<T>`（`:82`）、`GetModule(Type)`（`:113`）、`RegisterModule<T>`（`:162`）；`Destroy()` 末尾置位（`:250`）。命中时 `DLogger.Warning` + 返回 null。
- `RegisterModule<T>` 同时写具体类型 key（`:170`），与 `CreateModule` 保持一致。

> 守卫返回 null 是安全的：`GameModule.GetModule<T>()`（`GameModule.cs:134-139`）用 `DLogger.Assert` 只打断言；`AssetReference.CheckInit()`（`AssetReference.cs:36-49`）用静态字段缓存，正常运行过就直接 return，即使走到也有显式 null 检查抛明确异常，不会 NRE。

#### 4. DataCenter 宿主从未激活

生成器链路本身完全正常（meta 的 `RoslynAnalyzer` label、asmdef 就近挂载、HybridCLR 兼容性均已核实）。断点在运行期：`InitModule()` 只由 `DataCenterSys.OnInit()` 调用，而 `OnInit()` 由 `Singleton<T>.Instance` 首次访问触发 —— 启动流程从不碰它，全项目只有 `ClientSaveDataMgr.cs:86` 一处访问且在条件分支里。

**修法**：`GameStart.StartGame()`（`GameStart.cs:96-103`）加 `_ = DataCenterSys.Instance;`。

同时修了生成器两个缺陷（`DataCenterModuleGenerator.cs`）：生成端多余的 `sealed` 导致 `DataCenterSys` 的 API 表面随生成结果漂移；`namespaceName` 收集了却丢弃、生成裸类名。现已改为 `global::` 全限定。

### 仍然存在（未修）

#### A. 编辑器下 `ModuleSystem.Destroy()` 不执行

唯一调用点 `RootModule.cs:277-284` 的 `#if !UNITY_EDITOR` **有意保留**。

理由：`EditorSettings.asset:27` 的 `m_EnterPlayModeOptionsEnabled: 0` 说明 Domain Reload 开着，静态字段每次进 PlayMode 都重置，不存在跨 PlayMode 脏状态。打开它的收益只剩退出时清理 `System.Timers.Timer`（跑在线程池上）和 `Marshal.FreeCachedHGlobal`，而成本是要先处理下面的 B。

想打开需先满足的前置条件：`AssetReference.OnDestroy`（`AssetReference.cs:89-104`）需要容错，见 B。

#### B. `AssetReference.OnDestroy` 在模块销毁后会抛异常

`ObjectPoolModule.OnDestroy` 清空 `m_poolObjectsMap` 后，残留的 `AssetReference` 销毁时走 `UnloadAsset` → `ObjectPool.Recycle` → `throw DGameException("对象池中无此对象")`（`ObjectPool.cs:220-221`）。

目前只在 Release 包退出时发生（编辑器下 A 挡住了）。影响限于退出期日志噪音。

#### C. `ObjectPoolModule.OnDestroy` 行为不确定

`m_cachedAllObjectPools` 只在 `ReleaseAllUnused()` / `Release()` 里通过 `GetAllObjectPools(true, ...)` 刷新（`ObjectPoolModule.cs:635,646`），创建池时**不入列**。所以 `OnDestroy`（`:37-40`）是否真的销毁池子，取决于运行期是否调过 `ReleaseAllUnused`。`Update()`（`:46-52`）遍历同一个 list，池的自动过期回收同样受影响。

#### D. `SceneModule.OnDestroy` 的无效句柄警告

遍历 `m_subScenes` 调 `sceneHandle?.UnloadAsync()`（`SceneModule.cs:31-34`），YooAsset 在 handle 失效时只打 `SceneHandle is invalid.` 警告不抛（`SceneHandle.cs:124-130`）。噪音，无功能影响。

#### E. 生成器的两个非阻断缺陷

`Definition.UsingNameSpace` 声明未使用；`RegisterModule` 本身是生成物，存在鸡生蛋问题 —— 无法手写调用来注册第一个模块，第一个必须走生成器路径。

---

## 常见错误

| 错误 | 正确 | 原因 |
|------|------|------|
| `ModuleSystem.GetModule<ResourceModule>()` | `ModuleSystem.GetModule<IResourceModule>()` | 泛型参数必须是接口，传具体类抛 `DGameException`（`:72-75`） |
| 业务层散落 `ModuleSystem.GetModule<T>()` | `GameModule.XXX` | 门面有静态缓存；`ModuleSystem` 侧现已有接口别名索引，但门面仍是约定入口 |
| 新框架模块起名 `XxxManager` + `IXxxModule` | `XxxModule` + `IXxxModule` | `Type.GetType` 按 `IXxx` → `Xxx` 反推，名字不匹配直接创建失败 |
| 框架模块跨程序集放（接口在 Runtime、实现在 GameLogic） | 接口与实现同命名空间同程序集 | 反推用的是 `type.Assembly.GetName().Name` |
| 手动 `new SomeSingleton()` | `SomeSingleton.Instance` | `Singleton<T>` 构造函数有 `StackTrace` 校验（`Singleton.cs:33-42`） |
| 指望 `GameModule.Destroy()` 销毁模块 | 它只清缓存字段；真正销毁是 `ModuleSystem.Destroy()` | `GameModule.cs:141-160` |
| 把 `BaseObjectPool.Priority` 当模块优先级 | 那是**对象池**优先级 | `ObjectPool.cs:95` `public override int Priority { get; set; }`，与 `Module.Priority` 无关 |
| 把 `RootModule` 当 `Module` 子类 | 它是 `MonoBehaviour`，负责驱动 ModuleSystem | `RootModule.cs:9` |
| 期望框架模块能运行时禁用 | 框架层不支持；改用热更层 `Singleton<T>` | `Module` 基类无开关成员 |

---

## 交叉引用

| 关联主题 | 文档 | 说明 |
|---------|------|------|
| 模块 API 用法 | [modules.md](modules.md) | 各模块具体方法签名与使用模式 |
| 分层与程序集 | [architecture.md](architecture.md) | 热更边界、依赖方向、启动流程 |
| 热更代码边界 | [hotfix-workflow.md](hotfix-workflow.md) | 为何热更模块能被 ModuleSystem 反射到 |
| UI 模块生命周期 | [ui-lifecycle.md](ui-lifecycle.md) | `UIModule` 作为 `Singleton<T>` 的窗口管理 |
| 红点模块 | [reddot-system.md](reddot-system.md) | `RedDotModule` 红点树注册与刷新 |
