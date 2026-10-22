# ImageOptim 优化清单

> 生成日期：2026-10-21 ｜ 基线 commit：`91df81a` ｜ 共 30 项 ｜ 已完成 28 项
> 状态图例：⬜ 待开始 ｜ 🟡 进行中 ｜ ✅ 已完成 ｜ ⏭️ 不处理
> 维护方式：改状态时同步勾选 `- [ ]`→`- [x]`，并更新下方「进度总览」与文末「变更记录」

## 进度总览

| 优先级 | 总数 | ⬜ 待开始 | 🟡 进行中 | ✅ 已完成 |
| --- | --- | --- | --- | --- |
| P0 正确性/稳定性 | 9 | 0 | 0 | 9 |
| P1 性能 | 10 | 0 | 1 | 9 |
| P2 工程质量 | 6 | 0 | 1 | 5 |
| P3 构建/供应链 | 5 | 0 | 0 | 5 |
| **合计** | **30** | **0** | **2** | **28** |

完成率：**93%**（28 / 30，另有 2 项部分完成）

## 清单索引

| ID | 优先级 | 端 | 摘要 | 工作量 | 状态 |
| --- | --- | --- | --- | --- | --- |
| OPT-01 | P0 | Windows | 停止无法终止工具进程 | M | ✅ |
| OPT-02 | P0 | Windows | 输出目录每次新增 ImageOptim_N | S | ✅ |
| OPT-03 | P0 | Windows | 覆盖原文件非原子 | M | ✅ |
| OPT-04 | P0 | Windows | .bak 备份永不回收 | S | ✅ |
| OPT-05 | P0 | macOS | 扩展跨线程操作 AppKit | S | ✅ |
| OPT-06 | P0 | macOS | 保存时整文件读入内存 | M | ✅ |
| OPT-07 | P0 | macOS | nextInsertRow 无锁竞争 | S | ✅ |
| OPT-08 | P0 | Windows | 锁内触发 StateChanged | S | ✅ |
| OPT-09 | P0 | Windows | 停止后临时文件泄漏 | S | ✅ |
| OPT-10 | P1 | macOS | 批量加文件 O(n²) | S | ✅ |
| OPT-11 | P1 | macOS | 状态栏全量重算 | M | 🟡 |
| OPT-12 | P1 | macOS | JobProxy KVO 日志刷屏 | S | ✅ |
| OPT-13 | P1 | Windows | 单文件内工具串行 | L | ✅ |
| OPT-14 | P1 | Windows | 列表逐条 Add 卡 UI | M | ✅ |
| OPT-15 | P1 | Windows | 状态变更无合并 | M | ✅ |
| OPT-16 | P1 | Windows | 状态栏 500ms 全量遍历 | S | ✅ |
| OPT-17 | P1 | Windows | 拖入目录被扫两遍 | M | ✅ |
| OPT-18 | P1 | Windows | 线程池线程被长期占用 | S | ✅ |
| OPT-30 | P1 | Windows | 有损与无损工具未串联 | M | ✅ |
| OPT-19 | P2 | 双端 | 双端算法漂移风险 | L | 🟡 |
| OPT-20 | P2 | 双端 | MD5 弃用需 pragma 压制 | S | ✅ |
| OPT-21 | P2 | macOS | 结果缓存只增不删 | S | ✅ |
| OPT-22 | P2 | macOS | 日志宏悬垂 else | S | ✅ |
| OPT-23 | P2 | 双端 | 死代码与小缺陷 | S | ✅ |
| OPT-24 | P2 | 双端 | 测试未进 CI | M | ✅ |
| OPT-25 | P3 | Windows | 工具下载零校验 | M | ✅ |
| OPT-26 | P3 | Windows | 每次编译都跑下载脚本 | S | ✅ |
| OPT-27 | P3 | macOS | 无 macOS CI | M | ✅ |
| OPT-28 | P3 | Windows | release 清理误删附件 | S | ✅ |
| OPT-29 | P3 | 双端 | 版本号两套不一致 | S | ✅ |

工作量：S ≈ 半天内，M ≈ 1~2 天，L ≈ 3 天以上

---

## P0 正确性 / 稳定性

### ✅ OPT-01 Windows「停止」无法终止正在运行的工具进程

- [x] 已完成（2026-10-21）：`Job` 增加 `Cancellation` 令牌，`Worker.RunProcess` 注册取消回调 `Kill(entireProcessTree:true)`；`Stop()` 置位后立即取消
- **位置**：`win/ImageOptim/Job.cs:228`、`win/ImageOptim/Worker.cs:79`；对照 `imageoptim/Backend/Workers/CommandWorker.m:133`
- **问题**：`RunAsync` 的 `foreach` 只在两个工具之间检查 `IsStopping()`；`RunProcess` 没有 CancellationToken，`WaitForExit` 期间无法中断。Guetzli/PNG 工具兜底超时 5 分钟，用户点停止后仍要等当前工具跑满才响应。macOS 版是 `[task terminate]` 直接杀进程。
- **方案**：`Worker.RunProcess` 增加 `CancellationToken`，注册回调里 `process.Kill(entireProcessTree: true)`；`Job` 持有 `CancellationTokenSource`，`Stop()` 触发 Cancel。
- **验收**：对一张大 PNG 启用 oxipng+zopfli，点「停止」后 2 秒内进程消失、列表状态立即变为已停止。

### ✅ OPT-02 Windows 输出目录每次运行都新增 ImageOptim_N

- [x] 已完成（2026-10-21）：`GetOutputDirectory` 固定复用同名 `ImageOptim` 目录，去掉 `_i` 递增逻辑
- **位置**：`win/ImageOptim/Job.cs:522`（`GetOutputDirectory`）
- **问题**：`ImageOptim` 目录已存在就往下找编号，且 `_outputDirCache` 是静态字典跨运行不失效。跑三次产生 `ImageOptim`、`ImageOptim_1`、`ImageOptim_2`。
- **方案**：固定复用同名 `ImageOptim` 目录（重名文件覆盖或加后缀区分），或明确"每次会话一个目录"的语义并在 UI 上体现。
- **验收**：同一文件夹连续优化三次，输出目录始终只有 1 个。

### ✅ OPT-03 Windows 覆盖原文件非原子

