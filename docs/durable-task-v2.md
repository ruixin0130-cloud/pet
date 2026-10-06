# V2 — Durable Task + Permission V1

本文记录 V2 阶段基线；V3 增加 Memory，[Scheduler V1](scheduler-v1.md) 加入计划和触发审计，当前 [审计归档 V1](audit-archive-v1.md) 将事务库升为 schema 5，合法 2/3/4 可原子迁移。任务与具体操作批准的边界保持不变，Memory 边界见 [Memory V1](memory-v1.md)。

当前 [整组备份与校验恢复 V1](data-backup-v1.md) 在保持 schema 5 的情况下扩展本地存储布局。逻辑根和文件工具目录不变，首次明确恢复后由 `active-store.json` 选择新的主库及配对归档；旧批准失效，未知结果仍需人工核对，实际副作用不回滚。以下路径和验证数量记录 V2 阶段基线。

## 仓库审计与实现边界

2026-10-01 接手时，实际 Git 仓库为 `pet/`，分支 `codex/windows-macos-ui-v1`，最近提交为 `30e9d86`。工作区已有未提交的 Core Foundation V1，包括 Core、桥接、构建脚本、测试与文档；本次在这些文件上增量实现，没有重新实现 V1，也没有 commit / push。修改前的文件快照保存在被忽略的 `output/v2-baseline/`，可区分本次变更与原有未提交修改。

审计确认 V1 的通用 Task 检查点、Runtime 循环、工具注册/严格校验、scope 权限、Result/Feedback、构造函数注入、Fake Provider 和独立 Core 测试均存在。最初 61 项 Core 检查通过。缺口是内存审批只能停住、任务不能跨进程恢复、没有持久 execution / permission。没有需要改变既定架构方向的阻塞。

V2 复用 `AgentCoreRuntime` 的模型循环、工具校验、真实结果反馈、限额、取消和超时；增加异步检查点端口与稳定身份的内部运行入口。原 `AgentRuntime` / Pet Port / 学习模式签名与行为保持兼容。新增 Durable 宿主明确使用 Fake Provider；没有接入新的真实 API、Memory、Scheduler、CLI、Web、Mobile、外部服务或 Planner。

## 模块与依赖

| 文件 | 职责 |
| --- | --- |
| `src/Core/AgentDurableContracts.cs` | 异步存储/检查点接口、Task/Permission/Execution DTO、规范化参数与绑定哈希 |
| `src/Core/AgentDurableService.cs` | Durable 生命周期、批准/拒绝/参数替换、恢复、人工 reconciliation、调用 V1 Core |
| `src/Core/AgentCoreRuntime.cs` | 原有通用循环；在执行前和结果后等待注入的异步检查点 |
| `src/Core/AgentTools.cs`、`AgentCoreContracts.cs`、`AgentTasks.cs` | 风险等级、具体操作确认、参数哈希和追加状态；保留原枚举值顺序 |
| `src/Core/AgentFileWriteTool.cs` | 文件工具校验、文件写入端口、显式 Fake Provider；不访问文件系统 |
| `src/Persistence/JsonAgentTaskStore.cs` | 本地版本化 JSON、写入租约、revision CAS、原子提交、损坏检测 |
| `src/Persistence/LocalAgentFileWriter.cs` | 受限 UTF-8 文件创建适配器；不访问 Task 存储 |
| `src/AgentDurableHost.cs` | 不依赖窗口的组合入口：注入 store、Fake Provider、registry、policy、writer |
| `src/AgentDurableApp.cs`、`src/Panel.xaml` | 展示 Task/Permission/结果、接收按钮操作；不拥有状态机或恢复策略 |
| `tests/Core/AgentDurableTests.cs` | 独立流程、存储故障注入、真实子进程崩溃与重启验证 |

Core 源码没有磁盘访问、WPF 或厂商 SDK。磁盘访问集中在两个适配器，分别处理任务存储与工具效果。构建保持原 .NET Framework 工程与单 EXE 分发，不安装数据库或依赖包。独立 Core 测试 DLL 包含本地适配器，但不引用任何 WPF、Pet Port、Qwen 或素材。

## Task 状态与恢复

