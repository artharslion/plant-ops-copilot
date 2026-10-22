import { createApp } from 'vue'
import { createPinia } from 'pinia'
import { createRouter, createWebHistory } from 'vue-router'
import App from './App.vue'
import {
  ElAlert,
  ElButton,
  ElCheckbox,
  ElCheckboxGroup,
  ElDialog,
  ElDrawer,
  ElForm,
  ElFormItem,
  ElInput,
  ElLoading,
  ElOption,
  ElSelect,
  ElSkeleton,
  ElStep,
  ElSteps,
  ElTable,
  ElTableColumn,
  ElTabPane,
  ElTabs,
  ElTag,
  ElTree,
} from 'element-plus'
import 'element-plus/dist/index.css'
import OverviewPage from './pages/OverviewPage.vue'
import LoginPage from './pages/LoginPage.vue'
import AssetDirectoryPage from './pages/AssetDirectoryPage.vue'
import AlarmsPage from './pages/AlarmsPage.vue'
import DocumentsPage from './pages/DocumentsPage.vue'
import SettingsPage from './pages/SettingsPage.vue'
import { useSession } from './stores/session'
import './style.css'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', component: OverviewPage },
    { path: '/login', component: LoginPage },
    { path: '/assets', component: AssetDirectoryPage },
    {
      path: '/assets/:assetId/work-orders/:orderId',
      redirect: (to) => ({
        path: '/assets',
        query: {
          ...to.query,
          assetId: String(to.params.assetId),
          tab: 'orders',
          workOrderId: String(to.params.orderId),
        },
      }),
    },
    { path: '/alarms', component: AlarmsPage },
    { path: '/documents', component: DocumentsPage },
    { path: '/settings', component: SettingsPage },
    { path: '/:pathMatch(.*)*', redirect: '/' },
  ],
})
const pinia = createPinia()
router.beforeEach(async (to) => {
  if (
    !['/assets', '/alarms', '/documents', '/settings'].some(
      (path) => to.path === path || to.path.startsWith(`${path}/`),
    )
  )
    return
  const session = useSession(pinia)
  try {
    await session.load()
  } catch {
    return '/login'
  }
  if (!session.user) return '/login'
})
const app = createApp(App).use(pinia).use(router)
for (const component of [
  ElAlert,
  ElButton,
  ElCheckbox,
  ElCheckboxGroup,
  ElDialog,
  ElDrawer,
  ElForm,
  ElFormItem,
  ElInput,
  ElOption,
  ElSelect,
  ElSkeleton,
  ElStep,
  ElSteps,
  ElTable,
  ElTableColumn,
  ElTabPane,
  ElTabs,
  ElTag,
  ElTree,
])
  app.use(component)
app.use(ElLoading).mount('#app')
