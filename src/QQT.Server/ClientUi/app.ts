export {};

type Fraction = { numerator: number; denominator: number };
type ExactPosition = { x: Fraction; y: Fraction };
type Position = { x: number; y: number };
type ItemGains = { speed: number; capacity: number; range: number; vision: number };
type GroundItem = { id: number; position: Position; type: string; status: string; effect: number };
type Player = { id: number; position: Position; hit: boolean; alive: boolean; deathTick: Fraction | null; point: ExactPosition; speed: number; capacity: number; range: number; vision: number; pending: ItemGains; lifetime: Fraction };
type Bubble = { id: number; ownerId: number; position: Position; dueAt: Fraction };
type Move = { playerId: number; from: ExactPosition; to: ExactPosition; start: Fraction; end: Fraction };
type GameEvent = { sequence: number; kind: string; tick: Fraction; relatedTime: Fraction; point: ExactPosition; roundId: number; playerId: number; bubbleId: number; position: Position; from: Position; success: boolean; value: number; reason: string; itemType: string; previousValue: number; attributes: ItemGains; pending: ItemGains; phase: string };
type Frame = { type: "frame"; runId: string; name: string; totalTicks: number; status: string; error: string; tick: number; size: number; time: Fraction; durationMs: number; catchUpThreshold: number; playbackSpeed: number; animate: boolean; terrain: number[]; items: GroundItem[]; players: Player[]; bubbles: Bubble[]; moves: Move[]; events: GameEvent[]; crateCount: number; bubbleCount: number; ended: boolean; winnerId: number | null; endTick: Fraction | null; result?: { passed: boolean; safePlayers: number; walls: number; placed: number; exploded: number; destroyed: number; mapMatches: boolean; deadPlayers: number; deaths: number } };

function element<T extends HTMLElement>(id: string): T { return document.getElementById(id) as T; }
const canvas = element<HTMLCanvasElement>("map");
const context = canvas.getContext("2d")!;
const start = element<HTMLButtonElement>("start");
const demonstrate = element<HTMLButtonElement>("demonstrate");
const createMatch = element<HTMLButtonElement>("create-match");
type Session = { type: "session.state"; gameId: string; payload: { phase: string; playerCount: number; registeredCount: number; manualTickAdvance: boolean; seed: number; nextRoundId: number; canStart: boolean; canDemonstrate: boolean; waitingPlayerIds: number[]; defaults: {playerCount: number; manualTickAdvance: boolean; seed: number | null} } };
let session: Session | null = null;
const connection = element("connection");
const status = element("status");
const tick = element("tick");
const totalTicks = element("total-ticks");
const scenarioTitle = element("scenario-title");
const scenarioFooter = element("scenario-footer");
const progress = element("progress");
const bubbles = element("bubbles");
const crates = element("crates");
const safe = element("safe");
const result = element("result");
const players = element("players");
const log = element("log");
const eventCount = element("event-count");
// 按页面生命周期复用颜色表与渲染状态，绘制循环不构造临时集合。
const colors = ["#91d5ff", "#ffa99b", "#c4b0fa", "#b9ed8b"];
// 缓存只在收到服务端快照时更新；绘制循环复用，不分配每帧容器。
const placedEvents = new Map<number, GameEvent>();
const explodedEvents = new Map<number, GameEvent>();
const destroyedTimes = new Map<number, number>();
const exposedItemEvents = new Map<number, GameEvent>();
const removedItemEvents = new Map<number, GameEvent>();
const itemLabels: Record<string, string> = { speed: "移速", capacity: "泡泡容量", range: "火力", vision: "视野半径" };
// 四张图标在页面启动时加载并复用；不在绘制帧中创建图片或临时集合。
const itemIcons: Record<string, HTMLImageElement> = {
  speed: loadItemIcon("speed"), capacity: loadItemIcon("capacity"),
  range: loadItemIcon("range"), vision: loadItemIcon("vision")
};
function loadItemIcon(type: string): HTMLImageElement {
  const icon = new Image(); icon.decoding = "async"; icon.src = `/icons/item-${type}.svg`; return icon;
}
let displayedAttributeSequence = 0;
function number(value: Fraction): number { return value.numerator / value.denominator; }
function exact(value: Fraction): string { return value.denominator === 1 ? String(value.numerator) : `${value.numerator}/${value.denominator}`; }
let socket: WebSocket | null = null;
let current: Frame | null = null;
let target: Frame | null = null;
// 页面生命周期持有队列；只计尚未开始播放的完整轮，不计 target 当前动画。
const pendingFrames: Frame[] = [];
let playerOverride = false, manualOverride = false, seedOverride = false;
element("player-count").addEventListener("change", () => { playerOverride = true; });
element("manual-tick").addEventListener("change", () => { manualOverride = true; });
element("spawn-seed").addEventListener("input", () => { seedOverride = true; });
let animationStart = 0;
let flashStart = -10000;
let loggedSequence = 0;
let viewSize = 600;

