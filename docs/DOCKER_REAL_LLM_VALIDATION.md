# Docker + IndustrialSim + 一次真实 LLM 调查

2026-10-22。用户明确授权采用Docker和真实LLM测试一次。本轮使用独立容器与全新测试schema，不切换现有Docker工作台的数据源或默认Fake配置。

## 执行链路与边界

1. SDK容器内运行现有spike有限运行器及普通MeasurementWorker；同一容器的独立IndustrialSim示例进程提供三点OPC UA Server，继续使用loopback `127.0.0.1:48481` 和精确节点批准列表，没有放宽远程接入策略。
2. 在独立 `plantops_tests.opcua_runtime_dockerllm_20261022_once` schema 中保存真实协议观测、采样区间、健康和规则告警。设备观测是原创仿真，SourceTimestamp是框架投影/读取时间，不代表新的物理采样。
3. 独立Docker API加载同一schema，经CSRF及admin登录创建一次调查；仅一次性Docker Worker加载真实模型密钥并执行 `--investigation-once`。API只需Provider/Model元数据，不注入模型密钥。
4. 预算：一次调查、4轮模型、8次只读工具、16k输入/2k输出核算、单请求120秒/整次180秒，MaxRetries=0；不发送额外探针、不自动重跑失败调用。调用结果和引用展开保留，校验触发读数关联、唯一领取、告警版本和工单数不变。
5. 结束移除自己创建的测试容器，删除短期环境文件；保留证据与测试schema。原有业务容器、业务库和私密配置文件不修改。密钥不写进镜像、源码或报告。

## 复现入口

先构建协议运行器和框架示例，再构建独立标签的API/Worker镜像。私密配置沿用根目录 `appsettings.Ai.Local.json`、`appsettings.Local.json` 和 `artifacts/p4/database.local.json`，不要打印或提交其内容。

```powershell
dotnet build spikes/PlantOps.OpcUa.Spike --no-restore
dotnet build D:/Repos/industrial-device-simulation-framework/examples/PlantOps.Scenario
docker build -f deploy/Dockerfile.backend --target api -t plantops-api:docker-live-20261022 .
docker build -f deploy/Dockerfile.backend --target worker -t plantops-worker:docker-live-20261022 .
./deploy/Test-DockerIndustrialSimLlm.ps1 -Run <全新小写名称>
```

脚本拒绝复用目录，提交调查前先落盘一次尝试标记；调查Worker只启动一次，不自动重新领取。真实模型请求可能计费，取消本地等待不保证上游停止计算。`InputTokenAccounting/OutputTokenAccounting` 沿用现有实际usage与未知请求预留混合核算，不能全部当作账单用量；费用未知。脚本中的环境变量仅指定测试运行器的登录密码，旧运行器缺省随机密码行为保持不变。

## 验证范围

本测试目标覆盖Docker中的协议采集、API授权、作业领取、真实Gateway和现有事实/引用校验；本轮仅完成协议、授权取证和真实请求尝试，模型失败，没有结果正文可供事实/引用输出校验。不等于浏览器操作验收、供应商稳定性、现场设备、30分钟可靠性或P6多源联合分析完成。采集结束后才调查历史告警，不声称实时新鲜的多点证据已进入现有AI工具。没有工单创建/备注执行。

## 本次结果：协议通过，真实模型请求失败

运行名 `dockerllm_20261022_once`，北京时间10:07:38–10:10:27。Docker内的三点采集通过，共138条真实OPC UA入库读数，Server停服/恢复、两次重连及温度规则告警恢复均通过；采集结果 `artifacts/opcua-runtime/dockerllm_20261022_once/result.json`。这次完整的LLM调查 **未通过**，不能据此声称模型接入验收成功。

| 项目 | 本次实际结果 |
|---|---|
| API / 任务 | Docker API登录成功，经授权HTTP创建唯一AiRun，Docker一次性Worker仅领取一次 |
| 真实Provider / 请求模型 | `OpenAiCompatible` / `gpt-6.1-sol`；`AiSimulated=false`，没有Fake降级 |
| 调查起止 | 10:09:12.991–10:10:13.944，约60.95秒 |
| 模型结果 | 1次真实HTTP尝试，收到HTTP **502**；Gateway日志 `stage=http-status; status=502; attempt=1; cloudflare=True`，状态Failed / FailureCode=Unavailable；没有可用结果正文 |
| 预算与工具 | 1轮模型，5次既有只读取证工具，零自动重试 |
| Token / 费用 | 输入核算8609、输出核算1200；失败请求保留预留，不能声称模型实际消费了这些Token。计费未知，EstimatedCost=null |
| 关联与写入 | 告警触发读数存在于同一138条协议观测，引用账本关联通过；告警版本与工单数不变 |
| 清理 | 自建的采集/API/调查容器已移除，临时collector/api/worker环境文件已删除；测试schema和脱敏证据保留 |

AiRun `74449dc7-9e8c-4aa1-8600-b21c651c6fa3`，告警 `0e8f5a1e-1c77-42f9-afe2-24df90ad4bec`，触发读数 `d2f01470-8d05-49f3-b884-45c1ce23f97c`。HTTP502只能定位到模型服务链路返回错误，无法据此确定Cloudflare、代理、模型后端中哪一环是根因；本轮没有额外探针或第二次请求去验证。

证据目录 `artifacts/docker-live/dockerllm_20261022_once/`：`scope.json`（预算）、`investigation-attempt.json`（写入前的一次尝试标记）、`investigation-queued.json`、`investigation.json`、`expanded-evidence.json`、`investigation-worker.log`、各容器日志及 `result.json` / `failure.json`。模型账本明确提示无可用分钟趋势、停用后的当前可信状态不可验证，没有补造趋势。

`result.json` SHA256：`3D5B4698850990955BD86B5B702F53FC7597B2B8D976D3B85364D9E22D70002F`。本轮日志/报告检查未发现真实API密钥字符串，三个临时环境文件均已不存在；原业务API/Worker/Web/数据库仍运行且健康。

本轮还观察到Linux镜像缺少可选 `libgssapi_krb5.so.2` 的提示；数据库读写已完成，它不是HTTP502的证据。脚本后续对齐现有Compose连接设置，增加 `GSS Encryption Mode=Disable`。该设置变更只做语法/配置核对，**没有因此重新执行真实模型调用**。

构建：spike、API和Worker Docker镜像均通过；API/Worker采用独立 `docker-live-20261022` 标签，没有覆盖正在运行的`:local`容器。镜像摘要及三个实际容器ID见原始result.json；宿主编译的可移植.NET协议运行器在固定SDK镜像中运行，不声称它是发布版Worker镜像入口。未跑完整后端/前端回归或浏览器路径。本轮整体结果为失败，保留后等待用户决定是否另行重试。
