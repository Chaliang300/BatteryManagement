/* ==========================================================================
   PowerDock 设计还原 · 交互脚本
   仅做四件事：切换外观、切换壁纸、主窗口选中联动、演示状态与面板操作。
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

  document.querySelectorAll('.dev').forEach(initDeviceRow);

  /* ───────── 3.5 主窗口：Dock 切页 / 勾选 / 尺寸 ─────────
     只驱动带 data-live 的窗口；另外两个是静态状态图。
     「勾选」只有一个状态源 onSet：卡片右上角的圆点、组件页清单、挂件预览、各处计数都读它。 */

  document.querySelectorAll('.appwin[data-live]').forEach(function (win) {
    var panes = win.querySelectorAll('.pane');
    var navs = win.querySelectorAll('.dock__btn');

    function show(name) {
      panes.forEach(function (p) { p.classList.toggle('is-on', p.dataset.pane === name); });
      navs.forEach(function (b) { b.classList.toggle('is-on', b.dataset.go === name); });
    }
    navs.forEach(function (b) {
      b.addEventListener('click', function () { show(b.dataset.go); });
    });

    var cards = [].slice.call(win.querySelectorAll('.dcard'));
    var picks = [].slice.call(win.querySelectorAll('.pick'));
    var onSet = new Set(
      cards.filter(function (c) {
        return c.querySelector('.ck').getAttribute('aria-pressed') === 'true';
      }).map(function (c) { return c.dataset.name; })
    );

    var wg = win.querySelector('#wg');
    var wgRows = win.querySelector('#wgRows');
    var SIZE = { sm: { w: 173, h: 173, max: 3 }, md: { w: 362, h: 0, max: 5 }, lg: { w: 362, h: 362, max: 7 } };
    var cur = 'md';
    wg.dataset.size = cur;

    // 挂件里既显示在线的，也显示勾上了但掉线的（灰行 + 「—」）。
    // 表头已经去掉，在线状态只由行首那个点表达。
    function drawWidget() {
      var list = cards.filter(function (c) {
        return onSet.has(c.dataset.name);
      }).slice(0, SIZE[cur].max);

      wgRows.innerHTML = list.map(function (c) {
        var off = c.classList.contains('is-off');
        var t = off ? 'off' : (c.dataset.tone || '');
        var pct = off ? '—' : c.dataset.pct + '%';
        var w = off ? 31 : c.dataset.pct;
        return '<div class="wrow' + (off ? ' is-off' : '') + '">' +
          '<i class="' + t + '"></i>' +
          '<span class="n">' + c.dataset.name + '</span>' +
          '<span class="b"><i class="' + t + '" style="width:' + w + '%"></i></span>' +
          '<span class="p ' + t + '">' + pct + '</span></div>';
      }).join('');

    }

    function render() {
      var n = onSet.size;

      cards.forEach(function (c) {
        var on = onSet.has(c.dataset.name);
        c.querySelector('.ck').setAttribute('aria-pressed', on ? 'true' : 'false');
        if (c.classList.contains('is-off')) return;
        var f = c.querySelector('.dcard__foot');
        var tail = f.textContent.slice(f.textContent.indexOf(' · '));
        f.classList.toggle('on', on);
        f.textContent = (on ? '在组件中' : '未显示') + tail;
      });

      picks.forEach(function (p) {
        var on = onSet.has(p.dataset.name);
        p.dataset.on = on ? '1' : '0';
        p.querySelector('.ck').setAttribute('aria-pressed', on ? 'true' : 'false');
      });

      win.querySelectorAll('.onCount').forEach(function (b) { b.textContent = n; });
      drawWidget();
    }

    function toggle(name) {
      if (onSet.has(name)) onSet.delete(name); else onSet.add(name);
      render();
    }

    cards.forEach(function (c) {
      c.querySelector('.ck').addEventListener('click', function (e) {
        e.stopPropagation();
        toggle(c.dataset.name);
      });
      c.addEventListener('click', function () {
        cards.forEach(function (x) { x.classList.remove('is-sel'); });
        c.classList.add('is-sel');
        var alias = win.querySelector('.actbar input');
        var src = win.querySelector('.actbar .src');
        if (alias) alias.value = c.dataset.name;
        if (src) src.textContent = (c.dataset.pct || '—') + '% · 刚刚更新';
      });
    });

    picks.forEach(function (p) {
      p.addEventListener('click', function () { toggle(p.dataset.name); });
    });

    function setSize(s) {
      cur = s;
      wg.dataset.size = s;
      wg.style.width = SIZE[s].w + 'px';
      wg.style.height = SIZE[s].h ? SIZE[s].h + 'px' : '';
      win.querySelectorAll('[data-size]').forEach(function (b) {
        b.classList.toggle('is-on', b.dataset.size === s);
      });
      var cap = win.querySelector('.stage__bar b');
      if (cap) cap.textContent = ({ sm: '小 2×2 · 173×173', md: '中 4×2 · 362×173', lg: '大 4×4 · 362×362' })[s];
      drawWidget();
    }
    win.querySelectorAll('[data-size]').forEach(function (b) {
      b.addEventListener('click', function () { setSize(b.dataset.size); });
    });

    win.querySelectorAll('.sw').forEach(function (s2) {
      s2.addEventListener('click', function () {
        s2.setAttribute('aria-pressed', s2.getAttribute('aria-pressed') === 'true' ? 'false' : 'true');
      });
    });

    render();
  });

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

})();
