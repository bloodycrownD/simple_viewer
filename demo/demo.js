/* =========================================================
 * Simple Viewer 打标签 + 瀑布流 交互原型
 * 纯前端模拟：不落盘、不真的改名，刷新后按 localStorage 恢复
 * ========================================================= */
"use strict";

/* ---------- 工具 ---------- */
const $ = s => document.querySelector(s);
function mulberry32(a) {
  return function () {
    a |= 0; a = a + 0x6D2B79F5 | 0;
    let t = Math.imul(a ^ a >>> 15, 1 | a);
    t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t;
    return ((t ^ t >>> 14) >>> 0) / 4294967296;
  };
}
const rng = mulberry32(20260916);          // 固定种子 → 每次生成的图库一致
let _uid = 0;
const uid = p => p + (++_uid);
const esc = s => String(s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
function hueOf(str) { let h = 0; for (const c of str) h = (h * 31 + c.charCodeAt(0)) % 360; return h; }

/* ---------- 演示数据 ---------- */
const TOTAL = 420;
const ASPECTS = [[1, 1], [4, 3], [3, 4], [16, 9], [9, 16], [3, 2], [2, 3], [4, 5], [5, 4], [21, 9], [1, 2]];
const WORDS = ["sunset", "beach", "mountain", "street", "night", "food", "portrait", "flower",
  "city", "lake", "forest", "snow", "desert", "ocean", "temple", "market", "cafe", "bridge", "rain", "festival"];

function defaultGroups() {
  const g = (id, name, exclusive, tagNames) => ({
    id, name, exclusive,
    tags: tagNames.map(n => ({ id: uid("t"), name: n })),
    hue: hueOf(id + name),
  });
  return [
    g("g1", "星级", true, ["1星", "2星", "3星", "4星", "5星"]),
    g("g2", "状态", true, ["待筛选", "已选中", "已废弃"]),
    g("g3", "主题", false, ["风景", "人像", "街拍", "夜景", "美食", "静物"]),
    g("g4", "项目", false, ["旅行2026", "日常", "客片"]),
  ];
}

function genImages(n) {
  const imgs = []; const used = {};
  for (let i = 0; i < n; i++) {
    let base;
    if (rng() < 0.62) base = "IMG_" + String(1000 + i);
    else {
      const w = WORDS[(rng() * WORDS.length) | 0] + "_" + WORDS[(rng() * WORDS.length) | 0];
      used[w] = (used[w] || 0) + 1;
      base = w + (used[w] > 1 ? "_" + used[w] : "");
    }
    const a = ASPECTS[(rng() * ASPECTS.length) | 0];
    imgs.push({
      id: "im" + (i + 1), base, ext: ".jpg",
      w: a[0], h: a[1],
      folder: "2026-" + String(1 + ((rng() * 12) | 0)).padStart(2, "0"),
      tags: [], order: i,
    });
  }
  return imgs;
}

/* 预打一些标签，让筛选/统计一打开就有东西可看 */
function preTag(groups, images) {
  const find = (gname, tname) => groups.find(g => g.name === gname)?.tags.find(t => t.name === tname)?.id;
  for (const im of images) {
    if (rng() < 0.5) { const id = find("星级", (1 + (rng() * 5 | 0)) + "星"); if (id) im.tags.push(id); }
    if (rng() < 0.3) { const id = find("状态", ["待筛选", "已选中", "已废弃"][rng() * 3 | 0]); if (id) im.tags.push(id); }
    const themeN = rng() < 0.65 ? 1 + (rng() * 2 | 0) : 0;
    for (let k = 0; k < themeN; k++) {
      const id = find("主题", ["风景", "人像", "街拍", "夜景", "美食", "静物"][rng() * 6 | 0]);
      if (id && !im.tags.includes(id)) im.tags.push(id);
    }
    if (rng() < 0.25) { const id = find("项目", ["旅行2026", "日常", "客片"][rng() * 3 | 0]); if (id) im.tags.push(id); }
  }
}

/* ---------- 状态 ---------- */
const LS_KEY = "svproto.v1";
const state = {
  groups: defaultGroups(),
  images: genImages(TOTAL),
  filters: new Set(),      // tagId 集合，OR 语义
  selection: new Set(),    // imageId 集合
  discovered: 0,           // 模拟扫描进度
  scanDone: false,
  sortMode: "name",
  theme: "dark",
  lastClickId: null,
  collapsedGroups: new Set(), // 折叠的组 Id（会话内记忆，不落盘；默认空 = 全展开）
};
const cardEls = new Map(); // imageId -> card DOM

function loadState() {
  try {
    const raw = localStorage.getItem(LS_KEY);
    if (!raw) { preTag(state.groups, state.images); return; }
    const saved = JSON.parse(raw);
    if (saved.groups) state.groups = saved.groups;
    if (saved.imgTags) {
      for (const im of state.images) if (saved.imgTags[im.id]) im.tags = saved.imgTags[im.id];
    } else preTag(state.groups, state.images);
    if (saved.sortMode) state.sortMode = saved.sortMode;
    if (saved.theme) state.theme = saved.theme;
  } catch (e) { preTag(state.groups, state.images); }
}
function saveState() {
  const imgTags = {};
  for (const im of state.images) imgTags[im.id] = im.tags;
  localStorage.setItem(LS_KEY, JSON.stringify({
    groups: state.groups, imgTags, sortMode: state.sortMode, theme: state.theme,
  }));
}

/* ---------- 文件名协议（TagSpaces 文件名模式） ---------- */
function tagIdInfo(id) {
  for (const g of state.groups) { const t = g.tags.find(t => t.id === id); if (t) return { group: g, tag: t }; }
  return null;
}
/** 完整文件名：base[标签1 标签2].ext —— 标签按“组顺序 + 组内顺序”排列 */
function fileNameOf(im) {
  const names = state.groups.flatMap(g => g.tags.filter(t => im.tags.includes(t.id)).map(t => t.name));
  return im.base + (names.length ? "[" + names.join(" ") + "]" : "") + im.ext;
}
/** 展示名：剥离方括号标签段 */
function displayNameOf(im) { return im.base + im.ext; }

/* ---------- 查询 ---------- */
function tagCount(tagId) { return state.images.reduce((n, im) => n + (im.tags.includes(tagId) ? 1 : 0), 0); }
function groupTagCount(group) { return group.tags.reduce((n, t) => n + tagCount(t.id), 0); }
function discoveredImages() { return state.images.slice(0, state.discovered); }
function filteredImages() {
  let list = discoveredImages();
  if (state.filters.size) {
    const fs = [...state.filters];
    list = list.filter(im => fs.some(id => im.tags.includes(id)));
  }
  if (state.scanDone && state.sortMode === "name") {
    list = [...list].sort((a, b) => fileNameOf(a).localeCompare(fileNameOf(b), "zh-Hans-CN", { numeric: true }));
  }
  return list;
}

/* =========================================================
 * 渲染
 * ========================================================= */

/* ---- 左侧标签栏（目录树行式节点：组行 + 缩进子标签行，点击组行展开/折叠） ---- */
function renderSidebar() {
  const box = $("#groupList");
  box.innerHTML = "";
  for (const g of state.groups) {
    const collapsed = state.collapsedGroups.has(g.id);
    const el = document.createElement("div");
    el.className = "tag-group" + (collapsed ? " collapsed" : "");
    el.dataset.gid = g.id;
    el.style.setProperty("--hue", g.hue);
    el.innerHTML = `
      <div class="group-head" data-act="g-expand" title="展开/折叠组内标签">
        <span class="chevron">${collapsed ? "▸" : "▾"}</span>
        <span class="group-name">${esc(g.name)}</span>
        <span class="group-badge ${g.exclusive ? "excl" : "multi"}">${g.exclusive ? "互斥" : "兼容"}</span>
        <span class="row-count">${groupTagCount(g)}</span>
        <span class="row-cmds">
          <button class="icon-btn" data-act="t-add" title="新建标签">＋</button>
          <button class="icon-btn" data-act="g-toggle" title="切换互斥/兼容">⇄</button>
          <button class="icon-btn" data-act="g-rename" title="重命名组">✎</button>
          <button class="icon-btn" data-act="g-delete" title="删除组">✕</button>
        </span>
      </div>
      <div class="tag-rows">
        ${g.tags.map(t => tagRowHtml(g, t)).join("")}
      </div>`;
    box.appendChild(el);
  }
}
function toggleCollapsed(gid) {
  state.collapsedGroups.has(gid) ? state.collapsedGroups.delete(gid) : state.collapsedGroups.add(gid);
  renderSidebar();
}
function tagRowHtml(g, t) {
  const on = state.filters.has(t.id) ? "filter-on" : "";
  return `<div class="tag-row ${on}" data-tag="${t.id}" title="点击：按该标签筛选（多标签任一命中，再点取消）&#10;拖拽图片到此行：为图片打该标签">
    <span class="row-indent"><i class="tree-line"></i></span>
    ${g.exclusive ? '<span class="radio-dot"></span>' : ""}
    <span class="tag-name">${esc(t.name)}</span>
    <span class="row-count">${tagCount(t.id)}</span>
    <span class="row-cmds">
      <button class="icon-btn" data-act="t-rename" title="重命名标签">✎</button>
      <button class="icon-btn" data-act="t-delete" title="删除标签">✕</button>
    </span>
  </div>`;
}

/* ---- 瀑布流 ---- */
const TARGET_W = 240, GAP = 14;
function buildCard(im) {
  const card = document.createElement("div");
  card.className = "card" + (state.selection.has(im.id) ? " selected" : "");
  card.dataset.id = im.id;
  card.draggable = true;
  const hue = hueOf(im.id);
  const th = Math.round(360 * im.h / im.w);
  card.innerHTML = `
    <div class="thumb" style="aspect-ratio:${im.w}/${im.h}">
      <img loading="lazy" src="https://picsum.photos/seed/${im.id}/360/${th}" alt="">
      <div class="check">✓</div>
      <div class="badges">${badgesHtml(im)}</div>
    </div>
    <div class="card-meta">
      <span class="fname" title="${esc(fileNameOf(im))}">${esc(displayNameOf(im))}</span>
      <span class="ftags">${tagsLine(im)}</span>
    </div>`;
  const img = card.querySelector("img");
  img.onerror = () => {
    const tb = card.querySelector(".thumb");
    tb.classList.add("fallback");
    tb.style.background = `linear-gradient(135deg, hsl(${hue} 40% 42%), hsl(${(hue + 50) % 360} 40% 26%))`;
    tb.textContent = im.base;
    img.remove();
  };
  card.addEventListener("click", e => onCardClick(im.id, e));
  card.addEventListener("dblclick", () => openViewer(im.id));
  // 拖拽打标（2026-09-19 交互重构）：选中集内的卡 = 整集，否则仅该卡（不改选中集）
  card.addEventListener("dragstart", e => {
    const ids = state.selection.has(im.id) ? [...state.selection] : [im.id];
    e.dataTransfer.setData("text/sv-cards", JSON.stringify(ids));
    e.dataTransfer.effectAllowed = "copy";
  });
  return card;
}
function badgesHtml(im) {
  const items = [];
  for (const g of state.groups) {
    for (const t of g.tags) if (im.tags.includes(t.id)) items.push({ name: t.name, hue: g.hue });
  }
  const shown = items.slice(0, 3).map(i => `<span class="badge" style="--hue:${i.hue}">${esc(i.name)}</span>`).join("");
  const more = items.length > 3 ? `<span class="badge more">+${items.length - 3}</span>` : "";
  return shown + more;
}
function tagsLine(im) {
  const names = state.groups.flatMap(g => g.tags.filter(t => im.tags.includes(t.id)).map(t => t.name));
  return names.length ? names.join(" · ") : "无标签";
}

function renderWaterfallAll() {
  const wrap = $("#waterfall");
  wrap.innerHTML = ""; cardEls.clear();
  for (const im of filteredImages()) {
    const el = buildCard(im);
    cardEls.set(im.id, el);
    wrap.appendChild(el);
  }
  layoutCards();
  $("#emptyHint").classList.toggle("hidden", wrap.children.length > 0);
}
function appendCards(from, to) {
  const wrap = $("#waterfall");
  for (let i = from; i < to; i++) {
    const el = buildCard(state.images[i]);
    cardEls.set(state.images[i].id, el);
    wrap.appendChild(el);
  }
  layoutCards();
}
function updateCard(im) {
  const el = cardEls.get(im.id);
  if (!el) return;
  el.querySelector(".badges").innerHTML = badgesHtml(im);
  el.querySelector(".fname").textContent = displayNameOf(im);
  el.querySelector(".fname").title = fileNameOf(im);
  el.querySelector(".ftags").textContent = tagsLine(im);
}

function layoutCards() {
  const wrap = $("#waterfall");
  const W = $("#waterfallWrap").clientWidth - 28;
  if (W <= 0) return;
  const cols = Math.max(2, Math.floor(W / TARGET_W));
  const cw = Math.floor((W - (cols - 1) * GAP) / cols);
  const els = [...wrap.children];
  for (const el of els) el.style.width = cw + "px";
  const hs = els.map(el => el.offsetHeight);
  const heights = new Array(cols).fill(0);
  els.forEach((el, i) => {
    let c = 0; for (let k = 1; k < cols; k++) if (heights[k] < heights[c]) c = k;
    el.style.left = c * (cw + GAP) + "px";
    el.style.top = heights[c] + "px";
    heights[c] += hs[i] + GAP;
  });
  wrap.style.width = (cols * cw + (cols - 1) * GAP) + "px";
  wrap.style.height = Math.max(0, Math.max(...heights) - GAP) + "px";
}

/* ---- 筛选条 / 状态栏 ---- */
function renderFilterBar() {
  const bar = $("#filterBar");
  if (!state.filters.size) { bar.classList.add("empty"); return; }
  bar.classList.remove("empty");
  const chips = [...state.filters].map(id => {
    const info = tagIdInfo(id); if (!info) return "";
    return `<span class="filter-chip" style="--hue:${info.group.hue}">
      ${esc(info.group.name)}：${esc(info.tag.name)}
      <button data-clear="${id}" title="取消该筛选">✕</button></span>`;
  }).join("");
  $("#activeFilters").innerHTML = (state.filters.size > 1 ? `<span class="filter-chip" style="border-style:dashed">任一命中（OR）</span>` : "") + chips;
  $("#filterStats").textContent = `命中 ${filteredImages().length} / 已发现 ${state.discovered} 张`;
}
$("#filterBar").addEventListener("click", e => {
  const btn = e.target.closest("[data-clear]");
  if (btn) { state.filters.delete(btn.dataset.clear); refreshAll(); }
});
$("#clearFilterBtn").addEventListener("click", () => { state.filters.clear(); refreshAll(); });

function renderStatus() {
  const hit = filteredImages().length;
  $("#statusLeft").textContent = `已选 ${state.selection.size} 张 · 命中 ${hit} / ${state.discovered} 张`;
  $("#statusHint").textContent = state.selection.size
    ? "拖拽图片到左侧标签行 = 打标（整集） · Esc 取消选择"
    : "单击选择 · Shift 连选 · Ctrl+A 全选 · 双击看大图 · 拖到标签行 = 打标 · Esc 取消选择";
}

function refreshAll() { renderSidebar(); renderWaterfallAll(); renderFilterBar(); renderStatus(); saveState(); }
function refreshLight() { renderSidebar(); renderFilterBar(); renderStatus(); saveState(); }

/* =========================================================
 * 交互
 * ========================================================= */

/* ---- 卡片选择 ---- */
function onCardClick(id, e) {
  const list = filteredImages();
  if (e.shiftKey && state.lastClickId) {
    const a = list.findIndex(im => im.id === state.lastClickId);
    const b = list.findIndex(im => im.id === id);
    if (a >= 0 && b >= 0) {
      for (let i = Math.min(a, b); i <= Math.max(a, b); i++) state.selection.add(list[i].id);
    }
  } else {
    state.selection.has(id) ? state.selection.delete(id) : state.selection.add(id);
  }
  state.lastClickId = id;
  syncSelectionClass();
  renderStatus(); renderSidebar();
}
function syncSelectionClass() {
  for (const [id, el] of cardEls) el.classList.toggle("selected", state.selection.has(id));
}

/* ---- 标签点击：一律筛选（2026-09-19 交互重构：打标走拖拽/详情右栏） ---- */
function onChipClick(tagId) {
  state.filters.has(tagId) ? state.filters.delete(tagId) : state.filters.add(tagId);
  refreshAll();
}

/* ---- 拖拽打标（drop 目标在侧栏标签行，事件委托见 groupList） ---- */
function applyTagToIds(ids, group, tag) {
  let changed = 0;
  for (const id of ids) {
    const im = state.images.find(x => x.id === id); if (!im) continue;
    const before = im.tags.join(",");
    if (group.exclusive) {
      const groupIds = group.tags.map(t => t.id);
      im.tags = im.tags.filter(t => !groupIds.includes(t));
      im.tags.push(tag.id);
    } else if (!im.tags.includes(tag.id)) {
      im.tags.push(tag.id);
    }
    if (im.tags.join(",") !== before) { changed++; updateCard(im); }
  }
  showToast(`已为 ${changed} 张图片${group.exclusive ? "设置" : "添加"}「${tag.name}」${group.exclusive ? "（互斥组：同组旧标签已替换）" : ""}`);
  refreshLight();
}

/* ---- 侧栏管理（事件委托，处理增删改；行式树：.tag-row 为标签行） ---- */
$("#groupList").addEventListener("click", async e => {
  const btn = e.target.closest("[data-act]");
  if (btn) {
    e.stopPropagation();
    const gEl = btn.closest(".tag-group");
    const gid = gEl?.dataset.gid;
    const group = state.groups.find(g => g.id === gid); if (!group) return;
    const row = btn.closest(".tag-row");
    const tag = row ? group.tags.find(t => t.id === row.dataset.tag) : null;
    switch (btn.dataset.act) {
      case "t-add": return await actAddTag(group);
      case "t-rename": return tag && await actRenameTag(group, tag);
      case "t-delete": return tag && await actDeleteTag(group, tag);
      case "g-expand": return toggleCollapsed(gid);
      case "g-toggle": return actToggleExclusive(group);
      case "g-rename": return await actRenameGroup(group);
      case "g-delete": return await actDeleteGroup(group);
    }
    return;
  }
  const row = e.target.closest(".tag-row");
  if (row) onChipClick(row.dataset.tag);
});

/* 拖拽卡片 → 标签行：dragover 高亮（drop-on），drop 按拖拽 payload（整集/单卡）打标 */
$("#groupList").addEventListener("dragover", e => {
  const row = e.target.closest(".tag-row"); if (!row) return;
  if (!e.dataTransfer.types.includes("text/sv-cards")) return;
  e.preventDefault();
  e.dataTransfer.dropEffect = "copy";
  row.classList.add("drop-on");
});
$("#groupList").addEventListener("dragleave", e => {
  const row = e.target.closest(".tag-row");
  if (row) row.classList.remove("drop-on");
});
$("#groupList").addEventListener("drop", e => {
  const row = e.target.closest(".tag-row"); if (!row) return;
  e.preventDefault();
  row.classList.remove("drop-on");
  const raw = e.dataTransfer.getData("text/sv-cards");
  if (!raw) return;
  const info = tagIdInfo(row.dataset.tag); if (!info) return;
  applyTagToIds(JSON.parse(raw), info.group, info.tag);
});

/* ---- 标签/组管理动作 ---- */
function validateTagName(name, exceptId) {
  if (!name.trim()) return "标签名不能为空";
  if (/[\s\[\]]/.test(name)) return "标签名不能包含空格或方括号（TagSpaces 文件名语法约束）";
  for (const g of state.groups) for (const t of g.tags)
    if (t.name === name.trim() && t.id !== exceptId) return "与其他标签重名：文件名标签是平铺字符串，重名会导致无法区分";
  return null;
}
async function actAddTag(group) {
  const r = await showModal({ title: `在「${group.name}」中添加标签`, input: "", placeholder: "标签名（不能含空格/方括号）" });
  if (!r) return;
  const err = validateTagName(r.value);
  if (err) return showToast(err, 3000);
  group.tags.push({ id: uid("t"), name: r.value.trim() });
  refreshLight();
}
async function actRenameTag(group, tag) {
  const n = tagCount(tag.id);
  const r = await showModal({
    title: "重命名标签",
    msg: `「${tag.name}」被 ${n} 张图片引用。\n重命名将更新这些图片的文件名（打标即改名）。`,
    input: tag.name,
  });
  if (!r) return;
  const err = validateTagName(r.value, tag.id);
  if (err) return showToast(err, 3000);
  tag.name = r.value.trim();
  let i = 0;
  for (const im of state.images) if (im.tags.includes(tag.id)) updateCard(im), i++;
  showToast(`已更新 ${i} 个文件名`);
  refreshLight();
}
async function actDeleteTag(group, tag) {
  const n = tagCount(tag.id);
  const ok = await showModal({
    title: "删除标签",
    msg: `「${tag.name}」被 ${n} 张图片引用。\n删除将从这些图片的文件名中移除该标签，不会删除图片本体。`,
    okText: "删除", danger: true,
  });
  if (!ok) return;
  for (const im of state.images) {
    if (im.tags.includes(tag.id)) { im.tags = im.tags.filter(t => t !== tag.id); updateCard(im); }
  }
  group.tags = group.tags.filter(t => t.id !== tag.id);
  state.filters.delete(tag.id);
  refreshAll();
}
function actToggleExclusive(group) {
  group.exclusive = !group.exclusive;
  showToast(`「${group.name}」已切换为${group.exclusive ? "互斥（此后组内单选）" : "兼容"}。已打上的标签不变，仅影响后续交互。`, 3200);
  refreshLight();
}
async function actRenameGroup(group) {
  const r = await showModal({ title: "重命名标签组", input: group.name });
  if (!r || !r.value.trim()) return;
  group.name = r.value.trim();
  refreshLight();
}
async function actDeleteGroup(group) {
  const n = groupTagCount(group);
  const ok = await showModal({
    title: `删除标签组「${group.name}」`,
    msg: `该组标签共被 ${n} 处引用。\n删除将移除所有图片上的该组标签（文件名随之更新），不会删除图片。`,
    okText: "删除", danger: true,
  });
  if (!ok) return;
  const ids = group.tags.map(t => t.id);
  for (const im of state.images) {
    if (im.tags.some(t => ids.includes(t))) { im.tags = im.tags.filter(t => !ids.includes(t)); updateCard(im); }
  }
  ids.forEach(id => state.filters.delete(id));
  state.groups = state.groups.filter(g => g.id !== group.id);
  refreshAll();
}
$("#addGroupBtn").addEventListener("click", async () => {
  const r = await showModal({
    title: "新建标签组",
    input: "", placeholder: "组名，如：拍摄地点",
    checkbox: { label: "互斥组（组内标签单选，新标签替换旧标签；不勾选为兼容组）", checked: false },
  });
  if (!r || !r.value.trim()) return;
  state.groups.push({ id: uid("g"), name: r.value.trim(), exclusive: !!r.checked, tags: [], hue: (state.groups.length * 67 + 30) % 360 });
  refreshLight();
});

/* =========================================================
 * 单图查看（2026-09-19 交互重构：详情右栏 = 信息 + 标签管理）
 * ========================================================= */
let viewerIdx = -1;
function fmtSize(bytes) {
  const kb = bytes / 1024;
  return kb > 1024 ? (kb / 1024).toFixed(2) + " MB" : kb.toFixed(2) + " KB";
}
function openViewer(id) {
  const list = filteredImages();
  viewerIdx = list.findIndex(im => im.id === id);
  if (viewerIdx < 0) return;
  $("#viewerOverlay").classList.remove("hidden");
  catalogEl.classList.add("hidden");
  renderViewer();
}
function renderViewer() {
  const list = filteredImages();
  const im = list[viewerIdx];
  if (!im) return closeViewer();
  const full = fileNameOf(im);
  const seg = full.match(/^(\S*)\[[^\]]*\](\.\w+)$/);
  $("#viewerFileName").innerHTML = seg
    ? `${esc(seg[1])}<span class="tseg">${esc(full.slice(seg[1].length, full.length - seg[2].length))}</span>${esc(seg[2])}`
    : esc(full);
  $("#viewerPath").textContent = `D:\\Pics\\${im.folder}\\${full}`;
  $("#viewerImg").src = `https://picsum.photos/seed/${im.id}/1400/${Math.round(1400 * im.h / im.w)}`;
  // 详情右栏：结构化信息行（文件名与顶部一致；大小为演示模拟值，一次性生成）
  im.size ??= Math.round(3e5 + Math.random() * 8e6);
  $("#sideName").textContent = full;
  $("#sideSize").textContent = fmtSize(im.size);
  $("#sideDims").textContent = `${im.w} × ${im.h}`;
  $("#sideIndex").textContent = `${viewerIdx + 1} / ${list.length}`;
  renderSideTags(im);
}
/* 右栏标签 chips：当前图标签集合（✕ 移除）＋ 空态引导 */
function renderSideTags(im) {
  const chips = [];
  for (const id of im.tags) {
    const info = tagIdInfo(id); if (!info) continue;
    chips.push(`<span class="tag-chip" style="--hue:${info.group.hue}">
      ${esc(info.tag.name)}<button data-vremove="${id}" title="移除该标签">✕</button></span>`);
  }
  $("#sideTags").innerHTML = chips.length
    ? chips.join("")
    : `<span class="side-tags-empty">无标签——点击「＋」从标签目录添加</span>`;
}
/* chip ✕ 移除：当前图移除该标签（对齐 WinUI 单图 toggle 管线的移除方向） */
$("#sideTags").addEventListener("click", e => {
  const btn = e.target.closest("[data-vremove]");
  if (!btn) return;
  const list = filteredImages(); const im = list[viewerIdx]; if (!im) return;
  im.tags = im.tags.filter(x => x !== btn.dataset.vremove);
  updateCard(im); renderViewer(); refreshLight();
});