| 状态 | 含义 / 恢复处理 |
| --- | --- |
| `Created` | 稳定 TaskId / RunId 已提交；可明确启动或取消 |
| `Queued` | 已提交排队检查点；重启后显式提供哈希匹配的原输入可启动 |
| `Running` | 正在模型决策或工具执行；重启时分类为 Interrupted / NeedsReview |
| `WaitingForApproval` | 具体操作及审批已落盘，handler 尚未执行；Pending / Approved 均可恢复，无自动分发 |
| `Succeeded` | 正常成功，或人工观察成功后关闭 |
| `Failed` | 明确失败、拒绝，或人工观察未成功后关闭 |
| `Cancelled` | 分发前取消，或原 Core 记录了取消及实际效果 |
| `Interrupted` | 进程在无未确认执行的检查点之间退出；人工核对后关闭或取消，不自动重新规划 |
| `NeedsReview` | Dispatching / Unknown 执行没有持久化的确定结果；只允许人工 reconciliation，禁止重放 |
| `Blocked` | 保留 V1 兼容的阻塞状态；存储故障通过 StateUnavailable 返回，持久状态以最后成功检查点为准 |

正常顺序为 `Created → Queued → Running → WaitingForApproval → Running → Succeeded / Failed`，也可直接 Running → Succeeded。终态不能通过旧 revision 或旧运行快照重新打开。人工结论记录为 `ReconciledSucceeded` / `ReconciledFailed`，与工具真实返回区分；它不调用 handler、不自动补做失败操作。

恢复入口先读取已提交文档：只要存在 Dispatching / Unknown 就进入 NeedsReview；否则 Running 进入 Interrupted；Waiting、Queued、Created 原样保留。没有后台调度或自动重试。等待操作恢复时，使用冻结的 ToolCall 经过原 Core 再校验、授权、分发；不重新调用模型生成参数，也不重放先前调用。处理该操作后返回确定结果，结束任务；V2 不保存或自动继续整个模型计划。

## Persistence

`IAgentDurableTaskStore` 提供异步 `ListAsync` / `GetAsync` / `SaveAsync`。DTO 为与存储分离的副本；读取后修改对象不会改写记录。提交以 Task revision 比较交换，Task、Permission 与 Execution 一起提交。

生产路径：`%LOCALAPPDATA%\TamagoPet\agent-v2\tasks\tasks.v2.json`。JSON schema version 为 2，每次把完整文档写入同目录 `.tmp`，`Flush(true)` 后使用 Windows `MoveFileEx(REPLACE_EXISTING | WRITE_THROUGH)` 原子重命名，再更新内存文档。未提交的 `.tmp` 不会在恢复时提升为真状态。当前 Windows 环境的 ReplaceFile ACL 合并/替换出现间歇失败，因此使用同目录原子重命名，而非非原子的删除再移动。

存储目录有生命周期内独占的文件租约，阻止另一个进程同时读取旧状态并执行；进程退出后操作系统释放租约。单个适配器的异步 gate 串行化写入，revision 防止过期提交。发生实际写入故障后适配器拒绝继续使用，宿主须关闭并重新打开，再进行恢复。损坏、超限或未知版本不会静默重置或覆盖。

最多 256 个任务，每任务最多 3 项执行、64 次审批版本，文档最多 8 MiB；容量满则拒绝新任务，不删除等待任务或审计记录。此阶段没有历史归档、数据库迁移或跨平台持久化适配器。

## Permission 与审计

风险为 `SafeRead`、`LocalWrite`、`ExternalWrite`、`DestructiveAction`。Durable 策略保留宿主 scope 的默认拒绝，SafeRead 可直接允许，所有写入等级都要求人工确认。宿主撤销 scope / 拒绝后，即使已有人工批准也不能执行。工具自身 RequiresConfirmation 仍然有效，模型不能授予权限。

绑定为 SHA-256，输入包括：TaskId、RunId、CallId、ToolId、规范化参数 SHA-256、风险等级、scope、参数 schema、RequiresConfirmation、OperationContext。文件工具的 OperationContext 为规范化的目标根目录。JSON 对象按 ordinal key 排序，递归保留数组顺序，忽略对象字段顺序与排版差异。

`DecideAsync` 要求当前 permission ID 与 expected binding 完全匹配，先落盘决定再允许执行；`ExecuteApprovedAsync` 要求 Approved，并重新验证绑定、工具参数、允许名单与宿主权限。分发前同一原子检查点将批准标记 Consumed、移除参数正文并记录 Dispatching。重复批准、旧审批、跨任务批准、终态 resume 都不能执行。

`ReplaceArgumentsAsync` 校验新操作后，将旧审批变成 Superseded 并移除正文，保存新 Pending ID / hash。即使参数正文相同，只要目标目录或风险/契约变化，也需要新审批。Reject 终结任务，执行标记 Rejected，handler 调用次数为零。

Execution Record 嵌入对应 Task，包含 CallId、ToolId、RequestedAt / StartedAt / FinishedAt、ArgumentsHash、风险、permission 状态、execution 状态、结果码与固定结果摘要。保存实际结果码或人工观察结论，不保存任意 handler 输出或异常文本；没有复杂 telemetry 系统。SafeRead 的 permission 为 NotRequired。拒绝的模型请求使用 opaque call ID hash，未知工具名不按原文落盘。