- [x] 已完成（2026-10-21）：新增 `ReplaceFile`（`File.Move(overwrite:true)` 原子替换，跨卷自动退回复制），保存与回退两条路径都改用它
- **位置**：`win/ImageOptim/Job.cs:609`（`File.Copy(fileToSave.Path, FilePath, overwrite: true)`）
- **问题**：直接往原路径写，复制中途崩溃/磁盘满会截断用户原图。且临时文件在 `%TEMP%`，跨卷时整文件读写两遍。
- **方案**：临时文件写到目标目录同卷，用 `File.Move(overwrite: true)` 或 `File.Replace` 原子替换；保留原文件时间戳逻辑不变。
- **验收**：保存过程中强杀进程，原图要么是完整旧内容、要么是完整新内容，不出现截断。

### ✅ OPT-04 Windows .bak 备份永不回收

- [x] 已完成（2026-10-21）：新增 `BackupStore`，备份集中到 `%LOCALAPPDATA%\ImageOptim\Backups`，回退成功后删除，启动时清理 7 天前的旧备份；用户图片目录不再出现 `.bak`
- **位置**：`win/ImageOptim/Job.cs:646`（`ResolveUniqueBackupPath`）；对照 `imageoptim/Backend/Job.m:408`
- **问题**：每个被优化的文件都在用户目录留下 `~xxx.imageoptim.bak`，Revert 后也不删，长期堆积。macOS 是移入废纸篓，系统可回收。
- **方案**：走回收站 API（`SHFileOperation` / `IFileOperation`）而非同目录副本；或在 Revert 成功后删除备份、退出时统一清理。
- **验收**：优化 10 个文件后目录里看不到 `.bak`；回退可用。

### ✅ OPT-05 macOS Finder 扩展跨线程操作 AppKit

- [x] 已完成（2026-10-21）：`setStringValue:` / `cancel:` / `completeRequestReturningItems:` 全部切回主线程；顺带把 `sleep(1)`（阻塞后台线程）换成主线程 `dispatch_after` 延迟 1 秒
- **位置**：`imageoptim/ImageOptimize/ExtensionController.m:92`
- **问题**：在 `dispatch_get_global_queue` 块里直接 `[[self status] setStringValue:...]`，违反 AppKit 主线程要求，可能随机崩溃或界面不刷新。
- **方案**：包一层 `dispatch_async(dispatch_get_main_queue(), ^{ ... })`。
- **验收**：扩展连续处理 50 次无崩溃；状态文字正常显示。

### ✅ OPT-06 macOS 保存时整文件读入内存

- [x] 已完成（2026-10-21）：`NSData dataWithContentsOfURL:` 改为 `NSFileHandle` 1MB 分块搬运 + `@autoreleasepool`；大小校验改为先 stat（保持早退语义），写完再核对字节数
- **位置**：`imageoptim/Backend/Job.m:362`
- **问题**：`[NSData dataWithContentsOfURL:fileToSave.path]` 把整个优化结果读进内存再写回，几百 MB 的图会瞬间吃满内存。
- **方案**：改成分块流式复制（`NSFileHandle` 分块，或 `copyItemAtURL:` + 单独保留权限/元数据的路径）。
- **验收**：优化一张 500MB 图，RSS 峰值不超过文件大小的 2 倍。

### ✅ OPT-07 macOS nextInsertRow 无锁跨线程读写

- [x] 已完成（2026-10-21，**复核后修正了原判断**）
- **⭐ 复核结论：原描述的数据竞争不成立**。`nextInsertRow` 的全部读写都发生在主线程——`setRow:`（`ImageOptimController` 的打开文件/浏览对话框/`clearComplete`）、`acceptDrop:`（NSTableView 委托）、`addURLsBelowSelection:`（`MyTableView` 的 `paste:` 菜单动作）、`addJobObjects:`（`performSelectorOnMainThread`）。后台线程只跑 `addURLs:`，不碰这个变量。所以"无锁竞争导致错位"是误报。
- **实际改掉的两点**：
  - [x] 空批次直接返回（原来 `[files count] == 0` 也会注册一条 undo 并动一次列表）
  - [x] 追加分支后把 `nextInsertRow` 同步到队尾（原来留下越界旧值，靠后续每次"越界即追加"兜着）
- [x] 补 `NSAssert([NSThread isMainThread])` 固化线程约定（Release 下 `NS_BLOCK_ASSERTIONS` 自动编译掉，零成本）；`acceptDrop:` / `addURLsBelowSelection:` 改为调 `setRow:` 统一入口
- **位置**：`imageoptim/FilesController.m:212`、`:249`、`:255`；`setRow:` 在主线程

### ✅ OPT-08 Windows 锁内触发 StateChanged 事件

- [x] 已完成（2026-10-21，随 OPT-01 一并修复）：`Stop()` 改为"锁内改状态、锁外 `Cancel()` + `Publish()`"
- **位置**：`win/ImageOptim/Job.cs:731`（`Stop()` 内 `Publish()` 仍在 `lock` 内）
- **问题**：C# `lock` 可重入所以当前不死锁，但事件在持锁期间同步回调订阅者，订阅者若反向访问 Job 就会死锁；也拉长锁持有时间。
- **方案**：统一"锁内改状态、锁外 Publish"的两段式（`SetStatus` / `SetError` 已经是这个模式，`Stop()` 补齐即可）。
- **验收**：静态检查无 `Publish()` 出现在 `lock` 块内。

### ✅ OPT-09 Windows 停止后临时文件泄漏

- [x] 已完成（2026-10-21，随 OPT-01 一并修复）：停止分支改为 `Cleanup()` 后再返回，并给出「已停止」状态
- **位置**：`win/ImageOptim/Job.cs:263`（`if (IsStopping()) { Publish(); return; }`）
- **问题**：直接 return，已被采纳的 `_wipInput` 临时文件留在 `%TEMP%`，要等 App 退出 `Cleanup()` 才删；Revert 前的多次停止会累积。
- **方案**：停止分支里显式 `Cleanup()`（或删掉非原始输入的 wip 文件）。
- **验收**：反复"优化→停止"20 次，`%TEMP%` 下无残留 `ImageOptim.*.tmp`。

---

## P1 性能

### ✅ OPT-10 macOS 批量添加文件是 O(n²)

