# Memory V1 — V3

本文记录 Memory V1 语义与 V3 阶段基线。[Scheduler V1](scheduler-v1.md) 加入独立计划记录，当前 [审计归档 V1](audit-archive-v1.md) 将共享事务库升级为 schema 5，支持由合法 2/3/4 原子迁移，Memory/Conversation 的授权、保留与删除边界不变。归档不会复制 Memory 或对话正文。

[整组备份与校验恢复 V1](data-backup-v1.md) 提供独立的人工校验恢复入口。首次恢复后主库由同一逻辑根下的 `active-store.json` 选择，schema 仍为 5。忘记记忆或清除对话不擦除历史快照及保留的旧数据；明确恢复旧快照可能带回已删除内容，需用户核对和确认。普通 Memory 删除与后续检索语义保持不变。

## 基线与边界

开始时工作区干净，分支 `codex/windows-macos-ui-v1`，基线 `63c423c`。复用 V1 的无 WPF Core、Provider/工具/权限注入与 V2 的本地原子事务、操作绑定审批、执行检查点及保守恢复。原 `IAgentMemory` 是未接入的简单占位接口，缺少来源、确认、范围与版本，因此增加独立的强类型存储端口，没有重写既有 Runtime 或桌宠工具。

| 集合 | 保存内容 | 自动转为用户事实 |
| --- | --- | --- |
| `Tasks` | V2 任务、执行、审批；正文只为尚未分发的具体审批保留，终结/分发后清除 | 不会 |
| `Conversations` | 用户主动勾选保存的一次查询与最终回复，含角色和交换 ID | 不会 |
| `Memories` | 明确用户操作且已批准的事实 | 仅由批准后的操作写入 |

`IMemoryStore` / `IConversationStore` 是异步端口；`JsonAgentTaskStore` 的 partial 本地适配器实现两个端口及既有任务端口。磁盘访问、租约、序列化、校验和提交集中在 Persistence。Core 的 `AgentMemoryService` 负责来源、范围、授权和检索，内部 Memory 工具通过 V2 执行。WPF 只展示数据并提交用户操作，不拥有状态机、恢复、存储或权限规则。

每条 Memory 包含 GUID ID、内容、`ExplicitUser` 来源、用户操作 GUID 来源引用、创建/更新时间、Personal/Work/Study 范围、`UserConfirmed` 状态、确认时间和乐观版本。修改保留 ID/创建时间，更新来源引用与版本。V1 不提供范围迁移，需忘记后在新范围重新明确添加。未确认内容是独立持久审批请求，尚未成为 Memory。

## 操作与权限

工作台的“记忆”抽屉提供记住、查看、修正所选和忘记所选；单次确认显示在对话权限卡片中，技术数据在折叠详情。消息右键菜单也可明确请求记住，仍需批准。写入/修正为 LocalWrite，忘记为 DestructiveAction；所有变更需人工批准。普通聊天中的“记住”文本只是数据，不能冒充这个入口。

可信用户入口创建 `UserMemoryIntent.ExplicitUserAction`，固定 Provider 构造精确内容、范围、目标 ID、期望版本和来源引用。审批绑定既有 Task/Run/Call/Tool、规范化参数/哈希、风险等级、工具契约及存储根路径。旧版本、内容或根路径变化不能沿用旧批准。拒绝不会执行工具。

读写 scope 为 `memory:<personal|work|study>:<read|write>`，默认拒绝；本机单用户宿主显式授予自己的三个范围读写权限。检索和工具实际执行均重新检查当前授权。一个 Memory Service 串行处理变更，UI 共用 V2 操作锁；宿主应保持一个活动写入编排者，不跨服务并发恢复同一个活跃任务。

记忆写工具不公开给查询模型，查询注册表为空；模型指令、历史工具结果、外部文档、伪造的 Origin / 确认字段均不能取得写入能力。`UntrustedData` 无法声明 ExplicitUser；存储也拒绝非用户来源和未确认记录。边界约束模型文本与应用数据流，不提供对恶意 CLR 代码或直接修改本地文件的沙箱。公开存储接口与用户动作工厂供可信宿主代码使用。

## 检索与模型上下文

每次模型决策重新读取当前 scope 已确认记录，不缓存旧事实。英文/数字按词、中文按相邻双字匹配，按匹配项数、更新时间、ID 排序，提供 MatchScore。未匹配不返回；最多 5 条，序列化后的 UTF-8 JSON 最多 4096 字节，条目超限时跳过。没有向量、画像、摘要或事实推断。