function gridName(p: Position): string { return String.fromCharCode(65 + p.x) + (p.y + 1); }
function eventText(e: GameEvent): string {
  switch (e.kind) {
    case "itemGenerated": return `${gridName(e.position)} 箱内预藏${itemLabels[e.itemType]}道具（观战信息）`;
    case "itemExposed": return `${gridName(e.position)} 露出${itemLabels[e.itemType]} +1 道具`;
    case "itemDestroyed": return `${gridName(e.position)} 的${itemLabels[e.itemType]}道具被爆炸销毁`;
    case "itemPicked": return `玩家 ${e.playerId} 在 ${gridName(e.position)} 拾取${itemLabels[e.itemType]} +1，下轮生效`;
    case "attributeActivated": return `第 ${e.roundId} 轮玩家 ${e.playerId} ${itemLabels[e.itemType]}：${e.previousValue} → ${e.value}`;
    case "entered": return `玩家 ${e.playerId} 跨线进入 ${gridName(e.position)}`;
    case "returnStarted": return `玩家 ${e.playerId} 遇阻，原速退回 ${gridName(e.position)} 中心`;
    case "returned": return `玩家 ${e.playerId} 已退回 ${gridName(e.position)} 中心，本轮停止移动`;
    case "initialBubble": return `初始泡泡 #${e.bubbleId} 在 ${gridName(e.position)}，t=${exact(e.relatedTime)} 到期`;
    case "move": return `玩家 ${e.playerId}：${gridName(e.from)} → ${gridName(e.position)}`;
    case "blocked": return `玩家 ${e.playerId}：${gridName(e.position)} 被阻挡`;
    case "placed": return `玩家 ${e.playerId} 在 ${gridName(e.position)} 放置泡泡 #${e.bubbleId}，t=${exact(e.relatedTime)} 到期`;
    case "exploded": return `泡泡 #${e.bubbleId} 在 ${gridName(e.position)} 爆炸`;
    case "chained": return `泡泡 #${e.value} 连锁引爆 ${gridName(e.position)} 的泡泡 #${e.bubbleId}`;
    case "disconnected": return `玩家 ${e.playerId} 掉线淘汰`;
    case "matchEnded": return e.playerId > 0 ? `对局结束：玩家 ${e.playerId} 获胜` : "对局结束：平局";
    case "crateDestroyed": return `${gridName(e.position)} 箱子销毁`;
    case "safety": return `玩家 ${e.playerId} 在 ${gridName(e.position)}：${e.success ? "安全" : "受击"}`;
    case "death": return `玩家 ${e.playerId} 在 ${gridName(e.position)} 死亡，保留 ${e.value} 颗在场泡泡`;
    case "actionIgnored": return `玩家 ${e.playerId} 已死亡，第 ${e.value} 轮动作已忽略`;
    default: return `玩家 ${e.playerId} 在 ${gridName(e.position)} 放置${e.reason === "dead" ? "取消：已死亡" : e.reason === "capacity" ? "失败：容量不足" : "跳过：未到达中心"}`;
  }
}