/* ＋ 标签目录选择器（两级行列表：组行不可点，标签行可点；已选禁点） */
const catalogEl = $("#tagCatalog");
function renderCatalog() {
  const list = filteredImages(); const im = list[viewerIdx];
  $("#catalogList").innerHTML = state.groups.map(g => `
    <div class="cat-group">${esc(g.name)}
      <span class="group-badge ${g.exclusive ? "excl" : "multi"}">${g.exclusive ? "互斥" : "兼容"}</span>
    </div>
    ${g.tags.map(t => {
      const on = im && im.tags.includes(t.id);
      return `<div class="cat-tag ${on ? "on" : ""}" data-cg="${g.id}" data-ct="${t.id}" style="--hue:${g.hue}"
        title="${on ? "已含该标签" : "点击为当前图片打该标签"}">
        ${g.exclusive ? '<span class="radio-dot"></span>' : ""}<span class="ct-name">${esc(t.name)}</span>${on ? '<span class="ct-applied">✓ 已有</span>' : ""}
      </div>`;
    }).join("")}`).join("");
}
$("#addTagBtn").addEventListener("click", e => {
  e.stopPropagation();
  if (catalogEl.classList.contains("hidden")) renderCatalog();
  catalogEl.classList.toggle("hidden");
});
document.addEventListener("click", e => {
  if (!catalogEl.classList.contains("hidden") && !e.target.closest("#tagCatalog") && !e.target.closest("#addTagBtn")) {
    catalogEl.classList.add("hidden");
  }
});
$("#catalogList").addEventListener("click", e => {
  const row = e.target.closest(".cat-tag");
  if (!row || row.classList.contains("on")) return;
  const list = filteredImages(); const im = list[viewerIdx]; if (!im) return;
  const g = state.groups.find(x => x.id === row.dataset.cg);
  const t = g?.tags.find(x => x.id === row.dataset.ct); if (!t) return;
  if (g.exclusive) im.tags = im.tags.filter(x => !g.tags.some(gt => gt.id === x));
  im.tags.push(t.id);
  catalogEl.classList.add("hidden");
  updateCard(im); renderViewer(); refreshLight();
});
function viewerStep(d) {
  const list = filteredImages();
  viewerIdx = (viewerIdx + d + list.length) % list.length;
  renderViewer();
}
$("#viewerPrev").addEventListener("click", () => viewerStep(-1));
$("#viewerNext").addEventListener("click", () => viewerStep(1));
$("#viewerClose").addEventListener("click", closeViewer);
function closeViewer() {
  $("#viewerOverlay").classList.add("hidden");
  catalogEl.classList.add("hidden");
}
$("#viewerDelete").addEventListener("click", () => {
  const list = filteredImages(); const im = list[viewerIdx]; if (!im) return;
  state.images = state.images.filter(x => x.id !== im.id);
  state.selection.delete(im.id);
  cardEls.get(im.id)?.remove(); cardEls.delete(im.id);
  showToast(`已移入回收站：${fileNameOf(im)}（模拟）`);
  closeViewer(); refreshLight();
});

