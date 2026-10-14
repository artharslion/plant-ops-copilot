# P3.2 授权资料证据 Implementation Plan

> 用户已授权接续实施；在当前会话逐项执行、验证和提交，不创建子代理。

**Goal:** 为告警调查提供当前 actor 可读取、可重新授权展开的版本化 TXT/Markdown 证据。

**Architecture:** 复用 DocumentService 的身份、工厂及文档 ACL 边界。上传时在同一事务保存完整解析片段，旧版本仅在授权检索时按需补建；不启用向量服务或模型联网。以当前发布版本和实时源授权为准，不缓存授权结果。引用绑定设备范围、文档版本、片段及内容 hash，旧版替换/撤回/撤权后失效。

**Tech Stack:** .NET 10、EF Core/PostgreSQL、xUnit、现有 Cookie API。

## 选择与限制

- 使用持久化片段，避免每次查询重复解析；相较全量后台索引，按需补建可保留既有资料而无需迁移业务数据。迁移只在新建隔离测试 schema 验证，业务库留待显式部署。
- UTF-8 解析不执行 Markdown/HTML。保留标题路径、原始行号、ordinal、parserVersion 和 SHA-256。单版本最多 256 KiB/256 片段，每片段最多 2,000 字符；超限整版不纳入，禁止无声截断。PDF 标记 NeedsText。
- 确定性查询最多 200 字符/12 术语、6 个结果，每次最多 50 个授权当前版本/1 MiB 旧资料补建。达到上限返回服务端缺口提示。术语命中分数只代表相关性。
- 查询固定到服务端传入的设备及工厂，仅本设备及工厂公共资料。授权过滤在取正文前执行。检索/展开持有与发布和 ACL 更新相同的工厂事务锁；返回前再验证 enabled、角色和工厂权限。
- 引用由服务端生成路径；HTTP no-store。引用 ID 本身不授予权限，不向客户端输出存储 key、上传人或未授权资料提示。

## Task 1：片段与解析

Create: `src/PlantOps.Application/Knowledge/TextDocumentParser.cs`、`DocumentEvidenceContracts.cs`。
Modify: `src/PlantOps.Domain/Knowledge/Documents.cs`、`src/PlantOps.Infrastructure/Persistence/KnowledgeModel.cs`、`PlantOpsDbContext.cs`、`Knowledge/DocumentService.cs`。
Test: `tests/PlantOps.Tests/DocumentParserTests.cs`。

先测试 UTF-8/标题/行号/hash/大小与片段上限，再实现纯解析器；上传与片段原子保存，生成 additive migration，不运行业务库迁移。

## Task 2：授权检索与展开

Create: `src/PlantOps.Infrastructure/Knowledge/DocumentEvidenceService.cs`。
Modify: `DocumentService.cs`（partial 与实时用户读取）、`DependencyInjection.cs`、`src/PlantOps.Api/DocumentEndpoints.cs`。
Test: `tests/PlantOps.Tests/DocumentEvidenceTests.cs`。

验证草稿/旧版/撤回/跨厂/其他设备/ACL/角色与工厂撤权、禁用用户均不可读取；同 scoped 服务也不能沿用已跟踪的旧授权。测试 TXT/Markdown、中英文术语、PDF/解析缺口、伪造引用、取消及旧资料补建幂等。

## Task 3：验证与提交

运行解决方案构建、解析器测试，以及新隔离 schema/资料目录中的 DocumentTests + DocumentEvidenceTests。记录真实通过/跳过数量和保留位置；补充 `docs/AI_EVIDENCE_VALIDATION.md` 与 ROADMAP/AI_DESIGN 进度并立即提交。

本增量完成后仍继续 P3.3–P3.5：AiRun、联合工具、事实语义校验、告警 UI 和 20 案例；不把检索交付等同完整 AI 调查。

## 实施结果

2026-10-10 Task 1–3 完成。91 项测试全部通过，构建零警告，前端构建/lint/改动文案格式通过。新增 `deploy/Test-DocumentEvidence.ps1` 支持每轮独立 schema 和资料目录，文件读取层增加取消与实际字节上限。仅变更文档页解析状态提示，没有加入调查按钮。细节和保留记录见 [验收](../AI_EVIDENCE_VALIDATION.md)。
