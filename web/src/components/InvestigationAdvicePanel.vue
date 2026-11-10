<script setup lang="ts">
import { computed } from 'vue'
import { t } from '../i18n'
import type { InvestigationAdvice } from '../investigationAdvice'

const props = defineProps<{
  advice: InvestigationAdvice
  recommendationPrefix: string
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
  Inspect: t('现场核查'),
  VerifyMeasurement: t('测量核验'),
  CollectEvidence: t('补充证据'),
  ReviewProcedure: t('规程复核'),
  Monitor: t('跟踪观察'),
  Escalate: t('提交复核'),
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
      <h4>{{ t('工程师建议') }}</h4>
      <p v-if="!advice.recommendations.length" class="path-note">
        {{ t('暂无有依据的核查建议，请先查看证据与缺项。') }}
      </p>
      <ol v-else class="task-list">
        <li
          v-for="item in advice.recommendations"
          :key="item.id"
          :id="recommendationPrefix + item.id"
          tabindex="-1"
        >
          <p>
            <strong>{{ item.id }} · {{ label(item.kind) }}</strong> {{ item.task }}
          </p>
          <p class="path-note">{{ item.subjectRefs.map(subject).join('、') }}</p>
          <details>
            <summary>{{ t('查看依据与执行条件') }}</summary>
            <p>{{ t('需记录：') }}{{ item.record }}</p>
            <p>{{ t('完成条件：') }}{{ item.completionCriteria }}</p>
            <p>
              {{ t('升级条件：') }}{{ item.escalation.condition }} ·
              {{ label(item.escalation.responsibleRole) }}
            </p>
          </details>
          <el-button
            v-for="id in item.evidenceRefs"
            :key="id"
            link
            type="primary"
            @click="emit('evidence', id)"
          >
            {{ t('查看依据') }} {{ id }}
          </el-button>
        </li>
      </ol>
    </article>
    <details v-if="advice.hypotheses.length" class="advice-details">
      <summary>{{ t('待验证假设') }} · {{ advice.hypotheses.length }}</summary>
      <div v-for="item in advice.hypotheses" :key="item.id" class="investigation-statement">
        <p>{{ item.description }} · {{ t('证据支持：') }}{{ label(item.support) }}</p>
        <p v-if="item.verificationRecommendationIds.length">
          {{ t('核实方式：') }}
          <el-button
            v-for="id in item.verificationRecommendationIds"
            :key="id"
            link
            type="primary"
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
      <div v-for="item in advice.gaps" :key="item.id" class="investigation-statement">
        <p>
          <strong>{{ item.id }} · {{ label(item.kind) }}</strong> {{ item.description }}
        </p>
        <p>{{ item.whyItMatters }}</p>
        <p v-if="item.resolutionRecommendationIds.length">
          {{ t('补证方式：') }}
          <el-button
            v-for="id in item.resolutionRecommendationIds"
            :key="id"
            link
            type="primary"
            @click="emit('recommendation', id)"
            >{{ id }}</el-button
          >
        </p>
        <el-button
          v-for="id in item.evidenceRefs"
          :key="id"
          link
          type="primary"
          @click="emit('evidence', id)"
          >{{ t('查看依据') }} {{ id }}</el-button
        >
      </div>
    </details>
    <details v-if="advice.findings.length" class="advice-details">
      <summary>{{ t('调查发现') }} · {{ advice.findings.length }}</summary>
      <div v-for="item in advice.findings" :key="item.id" class="investigation-statement">
        <p>
          <strong>{{ item.id }} · {{ item.subjectRefs.map(subject).join('、') }}</strong>
        </p>
        <p class="path-note">
          {{ label(item.scope) }} · {{ label(item.domain) }} · {{ label(item.status) }}
        </p>
        <p>{{ item.interpretation }}</p>
        <el-button
          v-for="id in item.evidenceRefs"
          :key="id"
          link
          type="primary"
          @click="emit('evidence', id)"
        >
          {{ t('查看依据') }} {{ id }}
        </el-button>
      </div>
    </details>
  </section>
</template>

<style scoped>
.investigation-advice {
  overflow-wrap: anywhere;
}
.investigation-advice h4 {
  margin: 6px 0 12px;
}
.task-list {
  display: grid;
  gap: 8px;
  list-style: none;
  padding: 0;
}
.task-list li,
.advice-details {
  padding: 10px 16px;
  border: 1px solid var(--line);
  border-radius: 6px;
  background: var(--el-bg-color);
}
.task-list li:focus {
  outline: 2px solid var(--el-color-primary);
}
.task-list p {
  margin: 4px 0;
}
.advice-details {
  margin-block: 8px;
}
.advice-details > summary {
  cursor: pointer;
  font-weight: 600;
}
</style>
