<script setup lang="ts">
import { computed } from 'vue'
import { t } from '../i18n'
import type { InvestigationAdvice } from '../investigationAdvice'

const props = defineProps<{
  advice: InvestigationAdvice
  recommendationPrefix: string
  taskListId: string
  taskKinds: Record<string, string>
  subjects: { ref: string; label: string }[]
}>()
const emit = defineEmits<{ evidence: [id: string]; recommendation: [id: string] }>()
const labels = computed<Record<string, string>>(() => ({
  AlarmEvent: t('告警事件'),
  Recovery: t('恢复阶段'),
  Current: t('当前快照'),
  EquipmentCondition: t('设备状态'),
  MeasurementChain: t('测量链'),
  AnomalySupported: t('有异常证据'),
  NoAnomalyFound: t('本范围未发现异常'),
  Inconclusive: t('暂不能判断'),
  MaintenanceEngineer: t('维护工程师'),
  Operator: t('操作人员'),
  Weak: t('有限'),
  Moderate: t('中等'),
  Missing: t('缺少信息'),
  InsufficientDetail: t('细节不足'),
  Conflicting: t('证据冲突'),
  RequiresOnSite: t('需要现场核实'),
}))
const label = (value: string) => labels.value[value] ?? value
const subject = (value: string) => props.subjects.find((x) => x.ref === value)?.label ?? value
</script>

<template>
  <section class="investigation-advice" :aria-label="t('工程师建议')">
    <article>
      <h4 :id="taskListId" tabindex="-1">{{ t('工程师建议') }}</h4>
      <p v-if="!advice.recommendations.length" class="path-note">
        {{ t('暂无有依据的核查建议，请先查看证据与缺项。') }}
      </p>
      <ol v-else class="task-list">
        <li v-for="item in advice.recommendations" :key="item.id">
          <details :id="recommendationPrefix + item.id" class="task-row">
            <summary>
              <strong>{{ taskKinds[item.kind] ?? item.kind }}</strong>
              <small class="detail-meta">{{ item.id }}</small>
              <small
                v-if="advice.workOrder.draft?.recommendationIds.includes(item.id)"
                class="task-draft"
              >
                {{ t('纳入工单建议') }}
              </small>
              <span class="task-text">{{ item.task }}</span>
            </summary>
            <div class="task-details">
              <p v-if="item.subjectRefs.length" class="detail-meta">
                {{ item.subjectRefs.map(subject).join('、') }}
              </p>
              <dl class="execution-fields">
                <dt>{{ t('记录要求') }}</dt>
                <dd>{{ item.record }}</dd>
                <dt>{{ t('完成条件') }}</dt>
                <dd>{{ item.completionCriteria }}</dd>
                <dt>{{ t('何时升级') }}</dt>
                <dd>
                  {{ item.escalation.condition }} · {{ label(item.escalation.responsibleRole) }}
                </dd>
              </dl>
              <div v-if="item.evidenceRefs.length" class="detail-links">
                <span>{{ t('证据引用') }}</span>
                <el-button
                  v-for="id in item.evidenceRefs"
                  :key="id"
                  link
                  type="primary"
                  :aria-label="t('查看证据 {0}', [id])"
                  @click="emit('evidence', id)"
                  >{{ id }}</el-button
                >
              </div>
            </div>
          </details>
        </li>
      </ol>
    </article>
    <details v-if="advice.hypotheses.length" class="advice-details">
      <summary>{{ t('待验证假设') }} · {{ advice.hypotheses.length }}</summary>
      <div v-for="item in advice.hypotheses" :key="item.id" class="detail-entry">
        <p>{{ item.description }} · {{ t('证据支持：') }}{{ label(item.support) }}</p>
        <p v-if="item.verificationRecommendationIds.length">
          {{ t('核实方式：') }}
          <el-button
            v-for="id in item.verificationRecommendationIds"
            :key="id"
            link
            type="primary"
            :aria-label="t('打开核查任务 {0}', [id])"
            @click="emit('recommendation', id)"
            >{{ id }}</el-button
          >
        </p>
        <p v-if="item.supportingEvidenceRefs.length">
          {{ t('支持依据：') }}
          <el-button
            v-for="id in item.supportingEvidenceRefs"
            :key="id"
            link
            type="primary"
            :aria-label="t('查看证据 {0}', [id])"
            @click="emit('evidence', id)"
            >{{ id }}</el-button
          >
        </p>
        <p v-if="item.contradictingEvidenceRefs.length">
          {{ t('反对依据：') }}
          <el-button
            v-for="id in item.contradictingEvidenceRefs"
            :key="id"
            link
            type="primary"
            :aria-label="t('查看证据 {0}', [id])"
            @click="emit('evidence', id)"
            >{{ id }}</el-button
          >
        </p>
      </div>
    </details>
    <details
      v-if="advice.gaps.length"
      class="advice-details"
      :open="advice.blockingGapIds.length > 0 || advice.workOrder.blockingGapIds.length > 0"
    >
      <summary>{{ t('仍缺少的信息') }} · {{ advice.gaps.length }}</summary>
      <article v-for="item in advice.gaps" :key="item.id" class="detail-entry">
        <h5>{{ item.description }}</h5>
        <p class="detail-meta">
          {{ item.id }} · {{ label(item.kind) }}
          <template v-if="advice.blockingGapIds.includes(item.id)">
            · {{ t('影响调查结论') }}</template
          >
        </p>
        <p>
          <strong>{{ t('影响') }}</strong> {{ item.whyItMatters }}
        </p>
        <div v-if="item.resolutionRecommendationIds.length" class="detail-links">
          <span>{{ t('补证任务') }}</span>
          <el-button
            v-for="id in item.resolutionRecommendationIds"
            :key="id"
            link
            type="primary"
            :aria-label="t('打开核查任务 {0}', [id])"
            @click="emit('recommendation', id)"
            >{{ id }}</el-button
          >
        </div>
        <div v-if="item.evidenceRefs.length" class="detail-links">
          <span>{{ t('证据引用') }}</span>
          <el-button
            v-for="id in item.evidenceRefs"
            :key="id"
            link
            type="primary"
            :aria-label="t('查看证据 {0}', [id])"
            @click="emit('evidence', id)"
            >{{ id }}</el-button
          >
        </div>
      </article>
    </details>
    <details v-if="advice.findings.length" class="advice-details">
      <summary>{{ t('调查发现') }} · {{ advice.findings.length }}</summary>
      <article v-for="item in advice.findings" :key="item.id" class="detail-entry">
        <h5>{{ label(item.scope) }} · {{ label(item.domain) }}</h5>
        <p class="detail-meta">
          {{ item.id }} · {{ label(item.status) }} · {{ item.subjectRefs.map(subject).join('、') }}
        </p>
        <p>{{ item.interpretation }}</p>
        <div v-if="item.evidenceRefs.length" class="detail-links">
          <span>{{ t('证据引用') }}</span>
          <el-button
            v-for="id in item.evidenceRefs"
            :key="id"
            link
            type="primary"
            :aria-label="t('查看证据 {0}', [id])"
            @click="emit('evidence', id)"
            >{{ id }}</el-button
          >
        </div>
      </article>
    </details>
  </section>