/* =========================================================
 * 键盘 / 全局
 * ========================================================= */
document.addEventListener("keydown", e => {
  const modalOpen = !$("#modalOverlay").classList.contains("hidden");
  if (modalOpen) { if (e.key === "Escape") $("#modalCancel").click(); return; }
  const viewerOpen = !$("#viewerOverlay").classList.contains("hidden");
  if (viewerOpen) {
    if (e.key === "Escape") closeViewer();
    else if (e.key === "ArrowLeft") viewerStep(-1);
    else if (e.key === "ArrowRight") viewerStep(1);
    return;
  }
  if (e.key === "Escape" && !["INPUT", "SELECT", "TEXTAREA"].includes(e.target.tagName)) {
    state.selection.clear(); syncSelectionClass(); renderStatus(); renderSidebar();
  }
  if (e.key.toLowerCase() === "a" && (e.ctrlKey || e.metaKey) && !["INPUT", "TEXTAREA"].includes(e.target.tagName)) {
    e.preventDefault();
    for (const im of filteredImages()) state.selection.add(im.id);
    syncSelectionClass(); renderStatus(); renderSidebar();
  }
});

/* ---- 排序 / 主题 / 重置 ---- */
$("#sortMode").addEventListener("change", e => { state.sortMode = e.target.value; refreshAll(); });
$("#themeBtn").addEventListener("click", () => {
  state.theme = state.theme === "dark" ? "light" : "dark";
  document.body.classList.toggle("dark", state.theme === "dark");
  saveState();
});
$("#resetDemoBtn").addEventListener("click", async () => {
  const ok = await showModal({ title: "重置演示数据", msg: "清空本地改动（标签组、打标记录），重新生成演示图库。", okText: "重置", danger: true });
  if (!ok) return;
  localStorage.removeItem(LS_KEY);
  location.reload();
});

