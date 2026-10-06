<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { listAll, type DirectoryRecord } from '../api'
import DocumentPanel from '../components/DocumentPanel.vue'
const plants = ref<DirectoryRecord[]>([]),
  plantId = ref(''),
  error = ref('')
onMounted(async () => {
  try {
    plants.value = await listAll('/plants')
    plantId.value = plants.value.find((x) => !x.isArchived)?.id ?? ''
  } catch (e) {
    error.value = e instanceof Error ? e.message : '工厂读取失败。'
  }
})
</script>
<template>
  <header class="topbar"><span>工作空间 / 知识资料</span></header>
  <section class="page-heading">
    <div>
      <p class="eyebrow">KNOWLEDGE / 04</p>
      <h1>让维护经验有据可查。</h1>
      <p class="intro">发布资料、追溯版本与管理读取范围。设备专属资料可在设备详情上传。</p>
    </div>
  </section>
  <p v-if="error" class="error-banner" role="alert">{{ error }}</p>
  <label
    >工厂<select v-model="plantId">
      <option v-for="p in plants" :key="p.id" :value="p.id">{{ p.name }}</option>
    </select></label
  ><DocumentPanel
    v-if="plantId"
    :key="plantId"
    :plant-id="plantId"
    :archived="plants.find((p) => p.id === plantId)?.isArchived"
  />
</template>
