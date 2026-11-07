/* ==========================================================================
   PowerDock 设计还原 · 交互脚本
   仅做三件事：切换外观、切换壁纸、演示状态与面板操作。
   所有状态都写在 DOM 属性上，CSS 负责表现，便于直接改属性调试。
   ========================================================================== */

(function () {
  'use strict';

  /* ───────── 1. 外观切换 ─────────
     改写 <html data-theme>，页面级配色随之变化。
     注意：带 .theme-scope 的区块有自己的 data-theme，用于深浅并排对照，不跟随切换。 */

  const root = document.documentElement;
  const themeButtons = document.querySelectorAll('[data-set-theme]');

  function setTheme(name) {
    root.setAttribute('data-theme', name);
    themeButtons.forEach((btn) => {
      btn.classList.toggle('is-active', btn.dataset.setTheme === name);
    });
    try {
      localStorage.setItem('pd-theme', name);
    } catch (e) { /* 无痕模式下忽略 */ }
  }

  themeButtons.forEach((btn) => {
    btn.addEventListener('click', () => setTheme(btn.dataset.setTheme));
  });

  // 恢复上次选择；没有则跟随系统
  try {
    const saved = localStorage.getItem('pd-theme');
    if (saved === 'dark' || saved === 'light') {
      setTheme(saved);
    } else if (window.matchMedia('(prefers-color-scheme: light)').matches) {
      setTheme('light');
    }
  } catch (e) { /* 忽略 */ }

  /* ───────── 2. 壁纸切换 ─────────
     循环 1 → 2 → 3，用来观察同一块玻璃在不同底色上的采样结果。 */

  const wallpaperBtn = document.getElementById('wallpaperBtn');
  let wall = 1;

  if (wallpaperBtn) {
    wallpaperBtn.addEventListener('click', () => {
      wall = wall === 3 ? 1 : wall + 1;
      document.body.dataset.wall = String(wall);
      wallpaperBtn.textContent = '壁纸 ' + wall + '/3';
    });
    wallpaperBtn.textContent = '壁纸 1/3';
  }

  /* ───────── 3. 设备行：在线 / 离线切换 ─────────
     在线 → 离线：状态点转灰、整行降透明、电量位替换为「—」。
     离线 → 在线：恢复原始数值（存在 dataset.pct 里）。
     条宽始终保留，因为「最后一次读数」仍有参考价值。 */

  function initDeviceRow(row) {
    const pct = row.querySelector('.dev__pct, .row__pct');
    if (!pct) return;

    // 原始数值优先从电量条的 --pct 反推：
    // 初始就是离线的行，文本已经是「—」，不能当成原始值存下来。
    if (!row.dataset.pct) {
      const bar = row.querySelector('.dev__bar > i, .row__bar > i');
      const raw = bar ? bar.style.getPropertyValue('--pct').trim() : '';
      row.dataset.pct = raw || pct.textContent.trim();
    }

    row.setAttribute('role', 'button');
    row.setAttribute('tabindex', '0');
    row.setAttribute('aria-pressed', row.dataset.state === 'offline' ? 'false' : 'true');

    const toggle = () => {
      const offline = row.dataset.state === 'offline';
      row.dataset.state = offline ? 'online' : 'offline';
      row.setAttribute('aria-pressed', offline ? 'true' : 'false');
      pct.textContent = offline ? row.dataset.pct : '—';
    };

    row.addEventListener('click', toggle);
    row.addEventListener('keydown', (e) => {
      if (e.key === 'Enter' || e.key === ' ') {
        e.preventDefault();
        toggle();
      }
    });
  }

  document.querySelectorAll('.dev, .row').forEach(initDeviceRow);

  /* ───────── 4. 配置页：勾选与计数 ─────────
     勾选决定是否进组件；与「离线」是两个独立维度，互不影响。 */

  const picks = document.getElementById('picks');
  const countEl = document.getElementById('pickedCount');

  function refreshCount() {
    if (!picks || !countEl) return;
    const total = picks.querySelectorAll('.pick').length;
    const picked = picks.querySelectorAll('.pick__check[aria-pressed="true"]').length;
    countEl.textContent = '已选 ' + picked + ' / ' + total;
  }

  if (picks) {
    picks.addEventListener('click', (e) => {
      // 整行可点：命中行内任意位置都切换，不必精确戳到 20px 的圆点
      const row = e.target.closest('.pick');
      if (!row) return;

      const btn = row.querySelector('.pick__check');
      if (!btn) return;

      const on = btn.getAttribute('aria-pressed') === 'true';
      btn.setAttribute('aria-pressed', on ? 'false' : 'true');
      row.setAttribute('aria-selected', on ? 'false' : 'true');

      refreshCount();
    });

    // 初始化每行的 aria-selected，让 CSS 的 .42 透明度生效
    picks.querySelectorAll('.pick').forEach((row) => {
      const on = row.querySelector('.pick__check')?.getAttribute('aria-pressed') === 'true';
      row.setAttribute('aria-selected', on ? 'true' : 'false');
    });

    refreshCount();
  }

  /* ───────── 5. 分段控件与筛选 ─────────
     纯视觉演示：同组内互斥高亮，不触发真实逻辑。
     顶栏那组由 setTheme 统一管理，先打上 data-manual 排除掉，避免重复绑定。 */

  document.querySelectorAll('.topbar .seg').forEach((seg) => {
    seg.setAttribute('data-manual', '');
  });

  document.querySelectorAll('.seg:not([data-manual])').forEach((seg) => {
    seg.addEventListener('click', (e) => {
      const btn = e.target.closest('.seg__btn');
      if (!btn || !seg.contains(btn)) return;
      seg.querySelectorAll('.seg__btn').forEach((b) => b.classList.remove('is-active'));
      btn.classList.add('is-active');
    });
  });

  const filters = document.querySelectorAll('.filter');
  filters.forEach((btn) => {
    btn.addEventListener('click', () => {
      filters.forEach((b) => b.classList.remove('is-active'));
      btn.classList.add('is-active');
    });
  });

})();
