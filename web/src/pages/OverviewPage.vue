<script setup lang="ts">
import { onMounted, ref } from 'vue'

type Status = 'checking' | 'online' | 'offline'
const api = ref<Status>('checking')
const database = ref<Status>('checking')
const loading = ref(false)
const checkedAt = ref('尚未检查')
const labels: Record<Status, string> = { checking: '检查中', online: '已就绪', offline: '未就绪' }

async function probe(path: string): Promise<Status> {
  try {
    const response = await fetch(path, { signal: AbortSignal.timeout(6000), cache: 'no-store' })
    const body = await response.text()
    return response.ok && body.trim() === 'Healthy' ? 'online' : 'offline'
  } catch {
    return 'offline'
  }
}

async function refresh() {
  if (loading.value) return
  loading.value = true
  api.value = database.value = 'checking'
  try {
    ;[api.value, database.value] = await Promise.all([
      probe('/health/live'),
      probe('/health/ready'),
    ])
    checkedAt.value = new Date().toLocaleTimeString('zh-CN')
  } finally {
    loading.value = false
  }
}
onMounted(refresh)
</script>

<template>
  <header class="topbar">
    <span>工作空间 / 总览</span><span class="stage-chip">PHASE 01</span>
  </header>
  <section class="page-heading">
    <div>
      <p class="eyebrow">PLANT OPERATIONS</p>
      <h1>从可信数据，走向可靠行动。</h1>
      <p class="intro">工业资产、告警与维护的统一工作空间。登录后可浏览授权工厂的资产目录。</p>
      <RouterLink class="directory-link" to="/assets">进入资产目录 →</RouterLink>
    </div>
    <button :disabled="loading" @click="refresh">
      {{ loading ? '正在检查…' : '刷新连接状态' }}
    </button>
  </section>
  <section class="status-grid" aria-label="环境状态" aria-live="polite" :aria-busy="loading">
    <article class="status-card">
      <div class="card-label">应用服务 <span>01</span></div>
      <h2><i :class="api" />{{ labels[api] }}</h2>
      <p>服务进程可响应请求</p>
    </article>
    <article class="status-card">
      <div class="card-label">业务数据库 <span>02</span></div>
      <h2><i :class="database" />{{ labels[database] }}</h2>
      <p>连接可用且迁移已应用</p>
    </article>
    <article class="status-card planned">
      <div class="card-label">调查助手 <span>03</span></div>
      <h2>计划中</h2>
      <p>Phase 3 · Investigation Copilot</p>
    </article>
  </section>
  <p class="checked-at">
    最后检查 {{ checkedAt }}<span v-if="api === 'offline'"> · 请确认应用服务已启动</span
    ><span v-else-if="database === 'offline'"> · 请确认数据库连接及初始化已完成</span>
  </p>
  <section class="path-panel">
    <div class="section-title">
      <div>
        <p class="eyebrow">GOLDEN PATH</p>
        <h2>一台泵，一条完整的业务路径。</h2>
      </div>
      <span>演示规划</span>
    </div>
    <div class="asset-story">
      <span class="asset-code">P-101</span>
      <div>
        <h3>Cooling Pump P-101</h3>
        <p>North Plant <span>→</span> Utilities</p>
      </div>
      <span class="fictional">虚构演示设备</span>
    </div>
    <ol class="path-steps">
      <li><b>01</b>振动上升</li>
      <li><b>02</b>规则告警</li>
      <li><b>03</b>人工确认</li>
      <li><b>04</b>维护工单</li>
      <li><b>05</b>完成关闭</li>
    </ol>
    <p class="path-note">
      已接入模拟振动、持续时间与回差告警、人工确认和关联维护工单。完成工单后保留处理记录，可继续查询设备履历与已发布文档。
    </p>
  </section>
  <footer class="principle">
    <span>设计原则</span><strong>AI is not the source of truth.</strong>
    <p>业务状态与动作由数据和业务服务决定；AI 负责检索、解释与建议。</p>
  </footer>
</template>