function applyFrame(frame: Frame, now: number): void {
  const newRun = current?.runId !== frame.runId;
  if (newRun) { loggedSequence = 0; log.replaceChildren(); flashStart = -10000; }
  current = frame;
  flashStart = -10000; // 途中爆炸已按轨迹播放，轮末不重新播放一次。
  tick.textContent = String(frame.tick);
  totalTicks.textContent = frame.totalTicks > 0 ? String(frame.totalTicks) : "—";
  scenarioTitle.textContent = frame.name;
  scenarioFooter.textContent = frame.totalTicks > 0 ? `M0 · ${frame.totalTicks} 轮脚本` : "M0 · 预设脚本";
  progress.style.width = `${frame.ended ? 100 : frame.totalTicks > 0 ? frame.tick / frame.totalTicks * 100 : 0}%`;
  bubbles.textContent = String(frame.bubbleCount);
  crates.textContent = String(frame.crateCount);
  if (players.children.length !== frame.players.length) initializePlayerCards(frame.players.length);
  let safeCount = 0;
  for (const p of frame.players) {
    if (p.alive) safeCount++;
    const card = players.children[p.id - 1] as HTMLElement;
    card.classList.toggle("is-dead", !p.alive);
    card.querySelector(".player-location")!.textContent = gridName(p.position);
    card.querySelector(".player-status")!.textContent = p.alive ? "存活" : "已淘汰";
    updateAttributeRow(p.id, p, p.pending);
  }
  safe.textContent = String(safeCount);
  status.textContent = frame.status === "running" ? "验证进行中" : frame.status === "passed" ? "验证通过" : frame.status === "failed" ? "验证失败" : frame.status === "error" ? "运行错误" : "准备就绪";
  const outcome = frame.ended ? `${frame.winnerId === null ? "平局" : `玩家 ${frame.winnerId} 获胜`}（t=${exact(frame.endTick!)}）` : "";
  if (outcome) status.textContent += ` · ${outcome}`;
  result.className = "result";
  if (frame.status === "passed") {
    result.textContent = `验证通过：${outcome ? outcome + "；" : ""}存活 ${frame.result!.safePlayers} 人，死亡 ${frame.result!.deadPlayers} 人，${frame.result!.exploded} 颗泡泡已爆炸，${frame.result!.destroyed} 个指定箱子已销毁。`; result.classList.add("success");
  } else if (frame.status === "failed" || frame.status === "error") {
    result.textContent = frame.error || "验证失败：请查看位置、计时和服务端日志。"; result.classList.add("failure");
  } else result.textContent = "运行后按脚本预期核对位置、死亡时刻和泡泡处理。";
  for (const e of frame.events) {
    if (e.sequence <= loggedSequence) continue;
    if (e.kind === "exploded" && number(e.tick) === number(frame.time) && e.roundId === frame.tick && !newRun) flashStart = now;
    if (e.kind !== "covered" && e.kind !== "state") {
      const row = document.createElement("div"); row.className = "log-entry";
      const time = document.createElement("time"); time.textContent = `t=${exact(e.tick)}`;
      const text = document.createElement("span"); text.textContent = eventText(e);
      row.append(time, text); log.append(row);
    }
    loggedSequence = e.sequence;
  }
  eventCount.textContent = `${log.childElementCount} 条`;
  log.scrollTop = log.scrollHeight;
  displayedAttributeSequence = loggedSequence;
  start.textContent = frame.tick > 0 ? "重新运行 ↗" : "开始验证 ↗";
  start.disabled = socket?.readyState !== WebSocket.OPEN || frame.status === "running";
  if (session) renderSession();
}

// 四张卡片按页面生命周期持有，消息和动画事件只改值，避免逐 tick 重建 DOM。
function initializePlayerCards(count = 4): void {
  players.replaceChildren();
  for (let id = 1; id <= count; id++) {
    const card = document.createElement("article"); card.className = "player player-card";
    card.style.setProperty("--player-color", colors[id - 1]); card.setAttribute("aria-label", `玩家 ${id} 实时属性`);
    card.innerHTML = `<div class="player-header"><span class="player-id">${id}</span><div class="player-heading"><strong>玩家 ${id}</strong><span class="player-location">—</span></div><span class="player-status">等待</span></div>
      <dl class="player-stats" aria-live="polite">
        <div data-stat="speed"><dt><img src="/icons/item-speed.svg" alt="">移速</dt><dd><strong class="stat-value">—</strong><small class="stat-unit">格/tick</small></dd><span class="stat-pending" hidden></span></div>
        <div data-stat="range"><dt><img src="/icons/item-range.svg" alt="">火力</dt><dd><strong class="stat-value">—</strong><small class="stat-unit">格/方向</small></dd><span class="stat-pending" hidden></span></div>
        <div data-stat="capacity"><dt><img src="/icons/item-capacity.svg" alt="">泡泡容量</dt><dd><strong class="stat-value">—</strong><small class="stat-unit">颗</small></dd><span class="stat-pending" hidden></span></div>
        <div data-stat="vision"><dt><img src="/icons/item-vision.svg" alt="">视野</dt><dd><strong class="stat-value">—</strong><small class="stat-unit">R=—</small></dd><span class="stat-pending" hidden></span></div>
      </dl>`;
    players.append(card);
  }
}
function updateStat(card: HTMLElement, name: string, value: string, pending: number, unit?: string): void {
  const stat = card.querySelector(`[data-stat="${name}"]`)!;
  stat.querySelector(".stat-value")!.textContent = value;
  if (unit !== undefined) stat.querySelector(".stat-unit")!.textContent = unit;
  const next = stat.querySelector(".stat-pending") as HTMLElement;
  next.hidden = pending === 0;
  next.textContent = pending > 0 ? `下轮 +${pending}` : "";
}
function updateAttributeRow(id: number, values: ItemGains, pending: ItemGains): void {
  const card = players.children[id - 1] as HTMLElement | undefined;
  if (!card || !current) return;
  updateStat(card, "speed", String(values.speed), pending.speed);
  updateStat(card, "range", String(values.range), pending.range);
  updateStat(card, "capacity", String(values.capacity), pending.capacity);
  const width = values.vision * 2 + 1;
  updateStat(card, "vision", `${width}×${width}`, pending.vision, `R=${values.vision}`);
  const p = current.players[id - 1];
  card.title = `速度 ${values.speed} 格/tick · 容量 ${values.capacity} · 火力 ${values.range} 格/方向 · 视野 R=${values.vision}（地图边缘裁剪） · 寿命 ${exact(p.lifetime)} tick · 质点 (${exact(p.point.x)}, ${exact(p.point.y)})`;
}