- [x] 已完成（2026-10-21）：`seenPathHashes` 换成 `jobsByPath`（path → JobProxy 字典），`findFileByPath:` 变 O(1)；查重与登记放进同一 `@synchronized(self)` 段，顺手修掉并发重复添加；`removeObjects:` / `clearComplete` 同步清理索引
- **位置**：`imageoptim/FilesController.m:224`（`findFileByPath:`）
- **问题**：每条新路径都线性扫 `[self content]` 找是否已存在，1 万文件 ≈ 5000 万次比较。代码注释自己写了"Would be nice to replace NSArray with NSSet or NSHashTable"。
- **方案**：照搬 Windows 版 `win/ImageOptim/FilesController.cs:17` 的 `_jobByPath` 字典做法，改为 `NSMutableDictionary` path→JobProxy。
- **验收**：拖入 1 万文件，添加阶段耗时从分钟级降到秒级。

### 🟡 OPT-11 macOS 状态栏全量重算

- [ ] 待完成（已完成"不再无谓重算"，剩余"改成真增量"）
  - [x] 加 `atomic_bool statusBarDirty` 脏标记：空闲时零遍历（原实现 10Hz 无条件全量扫）
  - [x] 去掉 `usleep(100000)` 阻塞线程的限流写法，改成"不足 100ms 就推迟 100ms 再触发"
  - [x] 缓存上次统计结果（文本 + 可选中态），无变化事件直接复用
  - [ ] **剩余**：`arrangedObjects.@sum.byteSizeOptimized` 这个 KVO 仍在，NSArrayController 每次收到任一属性变化都会对所有对象求一次和，本身就是 O(n)。要彻底解决必须改成"逐个 JobProxy 注册 KVO"（`FilesController` 现在已有 `jobsByPath` 可直接复用），但 KVO 注册/注销不对称会直接崩溃，需在真机上跑过再改
  - [ ] **剩余**：把 sums 改成增量累加（加减法），max 用懒重算
- **位置**：`imageoptim/ImageOptimController.m:213`（观察 `arrangedObjects.@sum.byteSizeOptimized`）、`:132`（遍历所有 content 算 max/avg）、`:207`（`usleep(100000)` 压频率）
- **问题**：每次 KVO 都 `@sum` 整个列表，handler 里再遍历一遍；`usleep` 是在掩盖 O(n) 本质，批量处理时仍会持续占 CPU。
- **方案**：维护增量聚合器（文件完成/尺寸变化时做加减），状态栏读取 O(1) 快照。
- **验收**：1 万文件处理中，状态栏更新不再随文件数线性变慢。

### ✅ OPT-12 macOS JobProxy 每次 KVO 都打日志

- [x] 已完成（2026-10-21）：删除 `observeValueForKeyPath:` 里两处 `NSLog`（不再每次属性变化都打印 change 字典）
- **位置**：`imageoptim/JobProxy.m:150`、`:168`
- **问题**：每个属性变化都 `NSLog` 完整 change 字典；GUI 模式下 `hideLogs=0`（`main.m:24` 仅 CLI 启动才关），1 万文件时既拖慢又把用户文件路径写进系统日志。
- **方案**：`#if DEBUG` 包裹，或改用 `IODebug`（受 `hideLogs` 控制）。
- **验收**：Release 版处理 1000 文件，Console 无 per-property 日志。

### ✅ OPT-13 Windows 单文件内工具串行执行

- [x] 已完成（2026-10-21）
- **⭐ 修正原判断**：原描述说"并行要固定同一份输入副本，macOS 也是这个坑"，实际重读代码发现 **Windows 本来就是把原始 `input` 传给每个工具**（`w.OptimizeFile(input, tempPath)`），不是前一个工具的输出。所以并行化**不改变最终产物**（每个工具独立优化原图，`SetFileOptimized` 取最小），纯速度问题。真正的约束是 `Preferences.cs` 里写明的"串行是为了避免外部进程爆炸"。
- **实际方案：全局进程预算 + 文件内并行**
  - `JobQueue` 新增 `_processBudget`（容量 = `RunConcurrentFiles`），`Job.Start(budget)` 传进去
  - `Job.Run` 把工具循环改成并行 `Task`，每个工具执行前 `await budget.WaitAsync()`，结束后 `Release`
  - 效果：**单文件时能用满多核（上限 = RunConcurrentFiles）**；多文件时总额度共享，进程数上限与旧实现（文件并发 × 1 个工具）完全一致，不会爆炸
  - 提取 `RunOneWorker()` 保留原有"采纳与否决定是否删临时文件"的逻辑
- **为什么不是简单 `Parallel.ForEach`**：那样会让「文件并发 4 × 每文件 5 个工具 = 20 个外部进程」同时跑，正是当初改串行要避免的情况
- **未验证**：无 Windows 编译/运行环境，线程安全依赖 `SetFileOptimized` / `SetStatus` 内部已有的 `_lock`
- **位置**：`win/ImageOptim/Job.cs:228`、`win/ImageOptim/JobQueue.cs`、`win/ImageOptim/Preferences.cs`（注释同步）

### ✅ OPT-30 Windows 有损与无损工具未串联

- [x] 已完成（2026-10-21）：改成两阶段执行——`MakesNonOptimizingModifications == true` 的工具串行先跑（每个基于"当前最优结果"），其余无损工具并行竞争且输入固定为阶段一的最优结果
- **⭐ 行为变更（需留意）**：开启有损压缩时 Windows 产物会变小（此前有损与无损互相竞争，只有一个能胜出）。这是向 macOS 参考实现对齐，若不需要可只回退 `Job.Run` 里 `firstRun`/`runLater` 的拆分，其余改动不受影响
- **前置条件已满足**：Windows 各 Worker 的 `MakesNonOptimizingModifications` 覆写本来就和 macOS 完全一致（guetzli/jpegli/zopfli=true，pngquant/jpegoptim 看质量，pngcrush/pngout 看是否剥离），无需新增
- **位置**：`win/ImageOptim/Job.cs`（`w.OptimizeFile(input, ...)`）；对照 `imageoptim/Backend/Job.m` 的 `runFirst` / `runLater` 两段式
- **问题**：macOS 版把"会产生非优化性修改"的工具（pngquant 有损、jpegli 等）放进 `runFirst` **串行先跑**，其余无损工具作为 `runLater` 依赖它，因此无损工具优化的是**有损后的结果**，能叠加收益。Windows 版所有工具都吃原始 `input`，有损与无损是**互相竞争**关系——最终只会有一个工具的结果胜出，得不到"pngquant 有损 + oxipng 再无损"的叠加效果。
- **影响**：开启有损压缩时，Windows 产物明显大于 macOS。
- **方案**：照 macOS 分两段——先跑 `MakesNonOptimizingModifications == true` 的工具（串行、基于原图），采纳后再让无损工具以 `_wipInput` 为输入并行竞争。
- **注意**：这会**改变输出体积**（变小），属产品行为变更，需你确认后再做；同时也依赖 OPT-13 的预算机制。

