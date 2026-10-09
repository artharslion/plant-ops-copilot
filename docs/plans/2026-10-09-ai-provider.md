# P3.1 模型接入 Implementation Plan

> 按本计划逐项实施和验证；用户已授权继续。当前会话直接执行，不创建额外会话或子代理。

**Goal:** 为已确认的 `https://zzone.cc.cd/v1`、`glm-5.3-prime` 准备可测的模型适配和本地密钥入口。

**Architecture:** Application 定义与供应商无关的 ILlmGateway 和调用预算；独立 PlantOps.AI 实现 OpenAI-compatible Chat Completions 及确定性 Fake。Api/Worker 仅注册依赖，不在启动时联网。独立控制台探针只发送原创虚构数据，验证普通回复、只读工具往返和 InvestigationResult.v1 原生 JSON Schema；不连接业务数据库，不注册任何业务写工具。

**Tech Stack:** .NET 10、HttpClient、System.Text.Json、现有 xUnit；不添加模型 SDK 或 JSON Schema 包。

---

## 设计选择

- 使用用户提供的 Chat Completions 协议，避免要求中转站支持 Responses API。
- 密钥放根目录独立 `appsettings.Ai.Local.json`，加入 Git ignore；本地加载脚本仅导出白名单 AI 配置，不碰现有 `appsettings.Local.json` 的账号密码。提交不含密钥的 example。
- 失败明确返回受控类型（未配置、超时、限流、鉴权、协议/格式不兼容、拒绝、截断、预算）；不自动更换模型或降成 Fake 成功，不打印响应正文、请求正文或异常内层。
- 总时限 60 秒，单次最多 30 秒，最多 4 轮/8 工具/16,000 输入 token 保守预留/2,000 输出 token 预留；最多一次瞬态重试，重试同样占预算。usage 缺失则成本未知。本轮不声称价格为零或完整 P3 通过。
- HTTPS，HTTP 仅 loopback；禁止重定向带走凭据。响应流有大小限制，取消传到底层网络。外部地址仅来自服务器配置。

## Task 1：本地配置与接入契约

Create: `appsettings.Ai.example.json`、`deploy/Use-LocalAi.ps1`、`src/PlantOps.Application/AI/LlmContracts.cs`、`LlmBudget.cs`。

Modify: `.gitignore`。

实现明确的角色、工具调用/结果、schema、usage、失败分类和每次尝试预留预算；本地文件存在则保留，缺失时创建空密钥模板。测试取消、时间/轮数/输入输出/工具限制。

## Task 2：独立适配层与版本化输出

Create: `src/PlantOps.AI/PlantOps.AI.csproj`、`AiOptions.cs`、`DependencyInjection.cs`、`OpenAiCompatibleGateway.cs`、`FakeLlmGateway.cs`、`InvestigationContract.cs`。

Modify: `PlantOps.sln`、Api/Worker 项目引用与 Program 注册；按需更新 lock 文件。

使用 HttpClient 发送明确消息/工具/schema。mock HTTP 验证地址/认证/请求内容、工具往返、schema、usage、错误正文不泄漏、取消/超时、瞬态重试预算、拒绝/截断/异常 JSON/超长响应和重定向。缺少密钥时 API/Worker 仍启动。

## Task 3：有限能力探针

Create: `spikes/PlantOps.Ai.Spike/PlantOps.Ai.Spike.csproj`、`Program.cs`、`deploy/Test-AiProvider.ps1`。

在 60 秒总预算内完成：普通回复 → 强制一个只读工具 → 合成工具结果续答 → InvestigationResult.v1 strict schema。没有密钥时明确 NotConfigured；Fake 与真实结果分别标记。JSON 报告只含能力状态、供应商/模型、用量/耗时和安全错误代码，不保存正文与密钥。原生 schema 不支持时明确失败，不降低验收要求。

## Task 4：验证与交付

Create: `tests/PlantOps.Tests/AiGatewayTests.cs`、`AiBudgetTests.cs`、`docs/AI_PROVIDER_SETUP.md`。

Modify: `README.md`、`ROADMAP.md`、`docs/AI_DESIGN.md`（标明 P3.1 基础设施进度）。

运行锁定还原、解决方案构建、新增测试、现有无数据库健康检查及 Fake 探针。验证本地文件确实被忽略、加载脚本不输出密钥。用户尚未填写密钥时，真实站点兼容性保留“待验证”，不做无授权数据发送。每个完成且验证的实现阶段立即提交。

本轮交付是 P3.1 接入基础，不包括 AiRun 持久化、授权检索、调查编排、告警 UI 与 20 案例评估；这些仍按 AI 优先计划后续推进。真实 Provider 联调需要用户在本地填写 key。

## 实施更新

用户随后在本地填写 key，并确认仅支持 `gpt-6.1-sol`，本计划初始 GLM 目标由该选择替代。保持用户本地 JSON/密钥原样，公共 example/default 更新为 Sol。使用官方支持的 low 推理强度、`max_completion_tokens`；实际中转 Chat Completions 工具行为以实验为准，不能外推官方兼容性。

首轮发现中转报告约 4,400 输入 token 的基线开销，已增加实际 usage 向上对账与失败预算锁定。能力探针改为三个独立测试预算、整套共用 60 秒，不将三个独立用例视为一次调查。业务调查上限仍是 16,000/2,000。

最终 70 项测试和构建通过；真实四项分别成功，但最近两次套件含超时/上游失败，未标记真实套件通过。详情与复现入口见 `docs/AI_PROVIDER_SETUP.md`。后续仍需 AiRun 和授权证据调查，不能声称 P3 完成。
