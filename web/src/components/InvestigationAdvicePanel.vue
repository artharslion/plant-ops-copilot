<script setup lang="ts">
import { computed } from 'vue'
import { t } from '../i18n'
import type { InvestigationAdvice } from '../investigationAdvice'

const props = defineProps<{
  advice: InvestigationAdvice
  subjects: { ref: string; label: string }[]
}>()
const emit = defineEmits<{ evidence: [id: string] }>()
const labels = computed<Record<string, string>>(() => ({
  Ready: t('建议已可用'),
  NeedsMoreEvidence: t('需要补充关键证据'),
  NeedsExpertReview: t('需要专家复核'),
  DraftReady: t('可形成工单草稿'),
  NeedsInformation: t('缺少工单起草条件'),
  NotRecommended: t('暂不建议建单'),
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
  Low: t('低'),
  Medium: t('中'),
  High: t('高'),
  Missing: t('缺少信息'),
  InsufficientDetail: t('细节不足'),
  Conflicting: t('证据冲突'),
  RequiresOnSite: t('需要现场核实'),
}))
const label = (value: string) => labels.value[value] ?? value
const subject = (value: string) => props.subjects.find((x) => x.ref === value)?.label ?? value
const recommendation = (id: string) =>
  props.advice.recommendations.find((x) => x.id === id)?.task ?? id
</script>

<template>
  <section class="investigation-advice" :aria-label="t('工程师建议')">
    <article>
      <h4>{{ label(advice.status) }}</h4>
      <p>{{ advice.summary }}</p>
      <p class="path-note">{{ advice.statusReason }}</p>
      <p v-if="advice.blockingGapIds.length" class="path-note">
        {{ t('关键缺项：') }}{{ advice.blockingGapIds.join('、') }}
      </p>
    </article>
    <article v-if="advice.findings.length">
      <h4>{{ t('调查发现') }}</h4>
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
    </article>
    <article v-if="advice.recommendations.length">
      <h4>{{ t('工程师建议') }}</h4>
      <ol class="next-checks">
        <li v-for="item in advice.recommendations" :key="item.id" :id="'advice-' + item.id">
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
            <el-button
              v-for="id in item.evidenceRefs"
              :key="id"
              link
              type="primary"
              @click="emit('evidence', id)"
            >
              {{ t('查看依据') }} {{ id }}
            </el-button>
          </details>
        </li>
      </ol>
    </article>
    <article v-if="advice.hypotheses.length">
      <h4>{{ t('待验证假设') }}</h4>
      <div v-for="item in advice.hypotheses" :key="item.id" class="investigation-statement">
        <p>{{ item.description }} · {{ t('证据支持：') }}{{ label(item.support) }}</p>
        <p v-for="id in item.verificationRecommendationIds" :key="id">
          {{ t('核实方式：') }}{{ recommendation(id) }}
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
    </article>
    <article v-if="advice.gaps.length">
      <h4>{{ t('仍缺少的信息') }}</h4>
      <div v-for="item in advice.gaps" :key="item.id" class="investigation-statement">
        <p>
          <strong>{{ item.id }} · {{ label(item.kind) }}</strong> {{ item.description }}
        </p>
        <p>{{ item.whyItMatters }}</p>
        <p v-for="id in item.resolutionRecommendationIds" :key="id">
          {{ t('补证方式：') }}{{ recommendation(id) }}
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
    </article>
    <article>
      <h4>{{ label(advice.workOrder.status) }}</h4>
      <p>{{ advice.workOrder.reason }}</p>
      <p v-if="advice.workOrder.blockingGapIds.length">
        {{ t('关键缺项：') }}{{ advice.workOrder.blockingGapIds.join('、') }}
      </p>
      <template v-if="advice.workOrder.draft">
        <p>
          <strong>{{ advice.workOrder.draft.title }}</strong> ·
          {{ label(advice.workOrder.draft.priority) }}
        </p>
        <p>{{ advice.workOrder.draft.priorityReason }}</p>
        <p>{{ advice.workOrder.draft.objective }}</p>
        <ul>
          <li v-for="id in advice.workOrder.draft.recommendationIds" :key="id">
            {{ recommendation(id) }}
          </li>
        </ul>
      </template>
    </article>
  </section>
</template>