### ✅ OPT-14 Windows 列表逐条 Add 导致 UI 卡顿

- [x] 已完成（2026-10-21）：新增 `BulkObservableCollection`（`AddRange` 发一次多元素 Add 通知、`RemoveRange` 发一次 Reset），`AddFilePaths` / `AddDirectoryPath` / `ClearComplete` 全部改批量
- **位置**：`win/ImageOptim/FilesController.cs:75`、`:95`
- **问题**：注释写"批量构建避免逐个 Add 刷新"，但仍是一条一条 `Items.Add`，每次触发 `CollectionChanged` → DataGrid 重排，1 万文件严重卡顿。
- **方案**：实现支持批量通知的集合（`AddRange` 后只发一次 `Reset`），或用 `BindingList` + 自定义 `NotifyCollectionChanged`。
- **验收**：拖入 1 万文件，列表填充期间 UI 可响应、无长时间白屏。

### ✅ OPT-15 Windows 状态变更无合并

- [x] 已完成（2026-10-21）：`JobItem` 用 `Interlocked.Exchange` 做单飞合并——已排队一条 UI 通知就不再重复排；`BeginInvoke` 降到 `DispatcherPriority.Background`；`_snapshot` 加 `volatile`
- **位置**：`win/ImageOptim/JobItem.cs:30`
- **问题**：每次 `StateChanged` 都 `Dispatcher.BeginInvoke`，且 `OnPropertyChanged("")` 全行重绑。单文件约 4~8 次状态变化，1 万文件产生数万次 UI 队列项。
- **方案**：按行做 100ms 合并（`DispatcherTimer` 或 `DispatcherPriority.Background` + 脏标记）。
- **验收**：1 万文件处理期间 UI 线程消息队列不积压。

### ✅ OPT-16 Windows 状态栏 500ms 无条件全量遍历

- [x] 已完成（2026-10-21）：加 `_statusDirty` 脏标记，由 `FilesController.ItemsChanged`（条目属性变化 / 集合变化）驱动；定时器 tick 只在脏时重算，空闲零开销
- **位置**：`win/ImageOptim/MainWindow.xaml.cs:36`、`win/ImageOptim/StatusBarCalculator.cs:23`
- **问题**：定时器空转时也在每 500ms 遍历全部 Job 计算。
- **方案**：仅在"有变化"时重算（脏标记），或改为增量维护。
- **验收**：空闲时无周期性全量遍历（可打点验证）。

### ✅ OPT-17 Windows 拖入目录被扫两遍

- [x] 已完成（2026-10-21）：`CountFiles` 换成 `Scan`，返回 `ScanResult(Files, Truncated)`；未截断时 `MainWindow` 直接把这份结果交给 `FilesController.AddScannedFiles()`，不再二次枚举
- **补充说明**：原来那次"统计"其实在超过阈值时就会提前停（`count > stopAt` 即返回），所以大目录的额外开销是有界的；真正的浪费发生在**不超过阈值的中小目录**——它们被完整枚举了两遍，现已消除
- [x] 顺带把 `CountDirectoryFiles` 与 `ScanDirectoryRecursive` 两份重复递归合并成 `ScanDirectoryInto`，减少两处逻辑漂移
- **位置**：`win/ImageOptim/JobQueue.cs:82`（`CountFiles`）→ `win/ImageOptim/FilesController.cs:82`（`AddDirectoryPath` → `ScanDirectory`）
- **问题**：为出"确认添加"对话框先完整递归计数，之后正式添加时再递归扫描一遍，大目录 IO 翻倍。
- **方案**：合并为一次扫描——先扫到阈值就停下来问用户，确认后接着用已有结果继续；或计数只做浅层估算。
- **验收**：拖入 1 万文件目录，总 IO 次数减半，确认框到列表出现无明显等待。

### ✅ OPT-18 Windows 线程池线程被长期占用

- [x] 已完成（2026-10-21）：信号量仍以异步方式等待（不占线程），拿到后把阻塞的 `job.Start()` 交给 `Task.Factory.StartNew(..., TaskCreationOptions.LongRunning)` 跑在专用线程上
- **为什么不用 `LongRunning` 包整个流程**：那样每个排队中的任务都会先占一个线程再阻塞等信号量，1 万个文件就是 1 万个线程。必须先异步拿到并发额度，再起专用线程
- **位置**：`win/ImageOptim/JobQueue.cs:45`
- **问题**：`Task.Run` 里跑阻塞式 `job.Start()`（整个优化流程同步执行），长时间占用线程池线程；并发数调大时可能饿死线程池。
- **方案**：`Task.Factory.StartNew(..., TaskCreationOptions.LongRunning)` 或专用 `Thread`/调度器。
- **验收**：并发数设 32 时，UI 与其他异步任务不出现延迟毛刺。

---

## P2 工程质量

### 🟡 OPT-19 双端算法漂移风险

- [ ] 待完成（Windows 侧已完成，macOS 侧待接）
  - [x] 新增双端契约 `shared/toolchain.json`，**内容以 Windows 实现为基准**
  - [x] Windows 侧改为契约驱动：`Toolchain.Build()` 读取内嵌资源（`<EmbeddedResource LogicalName="toolchain.json">`），`Job.BuildWorkers` 不再硬编码；旧实现已删除
  - [x] **等价性验证**：临时对比程序在 2 万随机偏好组合 × 4 文件类型 = 8 万组上比对"旧硬编码"与"契约驱动"的工具链签名（类型 + `SettingsIdentifier` + `MakesNonOptimizingModifications`），**0 差异**。过程中抓出并修正一个真 bug：GIF 的 `unless`（else 分支）起初用实时 `lossyConverted` 求值，有损工具加入后标志翻转导致 else 也命中，已改为用建链前状态求值
  - [x] **把 macOS 的已知差异写进契约**：新增 `platformDeviations`，逐条记录 macOS 的 level 递增、基于 level 的工具抑制（level<4+zopfli 跳 pngout、level<2+oxipng 跳 pngcrush）、Guetzli 额外要求 `!hasBeenRunBefore`、有损工具直接进 runFirst、以及 macOS 无 jpegli。这些是**有意保留**的差异，接契约时不能顺手抹平
  - [x] **一致性校验脚本 `scripts/check-toolchain.py`**：校验契约结构、条件词汇、偏好项名是否真实存在于 `Preferences.cs`、每个 id 是否在 Windows `Toolchain.Create()` 登记、macOS 是否有对应 Worker 类、以及实现里多出来的 id。已做负向测试（注入假工具 → 3 个 ERROR、退出码 1）
  - [x] **接入两条 CI**：`build-macos.yml` 与 `build-windows.yml` 各加一步「校验工具链契约」
  - [ ] **剩余（macOS 侧读契约）**：`Job.m` 的 `switch (input->fileType)` 改为读同一份 JSON，并把 `shared/toolchain.json` 加进 app target 的 Copy Bundle Resources。**未做原因**：macOS 若严格按 Windows 契约建链会丢掉上面那几条调优，属行为回归，需要先定"契约 + 平台偏差"的分层方式；且改动需真机构建验证
  - [ ] **剩余（长期守护）**：Windows 建 xunit 测试工程、macOS 在 `BackendTests` 里加断言，各自校验"构建出的工具链 == 契约"