function receive(message: MessageEvent<string>): void {
  // JSON.parse 返回的数据由消息持有；渲染只借用它，不复制数组或生成每帧快照。
  const data = JSON.parse(message.data) as Frame | Session | { type: "error"; message?: string; payload?: { error: { message: string } } };
  if (data.type === "session.state") { session = data; renderSession(); return; }
  if (data.type === "error") { result.textContent = data.message ?? data.payload?.error.message ?? "请求失败"; result.className = "result failure"; return; }
  const now = performance.now();
  if (current?.runId !== data.runId) { placedEvents.clear(); explodedEvents.clear(); destroyedTimes.clear(); exposedItemEvents.clear(); removedItemEvents.clear(); displayedAttributeSequence = 0; }
  for (const event of data.events) {
    if (event.kind === "placed" || event.kind === "initialBubble") placedEvents.set(event.bubbleId, event);
    if (event.kind === "exploded") explodedEvents.set(event.bubbleId, event);
    if (event.kind === "itemExposed") exposedItemEvents.set(event.position.y * data.size + event.position.x, event);
    if (event.kind === "itemDestroyed" || event.kind === "itemPicked") removedItemEvents.set(event.position.y * data.size + event.position.x, event);
    if (event.kind === "crateDestroyed") destroyedTimes.set(event.position.y * data.size + event.position.x, number(event.tick));
  }
  if (current?.runId !== data.runId || !data.animate) {
    pendingFrames.length = 0; target = null; applyFrame(data, now); return;
  }
  if (target) {
    pendingFrames.push(data);
    if (pendingFrames.length >= data.catchUpThreshold) { pendingFrames.length = 0; target = null; applyFrame(data, now); }
    return;
  }
  startFrame(data, now);
}

function startFrame(data: Frame, now: number): void {
  if (data.animate && current?.runId === data.runId && data.tick === current.tick + 1) {
    target = data; animationStart = now; start.disabled = true; status.textContent = "验证进行中";
    if (session) renderSession();
  } else { target = null; applyFrame(data, now); }
}

function connect(): void {
  const protocol = location.protocol === "https:" ? "wss:" : "ws:";
  socket = new WebSocket(`${protocol}//${location.host}/ws/ui`);
  socket.onmessage = receive;
  socket.onopen = () => { connection.textContent = "● 连接正常"; };
  socket.onclose = () => { connection.textContent = "连接已断开 · 刷新页面重连"; start.disabled = true; };
  socket.onerror = () => { connection.textContent = "连接错误"; };
}
start.addEventListener("click", () => {
  if (socket?.readyState === WebSocket.OPEN) { start.disabled = true; socket.send(JSON.stringify(session ? {type:"start", gameId:session.gameId} : {type:"start"})); }
});

