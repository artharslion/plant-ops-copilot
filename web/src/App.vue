<template>
  <div class="app-shell">
    <aside class="sidebar">
      <a class="brand" href="/" aria-label="Plant Ops Copilot 首页"><span class="brand-mark">PO</span><span>Plant Ops<br /><small>Copilot</small></span></a>
      <div class="nav-caption">工作空间</div>
      <RouterLink class="nav-item" to="/">总览 <span>01</span></RouterLink>
      <p class="sidebar-note">资产、告警与维护先行。<br />AI 提供有证据的辅助调查。</p>
      <div class="sidebar-footer">LOCAL DEVELOPMENT<br /><span>基础骨架 · P1.1</span></div>
    </aside>
    <main><RouterView /></main>
  </div>
</template>