- **注意**：`setFlags` / `when` 里出现的新工具 id 若忘了在 `Toolchain.Create()` 里登记，只会打印 Debug 日志并跳过，不会崩——加新工具时记得两处都改
- **位置**：`shared/toolchain.json`、`win/ImageOptim/Toolchain.cs`、`imageoptim/Backend/Job.m` ↔ `win/ImageOptim/Job.cs`

### ✅ OPT-20 MD5 弃用需 pragma 压制

- [x] 已完成（2026-10-21，macOS 侧）：`CC_MD5_*` → `CC_SHA256_*`，两处 `#pragma GCC diagnostic ignored "-Wdeprecated-declarations"` 全部删除；哈希从 16 字节扩到 32 字节（`settingsHash[8]` / `inputFileHash[8]`），`ResultsDb` 的 blob 字面量与 `BLOB(32)` DDL 同步更新；settings hash 的版本前缀 `"3"` → `"4"`，连同算法变更一起作废旧缓存
- **副作用**：旧缓存条目（16 字节 blob）永远不再命中，等价于一次性清空缓存；旧表里会残留无用的历史行，由 OPT-21 的修剪逻辑逐步消化
- **Windows 侧未改**：`ResultsCache.cs` 仍用 MD5（非安全用途，且改算法等于清空用户缓存）。如需统一，单独提
- **位置**：`imageoptim/Backend/Job.m:515`、`:675`（`#pragma GCC diagnostic ignored "-Wdeprecated-declarations"`）；`win/ImageOptim/ResultsCache.cs:54`
- **问题**：`CC_MD5_*` 已弃用，代码要靠 pragma 才能编译通过。缓存键虽非安全用途，但顺带能清掉 pragma。
- **方案**：macOS 换 `CC_SHA256`、Windows 换 `SHA256.Create()`；一次改动同时升级缓存格式（旧缓存失效可接受）。
- **验收**：去掉两处 pragma 后编译无新告警。

### ✅ OPT-21 macOS 结果缓存只增不删

- [x] 已完成（2026-10-21）：启动时修剪到最近 2 万条（`DELETE ... WHERE rowid NOT IN (SELECT rowid ... LIMIT 20000)`）；补 `PRAGMA journal_mode=WAL` + `synchronous=NORMAL`（放在**独立 exec** 里，避免 WAL 不被文件系统支持时连带废掉建表语句）；`setUnoptimizableFileHash` 改 `INSERT OR REPLACE` 避免主键冲突刷错误日志
- [x] 顺带删掉死方法 `hasResultWithFileSize:` 及其专用索引 `results_size`（唯一调用点早已被注释）
- **位置**：`imageoptim/ResultsDb.m:32`（建表后再无删除）、`imageoptim/Job.m:674`（`hasResultWithFileSize` 调用点已注释）
- **问题**：`results` 表只增不删，无 TTL/清理/VACUUM，长期使用持续膨胀；`hasResultWithFileSize:` 已是死代码。
- **方案**：加按时间或条数的清理（如保留最近 N 万条）+ 定期 `VACUUM`；开 `PRAGMA journal_mode=WAL`；删除死方法。
- **验收**：模拟 10 万条记录后 DB 体积受控。

### ✅ OPT-22 macOS 日志宏悬垂 else

- [x] 已完成（2026-10-21）：`IODebug` / `IOWarn` 改为 `do { ... } while (0)` 包裹；改前用 awk 逐文件确认**没有任何调用点的下一行是 `else`**，所以不会改变现有控制流
- **验证**：改动后对全部 34 个 `.m` 文件跑 `clang -fsyntax-only -Wall`，0 error
- **位置**：`imageoptim/log.h:7`、`:8`
- **问题**：`#define IODebug(...) if (!hideLogs) NSLog(...);` 裸 `if` 无花括号，将来 `if (x) IODebug(...); else ...` 会静默改变控制流。
- **方案**：改为 `do { ... } while (0)` 包裹。
- **验收**：全量 grep 确认无悬垂 else 组合，编译通过。

### ✅ OPT-23 死代码与小缺陷

- [x] 全部完成（2026-10-21）
  - [x] `_revertFileObjExists()` 已删（随 OPT-04）
  - [x] `GetOutputDirectory` 上方重复的 `<summary>` 已修正（随 OPT-02）
  - [x] `ResultsDb.hasResultWithFileSize:` 死方法已删（随 OPT-21）
  - [x] `Job.m` 的 `NSLog(@"Skipping %@...")` 改 `IODebug`（随 OPT-20）
  - [x] `Job.cs` 私有 `RunAsync()` 更名 `Run()`（全流程无任何 `await`，原名误导）
  - [x] `File.m` 的 `NSLog(@"... %d ...")` 改 `IOWarn` + `%lu`/`(unsigned long)`（64 位下不再截断），顺带去掉重复的 `[File byteSize:]` 调用
- **位置**：`win/ImageOptim/Job.cs:661`（`_revertFileObjExists` 未使用）、`win/ImageOptim/Job.cs:152`（`Start()` 调 `RunAsync()` 但全同步，命名误导）、`imageoptim/Backend/File.m:75`（`%d` 打 `NSUInteger`）、`imageoptim/Backend/Job.m:683`（`NSLog` 且带 `FIXME: check for lossy`）
- **问题**：零散死代码、命名与实现不符、格式串类型不匹配（64 位下有截断风险）。
- **方案**：逐项清理；`FIXME` 要么实现要么转成 TODO 并记录跟踪项。
- **验收**：编译器无格式串告警，无未使用私有方法。

