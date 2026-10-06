# Scheduler V1

本文保留 Scheduler 阶段验证基线；当前 [审计归档 V1](audit-archive-v1.md) 使用 schema 5，并增加手动归档、退役 ID 和单调触发序号，下面的调度/预算/审批规则保持不变。

[整组备份与校验恢复 V1](data-backup-v1.md) 只能在宿主停止并释放存储后进行。恢复会永久取消快照中的旧计划、撤销旧批准并隔离未完成运行；重新连接不启动宿主，继续运行需显式创建新计划和新的操作批准。原调度边界保持不变，所选主库仍为 schema 5。

用户显式创建的本地文件计划；复用 V2 的 `AgentDurableService`、`AgentCoreRuntime`、工具校验和操作审批。没有新增模型能力、外部工具、远程入口、系统服务或开机启动。测试只使用 Fake Provider 和隔离合成数据。

## 使用

在“任务”中展开“本地计划任务”。上方的文件名作为前缀、文本作为每次运行的内容；填写名称、首次当地时间和 Windows 时区，选择一次/每天/每周并保存。**保存计划不会启动宿主**，点击“启动宿主”后才轮询；每次文件写入仍须在对话中的权限卡片核对并允许。文件名为 `前缀-触发ID前16位.txt`，限定 `agent-files` 目录、仅创建不覆盖。

宿主可暂停/恢复分发和停止；关闭桌面面板仅隐藏窗口，进程仍调度，UI 刷新计时器不参与调度。退出玉子会取消当前运行、等待安全检查点，然后释放本地存储。系统关机/强制退出后只能在下次**手动启动**宿主时恢复，无关机后执行保证。没有任何 Windows Service、启动项、计划任务或网络配置变更。

计划可暂停/恢复/取消。计划暂停不挂起已经分发的操作；恢复保留原游标并按 Skip 策略记录错过时间。取消计划同时取消可取消的关联运行，并关闭待确认批准；实际完成的效果不回滚。每个运行可在排队/等待确认时暂停和恢复；正在执行的运行只能请求取消，不能暂停工具。取消已提交操作不会抹除结果或猜测成功。

## 调度语义与边界

| 项目 | V1 规则 |
| --- | --- |
| 日期/精度 | 2000–2099，`yyyy-MM-ddTHH:mm`，分钟精度，无秒、cron、月份、工作日组合 |
| 一次 | 指定当地时刻一次，游标结束后禁用计划 |
| 每天 | 从首次日期开始，每个日历日同一当地时间，不是固定间隔 24 小时 |
| 每周 | 首次日期的星期，每隔 7 个当地日历日同一时刻；不支持一次多个星期 |
| 时区 | 固定 Windows TimeZone ID；不随机器当前时区改变，持久保存当地游标和 UTC deadline |
| 春季 DST 空洞 | 记录 `Missed / InvalidLocalTime`，不创建运行；恢复下一个合法日历时刻 |
| 秋季 DST 重叠 | 明确选择较早 UTC 时刻，重复的当地时间仅产生一个触发 |
| 轮询 | 活跃宿主默认每秒；睡眠/阻塞不提供精确定时保证 |
| 到期宽限 | ≤60 秒视为当前触发；超过宽限即 Skip，无补跑选项 |
| 大跨度漏触发 | 一条范围记录保存开始、结束和 occurrence count；不生成一堆补跑任务 |
| 并发 | 最多 1 个调度运行；同时与手动 durable 文件操作共用 service gate，不并行绕过边界 |
| 预算 | 每次运行 1–120 秒，UI 默认 30 秒；包括各执行阶段实际耗时，等待用户审批/排队不计时 |
| 内容/容量 | 名称 64 字符，文件内容 400 字符、前缀 32 字符；32 个计划、512 条触发审计、原有 256 个 durable 任务、整个文件 8 MiB |

规则和内容创建后不可编辑；需要变化时取消并创建新计划。批量错过范围的 reason 表示开始那次的原因，范围内不会逐条区分 DST 和普通错过。凭证及常见不必要敏感内容复用现有内容策略拒绝保存；规则不是完整的 DLP。计划不产生 Memory 或保存任意对话历史。

预算累加至 trigger `UsedMilliseconds`，在每个执行阶段前写入 Running，阶段完成后记录耗时。审批恢复在 **Durable Service 内**再次获取执行 lease、检查暂停/取消、剩余预算和未知任务，再进入原 Runtime。模型由关联 cancellation token 中断；已经分发的工具不盲目取消或再次执行，工具等待时间上限缩到剩余预算，超时/晚返回结果记为 Unknown → NeedsReview。预算耗尽的调度记录为 TimedOut，底层 task 仍保留实际 Cancelled/Failed/Succeeded 或 NeedsReview；已知成功不伪装失败。

进程在阶段结束与预算提交之间退出时，不假定预算可以重新领取：未确认阶段预算记 `BudgetUnconfirmed` 并保守耗尽；有未知副作用的任务继续优先 NeedsReview。暂停和停止只能阻止后续分发；已经接收的那次 tick/操作会到达安全边界。停止宿主不撤销尚未执行的审批；用户仍可显式处理对话卡片，仍受运行暂停、取消及预算限制。

