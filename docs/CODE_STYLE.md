# 源码可读性与检查

## 约定

- C# 使用 4 空格；Vue/TypeScript 使用 2 空格；源码统一 LF。
- 一行一个语句，条件分支使用大括号，成员之间留空行。
- 业务对象使用完整名称，例如 `workOrder`、`transaction`；异步辅助方法使用 `Async` 后缀并表达目的。
- 服务方法按授权、输入校验、事务和加锁、重新校验、状态修改、审计、提交展开。拆分解析或规则辅助方法时保留事务顺序。
- 注释解释锁、权限复查、幂等或质量规则的原因，不要求所有成员编写 XML 注释。
- Vue 模板的多步骤事件放入具名函数，避免在属性中堆叠语句。
- 迁移代码与 OpenAPI 生成类型由生成工具管理，不做批量格式化。

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