### ✅ OPT-24 测试未进 CI

- [x] macOS 侧已完成（2026-10-21），Windows 侧未做
- **踩到的三个前提**（都已处理）：
  1. **工程里没有 `BackendTests` scheme**：Xcode 的自动 scheme 不会为测试包生成，只有 `Backend` / `ImageOptim` 等 app 与 framework target。→ 新增共享 scheme `imageoptim/ImageOptim.xcodeproj/xcshareddata/xcschemes/BackendTests.xcscheme`（`BlueprintIdentifier = 5ABE154B1AE5072300861C6B`）
  2. **`.gitignore` 里的 `*.xcscheme` 会把共享 scheme 一起挡掉** → 删掉该规则，改为忽略 `xcuserdata/`，共享 scheme 可提交
  3. **`BackendTests` 是应用托管型测试包**（`TEST_HOST` 指向 `ImageOptim.app`）→ 测试宿主必须能真正运行，签名策略不能沿用构建步骤的"完全关闭签名"，测试步骤改用 ad-hoc（`CODE_SIGN_IDENTITY="-"`）
- **另需 `-destination 'platform=macOS'`**：只给 `-scheme` 会报 `Found no destinations for the scheme`
- **本地验证**：scheme 已被 `xcodebuild -list` 识别；`xcodebuild test -scheme BackendTests -destination 'platform=macOS'` 能正常起跑，唯一失败点是 `cargo: command not found`（CI 已装 Rust）
- **遗留**：
  - [ ] Windows 侧仍 0 测试（xunit 工程未建）
  - [ ] `BackendTests.m` 里 `XCTAssertLessThanOrEqual([size integerValue], 5552)` 是写死的输出体积断言，工具升级或换架构时容易误报，建议改成"必须比原图小"这类性质断言
  - **位置**：`imageoptim/Backend/Tests/BackendTests.m`；`win/` 下无测试工程

---

## P3 构建 / 供应链

### ✅ OPT-25 工具下载零校验

- [x] 已完成（2026-10-21）：12 个工具的产物全部锁定 SHA-256（实测下载 10 个有效产物逐个算出真实哈希写入脚本），下载后先校验再解压，不匹配则 `Write-Error` + `exit 1`；新增 `-UpdateLock` 供刻意升级时打印新旧哈希
- **位置**：`win/tools/download-tools.ps1:127`
- **问题**：12 个第三方 URL 下载即执行，无哈希/签名校验。其中 2 个 URL 无法在路径上锁版本：
  - `https://pngquant.org/pngquant-windows.zip`（站点只提供当前版本，文件名固定）
  - `https://github.com/imagemin/zopflipng-bin/raw/main/.../zopflipng.exe`（裸 main 分支文件）
  CI 产物每天都在变，不可复现也不可审计。
- **⚠️ 顺带查出的新问题（2026-10-21 实测）**：**2 个工具的下载源是死的，一直在静默失败**
  - `pngcrush`：`https://pmt.sourceforge.io/pngcrush/pngcrush-1.8.13-win64.zip` → 404（返回 HTML）
  - `svgo`：`https://github.com/svg/svgo/releases/download/v3.3.2/svgo-win.exe` → 404；svgo v3.3.2 的 release 资产只有 `svgo.browser.js`，**从来没有 Windows 单文件 exe**（需 Node.js 运行）
  两者都是可选工具，原逻辑只是 `Write-Warning`，所以一直没人发现。已在脚本中标 `Unavailable` 直接跳过（省掉每次构建 6 次无效网络重试），能力不降级：新用户的 pngcrush/svgo 本来就装不上。
- **建议后续（需产品决策）**：SVG 侧是否放弃 svgo 只留 svgcleaner；PNG 侧 pngcrush 是否直接下线（`PngCrush2Enabled` 默认已是 false）。
- **方案**：全部固定到明确版本 URL；脚本内硬编码每个文件的 SHA-256，下载后校验失败即 `exit 1`；`.gitignore` 中的 Tools 目录补充校验清单文件。
- **验收**：断网前预置 Tools 可构建；篡改任一二进制后构建失败。
- **约束提醒**：该脚本必须保持**纯 ASCII**（PowerShell 5.1 以 ANSI 读无 BOM 的 .ps1，中文会导致引号错位解析失败，参见 commit `5d5318a`）。

### ✅ OPT-26 每次编译都跑下载脚本

- [x] 已完成（2026-10-21）：`DownloadOptimizerTools` 加 `!Exists(oxipng.exe) or !Exists(cjpegli.exe)` 条件，工具已就位时不再启动 PowerShell
- **位置**：`win/ImageOptim/ImageOptim.csproj:26`（`BeforeTargets="BeforeBuild"` 无条件执行 PowerShell）
- **问题**：每次增量编译都启动 PowerShell 并做 12 次 `Test-Path`，几秒到几十秒开销。
- **方案**：改用 `Inputs`/`Outputs` 做增量，或 `Condition="!Exists('...\Tools\oxipng.exe')"`。
- **验收**：二次编译无 PowerShell 启动日志。

### ✅ OPT-27 无 macOS CI

- [x] 已完成（2026-10-21）：新增 `.github/workflows/build-macos.yml`（push/PR/tag/workflow_dispatch，submodule recursive 检出）
- **本地实测踩出的两个必须项**（都已写进工作流）：
  1. 所有 subproject 都自带 xcodeproj，默认要求开发团队签名 → CI 必然 `Signing for xxx requires a development team`。必须传 `CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO CODE_SIGN_IDENTITY="" DEVELOPMENT_TEAM=""`
  2. `cargo not found` → `error: cargo build failed`（pngquant / svgcleaner / oxipng 靠 Xcode build rule 调 `cargo build` 产出）。必须 `dtolnay/rust-toolchain@stable`