function renderSession(): void {
  const s = session!; const p = s.payload;
  if (!playerOverride) element<HTMLSelectElement>("player-count").value = String(p.defaults.playerCount);
  if (!manualOverride) element<HTMLInputElement>("manual-tick").checked = p.defaults.manualTickAdvance;
  if (!seedOverride) element<HTMLInputElement>("spawn-seed").value = p.defaults.seed === null ? "" : String(p.defaults.seed);
  element("battle-controls").hidden = false;
  element("seats").textContent = `${p.registeredCount}/${p.playerCount} 已登记`;
  start.textContent = "开始对战"; start.disabled = !p.canStart || socket?.readyState !== WebSocket.OPEN;
  demonstrate.hidden = !p.manualTickAdvance; demonstrate.disabled = !p.canDemonstrate;
  createMatch.disabled = p.phase !== "idle" && p.phase !== "ended" && p.phase !== "aborted";
  const labels: Record<string,string> = {idle:"等待创建对局",preparing:p.canStart ? "满员就绪，等待开始" : "等待玩家登记",awaitingTick:"等待 tick演示",collecting:`等待决策：${p.waitingPlayerIds.join("、")}`,simulating:"正在模拟",ended:"对战结束",aborted:"对战已中止"};
  status.textContent = labels[p.phase] ?? p.phase;
  scenarioFooter.textContent = `联机对战 · ${p.playerCount} 人 · ${p.manualTickAdvance ? "逐帧" : "自动"} · seed=${p.seed}`;
  if (current?.ended) {
    const outcome = current.winnerId === null ? "平局" : `玩家 ${current.winnerId} 获胜`;
    result.textContent = `${outcome}（t=${exact(current.endTick!)}）`; result.className="result success";
  } else if (p.phase !== "aborted") { result.textContent = "按各客户端提交的计划推进对战。"; result.className="result"; }
}
createMatch.addEventListener("click", () => {
  const payload: { playerCount?: number; manualTickAdvance?: boolean; seed?: number | null } = {};
  if (playerOverride) payload.playerCount = Number(element<HTMLSelectElement>("player-count").value);
  if (manualOverride) payload.manualTickAdvance = element<HTMLInputElement>("manual-tick").checked;
  const seed = element<HTMLInputElement>("spawn-seed").value.trim();
  if (seedOverride) payload.seed = seed === "" ? null : Number(seed);
  socket?.send(JSON.stringify({type:"match.create", payload}));
});
demonstrate.addEventListener("click", () => {
  if (!session) return;
  demonstrate.disabled = true;
  socket?.send(JSON.stringify({type:"tick.demonstrate",gameId:session.gameId,roundId:session.payload.nextRoundId,requestId:`ui-${Date.now()}`}));
});

function resize(): void {
  viewSize = canvas.getBoundingClientRect().width;
  const ratio = window.devicePixelRatio || 1;
  canvas.width = Math.round(viewSize * ratio); canvas.height = Math.round(viewSize * ratio);
  context.setTransform(ratio, 0, 0, ratio, 0, 0);
}
window.addEventListener("resize", resize);