不持久化用户输入全文、宿主上下文、模型回复、任意工具输出、异常原文或凭据。等待审批需要保存经过校验的具体参数，供重启后展示和执行；识别 credential 字段名、Bearer、常见 API key / token / 私钥形式并拒绝，文件工具再执行同样校验。工具宿主仍负有明确契约：不要把敏感值放进可持久化参数；通用检测不是任意秘密的语义识别器。关闭/替换/消费的审批仅保留绑定与时间，不保留参数正文。

## 副作用不变量

1. 先提交 Dispatching，再调用 handler；检查点失败则 handler 不执行。
2. handler 成功后提交实际结果；该提交失败时停止后续循环，保留已提交的 Dispatching。
3. 重启后的 Dispatching 代表“不能证明是否执行”，转换为 Unknown / NeedsReview。绝不自动 retry，即便操作看似可以再次执行。
4. 超时或执行异常同样记录 Unknown。忽略取消的工具可能晚完成，晚结果不能覆盖已经持久化的未知状态。
5. 人工核对前必须确认旧执行停止，再检查实际效果。reconciliation 只记结论；不代表自动补偿，也不是 exactly-once 保证。

这覆盖“工具成功 → 在成功检查点前进程崩溃 → 重启”的关键窗口。验证的是进程崩溃与磁盘 I/O 故障；未做真实断电、文件系统损坏、恶意同用户进程或硬件故障注入。

文件工具仅支持宿主 agent-files 根下单个白名单名称的 `.txt` 文件和最多 1024 字文本，拒绝路径穿越、目录、NTFS stream、设备名和 reparse-point 根。`FileMode.CreateNew` 阻止覆盖；开始创建后发生错误一律保守报告 ExecutionUnknown。环境不是不可信插件执行沙箱。

## 接口用法

宿主构造 DurableService 时注入 store、Fake Provider、registry 与 policy。

- `CreateAsync(request)`：提交 Created / Queued；`StartQueuedAsync(id, matchingRequest, token)`：显式启动已恢复排队任务。
- `RunAsync(request, token)`：创建并立即处理，遇到确认返回 AwaitingApproval。
- `RecoverAsync()` / `ListAsync()`：恢复分类并查看记录；没有自动执行。
- `DecideAsync(taskId, permissionId, binding, approve)`：持久化批准 / 拒绝。
- `ExecuteApprovedAsync(...)`：执行冻结操作并持久化结果。
- `ReplaceArgumentsAsync(...)`：使旧审批失效，并保存新 Pending 请求。
- `CancelAsync(id)`：取消安全等待状态。
- `ReconcileAsync(id, uncertainCallId, observedSucceeded)`：保存人工观察；Interrupted 无未知调用时传 null。

## 验证结果与限制

本机 Windows 最终验证：独立 Core **126 项通过**，其中 V1 61 项、V2 65 项；原有 **832 项通过**；玉子 WPF、test-orb 与缺失包回退重启冒烟通过。V2 UI 使用隔离 Fake host，验证请求停在审批、点击批准后真实创建文件并显示 Succeeded、拒绝不创建文件。截图为 `output/durable-permission-waiting.png` / `durable-permission-succeeded.png`。所有测试产物均在被忽略的 output 内，不改用户任务、学习记录或设置。

新增测试还覆盖审批 Pending / Approved 两种重启恢复、旧参数/跨任务/根目录/风险变化、scope 撤回、取消、记录副本/CAS、独占 lease、坏 JSON/未知版本、未提交临时文件、分发前存储故障、成功后结果存储故障、超时/晚完成，以及真实子进程在写成功后 `Environment.Exit(73)` 并重开存储的场景。

运行 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\test.ps1`。构建仅出现原 `PackTests.cs` 两项 FormattedText 过时警告。由于用户的分发 `玉子桌宠.exe` 正在运行，验证通过 `build.ps1 -SkipDistribution` 构建并运行 `bin/Tamago.exe`；未停止用户进程或覆盖被占用的分发文件。

限制：Windows / .NET Framework 本地 JSON；无自动恢复模型计划、历史归档、审批过期策略或身份认证；单次恢复审批只完成冻结操作；Queued 跨重启启动需重新提供哈希匹配的输入，UI 当前提供审批、取消及人工核对，排队启动接口供宿主调用；新 Durable 功能通过独立 Fake 面板提供，已有 Qwen 聊天和桌宠瞬时动作仍使用 V1 兼容入口。Mac 未迁移，未在 Windows 宣称 Mac 验证通过。本次未实施 V3。