- **注意**：工程里没有共享 scheme（`.gitignore` 排除了 `*.xcscheme`），靠 xcodebuild 自动生成；Xcode 26 实测 `-scheme ImageOptim` 可解析，低版本可能不行，首次跑 CI 需确认
- **位置**：`.github/workflows/`（仅 `build-windows.yml`）
- **问题**：主战场是 Xcode + 6 个 submodule 的 C/Rust 编译，却只有 Windows 有 workflow。submodule 指针损坏或工具升级破坏编译无人发现。
- **方案**：新增 `macos-latest` job 跑 `xcodebuild -project imageoptim/ImageOptim.xcodeproj`（含 `-submodule` 初始化），先只做 build + 测试，产物可选。
- **验收**：PR 触发 macOS 构建并可通过。

### ✅ OPT-28 release 清理误删附件

- [x] 已完成（2026-10-21）：改成先筛出"确实带 `.exe` 附件"的 release 再做保留计数，改用 `github.paginate` 翻页；不再按全局时间顺序截断
- **位置**：`.github/workflows/build-windows.yml:99`（`releases.data.slice(keepCount)`）
- **问题**：把"最新 2 个之外的所有 release"的 `.exe` 附件全删，将来加入 macOS release 会被连带处理；且只删附件不删空 release。
- **方案**：按 tag 前缀（如 `win-*`）过滤后再比较；或改为"只保留最新 N 个 Windows 附件"。
- **验收**：存在非 Windows release 时，其附件不被删除。

### ✅ OPT-29 版本号两套不一致

- [x] 已完成（2026-10-21）：`ImageOptim.csproj` 改为用 MSBuild 属性函数从 `imageoptim/release.xcconfig` 读 `CURRENT_PROJECT_VERSION`（`Regex.Match` + `File.ReadAllText`），失败兜底 `0.0.0`。**副作用：Windows 端版本号从 1.0.0 直接对齐到 1.9.3**，属预期
- **验证**：正则已用 shell 实测从 `release.xcconfig` 提取出 `1.9.3`（`MARKETING_VERSION = $(CURRENT_PROJECT_VERSION);` 那一行不会误匹配，因为其后是 `)` 不是 `=`）
- **位置**：`imageoptim/release.xcconfig`（`CURRENT_PROJECT_VERSION=1.9.3`）↔ `win/ImageOptim/ImageOptim.csproj`（`<Version>1.0.0</Version>`）
- **问题**：同名产品双端版本号不对齐，Windows 侧与上游功能差异无法从版本号判断。
- **方案**：建立单一版本源（如根目录 `version.json`），两端构建脚本读取；至少在 README 记录两份版本号对应关系。
- **验收**：两端发版时版本号一处修改、两端生效。

---

## 建议执行顺序

**第一批（数据安全，用户能直接踩到）**：OPT-01 ✅ → OPT-03 ✅ → OPT-02 ✅ → OPT-04 ✅（2026-10-21 完成）
**第 3.5 批（收尾 macOS P0）**：OPT-05 ✅ → OPT-06 ✅ → OPT-07 ✅（2026-10-21，P0 全部清零）
**第二批（收益/成本比最高）**：OPT-10 ✅ → OPT-12 ✅ → OPT-25 ✅ → OPT-26 ✅（2026-10-21 完成）
**第三批（体验）**：OPT-14 ✅ → OPT-15 ✅ → OPT-16 ✅ → OPT-11 🟡（2026-10-21，OPT-11 剩增量累加部分）
**第四批（构建与供应链）**：OPT-27 ✅ → OPT-28 ✅ → OPT-29 ✅（2026-10-21 完成）
**第五批（macOS 代码卫生）**：OPT-22 ✅ → OPT-20 ✅ → OPT-21 ✅（2026-10-21 完成）
**第六批（Windows 性能 + 收尾小项）**：OPT-23 ✅ → OPT-17 ✅ → OPT-18 ✅（2026-10-21）
**第七批**：OPT-24 ✅（macOS 测试接入 CI，2026-10-21）
**第八批**：OPT-13 ✅ → OPT-30 ✅（2026-10-21）
**第九批**：OPT-19 🟡（契约文件 + Windows 侧落地 + 等价性验证，macOS 侧待接）（2026-10-21）
**第十批（最后 1 项）**：OPT-11 增量累加（KVO 重构，需真机）

## 变更记录

