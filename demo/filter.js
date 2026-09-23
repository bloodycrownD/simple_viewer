/* eslint-env browser */
/* ============ 模拟数据 ============ */
const TAG_GROUPS = [
  { id: 'g1', name: '题材', exclusive: true,  tags: [
    { id: 't1', name: '风景' }, { id: 't2', name: '人像' }, { id: 't3', name: '街拍' }, { id: 't4', name: '动物' },
  ]},
  { id: 'g2', name: '状态', exclusive: false, tags: [
    { id: 't5', name: '已修' }, { id: 't6', name: '待修' }, { id: 't7', name: '废片' },
  ]},
  { id: 'g3', name: '收藏', exclusive: false, tags: [
    { id: 't8', name: '星标' },
  ]},
  { id: 'g4', name: '时段', exclusive: false, tags: [
    { id: 't9', name: '夜景' }, { id: 't10', name: '日景' },
  ]},
];
const ALL_TAGS = TAG_GROUPS.flatMap(g => g.tags);
const TAG_NAME = Object.fromEntries(ALL_TAGS.map(t => [t.id, t.name]));
const TAG_GROUP = Object.fromEntries(ALL_TAGS.flatMap(t => TAG_GROUPS.filter(g => g.tags.includes(t)).map(g => [t.id, g])));

const WORDS = ['sunset', 'bridge', 'forest', 'city', 'cat', 'harbor', 'mountain', 'street', 'flower', 'lake', 'desert', 'night', 'market', 'coast', 'valley', 'bird'];

function genItems(n) {
  const items = [];
  let seed = 42;
  const rnd = () => { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed / 0x7fffffff; };
  for (let i = 0; i < n; i++) {
    const tags = new Set();
    // 题材（互斥组）：80% 概率取一个
    const g1 = TAG_GROUPS[0];
    if (rnd() < 0.8) tags.add(g1.tags[Math.floor(rnd() * g1.tags.length)].id);
    // 状态：70% 概率取一个
    const g2 = TAG_GROUPS[1];
    if (rnd() < 0.7) tags.add(g2.tags[Math.floor(rnd() * g2.tags.length)].id);
    if (rnd() < 0.18) tags.add('t8'); // 星标
    if (rnd() < 0.75) tags.add(rnd() < 0.5 ? 't9' : 't10'); // 时段
    const w = WORDS[Math.floor(rnd() * WORDS.length)] + (i + 1);
    items.push({
      id: 'i' + i,
      base: w,
      ext: rnd() < 0.75 ? 'jpg' : 'png',
      tags,
      ratio: 0.6 + rnd() * 1.0,           // 高/宽
      hue: Math.floor(rnd() * 360),
      kb: Math.floor(800 + rnd() * 8000),
    });
  }
  return items;
}
const ITEMS = genItems(140);

/* ============ 筛选状态 ============ */
let uid = 1;
const newCond = (matcher = 'in', values = []) =>
  ({ id: 'c' + uid++, kind: 'cond', matcher, values: [...values] });
const newGroup = (op = 'and', children = []) =>
  ({ id: 'g' + uid++, kind: 'group', op, children });

let root = newGroup('and');
let panelOpen = false;
let editingCondId = null;   // 值选择 popover 挂在哪一行

const MATCHERS = { in: '包含任一', nin: '不包含任一' };

/* ============ 求值 ============ */
function evalCond(c, item) {
  if (!c.values.length) return true; // 未选值 = 未启用
  const hit = c.values.some(id => item.tags.has(id));
  return c.matcher === 'in' ? hit : !hit;
}
function evalNode(n, item) {
  if (n.kind === 'cond') return evalCond(n, item);
  if (!n.children.length) return true; // 空组忽略
  const rs = n.children.map(c => evalNode(c, item));
  return n.op === 'and' ? rs.every(Boolean) : rs.some(Boolean);
}
const hitItems = () => ITEMS.filter(it => evalNode(root, it));

