# DGame 模块系统架构

> **适用场景**：理解框架有哪些模块、模块如何注册与销毁、能否运行时启停（热插拔）、新模块该落在哪一套机制里 | **关联文档**：[modules.md](modules.md)（模块 API 速查）、[architecture.md](architecture.md)（分层与程序集）
>
> **核实基准**：Unity 6000.3.10f1，分支 `Unity6000.3`，核实日期 2026-09-02。本文所有行号均经源码逐条核实；若与当前源码不符，以源码为准并回头修正本文。

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

`ModuleSystem.cs:9` 是 `public static class`，非单例、非 MonoBehaviour，全文仅 208 行。

### 容器与轮询

`ModuleSystem.cs:15-18`：

| 字段 | 类型 | 用途 |
|------|------|------|
| `m_moduleMaps` | `Dictionary<Type, Module>` | 类型 → 实例（初始容量 `DEFAULT_MODULE_COUNT` = 16，`:14`） |
| `m_modules` | `LinkedList<Module>` | 全部模块，按 `Priority` **降序**插入 |
| `m_updateModules` | `LinkedList<Module>` | 仅实现 `IUpdateModule` 的模块 |
| `m_updateExecuteList` | `List<IUpdateModule>` | 实际轮询列表，脏标记重建（`:30-34`、`:42-50`） |

驱动来自 `RootModule.cs:261-265`（`MonoBehaviour`）→ `ModuleSystem.Update(GameTime.DeltaTime, GameTime.UnscaledDeltaTime)`。

### 获取模块：反射 + 命名约定

```csharp
GameModule.ResourceModule                      // 业务层标准写法
ModuleSystem.GetModule<IResourceModule>()      // 框架层 / 未进门面的模块
```

- 泛型参数**必须是接口**，否则抛 `DGameException`（`:60-63`）。
- 类型名由接口名反推（`:71`）：`$"{type.Namespace}.{type.Name.Substring(1)}, {type.Assembly.GetName().Name}"` → `Type.GetType`（`:73`）。
  即约定 **`IXxxModule` → 同命名空间、同程序集的 `XxxModule`**。
- 惰性创建（`:83`、`:96-98`）：`Activator.CreateInstance(moduleType) as Module`。

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
ModuleSystem.Destroy()      // ModuleSystem.cs:190-206
```

- 从 `m_modules.Last` **反向**遍历（低优先级先销毁）调 `OnDestroy()`（`:193-198`）。
- 清空全部 4 个容器（`:200-203`）。
- 额外调 `MemoryPool.ClearAll()`（`:204`）与 `Utility.Marshal.FreeCachedHGlobal()`（`:205`）。

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
- **当前无任何 `DataCenterModule<T>` 子类**，生成器不产出注册代码，`InitModule()` 是空 partial。这套机制目前空跑。

---

## 五、三套的关系与关停顺序

```
ModuleSystem (DGame.Runtime, 跨 AOT/热更)  ← 唯一底座
    ├── 12 个框架模块
    ├── LocalizationModule / InputModule   ← 热更程序集，靠反射注册进来
    └── IMonoDriver
            ↑ 借用 Unity 生命周期
        SingletonSystem (GameLogic, 纯热更)
            └── DataCenterSys
                    └── IDataCenterModule × N（当前 0 个）

GameModule 门面把三者混在一起对外：
    前 11 个属性 → ModuleSystem
    UIModule / RedDotModule / GuideModule → SingletonSystem
```

**关停顺序**（`GameStart.cs:101-107` 的 `OnDestroy`，经 `UnityUtil.AddDestroyListener`（`:32`）挂载）：

```
SingletonSystem.Destroy()
    → UIImageEffect.ClearCache()
        → GameModule.Destroy()
```

⚠️ 这里**没有调 `ModuleSystem.Destroy()`**，详见下方隐患 3。

---

## 六、热插拔能力：框架层不支持

逐关键词全库 `.cs` 核实：

| 关键词 | 结果 |
|--------|------|
| `Plugin` / `IPlugin` / `HotPlug` / 热插拔 | 零命中 |
| `AddModule` / `RemoveModule` / `UnloadModule` / `UnregisterModule` | 零命中 |
| `Shutdown` | 零命中 |
| `Module` 基类的 `Enable`/`Disable`/`IsEnabled`/`Active` | 不存在（`Module.cs:16-33` 全文仅 3 个成员） |

`ModuleSystem` 无按模块启停：轮询列表 `m_updateExecuteList` 只在 `m_isExecuteListDirty` 时整体重建（`:30-34`），脏标记只在 `RegisterUpdateModule` 里置 true（`:180`），**没有任何路径能把已创建的模块从轮询中摘出去**。

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

## 七、已知隐患（2026-09-02 核实，尚未修复）

### 1. `ModuleSystem` 的缓存快路径是死分支

`RegisterModule<T>(Module)`（`:117`）按**接口 Type** 写 key（`:126`），但**全项目无任何调用点**；`CreateModule` 按**具体类型**写 key（`:105`）。
结果 `:65-68` 的接口查表永远 miss → 每次 `GetModule<T>()` 都要付一次字符串拼接 + `Type.GetType` 反射，再由 `:83` 用具体类型命中缓存。

实例单例性是正确的，只是白付反射开销。业务侧靠 `GameModule` 的静态字段缓存规避了；直接调 `ModuleSystem.GetModule<T>()` 的地方没有。

> 另注：`RegisterUpdateModule(Module)`（`:135`）名字有误导 —— 它对**所有**模块做优先级插入（`:137-155`），再判断是否 `IUpdateModule` 决定是否插入 update 链（`:157-181`），最后调 `module.OnCreate()`（`:182`）。

### 2. 编辑器下 `ModuleSystem.Destroy()` 永不执行

唯一调用点 `RootModule.cs:277-284` 被 `#if !UNITY_EDITOR` 包住。编辑器多次进出 PlayMode 时，模块的 `OnDestroy()` 不会走。

### 3. 热更重启会漏模块

`GameStart.cs:101-107` 只调了 `SingletonSystem.Destroy()` 和 `GameModule.Destroy()`，而后者只把静态缓存置 null。
属于热更程序集的 `LocalizationModule` / `InputModule` 实例会残留在 `ModuleSystem.m_moduleMaps` 里。

### 4. `DataCenterModule<T>` 目前零子类

源生成器扫不到东西，`InitModule()` 是空 partial。新增数据中心时注意这套链路尚未被真实验证过。

---

## 常见错误

| 错误 | 正确 | 原因 |
|------|------|------|
| `ModuleSystem.GetModule<ResourceModule>()` | `ModuleSystem.GetModule<IResourceModule>()` | 泛型参数必须是接口，传具体类抛 `DGameException`（`:60-63`） |
| 业务层散落 `ModuleSystem.GetModule<T>()` | `GameModule.XXX` | 门面有静态缓存，可规避每次反射（见隐患 1） |
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