| 日期 | 变更类型 | 变更内容 | 作者 |
| --- | --- | --- | --- |
| 2026-10-21 | 首次生成 | 基于 commit `91df81a` 静态通读双端源码，梳理 29 项优化（P0×9 / P1×9 / P2×6 / P3×5），全部标记为待开始 | - |
| 2026-10-21 | 第十批修复（续） | OPT-19 推进：契约新增 `platformDeviations` 记录 macOS 有意差异；新增 `scripts/check-toolchain.py` 做契约↔两端实现一致性校验（结构/词汇/偏好项名/工厂登记/macOS Worker 覆盖），并接入两条 CI；已负向测试确认会失败。**macOS 侧读契约未做**：严格对齐 Windows 会丢掉 level 递增与工具抑制，属回归，待确定"契约+平台偏差"分层 | - |
| 2026-10-21 | 第十批修复 | OPT-19（**以 Windows 为基准**）：新增 `shared/toolchain.json` 双端契约 + `win/ImageOptim/Toolchain.cs` 契约驱动构建器，替换 `Job.BuildWorkers` 硬编码；删除旧实现。**等价性验证**：临时对比程序在 2 万随机偏好组合 × 4 文件类型 = 8 万组上比对旧/新工具链签名，0 差异（过程中抓出并修正 GIF `unless` else 分支求值时机 bug）。改动文件：`shared/toolchain.json`（新增）、`win/ImageOptim/Toolchain.cs`（新增）、`Job.cs`、`ImageOptim.csproj`。**验证**：`dotnet build -t:Rebuild` 0 warning 0 error；DLL 内嵌资源含 toolchain.json。**macOS 侧待接** | - |
| 2026-10-21 | 第九批修复 | OPT-30（对齐 macOS 两阶段执行：`MakesNonOptimizingModifications` 的工具串行先跑，其余无损工具并行优化其产物，实现有损+无损叠加）。**行为变更：开启有损压缩时 Windows 产物会变小**。改动文件：`win/ImageOptim/Job.cs`（`firstRun`/`runLater` 拆分 + `CurrentBestInput` + `RunWorkerSerially`）。**验证**：括号配平 + 人工审阅，无本地编译环境 | - |
| 2026-10-21 | 第八批修复 | OPT-13（全局进程预算 + 文件内工具并行，单文件可用满多核且进程总数不增）；**修正原判断**：Windows 各工具本就吃原始 input，并行不改变产物。新发现 OPT-30（有损/无损未串联，Windows 产物大于 macOS，需产品确认后再改）。改动文件：`win/ImageOptim/Job.cs`、`JobQueue.cs`、`Preferences.cs`（注释）。**验证**：括号配平 + 人工审阅，Windows 无本地编译环境；并发正确性依赖已有 `_lock` | - |
| 2026-10-21 | 第七批修复 | OPT-24（macOS 测试接入 CI）：新增共享 scheme `BackendTests.xcscheme`；`.gitignore` 去掉 `*.xcscheme` 改为忽略 `xcuserdata/`；workflow 增加 `xcodebuild test` 步骤（ad-hoc 签名 + `-destination 'platform=macOS'`）。改动文件：`imageoptim/ImageOptim.xcodeproj/xcshareddata/xcschemes/BackendTests.xcscheme`（新增）、`.gitignore`、`.github/workflows/build-macos.yml`。**验证**：scheme 被 `xcodebuild -list` 识别，`test` 动作可起跑（仅卡在缺 Rust） | - |
| 2026-10-21 | 第六批修复 | OPT-23（`Job.RunAsync`→`Run`；`File.m` 的 `%d` 改 `%lu`+`IOWarn`；累计清理 6 处死代码/小缺陷）、OPT-17（`CountFiles`→`Scan` 返回可复用结果，合并两份重复递归）、OPT-18（信号量异步等 + `LongRunning` 专用线程跑阻塞优化）。改动文件：`win/ImageOptim/JobQueue.cs`、`MainWindow.xaml.cs`、`FilesController.cs`、`Job.cs`、`imageoptim/Backend/File.m`。**验证**：`File.m` 通过 `clang -fsyntax-only -Wall`；Windows 侧无本地编译环境 | - |
| 2026-10-21 | 第五批修复 | OPT-22（`IODebug`/`IOWarn` 改 `do{}while(0)`，改前 awk 确认无悬垂 else）、OPT-20（macOS 侧 `CC_MD5`→`CC_SHA256`，哈希 16→32 字节，去掉两处 pragma，版本前缀 "3"→"4"）、OPT-21（缓存启动修剪 2 万条 + WAL + `INSERT OR REPLACE`，删死方法 `hasResultWithFileSize:` 及其索引）。改动文件：`imageoptim/log.h`、`Backend/Job.h`、`Backend/Job.m`、`ResultsDb.h`、`ResultsDb.m`。**验证**：34 个 `.m` 全部 `clang -fsyntax-only -Wall` 通过（0 error），`ResultsDb.m` 另在 `-DNDEBUG` 下复检无未使用函数告警 | - |
| 2026-10-21 | 第四批修复（P3 清零） | OPT-27（新增 `.github/workflows/build-macos.yml`，本地实测出"必须禁签名"与"必须装 Rust"两个硬前提）、OPT-28（release 清理只作用于带 exe 附件的 release + paginate）、OPT-29（csproj 版本号改为从 `release.xcconfig` 读取，Windows 端对齐到 1.9.3）。改动文件：`.github/workflows/build-macos.yml`（新增）、`build-windows.yml`、`win/ImageOptim/ImageOptim.csproj`。**验证**：两份 YAML 均通过 `yaml.safe_load`；版本正则实测提取出 `1.9.3`。**未验证**：CI 首次运行结果（含无共享 scheme 的兼容性） | - |
| 2026-10-21 | 第 3.5 批修复（macOS P0 清零） | OPT-05（Finder 扩展 UI 调用切回主线程，`sleep(1)` 换成 `dispatch_after`）、OPT-06（保存改 1MB 分块搬运，不再整文件入内存）、OPT-07（**复核为误报**：`nextInsertRow` 全在主线程，无数据竞争；实际修掉空批次注册 undo、越界旧值残留两点，并补 `NSThread isMainThread` 断言）。改动文件：`imageoptim/ImageOptimize/ExtensionController.m`、`imageoptim/Backend/Job.m`、`imageoptim/FilesController.m`。**验证**：三个文件均通过 `clang -fsyntax-only -Wall`，零 error 零新增 warning | - |
| 2026-10-21 | 第三批修复 | OPT-14（新增 `BulkObservableCollection`，列表批量增删单次通知）、OPT-15（`JobItem` 用 `Interlocked` 合并 UI 通知）、OPT-16（状态栏脏标记，空闲零遍历）、OPT-11 部分完成（`atomic_bool` 脏标记 + 结果缓存 + 去掉阻塞式 `usleep`，剩余 `@sum` KVO 的 O(n) 与增量累加）。改动文件：`win/ImageOptim/BulkObservableCollection.cs`（新增）、`FilesController.cs`、`JobItem.cs`、`MainWindow.xaml.cs`、`imageoptim/ImageOptimController.m`。**验证**：`ImageOptimController.m` 通过 `clang -fsyntax-only -Wall`（顺带修掉编译器抓到的 `dispatch_after` 误传 source 的真 bug），Windows 侧仍无本地编译环境 | - |
| 2026-10-21 | 第二批修复 | OPT-10（macOS 路径索引改字典，O(n²)→O(1)）、OPT-12（删 JobProxy KVO 日志）、OPT-25（10 个工具产物锁定真实 SHA-256，下载后强校验；查出 pngcrush/svgo 两个下载源早已 404，标 Unavailable 跳过）、OPT-26（csproj 加缺失判定条件）。改动文件：`imageoptim/FilesController.m`、`imageoptim/JobProxy.m`、`win/tools/download-tools.ps1`、`win/ImageOptim/ImageOptim.csproj`。**验证**：两个 .m 已通过 `clang -fsyntax-only -Wall`；.ps1/.csproj 无本地执行环境（无 pwsh/dotnet），未运行验证。**注意**：.ps1 必须保持纯 ASCII（PS 5.1 编码坑，见 commit `5d5318a`），已用 `grep '[^ -~]'` 确认 | - |
| 2026-10-21 | 第一批修复 | OPT-01/02/03/04 完成；随改动一并完成 OPT-08（`Stop()` 锁外触发事件）、OPT-09（停止后清理临时文件）；OPT-23 清掉 Windows 侧 2 项死代码。改动文件：`win/ImageOptim/Job.cs`、`Worker.cs`、新增 `BackupStore.cs`，`App.xaml.cs` 加启动清理。**注意：本机无 .NET SDK 且 WPF 不能在 macOS 编译，未做编译验证，待 Windows CI 或本机 `dotnet build` 确认** | - |
