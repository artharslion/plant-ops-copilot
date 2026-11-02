# 源码可读性与检查

## 约定

- C# 使用 4 空格；Vue/TypeScript 使用 2 空格；源码统一 LF。
- 一行一个语句，条件分支使用大括号，成员之间留空行。
- 业务对象使用完整名称，例如 `workOrder`、`transaction`；异步辅助方法使用 `Async` 后缀并表达目的。
- 服务方法按授权、输入校验、事务和加锁、重新校验、状态修改、审计、提交展开。拆分解析或规则辅助方法时保留事务顺序。
- 注释解释锁、权限复查、幂等或质量规则的原因，不要求所有成员编写 XML 注释。
- Vue 模板的多步骤事件放入具名函数，避免在属性中堆叠语句。
- 迁移代码与 OpenAPI 生成类型由生成工具管理，不做批量格式化。

## AI 代码与 Prompt 变更审查

AI 行为相关变更必须遵循 [AI 工程约束](AI_ENGINEERING_GUARDRAILS.md)；当前 v3 字段和状态以 [调查契约](INVESTIGATION_RESPONSE_V3.md) 为准。

- **禁止以特定措辞冒充语义判断**：新增 `Regex`、`Contains`、`StartsWith`、关键词表、按告警/症状特判的分支时，审查它是否在推断原因、建议是否安全或判断状态；合法协议解析、输入校验、结构化枚举和确定性质量/阈值规则不受此限制。
- **变更前先分类**：区分数据/证据缺陷、工具编排缺陷、输出契约缺陷及模型语义缺陷；写清权威不变量，避免为了单条失败样例堆叠校验和 Prompt 特例。连续出现类似补丁时，先审视证据表示与设计。
- **双轨验证**：确定性单元/集成测试覆盖结构、引用、权限、质量和事务；语义评估单独覆盖自然语言改写、反例、未见场景、误通过与误拒。单测或 Fake 成功不等于真实模型语义通过。
- **变更说明**：简述失败根因、职责层、泛化理由、新增特判或 Prompt 规则、回归与反例、兼容与成本影响，以及实际执行/未执行的检查。纯文档或格式调整不需要模型运行。
- 外部模型评估须取得**当次**明确授权，遵守现有请求账本与额度；测试数据库和保留证据按既有要求处理，不自动续用历史预算。

## 后端

`.editorconfig` 配置格式与规则，`Directory.Build.props` 开启构建检查和锁定版本的 StyleCop.Analyzers。
当前只启用 SA1107（每行一条语句）、SA1500（大括号布局）、SA1502（元素展开）和 SA1516（成员间距），以及 IDE0011（大括号）与 IDE0055（格式）。
SA0001 被显式关闭，因为本仓库没有开启 XML 文档检查。其余 StyleCop 类别默认关闭。

在仓库根目录执行：

```powershell
dotnet restore PlantOps.sln --locked-mode
dotnet format PlantOps.sln --no-restore --exclude src/PlantOps.Infrastructure/Persistence/Migrations
dotnet format PlantOps.sln --no-restore --verify-no-changes --exclude src/PlantOps.Infrastructure/Persistence/Migrations
dotnet build PlantOps.sln --no-restore
dotnet test PlantOps.sln --no-restore
```

后端数据库测试要求独立 `plantops_tests` 数据库，连接设置见 README。长期重复运行会保留测试数据；需要干净状态时可在该测试库新建独立 schema，并通过测试连接的 `Search Path` 指定。不要将测试指向业务库。

## 前端

在 `web` 目录执行：

```powershell
npm ci
npm run format
npm run format:check
npm run lint
npm run build
npm test
```

Prettier 负责排版；ESLint 使用 JavaScript/TypeScript 推荐规则和 Vue 基础规则，关闭与 Prettier 冲突的格式规则。工具版本与依赖锁文件一并提交。
ESLint 10 要求 Node 22.13 或更高的 22.x；本仓库开发与 CI 使用 22.23.1。

## 持续检查

`.github/workflows/code-quality.yml` 对 push 和 pull request 执行锁定依赖恢复、格式检查、构建和测试，数据库测试使用临时 PostgreSQL 服务。
新增规则应先清理现有诊断，再启用 warning；本仓库已有 `TreatWarningsAsErrors`，warning 会阻止构建。

格式修改和业务重构分开提交，方便审阅。格式化工具不会替代业务命名和职责审查。


## UI 组件约定

- Element Plus 组件在 `main.ts` 显式注册；新增组件时同步注册，避免完整插件安装带入全部组件。`ElConfigProvider` 跟随 `i18n.ts` 的当前语言提供中英文组件提示。界面文字使用 `t()`，英文词条维护在 `locales/en.ts`；包含翻译的标签集合使用 `computed`，保存/错误提示使用 `messageRef` 延迟求值，以支持即时切换。业务内容及接口枚举保持原值。依赖固定版本并更新锁文件。
- 业务列表使用 `ElTable`，行键使用稳定业务 ID；设备/资料/工单名称提供可聚焦按钮，不只依赖鼠标点击行。空态使用业务文本，前台读取使用 `v-loading` / Skeleton，后台补查不得反复遮盖表单。
- 表单使用 `ElForm` / `ElFormItem` / Input / Select / Checkbox；`prop` 对应模型字段，提交前等待 `validate()`。输入长度和必填约束以及服务端校验必须保留。文件仍用原生文件选择器，后续上传沿用授权 API，禁止绕开下载与 AI 引用重授权。
- 主按钮明确 `type="primary"`；提交按钮设置 `native-type="submit"`，普通操作保持默认 button。草稿/复核等分支不得随组件迁移改变提交含义。
- `SidePanel` 是 Drawer/Dialog 的薄封装，统一 title、焦点约束、Escape、关闭保护及非点击遮罩关闭。简单编辑用 `mode="dialog"`；维护工单通过全局 `WorkOrderDrawer` 在当前设备/告警上下文上打开一层新抽屉，不跳转独立页面。App 层抽屉启用可卸载的全局路由离开保护。嵌入设备 Drawer 的资料采用 Dialog，独立资料页采用 Drawer。
- `useLeaveProtection` 接收响应式 dirty/busy 读取函数，不在后台响应时重置表单来解除保护。各配置子组件暴露 busy 供外层浮层使用；保存成功后再更新快照。
- 标签内容必须放在对应 `ElTabPane` 中。测量用条件挂载；工单/资料隐藏分区保留状态并停用后台读取。销毁时注销订阅和计时器。
- 全局基础 CSS 不得覆盖 `.el-*` 内部输入、按钮与焦点。主题采用 `--el-*` token；基准间距 4/8/16/24/32px、4px 圆角、13px 正文。窄屏允许表格内部水平滚动，页面本身不得横向溢出，工单操作栏不能遮住最后一项输入。

官方接口参考：[安装](https://element-plus.org/en-US/guide/installation.html)、[Drawer](https://element-plus.org/en-US/component/drawer.html)、[Dialog](https://element-plus.org/en-US/component/dialog.html)、[Form](https://element-plus.org/en-US/component/form)、[Table](https://element-plus.org/en-US/component/table.html)、[Tree](https://element-plus.org/en-US/component/tree.html)、[Steps](https://element-plus.org/en-US/component/steps.html)。升级依赖时先核对这些 API，再检查关闭保护、路由深链接及后台刷新行为。
