# REF-AOT适配（Native AOT 适配事实）

> **读它的时机**：要做 AOT 发布 / 改打包参数，或遇到"AOT 下绑定读不到值、集合跨 ABI 报错、
> 类型判定静默失败、反射 JSON 被禁、切页内存只涨不落"这类怪象时。
> 只收"Native AOT 下必须这么写"的事实与实测数值；排查过程不写（历史在 git）。
> AOT 是**旁路可选项**：csproj 里**故意没有** `PublishAot`，一律命令行传参 `/p:PublishAot=true`。
> 相关文件：`main/Services/AppJsonContext.cs`、`NoSteamLauncher/Services/NoSteamJsonContext.cs`、`main/SteamApi/*`。

## 一、绑定提供器看不到源生成成员

- 症状：AOT 下 XAML `{Binding}` 读不到值，且**不报错**（属性恒为默认值）。
- 为什么：CsWinRT 绑定提供器按类型元数据枚举成员，而 `[ObservableProperty]` / `[RelayCommand]`
  是**源生成**成员，提供器看不到。
- 怎么办：被 XAML 绑定的成员**一律手写**成普通属性/命令（`set => SetProperty(...)`）；`ViewModels/` 已归零。
- 范围：判定依据是 XAML 里的 `x:DataType` 与模板 `{Binding}` —— `Models/`、`Services/` 里被绑定的类型同样适用；
  16 个绑定源类已加 `[WinRT.GeneratedBindableCustomProperty]`。

## 二、绑定集合属性声明成 IList<T>

- 症状：AOT 下给 `ItemsSource` 传 `ObservableCollection<T>` 抛 `E_INVALIDARG`（崩溃）。
- 为什么：该闭合泛型没进生成的 `WinRTGlobalVtableLookup.g.cs`，跨 ABI 查不到 vtable。
- 怎么办：属性**声明类型写 `IList<T>`**（WinRT 内建映射 = `IVector<T>`），字段仍保持 `ObservableCollection<T>`。
- 已改：`SearchResults` / `LibraryItems` / `Games` / `VisibleRows` / `Items` / `Bindings` /
  `LocalItems` / `VisibleSources`。

## 三、XAML 侧对象 → CLR 投影类型：转换会失败

- 症状：硬转抛 `InvalidCastException`；`is` / `as` **静默判负**（不抛异常、走 else 分支，表现为"某功能没反应"）。
- 为什么：XAML 侧创建的对象与 CLR 投影类型之间的 vtable 查表在 AOT 下不匹配（CsWinRT #2516 / #2475 / #2536）。
- 怎么办：需要转换时用 `WinRT.CastExtensions.As<T>()`；只需判存在性时**只判 null**，不做类型判定。
- 实例：取 `Resources["MoreMenu"]`、`card.RenderTransform is ScaleTransform`（后者改成只判 null + `??=`）。

## 四、System.Text.Json 必须源生成

- 症状：`IL2026` + `IL3050`；AOT 下反射序列化被禁用，运行时可能直接抛异常。
- 为什么：反射重载带 `RequiresUnreferencedCode` / `RequiresDynamicCode`。
- 怎么办：调用点改成带 `JsonTypeInfo` 的重载；options **精确复刻**改造前那套（缩进 / 命名策略 / encoder 一个都不能变）。
- 两处 context：`main/Services/AppJsonContext.cs`（甲/乙/丙三套 options）、
  `NoSteamLauncher/Services/NoSteamJsonContext.cs`（Bypass 规则）。
- 属性表达不了的一律走 `new XxxContext(options)` 实例模式（static 缓存一次），例如自定义 encoder、
  `PropertyNameCaseInsensitive`、`DefaultIgnoreCondition = WhenWritingNull`。
- 匿名类型值无法源生成：`Dictionary<string, object>` 装匿名类型 ⇒ 运行时解析不到类型抛 `NotSupportedException`
  ⇒ 必须换成显式模型类（键名键序对齐）。
