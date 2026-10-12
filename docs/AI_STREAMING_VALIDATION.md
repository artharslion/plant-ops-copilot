# 延长等待与流式调查

2026-10-12。根据用户授权，将慢响应调查改为单请求默认120秒、整次默认180秒，允许上限180秒；token/工具/轮数上限仍为16k/2k、8工具/4轮。AiRun租约210秒，有限脚本等待Worker最多195秒。采集仍50秒，未增加设备采样时间。

## 实现与使用

`AI:StreamResponses=true`开启Chat Completions SSE，携带`stream_options.include_usage=true`。为兼容未验证流式的其他部署，应用该设置默认false；本轮有限演示显式true。`Use-LocalAi.ps1`支持加载此项；旧私密配置不改写。原文件中显式的较短timeout会覆盖代码默认值，启动真实API/Worker时可在加载私密配置后设置进程环境：

```powershell
. ./deploy/Use-LocalAi.ps1
$env:AI__RequestTimeoutSeconds = '120'
$env:AI__RunTimeoutSeconds = '180'
$env:AI__StreamResponses = 'true'
```

SSE按网络块增量读取并重组文本/工具参数，限制响应总字节数，要求finish和[DONE]完整结束；usage用于既有预算结算。随后仍经相同工具allowlist、actor重授权、schema及逐字事实/引用校验。中断、超时、取消、拒绝、截断不会返回半个答案。非流式路径保留；请求失败不自动改用非流式重复扣费。

页面按秒显示已等待时长、已获取证据数及排队/取证/分析状态，可展开已有证据或取消。这里是状态进度，不是逐字展示模型答案，也不是接收到token的实时计数；结构化结论完整校验后才显示。流式降低响应缓冲造成的无反馈等待，不承诺缩短模型生成时间。

## 验证记录

后端包含文本/中文跨网络分块、工具参数分块、独立usage帧、DONE后连接未关闭仍可完成、缺失DONE、坏JSON、超长流、拒绝/截断以及流中取消测试。原有非流式适配与二十个业务场景一起回归。前端TypeScript/Vite构建、ESLint通过；保留既有SignalR注释警告。未声称已完成新浏览器验收。

首轮`artifacts/ai-stream/focus_20261012`为新增代码格式错误、未执行测试，证据保留。完整回归`artifacts/ai-investigation/stream_stage_20261012/results.trx`为257/257通过、零跳过，约4分46秒；二十场景全部通过。另单独执行8项流式测试全部通过。脚本语法及diff空白检查通过。完整P3真实质量与P4仍待验收；本轮唯一真实流式调用见后续记录。