`AgentCoreModelTurn.MemoryData` 是独立不可变数据字段，JSON 包含 `dataOnly: true`，不会替换系统上下文或授予工具权限。Fake Provider 只反馈匹配条数。未来 Provider 必须保留数据语义，不能将 Memory 转成系统指令。本阶段未接入旧 Qwen Adapter，也没有真实模型联调。

## 保留与删除

- 最多 128 条长期记忆、每条 400 字符；容量满拒绝添加，不自动淘汰用户事实。用户忘记前一直保存。
- 对话默认不持久保存；仅本次明确勾选时保存最多 1000 字符的用户消息和安全最终回复，不保存工具输出、检索正文或系统上下文。最多 128 条角色消息（64 个完整交换）、30 天；读/写/重开时惰性清理，过期或超量按完整交换移除，没有后台 Scheduler。
- 清除对话只清除 `Conversations`；忘记只清除对应 Memory 及同 ID 尚未分发的变更审批正文，保留不含内容的任务审计。历史对话若含相同文字仍遵循独立保留规则，需要用户另行清除，但不会参与检索或生成事实。
- 删除、关联旧批准失效和清除正文在同一事务提交。后续请求不再携带删除事实；已发送给模型的在途数据不能回收。
- API Key、Token、密码、认证/Cookie、常见凭证格式及身份证/银行卡/手机号等非必要敏感内容在提案前拒绝；持久化再次校验，敏感模型回复不保存。过滤是保守规则，可能误拒绝，无法识别没有标签且格式未知的秘密，不应作为 Secret 存储。
- 本地明文文件受本机文件权限保护，没有加密和物理安全擦除；不保留包含旧内容的应用备份。系统备份、文件系统日志和截图由各自机制管理。

## 迁移与故障

仍使用 `%LOCALAPPDATA%\TamagoPet\agent-v2\tasks\tasks.v2.json`；文件名保留兼容路径，文档 Version 升为 3。首次打开合法 version 2 文档，保留 Tasks/执行/批准字段，增加空 Memories/Conversations 后原子提交，不推断用户事实。version 3 缺失集合、缺失必要字段、敏感/无效记录或损坏 JSON 均拒绝打开，保留原文件；不静默清空或跳过坏记录。没有自动修复损坏数据的界面。旧 V2 二进制不能读取 version 3，降级需用户管理的旧版本数据备份；测试没有打开或迁移用户真实数据库。

同目录临时写入、`Flush(true)`、`MoveFileEx(REPLACE_EXISTING | WRITE_THROUGH)` 原子替换、独占 writer.lock 和共享 async gate 沿用 V2。Tasks/Memory/Conversation 作为完整文档提交。磁盘提交失败后实例拒绝继续工作，需重开检查最后提交状态。未提交临时文件不会被提升成真状态。

工具执行前持久化 Dispatching 并消费批准。Memory 实际提交成功但任务成功记录未提交时，恢复为 Unknown / NeedsReview，不能重放旧操作；人工检查 ID、内容和版本，再在持久任务面板登记 reconciliation。Memory 事务和任务结果是两个检查点，不声称端到端 exactly-once 自动恢复。其他中断沿用 V2 Interrupted 语义，原任务状态枚举不变。

## 验证

`test-core.ps1` 独立编译 Core/Persistence DLL 和控制台测试，不引用 WPF、PetPort、HTTP、Qwen 或模型 API。新增合成数据验证 CRUD、跨重开、批准/拒绝、版本、scope 撤销、来源绕过、凭证拒绝、相关性/字节限额、删除后上下文、旧编辑失效、对话默认关闭/清除/保留/顺序、迁移、损坏和提交失败；还验证 Memory 成功后结果检查点失败、恢复不重放、人工核对及并发用户请求。

`test.ps1` 包含 Core、既有引擎/学习/Agent 回归、WPF Memory 全流程和同一 EXE 的 test-orb / 缺包 fallback 重启回归。Core 文件夹测试使用独立 `%TEMP%/TamagoCoreTests-<GUID>`，实际路径写在 `output/core-tests.txt` 首行；含真实子进程保存后退出、重新打开并完成原待批准操作的测试。UI smoke 使用独立 `output/durable-ui/<GUID>`，不触碰用户数据库或角色选择；截图和日志位于忽略的 output。构建使用 `-SkipDistribution`，避免覆盖运行中的独立分发 EXE。

本机源码工作区中的 Windows 原子替换曾间歇返回 Win32 5（沙箱内外均出现）；没有改变系统权限或增加自动重试。测试夹具改用 OS 临时目录，生产路径仍为 LocalAppData。若实际存储也被拒绝访问，沿用失败停止、重新打开/核对的规则；错误包含 Win32 编号以便诊断。

未加入 Memory 之外的后续阶段、外部工具、新模型 API、CLI、Web 或 Mobile。