function draw(now: number): void {
  let amount = 1;
  if (target) {
    amount = Math.min(1, (now - animationStart) * target.playbackSpeed / (target.durationMs * Math.max(0.001, number(target.time) - number(current!.time))));
    if (amount >= 1) {
      applyFrame(target, now); target = null;
      const next = pendingFrames.shift();
      if (next) { startFrame(next, now); amount = target ? 0 : 1; }
    }
  }
  context.clearRect(0, 0, viewSize, viewSize);
  if (current) {
    const scene = target ?? current;
    const logicalTime = target ? number(current.time) + (number(target.time) - number(current.time)) * amount : number(current.time);
    const cell = viewSize / 24;
    const origin = cell;
    context.font = `${cell * .32}px "Segoe UI", sans-serif`;
    context.textAlign = "center"; context.textBaseline = "middle";
    context.fillStyle = "#8399a3";
    for (let i = 0; i < 21; i++) {
      context.fillText(String.fromCharCode(65 + i), origin + (i + 1.5) * cell, cell * .4);
      context.fillText(String(i + 1), cell * .4, origin + (i + 1.5) * cell);
    }
    for (let y = -1; y <= 21; y++) for (let x = -1; x <= 21; x++) {
      const px = origin + (x + 1) * cell, py = origin + (y + 1) * cell;
      const outer = x === -1 || y === -1 || x === 21 || y === 21;
      const destroyedAt = destroyedTimes.get(y * 21 + x);
      const type = outer ? 1 : destroyedAt !== undefined && destroyedAt <= logicalTime ? 0 : current.terrain[y * 21 + x];
      context.fillStyle = type === 1 ? (outer ? "#34454f" : "#4b606a") : type === 2 ? "#9d7a46" : "#1e3039";
      context.fillRect(px + 1, py + 1, cell - 2, cell - 2);
      if (type === 2) {
        context.strokeStyle = "#ba985f"; context.lineWidth = Math.max(1, cell * .045);
        context.strokeRect(px + cell * .16, py + cell * .16, cell * .68, cell * .68);
        context.beginPath(); context.moveTo(px + cell * .18, py + cell * .2); context.lineTo(px + cell * .8, py + cell * .8); context.stroke();
      }
    }
    // 只按服务端事件时间显示露出者；终点已被拾取的道具仍可在本轮前半段正确播放。
    for (const [index, event] of exposedItemEvents) {
      const removed = removedItemEvents.get(index);
      if (number(event.tick) > logicalTime || (removed && number(removed.tick) <= logicalTime)) continue;
      const x = origin + (event.position.x + 1.5) * cell, y = origin + (event.position.y + 1.5) * cell;
      const icon = itemIcons[event.itemType];
      if (icon?.complete && icon.naturalWidth > 0) context.drawImage(icon, x - cell * .47, y - cell * .47, cell * .94, cell * .94);
    }
    for (const e of scene.events) if ((e.kind === "attributeActivated" || e.kind === "itemPicked") && e.sequence > displayedAttributeSequence && number(e.tick) <= logicalTime) {
      updateAttributeRow(e.playerId, e.attributes, e.pending); displayedAttributeSequence = e.sequence;
    }
    for (const b of placedEvents.values()) {
      if (number(b.tick) > logicalTime || (explodedEvents.has(b.bubbleId) && number(explodedEvents.get(b.bubbleId)!.tick) <= logicalTime)) continue;
      const x = origin + (b.position.x + 1.5) * cell, y = origin + (b.position.y + 1.5) * cell;
      context.fillStyle = "#68cee1"; context.strokeStyle = "#b7eff8"; context.lineWidth = cell * .045;
      context.beginPath(); context.arc(x, y, cell * .34, 0, Math.PI * 2); context.fill(); context.stroke();
      context.fillStyle = "#12363f"; context.font = `600 ${cell * .33}px sans-serif`; context.fillText((number(b.relatedTime) - logicalTime).toFixed(1), x, y);
    }
    for (const p of current.players) {
      let x = number(p.point.x), y = number(p.point.y);
      if (target) for (const move of target.moves) if (move.playerId === p.id) {
        const startTime = number(move.start), endTime = number(move.end);
        if (logicalTime < startTime) break;
        const fraction = Math.min(1, (logicalTime - startTime) / (endTime - startTime));
        x = number(move.from.x) + (number(move.to.x) - number(move.from.x)) * fraction;
        y = number(move.from.y) + (number(move.to.y) - number(move.from.y)) * fraction;
        if (logicalTime < endTime) break;
      }
      const nextPlayer = scene.players[p.id - 1];
      const dead = nextPlayer.deathTick !== null && number(nextPlayer.deathTick) <= logicalTime;
      const px = origin + (x + 1.5) * cell, py = origin + (y + 1.5) * cell;
      context.fillStyle = "#0d1920"; context.beginPath(); context.arc(px, py + cell * .06, cell * .37, 0, Math.PI * 2); context.fill();
      context.fillStyle = dead ? "#6b7378" : colors[p.id - 1];
      context.beginPath(); context.arc(px, py, cell * .31, 0, Math.PI * 2); context.fill();
      context.font = `700 ${cell * .34}px sans-serif`; context.fillStyle = "#172a31"; context.fillText(String(p.id), px, py);
      if (dead) {
        context.strokeStyle = "#efb3ac"; context.lineWidth = Math.max(1.5, cell * .06);
        context.beginPath(); context.moveTo(px - cell * .23, py - cell * .23); context.lineTo(px + cell * .23, py + cell * .23);
        context.moveTo(px + cell * .23, py - cell * .23); context.lineTo(px - cell * .23, py + cell * .23); context.stroke();
      }
    }
    const flash = target ? .8 : 1 - (now - flashStart) / 650;
    if (flash > 0) for (const e of scene.events) if (e.kind === "covered" && e.roundId === scene.tick && number(e.tick) <= logicalTime && (target ? logicalTime - number(e.tick) < .3 : number(e.tick) === logicalTime)) {
      context.fillStyle = `rgba(255,217,121,${flash * .8})`;
      context.fillRect(origin + (e.position.x + 1) * cell + 1, origin + (e.position.y + 1) * cell + 1, cell - 2, cell - 2);
    }
  }
  requestAnimationFrame(draw);
}

initializePlayerCards(); resize(); connect(); requestAnimationFrame(draw);
