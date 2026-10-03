import { createApp } from 'vue'
import { createPinia } from 'pinia'
import { createRouter, createWebHistory } from 'vue-router'
import App from './App.vue'
import OverviewPage from './pages/OverviewPage.vue'
import LoginPage from './pages/LoginPage.vue'
import AssetDirectoryPage from './pages/AssetDirectoryPage.vue'
import { useSession } from './stores/session'
import './style.css'

const router = createRouter({
  history: createWebHistory(),
  routes: [{ path: '/', component: OverviewPage }, { path: '/login', component: LoginPage },
    { path: '/assets', component: AssetDirectoryPage }, { path: '/:pathMatch(.*)*', redirect: '/' }],
})
const pinia = createPinia()
router.beforeEach(async to => {
  if (to.path !== '/assets') return
  const session = useSession(pinia)
  try { await session.load() } catch { return '/login' }
  if (!session.user) return '/login'
})
createApp(App).use(pinia).use(router).mount('#app')