/* =========================================================
 * 对话框 & Toast
 * ========================================================= */
function showModal({ title, msg = "", input = null, placeholder = "", checkbox = null, okText = "确定", danger = false }) {
  return new Promise(resolve => {
    $("#modalTitle").textContent = title;
    $("#modalMsg").textContent = msg;
    const inp = $("#modalInput");
    if (input !== null) { inp.classList.remove("hidden"); inp.value = input; inp.placeholder = placeholder; }
    else inp.classList.add("hidden");
    const cw = $("#modalCheckWrap");
    if (checkbox) { cw.classList.remove("hidden"); $("#modalCheck").checked = checkbox.checked; $("#modalCheckLabel").textContent = checkbox.label; }
    else cw.classList.add("hidden");
    const okBtn = $("#modalOk");
    okBtn.textContent = okText;
    okBtn.classList.toggle("danger-ok", !!danger);
    okBtn.style.background = danger ? "var(--danger)" : "var(--accent)";
    $("#modalOverlay").classList.remove("hidden");
    if (input !== null) setTimeout(() => inp.focus(), 30);
    const done = val => {
      $("#modalOverlay").classList.add("hidden");
      okBtn.onclick = null; $("#modalCancel").onclick = null; inp.onkeydown = null;
      resolve(val);
    };
    okBtn.onclick = () => done({ value: $("#modalInput").value, checked: $("#modalCheck").checked });
    $("#modalCancel").onclick = () => done(null);
    inp.onkeydown = e => { if (e.key === "Enter") okBtn.click(); };
  });
}
let toastTimer = null;
function showToast(text, ms = 2200) {
  const t = $("#toast");
  t.textContent = text;
  t.classList.remove("hidden");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.classList.add("hidden"), ms);
}

/* =========================================================
 * 扫描模拟 & 启动
 * ========================================================= */
function startScan() {
  const timer = setInterval(() => {
    const from = state.discovered;
    state.discovered = Math.min(state.images.length, from + 12);
    appendCards(from, state.discovered);
    renderFilterBar(); renderStatus();
    if (state.discovered >= state.images.length) {
      clearInterval(timer);
      state.scanDone = true;
      const s = $("#scanStatus");
      s.textContent = `${state.images.length} 张图片`;
      s.classList.add("done");
      renderWaterfallAll(); renderFilterBar(); renderStatus();  // 扫描完成后按当前排序模式整序
    } else {
      $("#scanStatus").textContent = `扫描中 · 已发现 ${state.discovered} / ${state.images.length}`;
    }
  }, 60);
}

let rafPending = false;
new ResizeObserver(() => {
  if (rafPending) return;
  rafPending = true;
  requestAnimationFrame(() => { rafPending = false; layoutCards(); });
}).observe($("#waterfallWrap"));

loadState();
document.body.classList.toggle("dark", state.theme === "dark");
$("#sortMode").value = state.sortMode;
renderSidebar(); renderFilterBar(); renderStatus();
startScan();
