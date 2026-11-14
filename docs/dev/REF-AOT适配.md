# REF-AOT适配（Native AOT 适配事实）

> 何时读：要 AOT 发布 / 改打包参数，或遇到 AOT 下绑定读不到值、集合跨 ABI 报错、类型判定静默失败、反射 JSON 被禁、切页内存只涨不落时。
> 管什么：AOT 下必须这么写的事实与实测数值；AOT 是旁路可选项，csproj 故意没有 `PublishAot`，一律命令行 `/p:PublishAot=true`。
> 不写什么：排查过程与历史在 git；AOT 验收流程见 `doc/PREFERENCES.md`。相关文件在各节点名。

## 一、绑定成员必须手写
- 被 XAML 绑定的成员一律手写普通属性 / 命令（`set => SetProperty(...)`），源生成的 `[ObservableProperty]` / `[RelayCommand]` 不生效；16 个绑定源类已加 `[WinRT.GeneratedBindableCustomProperty]`，`ViewModels/` 已归零。后果：不报错、属性恒为默认值。

## 二、集合属性声明 IList<T>
- 属性声明类型写 `IList<T>`（WinRT 内建映射 `IVector<T>`），字段仍用 `ObservableCollection<T>`。后果：否则 `ItemsSource` 赋值抛 `E_INVALIDARG` 崩溃（闭合泛型没进 `WinRTGlobalVtableLookup.g.cs`）。已改：`SearchResults` / `LibraryItems` / `Games` / `VisibleRows` / `Items` / `Bindings` / `LocalItems` / `VisibleSources`。

## 三、XAML 对象转投影类型失败
- 需要转换时用 `WinRT.CastExtensions.As<T>()`；只判存在性时只判 null、不做 `is` / `as`。后果：硬转抛 `InvalidCastException`，`is` / `as` 静默判负走 else（表现为功能没反应）。实例：`Resources["MoreMenu"]`、`card.RenderTransform is ScaleTransform`（已改成只判 null + `??=`）。

## 四、System.Text.Json 必须源生成
- 调用点改成带 `JsonTypeInfo` 的重载，options 精确复刻改造前那套（缩进 / 命名策略 / encoder 都不能变）：`AppJsonContext`（三套 options）与 `NoSteamJsonContext`（Bypass 规则），属性表达不了的走 `new XxxContext(options)` 实例模式。后果：否则 `IL2026` + `IL3050` 运行时可能抛异常；`Dictionary<string, object>` 装匿名类型会 `NotSupportedException`，须换显式模型类；落盘 UTF-8、不带 BOM。

## 五、绑定集合的通知到不了控件
- 搜索完成后显式给控件一份新 `List<T>`（`ItemsSource = VM.SearchResults.ToList()`），每次搜索都赋一次，绝不能赋 null。后果：AOT 下 `ObservableCollection<T>` 到 `IVector<T>` 订阅不到 `CollectionChanged`，控件 `Items` 恒 0；赋 null 实测崩在 `CoreMessagingXP.dll`（`0xc000027b`），`Items.Add` 在 `ItemsSource` 非空时也会抛。

## 六、日志面板的 LOH churn
- 页面订阅搬进 `Loaded` / `Unloaded` 成对（先 `-=` 再 `+=`），刷新节流 100ms（`DispatcherQueueTimer` + 脏标志，刷完即停表）。后果：每行 `string.Join` 全量重拼（≤1000 行 ≈ 120 KB > 85 KB 进 LOH，LOH 不压缩）导致卡顿、内存只涨不落。

## 七、Marshal 泛型重载与 DAM
- 改泛型重载 `Marshal.PtrToStructure<T>(nint)` / `Marshal.GetDelegateForFunctionPointer<TDelegate>(nint)`，包装方法泛型参数加 `[DynamicallyAccessedMembers]` 且一次给全 `PublicConstructors | NonPublicConstructors`，委托约束补 `where TDelegate : Delegate`。后果：否则 `IL3050`（只给前者仍报 `IL2091`）；57 个调用点都是具体委托类型，无需改。

## 八、内存基线与压法
- AOT 启动 49 MB 工作集（JIT 109 MB），切页 20 次后 254 / 273 MB，发布体积 96 / 191 MB；JIT 基线 ~128 MB 私有提交 / ~186 MB 工作集，已压四刀（Sudama 流式取键 ~110 MB → +6.1 MB；manifest 流式落盘；删 5 秒一次全库扫描；峰值后 `OstMemory.CompactAfterLargeBuffers()` 治标）。`GCConserveMemory` 各档绝对峰值一致、压不动，`HeapHardLimit*` / `InvariantGlobalization` / `ConcurrentGC=false` 不建议动；IL2026 / IL3050 / IL2091 全 0，仅剩 `WebView2.Core.Projection` 的 IL2104 / IL3053 各 1；AOT 产物没有 runtimeconfig.json（`IsDynamicCodeSupported=false`、`IsReflectionEnabledByDefault=false`）。