DST 判断使用系统 [TimeZoneInfo.IsInvalidTime](https://learn.microsoft.com/en-us/dotnet/api/system.timezoneinfo.isinvalidtime?view=netframework-4.8.1)、[IsAmbiguousTime](https://learn.microsoft.com/en-us/dotnet/api/system.timezoneinfo.isambiguoustime?view=netframework-4.8.1) 与明确 offset 选择，避免隐含的 ambiguous-time 转换默认值。

## 边界与持久化

- `AgentScheduleContracts.cs`：DTO、`IAgentScheduleStore`、日历规则、执行 lease 契约。
- `AgentSchedulerService.cs`：显式计划、错过策略、分发、恢复、暂停/取消、累计预算；`AgentSchedulerHost` 只管理手动生命周期和独立轮询。
- `JsonAgentScheduleStore.cs`：通过原 JSON adapter 实现事务和 CAS，无文件访问散落至 Core/Tool/UI。
- `AgentDurableHost.cs`：组合同一个 service/tool registry/permission policy/store；宿主打开后默认停止。
- `AgentSchedulerApp.cs`：绑定用户操作和展示状态，计时器只读刷新，无任务状态机或恢复策略。

路径仍为 `%LOCALAPPDATA%\TamagoPet\agent-v2\tasks\tasks.v2.json`。Scheduler 阶段的 schema **4** 增加独立 `Schedules` / `Triggers`；当前 schema **5** 加入只读归档清单及跨归档保留的单调触发序号，首次打开合法 schema 2/3/4 时一次原子升级。旧任务、审批、Memory 和对话不转换为计划。继续遵守已有对话保留策略。损坏/不支持的数据、未知时区或不合法游标拒绝打开并保留原文件。迁移失败不覆盖旧库；旧版 EXE 不支持 schema 5，降级须自行管理旧数据备份。实现与测试没有读取或迁移真实用户库。

触发 ID = SHA-256(schedule ID + 当地 occurrence)，TaskId/RunId 为新的 GUID。存储在**同一事务**中推进游标、加入不可重复的 trigger 和 Created task，然后才调用 `StartQueuedAsync`；没有“先运行再记录触发”的间隙。CAS 拒绝旧版本和重复触发。触发 Sequence 持久保存 FIFO 顺序，不因更新记录而重新排队。时钟回拨不倒退游标，时钟前跳按 Skip 留审计。

任务状态枚举数值保持兼容：Created / Queued / Running / WaitingForApproval / Succeeded / Failed / Cancelled / Blocked / Interrupted / NeedsReview。

调度记录状态：Queued / Paused / Running / WaitingForApproval / Succeeded / Failed / Cancelled / NeedsReview / Missed / TimedOut。底层 task 是真实执行结果来源，调度状态同步它，并补充计划暂停、预算和错过语义。

## 重启与副作用

1. 恢复原 Durable Store，Dispatching/Unknown → NeedsReview；Running 无未知效果 → Interrupted；均不重放。
2. 没有执行阶段的 Created/Queued 可使用持久冻结的、安全校验的请求继续进入 Runtime；WaitingForApproval 保留本次具体操作，调度器不自动批准或执行已批准操作。
3. 每次新 occurrence 使用新 Task/Run/Call/Tool/参数绑定，前一次批准不能用于下一次。参数变更由已有 `ReplaceArgumentsAsync` 使批准失效。
4. 任意 durable task 存在 NeedsReview/Interrupted 时，Scheduler 保守阻止新执行；可以持久排队新的到期运行，等待先确认旧工作停止、检查效果并人工 reconciliation。
5. 存储不通或检查点失败时 host Faulted，禁止自动重启/重试。修复存储后须重新打开进程并恢复。容量用尽也安全停止；现可停止宿主并显式预览/确认已结束记录的归档，不自动清理或丢弃审计记录。存储故障关闭的实例须先重开，修复容量后仍由用户手动启动宿主。

不承诺对任意不配合的工具保证“恰好一次”。未知效果和崩溃窗口通过人工核对关闭，核对前要确认旧工作已停止；迟到成功不能自动解除 Unknown。磁盘 I/O 没有独立硬截止，系统磁盘卡死时停止/预算可能延后；V1 是单进程、单写入者、本地 Windows 宿主。

## 验证

`test-core.ps1`：263 项 Core 检查（原 211 + Scheduler 52），覆盖 once/daily/weekly、时区、DST 两边、宽限与批量错过、计划/运行/宿主暂停、取消、FIFO/并发 1、预算、审批恢复累计预算、审批绑定/拒绝/换参/撤销 scope、Created/Waiting 恢复、CAS 重复触发、存储不可用与事务失败、晚返回工具、损坏数据、schema 2/3 迁移。额外子进程在真实隔离文件写入后 `Environment.Exit(75)`，重开验证文件保留、任务 NeedsReview、无再次写入。

`test.ps1 -BuildDirectory .\output\scheduler-v1\bin`：完整构建、847 项既有检查、桌宠/学习/V2/V3/会话/DPI 回归，以及新 Scheduler WPF 实际运行：保存计划而不启动、显式启动、面板隐藏后触发、对话权限卡片批准、真实隔离文件与结果持久化、显式停止；同一 EXE 的 test-orb / missing-package 重新启动回归。

预览在 `output/scheduler-v1/output/ui-scheduler-plan.png`、`ui-scheduler-permission.png`；构建产物在隔离 output 目录，没有覆盖已运行的分发文件。没有真实模型、真实个人数据或外部账户联调。