/* ============ 人话表达式 ============ */
function condText(c) {
  const v = c.values.map(id => TAG_NAME[id]).join('/') || '未选';
  return c.matcher === 'in' ? `含(${v})` : `不含(${v})`;
}
function exprParts(n, isRoot) {
  if (n.kind === 'cond') return [condText(n)];
  const parts = n.children.flatMap(c => exprParts(c, false)).filter(Boolean);
  if (!parts.length) return [];
  const joined = parts.join(n.op === 'and' ? ' 且 ' : ' 或 ');
  return (isRoot || parts.length === 1) ? [joined] : ['(' + joined + ')'];
}
const exprText = () => {
  const p = exprParts(root, true);
  return p.length ? p[0] : '（无筛选：显示全部图片）';
};

/* ============ 工具 ============ */
const $ = sel => document.querySelector(sel);
const esc = s => String(s).replace(/[&<>"]/g, m => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[m]));
function findNode(id, n = root, parent = null) {
  if (n.id === id) return { node: n, parent };
  if (n.kind !== 'group') return null;
  for (const c of n.children) { const r = findNode(id, c, n); if (r) return r; }
  return null;
}
const walkConds = (n, fn) => {
  if (n.kind === 'cond') { fn(n); return; }
  n.children.forEach(c => walkConds(c, fn));
};
function depthOf(id) {
  let d = 0, p = findNode(id).parent;
  while (p) { d++; p = findNode(p.id).parent; }
  return d;
}
let toastTimer = null;
function toast(msg) {
  const t = $('#toast');
  t.textContent = msg; t.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { t.hidden = true; }, 1800);
}
function countInFilter(tagId) {
  let used = false;
  walkConds(root, c => { if (c.values.includes(tagId)) used = true; });
  return used;
}

/* ============ 面板渲染 ============ */
function renderGroup(g, depth) {
  const isRoot = g.id === root.id;
  const head = `
    <div class="f-group-head">
      满足以下
      <select class="f-op-select" data-act="group-op" data-id="${g.id}">
        <option value="and" ${g.op === 'and' ? 'selected' : ''}>全部</option>
        <option value="or" ${g.op === 'or' ? 'selected' : ''}>任一</option>
      </select>
      条件
      <span class="sep-line"></span>
      ${isRoot ? '' : `<button class="icon-btn f-group-del" data-act="del" data-id="${g.id}" title="删除此组">✕</button>`}
    </div>`;

  const body = g.children.map(ch =>
    ch.kind === 'cond' ? renderCond(ch) : renderGroup(ch, depth + 1)
  ).join('');

  const canNest = depth < 2; // 根=0，最深嵌套到第 3 层
  const addbar = `
    <div class="f-addbar">
      <button class="add-btn" data-act="add-cond" data-id="${g.id}">＋ 条件</button>
      ${canNest ? `<button class="add-btn group" data-act="add-group" data-id="${g.id}">＋ 条件组（括号）</button>` : ''}
    </div>`;

  return `<div class="f-group-box${isRoot ? ' f-group-root' : ''}">${head}<div class="f-group">${body}${addbar}</div></div>`;
}

function renderCond(c) {
  const chips = c.values.map(id =>
    `<span class="val-chip">${esc(TAG_NAME[id])}<button data-act="del-val" data-id="${c.id}" data-val="${id}" title="移除">✕</button></span>`).join('');
  const empty = c.values.length ? '' : '<span class="val-empty">未选择标签（条件暂不生效）</span>';

  const neg = c.matcher === 'nin' ? ' neg' : '';
  return `
    <div class="f-row${neg}" data-row="${c.id}">
      <select class="f-select op${neg}" data-act="matcher" data-id="${c.id}">
        <option value="in" ${c.matcher === 'in' ? 'selected' : ''}>${MATCHERS.in}</option>
        <option value="nin" ${c.matcher === 'nin' ? 'selected' : ''}>${MATCHERS.nin}</option>
      </select>
      <div class="f-values">${chips}${empty}<button class="val-add" data-act="open-values" data-id="${c.id}">＋ 标签</button></div>
      <button class="icon-btn f-del" data-act="del" data-id="${c.id}" title="删除此条件">✕</button>
    </div>`;
}

function renderPanel() {
  const body = $('#flyoutBody');
  const scroll = body.scrollTop;
  body.innerHTML = renderGroup(root, 0);
  body.scrollTop = scroll;
  const hits = hitItems().length;
  $('#panelHits').innerHTML = `命中 <b>${hits}</b> / ${ITEMS.length} 张`;
  $('#exprText').innerHTML = esc(exprText())
    .replace(/且|或|\(|\)/g, m => `<span class="${m === '且' || m === '或' ? 'x-op' : 'x-op'}">${m}</span>`)
    .replace(/不含|名不含/g, m => `<span class="x-neg">${m}</span>`);
}

/* ============ 筛选条 / 工具栏 / 左栏 / 图墙 ============ */
function renderFilterBar() {
  const conds = [];
  walkConds(root, c => conds.push(c));
  const bar = $('#filterBar');
  if (!conds.length) { bar.classList.add('empty'); }
  else {
    bar.classList.remove('empty');
    $('#activeFilters').innerHTML = `<span class="filter-expr">${exprChips(root, true)}</span>`;
  }
  const badge = $('#condBadge');
  badge.hidden = !conds.length;
  badge.textContent = conds.length;
  $('#filterToggle').classList.toggle('active', !!conds.length);

  const hits = hitItems().length;
  $('#hitStats').innerHTML = `命中 <b>${hits}</b> / ${ITEMS.length}`;
  renderSidebar();
}

function exprChips(n, isRoot) {
  if (n.kind === 'cond') {
    const neg = c0(n) ? ' neg' : '';
    const notMark = neg ? '<span class="not-mark">非</span>' : '';
    const label = chipLabel(n);
    return `<span class="fx-cond${neg}" title="点击 ✕ 移除该条件">${notMark}${label}<button data-act="del" data-id="${n.id}">✕</button></span>`;
  }
  const parts = n.children.map(c => exprChips(c, false)).filter(Boolean);
  if (!parts.length) return '';
  const sep = `<span class="fx-op">${n.op === 'and' ? '且' : '或'}</span>`;
  const inner = parts.join(sep);
  return (isRoot || parts.length === 1) ? inner : `<span class="fx-paren">(</span>${inner}<span class="fx-paren">)</span>`;
}
const c0 = c => c.matcher === 'nin';
function chipLabel(c) {
  return `标签：${c.values.map(id => TAG_NAME[id]).join(' / ') || '未选'}`;
}

function renderSidebar() {
  $('#groupList').innerHTML = TAG_GROUPS.map(g => `
    <div class="tree-group-row">▸ ${esc(g.name)}${g.exclusive ? ' <span style="font-size:10px;font-weight:400">互斥</span>' : ''}</div>
    ${g.tags.map(t => {
      const cnt = ITEMS.filter(it => it.tags.has(t.id)).length;
      return `<div class="tree-tag-row${countInFilter(t.id) ? ' in-filter' : ''}" data-tag="${t.id}" title="点击加入筛选条件">
        <span class="tag-dot"></span>${esc(t.name)}<span class="tree-tag-count">${cnt}</span>
      </div>`;
    }).join('')}`).join('');
}

function renderWall() {
  const hits = hitItems();
  $('#wallEmpty').hidden = hits.length > 0;
  const wrapW = $('#wallWrap').clientWidth || 900;
  const colW = 230, gap = 12, pad = 32;
  const cols = Math.max(2, Math.floor((wrapW - pad + gap) / (colW + gap)));
  const heights = new Array(cols).fill(0);
  const colEls = Array.from({ length: cols }, () => []);
  hits.forEach(it => {
    let k = 0;
    for (let i = 1; i < cols; i++) if (heights[i] < heights[k]) k = i;
    heights[k] += it.ratio + 0.38;
    colEls[k].push(it);
  });
  $('#wall').innerHTML = colEls.map(list => `<div class="wall-col">${list.map(cardHtml).join('')}</div>`).join('');
}
function cardHtml(it) {
  const tags = [...it.tags];
  const tagSeg = tags.length ? `[${tags.map(id => TAG_NAME[id]).join(' ')}]` : '';
  const shown = tags.slice(0, 3).map(id => `<span class="card-chip">${esc(TAG_NAME[id])}</span>`).join('');
  const more = tags.length > 3 ? `<span class="card-chip">+${tags.length - 3}</span>` : '';
  const h = Math.round(200 * it.ratio);
  return `<div class="card">
    <div class="card-img" style="height:${h}px;background:linear-gradient(135deg,hsl(${it.hue} 45% 68%),hsl(${(it.hue + 50) % 360} 50% 52%))"></div>
    <div class="card-meta">
      <div class="card-name">${esc(it.base)}${tagSeg ? `<span class="tagseg">${esc(tagSeg)}</span>` : ''}.${it.ext} · ${(it.kb / 1024).toFixed(1)}MB</div>
      <div class="card-chips">${shown}${more}</div>
    </div>
  </div>`;
}

function refreshAll() { renderPanel(); renderFilterBar(); renderWall(); saveState(); }

/* ============ 状态持久化（仅主题 + 筛选树） ============ */
function saveState() {
  try { localStorage.setItem('filter-demo-state', JSON.stringify({ root, panelOpen })); } catch (_) { /* 忽略 */ }
}
function loadState() {
  try {
    const s = JSON.parse(localStorage.getItem('filter-demo-state'));
    if (s && s.root) {
      root = s.root;
      panelOpen = false;
      // 旧版本条件带 field（无标签/文件名），该能力已移除——清洗掉非标签条件
      const clean = n => {
        if (n.children) {
          n.children = n.children.filter(c => c.kind === 'group' || !c.field || c.field === 'tag');
          n.children.forEach(clean);
        }
      };
      clean(root);
      // 恢复的树保留旧 id，重编所有 id 防止与 uid 计数器撞车（findNode 会命中错误节点）
      const reId = n => {
        n.id = (n.kind === 'group' ? 'g' : 'c') + uid++;
        if (n.children) n.children.forEach(reId);
      };
      reId(root);
    }
  } catch (_) { /* 忽略 */ }
}

/* ============ flyout 定位与开合 ============ */
function positionFlyout() {
  const btn = $('#filterToggle').getBoundingClientRect();
  const fly = $('#flyout');
  fly.hidden = false;
  const w = fly.offsetWidth, h = fly.offsetHeight;
  let left = Math.min(btn.right - w, window.innerWidth - w - 10);
  left = Math.max(10, left);
  let top = btn.bottom + 8;
  if (top + h > window.innerHeight - 10) top = Math.max(10, btn.top - h - 8);
  fly.style.left = left + 'px'; fly.style.top = top + 'px';
}
function openPanel() { panelOpen = true; positionFlyout(); renderPanel(); $('#filterToggle').classList.add('active'); saveState(); }
function closePanel() { panelOpen = false; $('#flyout').hidden = true; hidePopover(); renderFilterBar(); saveState(); }

/* ============ 值选择 popover ============ */
function openPopover(condId, anchorBtn) {
  editingCondId = condId;
  const c = findNode(condId).node;
  const pop = $('#popover');
  pop.innerHTML = TAG_GROUPS.map(g => `
    <div class="pop-group">${esc(g.name)} <span class="mutex-mark">${g.exclusive ? '互斥组' : ''}</span></div>
    ${g.tags.map(t => {
      const cnt = ITEMS.filter(it => it.tags.has(t.id)).length;
      const on = c.values.includes(t.id);
      return `<button class="pop-tag${on ? ' on' : ''}" data-act="toggle-val" data-val="${t.id}">
        <span class="cb">${on ? '✓' : ''}</span>${esc(t.name)}<span class="pop-count">${cnt}</span>
      </button>`;
    }).join('')}`).join('');
  pop.hidden = false;
  const r = anchorBtn.getBoundingClientRect();
  let left = Math.min(r.left, window.innerWidth - pop.offsetWidth - 10);
  let top = r.bottom + 6;
  if (top + pop.offsetHeight > window.innerHeight - 10) top = Math.max(10, r.top - pop.offsetHeight - 6);
  pop.style.left = Math.max(10, left) + 'px'; pop.style.top = top + 'px';
}
const hidePopover = () => { $('#popover').hidden = true; editingCondId = null; };
function updatePopover() {
  const c = editingCondId && findNode(editingCondId);
  if (!c) return hidePopover();
  document.querySelectorAll('#popover .pop-tag').forEach(btn => {
    const on = c.node.values.includes(btn.dataset.val);
    btn.classList.toggle('on', on);
    btn.querySelector('.cb').textContent = on ? '✓' : '';
  });
}

/* ============ 事件 ============ */
document.addEventListener('click', e => {
  const actEl = e.target.closest('[data-act]');
  const tagRow = e.target.closest('.tree-tag-row');

  // popover 外点击先收起（除 popover 内的 toggle）
  if (!$('#popover').hidden && !e.target.closest('#popover') &&
      !(actEl && actEl.dataset.act === 'open-values')) hidePopover();

  if (tagRow && !actEl) { addQuickCond(tagRow.dataset.tag); return; }
  if (!actEl) {
    const inTrigger = e.target.closest('#filterToggle, #editFilterBtn');
    if (!e.target.closest('#flyout') && !inTrigger && panelOpen) closePanel();
    return;
  }
  const { act, id, val } = actEl.dataset;
  switch (act) {
    case 'add-cond': findNode(id).node.children.push(newCond()); refreshAll(); break;
    case 'add-group': findNode(id).node.children.push(newGroup('or')); refreshAll(); break;
    case 'del': {
      const { parent: p } = findNode(id);
      if (p) { p.children = p.children.filter(c => c.id !== id); refreshAll(); }
      break;
    }
    case 'del-val': {
      const c = findNode(id).node;
      c.values = c.values.filter(v => v !== val);
      refreshAll();
      if (editingCondId === id) updatePopover();
      break;
    }
    case 'open-values': openPopover(id, actEl); break;
    case 'toggle-val': {
      const c = findNode(editingCondId).node;
      c.values = c.values.includes(val) ? c.values.filter(v => v !== val) : [...c.values, val];
      refreshAll();
      updatePopover();
      break;
    }
    case 'group-op': break; // change 事件处理
    default: break;
  }
});

document.addEventListener('change', e => {
  const el = e.target.closest('[data-act]');
  if (!el) return;
  const { act, id } = el.dataset;
  const n = findNode(id);
  if (!n) return;
  switch (act) {
    case 'group-op': n.node.op = el.value; refreshAll(); break;
    case 'matcher': n.node.matcher = el.value; refreshAll(); break;
    default: break;
  }
});

document.addEventListener('keydown', e => {
  if (e.key === 'Escape') { hidePopover(); if (panelOpen) closePanel(); }
});

$('#filterToggle').addEventListener('click', () => (panelOpen ? closePanel() : openPanel()));
$('#flyoutClose').addEventListener('click', closePanel);
$('#doneBtn').addEventListener('click', closePanel);
$('#clearCondBtn').addEventListener('click', () => { root = newGroup('and'); refreshAll(); });
$('#clearFilterBtn').addEventListener('click', () => { root = newGroup('and'); refreshAll(); closePanel(); });
$('#editFilterBtn').addEventListener('click', openPanel);
$('#resetBtn').addEventListener('click', () => {
  localStorage.removeItem('filter-demo-state');
  root = newGroup('and'); refreshAll(); toast('已重置');
});
$('#themeBtn').addEventListener('click', () => {
  document.body.classList.toggle('dark');
  localStorage.setItem('filter-demo-theme', document.body.classList.contains('dark') ? 'dark' : 'light');
});

window.addEventListener('resize', () => { renderWall(); if (panelOpen) positionFlyout(); });

/* 左栏快捷加条件：根组追加「包含该标签」，已有同值条件则忽略 */
function addQuickCond(tagId) {
  let exists = false;
  walkConds(root, c => { if (c.matcher === 'in' && c.values.includes(tagId)) exists = true; });
  if (exists) { toast('已在筛选中'); return; }
  // 复用：若根组已有单值 in 条件，合并进同一行（OR 语义直观）
  let target = null;
  walkConds(root, c => { if (!target && c.matcher === 'in' && c.values.length === 1) target = c; });
  if (target && root.op === 'or') target.values.push(tagId);
  else root.children.push(newCond('in', [tagId]));
  refreshAll();
  toast('已加入筛选：含(' + TAG_NAME[tagId] + ')');
}

/* ============ 启动 ============ */
if (localStorage.getItem('filter-demo-theme') === 'dark') document.body.classList.add('dark');
loadState();
renderFilterBar();
renderWall();
if (panelOpen) openPanel();