- 验证手法：改造前后各产一份真实数据文件，`SHA256` + `fc /b` 逐字节比对
  （Bypass JSON 6206 字节一致；9 份 OSTGUI JSON 往返一致）。
- 落盘一律 UTF-8、`File.WriteAllText` 不带 BOM。

## 五、绑定集合的通知到不了控件

- 症状：VM 侧 `Count=10` 对、控件 `ItemsSource` 非空且**就是同一个实例**、但控件 `Items` 恒 0
  （界面空白、切列表/网格都一样）。
- 为什么：AOT 下 `ObservableCollection<T>` → `IVector<T>` 这条桥**订阅不到 `CollectionChanged`**，
  控件拿到集合后从不重新枚举。
- 怎么办：搜索完成后显式给控件一份 `List<T>`（`ItemsSource = VM.SearchResults.ToList()`），
  **每次搜索都赋一次**（新实例 ⇒ DP 值真变 ⇒ 重新枚举）。
- 绝不能 `ItemsSource = null`：实测崩在 `CoreMessagingXP.dll`、stowed exception `0xc000027b`；
  `Items.Add` 在 `ItemsSource` 非空时也会抛。
- 对照事实：入库管理页"加载时赋一次、之后不换"因此一直正常；搜索页每次换结果才暴露这条。

## 六、日志面板的 LOH churn

- 症状：日志一多就卡顿、内存只涨不落；每次切页涨一截（切页会写日志）。
- 为什么：`SettingsPage` 每来一行日志就 `string.Join` 全量重拼（≤1000 行 ≈ 60K 字符 ≈ 120KB > 85KB
  ⇒ 每次都进 LOH，LOH 不压缩）。
- 怎么办：页面订阅搬进 `Loaded` / `Unloaded` 成对（**先 `-=` 再 `+=`**；页面被 Frame 缓存会反复
  `Loaded`，不退订就重复订阅）；刷新节流 100ms（`DispatcherQueueTimer` + 脏标志，刷完即停表）。
- 为什么必须搬：事件源是静态/单例（`LogService.Logs`、单例 VM），在构造函数里订阅 = 静态对象永久钉住页面实例。
- 内存观测：`App` 启动、每次 `MainWindow.GoToPage`、每次入库/成就刷新各记一行 `内存：`
  （工作集 / 托管堆 / 已提交 / 累计分配 / Gen0 / Gen2）。

## 七、Marshal 泛型重载 + DAM 标注

- 症状：`IL3050`（`Marshal.PtrToStructure(nint, Type)`、`Marshal.GetDelegateForFunctionPointer(nint, Type)`）。
- 怎么办：改泛型重载 `Marshal.PtrToStructure<T>(nint)` / `Marshal.GetDelegateForFunctionPointer<TDelegate>(nint)`。
- 连带：泛型重载要求 `T` 声明构造器可见性 ⇒ 包装方法的泛型参数加 `[DynamicallyAccessedMembers(...)]`；
  成员范围按 ILC 原文一次给全 `PublicConstructors | NonPublicConstructors`（只给前者覆盖不住，`IL2091` 仍在）。
- 委托约束：`where TDelegate : Delegate` 必须补（否则返回类型转不回 `Delegate`）；
  57 个调用点都是具体委托类型，无需改调用点。

## 实测收益（本机，2026-09-27）

- 启动工作集：AOT **49 MB** vs JIT **109 MB**；切页 20 次后：**254 MB** vs **273 MB**；
  发布体积：**96 MB** vs **191 MB**。
- 发布产物：`IL2026` / `IL3050` / `IL2091` **全为 0**；仅剩第三方
  `Microsoft.Web.WebView2.Core.Projection` 的 `IL2104` + `IL3053` 各 1 条。
- 运行时事实：`RuntimeFeature.IsDynamicCodeSupported=false`、
  `JsonSerializer.IsReflectionEnabledByDefault=false`；AOT 产物**没有** `OSTGUI.runtimeconfig.json`
  （配置在发布时烧进 exe，GC 模式只能运行时读）。