</template>

<style scoped>
.investigation-advice {
  overflow-wrap: anywhere;
}
.investigation-advice h4 {
  margin: 6px 0 12px;
  scroll-margin-top: 12px;
}
.task-list {
  list-style: none;
  padding: 0;
  margin-block: 0 8px;
}
.task-row,
.advice-details {
  padding-block: 6px;
  border-top: 1px solid var(--line);
}
.task-row summary,
.advice-details > summary {
  cursor: pointer;
}
.task-row summary:focus,
.advice-details > summary:focus-visible {
  outline: 2px solid var(--el-color-primary);
  outline-offset: 2px;
}
.task-text {
  display: block;
  margin: 4px 0 0 16px;
  line-height: 1.5;
}
.task-row:not([open]) .task-text {
  display: -webkit-box;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
  overflow: hidden;
}
.task-draft {
  margin-inline-start: 8px;
  color: var(--muted);
}
.task-details {
  padding-inline-start: 16px;
}
.task-details p {
  margin: 6px 0;
}
.advice-details > summary {
  font-weight: 600;
}
.detail-meta,
.detail-links > span {
  color: var(--muted);
  font-size: 13px;
}
.task-row summary .detail-meta {
  margin-inline-start: 8px;
}
.detail-entry {
  padding-block: 8px;
}
.detail-entry + .detail-entry {
  border-top: 1px solid var(--line);
}
.detail-entry h5,
.detail-entry p {
  margin: 0 0 5px;
  line-height: 1.55;
}
.detail-entry h5 {
  font-size: 14px;
}
.execution-fields {
  display: grid;
  grid-template-columns: max-content minmax(0, 1fr);
  gap: 6px 10px;
  margin: 6px 0;
}
.execution-fields dt {
  font-weight: 600;
}
.execution-fields dd {
  margin: 0;
}
.detail-links {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 2px 8px;
}
.detail-links .el-button {
  margin-left: 0;
}
</style>
