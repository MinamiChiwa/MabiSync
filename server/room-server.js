// Room lifecycle and secure resume model reference: SyncCue e4d0fde.
// MabiSync has an independent protocol, epoch, sessions and action barrier.
import http from 'node:http';
import { randomBytes, randomInt, randomUUID, timingSafeEqual } from 'node:crypto';
import { WebSocket, WebSocketServer } from 'ws';
import { acceptObservation, clearObservation, expireObservations, ensembleFeedback, observedCharacters, memberIdentity, bindGameCharacter, memberMusic } from './ensemble-observation.js';

const PRODUCT = 'MabiSync', VERSION = 1, MAX_MEMBERS = 24;
const TERMINAL = new Set(['cancelled', 'stopped']);
const MODES = new Set(['emote', 'independent', 'ensemble']);
const validNumber = n => typeof n === 'number' && Number.isFinite(n);
function fail(code, details = {}) { throw Object.assign(new Error(code), { code, details }); }
function nickname(value) {
  if (typeof value !== 'string' || !value.trim() || [...value.trim()].length > 20) fail('BAD_NICKNAME');
  return value.trim();
}
function sameToken(a, b) {
  return typeof a === 'string' && /^[0-9a-f]{48}$/.test(a) && typeof b === 'string' && a.length === b.length &&
    timingSafeEqual(Buffer.from(a), Buffer.from(b));
}

export async function createRoomServer({ port = 3132, host = '127.0.0.1' } = {}) {
  const serverEpoch = randomUUID(), rooms = new Map(), closures = new Map(), timers = new Set();
  let closing = false;
  const now = () => performance.now();
  const identity = { product: PRODUCT, protocolVersion: VERSION, serverEpoch, features: { gameObservation: 1, ensembleRoster: 1, firstCycleStop: 1, rosterEditing: 1, roomClosure:1, adaptiveTiming:2 } };
  const server = http.createServer((req, res) => {
    res.writeHead(req.url === '/health' ? 200 : 404, { 'Content-Type': 'application/json; charset=utf-8' });
    res.end(JSON.stringify(req.url === '/health' ? { ok: true, ...identity, automaticGameEndVerified: false } : { error: 'not found' }));
  });
  const wss = new WebSocketServer({ server, maxPayload: 65_536, perMessageDeflate: false });
  wss.on('error', () => {});
  function send(ws, value) {
    if (ws?.readyState !== WebSocket.OPEN) return;
    if (ws.bufferedAmount > 1_048_576) { ws.close(4008, 'Slow connection'); return; }
    ws.send(JSON.stringify(value)); // ws serializes each socket's outgoing frames.
  }
  function later(fn, delay) {
    const timer = setTimeout(() => { timers.delete(timer); fn(); }, Math.max(0, delay));
    timers.add(timer); timer.unref(); return timer;
  }
  function clear(timer) { if (timer) { clearTimeout(timer); timers.delete(timer); } }
  const actions = p => [p.startAction, p.stopAction].filter(Boolean);
  const ensembleRole = member => member.ensembleRole ?? member.capabilities.musicRole ?? 'initiator';
  const ensembleGroup = member => member.ensembleGroup ?? 1;
  function roster(room) {
    const members = [...room.members.values()].sort((a, b) => ensembleGroup(a)-ensembleGroup(b) ||
      (ensembleRole(a)==='initiator'?0:1)-(ensembleRole(b)==='initiator'?0:1) || (a.ensemblePosition ?? 1000) - (b.ensemblePosition ?? 1000));
    return members.map(m => ({ memberId: m.id, group: ensembleGroup(m), role: ensembleRole(m),
      order: members.filter(n => ensembleGroup(n) === ensembleGroup(m)).findIndex(n => n.id === m.id) + 1 }));
  }
  function editEnsemble(room, msg) {
    if (room.performance && !TERMINAL.has(room.performance.phase)) fail('SESSION_ACTIVE');
    const member = room.members.get(msg.memberId);
    if (!member) fail('MEMBER_NOT_FOUND');
    if (msg.type === 'set-ensemble-role') {
      if (!['initiator', 'joiner'].includes(msg.musicRole)) fail('BAD_CAPABILITIES');
      if (msg.musicRole === 'initiator') for (const other of room.members.values())
        if (other.id !== member.id && ensembleGroup(other) === ensembleGroup(member)) other.ensembleRole = 'joiner';
      member.ensembleRole = msg.musicRole;
    } else {
      const target = room.members.get(msg.targetMemberId);
      if (msg.targetMemberId !== undefined && !target) fail('MEMBER_NOT_FOUND');
      if (target?.id === member.id) return;
      const group = target ? ensembleGroup(target) : msg.group;
      if (!Number.isInteger(group) || group < 1 || group > MAX_MEMBERS) fail('BAD_ENSEMBLE_GROUP');
      if (target && !['before', 'after'].includes(msg.placement)) fail('BAD_MESSAGE');
      if (ensembleGroup(member) !== group && [...room.members.values()].some(m => m.id !== member.id && ensembleGroup(m) === group && ensembleRole(m) === 'initiator')) member.ensembleRole = 'joiner';
      member.ensembleGroup = group;
      const list = roster(room).map(m => room.members.get(m.memberId)).filter(m => m.id !== member.id);
      const index = target ? list.findIndex(m => m.id === target.id) + (msg.placement === 'after' ? 1 : 0) : list.length;
      list.splice(index, 0, member); list.forEach((m, at) => { m.ensemblePosition = at; });
    }
    // Ensemble layout changes must not revoke an independently prepared emote.
    for (const m of room.members.values()) if (!m.capabilities.readyMode || m.capabilities.readyMode === 'ensemble') m.ready = false;
  }
  function editMember(room, msg) {
    if (room.performance && !TERMINAL.has(room.performance.phase)) fail('SESSION_ACTIVE');
    const member = room.members.get(msg.memberId);
    if (!member) fail('MEMBER_NOT_FOUND');
    if (msg.type === 'set-member-selection') {
      if (typeof msg.selected !== 'boolean') fail('BAD_MESSAGE');
      member.selected = msg.selected; return;
    }
    // Validate the entire edit before changing any member, including demoted initiators.
    if (msg.group !== undefined && (!Number.isInteger(msg.group) || msg.group < 1 || msg.group > MAX_MEMBERS)) fail('BAD_ENSEMBLE_GROUP');
    if (msg.order !== undefined && (!Number.isInteger(msg.order) || msg.order < 1 || msg.order > MAX_MEMBERS)) fail('BAD_ENSEMBLE_ORDER');
    if (msg.musicRole !== undefined && !['initiator','joiner'].includes(msg.musicRole)) fail('BAD_CAPABILITIES');
    if (msg.instrument !== undefined && (typeof msg.instrument !== 'string' || [...msg.instrument.trim()].length > 40 || /[\p{Cc}\p{Cf}]/u.test(msg.instrument))) fail('BAD_INSTRUMENT');
    if (['group','order','musicRole','instrument'].every(k=>msg[k]===undefined)) fail('BAD_MESSAGE');
    if (msg.group !== undefined && msg.group !== ensembleGroup(member)) editEnsemble(room,{type:'move-ensemble-member',memberId:member.id,group:msg.group});
    if (msg.musicRole !== undefined) editEnsemble(room,{type:'set-ensemble-role',memberId:member.id,musicRole:msg.musicRole});
    if (msg.order !== undefined) {
      const others=roster(room).filter(r=>r.group===ensembleGroup(member) && r.memberId!==member.id);
      const all=roster(room).map(r=>room.members.get(r.memberId)).filter(m=>m.id!==member.id);
      const position=Math.min(msg.order-1,others.length);
      const target=others[position];
      const insertion=target ? all.findIndex(m=>m.id===target.memberId) : others.length ? all.findIndex(m=>m.id===others.at(-1).memberId)+1 : all.length;
      all.splice(insertion,0,member);all.forEach((m,i)=>{m.ensemblePosition=i;});
    }
    if (msg.instrument !== undefined) member.instrument=msg.instrument.trim();
    for (const m of room.members.values()) if (!m.capabilities.readyMode || m.capabilities.readyMode==='ensemble') m.ready=false;
  }
  function capabilities(c) {
    if (!c || ['simulation', 'canStart', 'canStop'].some(k => typeof c[k] !== 'boolean')) fail('BAD_CAPABILITIES');
    const timingOffsetMs = c.timingOffsetMs ?? 0, musicRole = c.musicRole ?? 'initiator', readyMode = c.readyMode ?? null;
    const clockRoundTripMs=c.clockRoundTripMs??0;
    const windowActivationMs=c.windowActivationMs??0;
    if (!Number.isInteger(timingOffsetMs) || timingOffsetMs < -2000 || timingOffsetMs > 2000 ||
        !['initiator', 'joiner'].includes(musicRole) || (readyMode !== null && !MODES.has(readyMode)) ||
        !validNumber(clockRoundTripMs) || clockRoundTripMs<0 || clockRoundTripMs>30000 ||
        !Number.isInteger(windowActivationMs) || windowActivationMs<0 || windowActivationMs>5000) fail('BAD_CAPABILITIES');
    return { simulation: c.simulation, canStart: c.canStart, canStop: c.canStop, timingOffsetMs, musicRole, readyMode,clockRoundTripMs,windowActivationMs };
  }
  const actionMembers = (p, a) => a.kind === 'start' ? p.startParticipantIds : p.participantIds;
  const memberPrepared = (p, id) => (!p.manualJoinerIds.includes(id) || p.sessionAcks.has(id)) &&
    actions(p).filter(a => actionMembers(p, a).includes(id)).every(a => p.acks.get(a.id).has(id));
  function performanceView(p) {
    if (!p) return null;
    return { id: p.id, mode: p.mode, phase: p.phase, leaderId: p.leaderId, countdownMs: p.countdownMs,
      earliestStartServerMs: p.earliestStartServerMs, latestStartServerMs: p.latestStartServerMs, requestedCountdownMs:p.requestedCountdownMs,
      participantIds: p.participantIds, startParticipantIds: p.startParticipantIds, manualJoinerIds: p.manualJoinerIds,
      startAction: p.startAction, stopAction: p.stopAction,
      stopTimingSource: p.stopAction ? p.stopTimingSource??'user-duration' : null,
      stopRule:p.stopRule, cycleStopStatus:p.cycleStopStatus, cycleScore:p.cycleBinding,
      stopCommitted:!!p.stopAction && p.committed.has(p.stopAction.id),
      normalEndRule: p.mode === 'emote' ? 'none' : 'leader-first-cycle',
      firstCycleTimingVerified: false,
      stopTimingUnavailable: p.mode !== 'emote' && !p.stopAction ? 'playback-timing-unverified' : null,
      preparedMemberIds: p.participantIds.filter(id => memberPrepared(p, id)),
      receipts: [...p.receipts.values()], interruption: p.interruption,
      ensembleRoster: p.ensembleRoster,
      automaticGameEndVerified: false };
  }
  function view(room) {
    const layout = roster(room);
    const feedback = ensembleFeedback(room, layout, now());
    const observed = observedCharacters(room, now());
    return { code: room.code, hostId: room.hostId, leaderId: room.leaderId, mode:room.mode??'emote', observedCharacters: observed,
      members: [...room.members.values()].map(m => ({ id: m.id, nickname: m.nickname, role: m.role,
        ensembleGroup: ensembleGroup(m), ensembleRole: ensembleRole(m), ensembleOrder: layout.find(r => r.memberId === m.id).order,
        connected: !!m.ws, ready: m.ready, capabilities: m.capabilities, selected: m.selected !== false, instrument: m.instrument ?? '',
        gameIdentity: memberIdentity(m, room, now(), observed), music: memberMusic(m, now()), ensembleFeedback: feedback.get(m.id) })),
      performance: performanceView(room.performance) };
  }
  function broadcast(room, message) {
    const value = message ?? { type: 'state', room: view(room), serverEpoch, serverNowMs: now() };
    for (const member of room.members.values()) send(member.ws, value);
  }
  function participants(room, p, message, ids = p.participantIds) {
    for (const id of ids) send(room.members.get(id)?.ws, message);
  }
  function clearPerformanceTimers(p) { for (const timer of p.timers) clear(timer); p.timers.length = 0; }
  function abandonCycleStop(room,p,reason) {
    if (!p.stopAction || p.committed.has(p.stopAction.id)) return;
    participants(room,p,{type:'cancel-action',actionId:p.stopAction.id,sessionId:p.id,serverEpoch});
    (p.cancelledActions??=new Set()).add(p.stopAction.id);
    p.acks.delete(p.stopAction.id); p.stopAction=undefined; p.cycleStopStatus=reason;
    if (p.cycleTimer) { clear(p.cycleTimer); p.cycleTimer=null; }
  }
  function finishScheduledStop(room,p) {
    p.timers.push(later(()=>{
      if (room.performance!==p || TERMINAL.has(p.phase)) return;
      p.phase='stopped'; consumeMusicReadiness(room,p); broadcast(room);
    },p.stopAction.targetServerMs+p.delayMs-now()));
  }
  function commitCycleStop(room,p) {
    const stop=p.stopAction;
    if (!stop || p.committed.has(stop.id) || !p.participantIds.every(id=>p.acks.get(stop.id)?.has(id))) return;
    if (now()+50>=stop.targetServerMs+p.minimumOffsetMs) { abandonCycleStop(room,p,'too-late'); return; }
    clear(p.cycleTimer);p.cycleTimer=null;
    p.committed.add(stop.id);p.cycleStopStatus='scheduled';
    participants(room,p,{type:'commit-action',action:stop});finishScheduledStop(room,p);
  }
  function observeCycle(room,member) {
    const p=room.performance;
    if (!p || p.stopRule!=='first-cycle' || TERMINAL.has(p.phase) || member.id!==p.leaderId) return;
    const o=member.gameObservation,m=o?.music;
    if (p.cycleBinding) {
      const b=p.cycleBinding;
      const same=o?.generation===b.generation && o.identity.characterId===b.characterId && o.identity.realmId===b.realmId &&
        m?.concertId===b.concertId && m.scoreSha256===b.scoreSha256 && m.wholeScoreDurationSeconds===b.durationSeconds;
      if (!same && !p.cycleInterruptionReported && now()<b.estimatedEndServerMs) {
        p.cycleInterruptionReported=true;interruption(room,member,'game-observation');
      }
      return;
    }
    if (p.phase!=='committed' && p.phase!=='performing') return;
    if (!o || o.sessionId!==p.id || !m?.automaticStopAvailable) { p.cycleStopStatus='waiting-for-score';return; }
    if (p.musicBaseline.includes(o.identity.realmId+':'+m.concertId) || m.estimatedStartServerMs<p.earliestStartServerMs-250 || m.estimatedStartServerMs>now()+60000) return;
    const end=m.estimatedStartServerMs+m.wholeScoreDurationSeconds*1000;
    // The observed leader start already includes that member's manual offset.
    // Undo it once before the shared stop receives each member's frozen offset.
    const target=end-p.leaderOffsetMs;
    p.cycleBinding={generation:o.generation,realmId:o.identity.realmId,characterId:o.identity.characterId,concertId:m.concertId,
      scoreSha256:m.scoreSha256,title:m.title,durationSeconds:m.wholeScoreDurationSeconds,estimatedEndServerMs:end};
    if (target+p.minimumOffsetMs<=now()+50) {p.cycleStopStatus='too-late';return;}
    p.stopAction={id:randomUUID(),sessionId:p.id,kind:'stop',targetServerMs:target,serverEpoch};
    p.stopTimingSource='leader-capture-estimate';p.cycleStopStatus='preparing-stop';p.acks.set(p.stopAction.id,new Set());
    participants(room,p,{type:'prepare-action',action:p.stopAction,mode:p.mode});
    p.cycleTimer=later(()=>{abandonCycleStop(room,p,'prepare-timeout');broadcast(room);},target+p.minimumOffsetMs-now()-50);
    p.timers.push(p.cycleTimer);
  }
  function consumeMusicReadiness(room, p) {
    if (p.mode === 'emote') return;
    for (const id of p.participantIds) { const member = room.members.get(id); if (member) {member.ready = false;if(id===room.hostId)member.capabilities={...member.capabilities,canStart:false};} }
  }
  function retirePerformance(room) {
    const p=room.performance;
    if (!p) return;
    const history=room.retiredActions??=new Map();
    const sessions=room.retiredSessionAcks??=new Map();
    sessions.set(p.id,new Set(p.manualJoinerIds));
    while (sessions.size>64) sessions.delete(sessions.keys().next().value);
    for (const id of new Set([...actions(p).map(a=>a.id),...p.committed,...(p.cancelledActions??[])])) {
      const a=actions(p).find(a=>a.id===id);
      history.set(id,{members:new Set(a?actionMembers(p,a):p.participantIds),committed:p.committed.has(id)});
    }
    while (history.size>128) history.delete(history.keys().next().value);
  }
  function cancel(room, detail) {
    const p = room.performance;
    if (!p || TERMINAL.has(p.phase)) return;
    clearPerformanceTimers(p);
    p.phase = 'cancelled'; p.interruption = detail;
    consumeMusicReadiness(room, p);
    for (const action of actions(p)) participants(room, p, { type: 'cancel-action', actionId: action.id, sessionId: p.id, serverEpoch }, actionMembers(p, action));
    participants(room, p, { type: 'cancel-session', sessionId: p.id, serverEpoch }, p.manualJoinerIds);
  }
  function stopAll(room) {
    const p = room.performance;
    if (!p || TERMINAL.has(p.phase)) return;
    if (p.mode === 'emote') { cancel(room, { source: 'host-cancel' }); return; }
    // Explicit host stop goes out immediately, even if no one has acknowledged.
    const stopId = randomUUID();
    p.committed.add(stopId); // Immediate dispatch has a receipt identity too.
    participants(room, p, { type: 'stop-now', id: stopId, sessionId: p.id, serverEpoch });
    clearPerformanceTimers(p); p.phase = 'stopped';
    consumeMusicReadiness(room, p);
    for (const action of actions(p)) participants(room, p, { type: 'cancel-action', actionId: action.id, sessionId: p.id, serverEpoch }, actionMembers(p, action));
  }
  function interruption(room, member, source) {
    const p = room.performance;
    if (!p || TERMINAL.has(p.phase) || !p.participantIds.includes(member.id)) return;
    // A one-shot sender consumes its ready/chat draft after dispatch. Other members
    // may still be waiting for their positive local offset; do not cancel them.
    if (p.mode === 'emote' && p.receipts.get(p.startAction.id + ':' + member.id)?.status === 'executed') return;
    // Emotes are one-shot triggers: no music interruption/continue state or stop input applies.
    if (p.mode === 'emote' || p.phase === 'preparing') cancel(room, { memberId: member.id, source });
    else {
      if (p.stopRule==='first-cycle') abandonCycleStop(room,p,'interrupted-before-stop-commit');
      p.phase = 'interrupted'; p.interruption = { memberId: member.id, source };
      // Committed stop remains valid. Tool disconnect is not game stop.
    }
  }
  function closeRoom(room) {
    const p=room.performance;
    if(p && !TERMINAL.has(p.phase) && (p.phase==='preparing' || now()<p.earliestStartServerMs))cancel(room,{source:'host-cancel'});
    else stopAll(room);
    const closure={id:randomUUID(),host:room.members.get(room.hostId)?.ws,
      expected:new Set([...room.members.values()].map(m=>m.ws).filter(ws=>ws?.readyState===WebSocket.OPEN)),
      confirmed:new Set(),total:room.members.size,timer:null};
    closures.set(closure.id,closure);
    for (const member of room.members.values()) {
      send(member.ws, { type: 'room-closed', closureId:closure.id, serverEpoch });
      if (member.ws) { member.ws.room = null; member.ws.member = null; }
    }
    rooms.delete(room.code);
    closure.timer=later(()=>finishClosure(closure),2000);
    if(!closure.expected.size)finishClosure(closure);
  }
  function finishClosure(closure) {
    if(!closures.delete(closure.id))return;
    clear(closure.timer);
    send(closure.host,{type:'room-close-complete',closureId:closure.id,unconfirmedMembers:closure.total-closure.confirmed.size,serverEpoch});
  }
  function detach(ws, remove = false) {
    const { room, member } = ws;
    if (closing || !room || member.ws !== ws) return;
    if (remove && room.hostId === member.id) { closeRoom(room); return; }
    member.ws = null; member.ready = false; clearObservation(member);
    interruption(room, member, remove ? 'member-left' : 'tool-connection');
    if (remove) {
      room.members.delete(member.id);
      if (room.leaderId === member.id) room.leaderId = room.hostId;
    }
    ws.room = ws.member = null;
    room.touched = now(); broadcast(room);
  }
  function attach(ws, room, member) {
    const old = member.ws;
    member.ws = ws; member.ready = false; clearObservation(member); ws.room = room; ws.member = member;
    if (old && old !== ws) {
      interruption(room, member, 'tool-session-replaced');
      old.close(4001, 'Session resumed elsewhere');
    }
    room.touched = now();
    send(ws, { type: 'session', code: room.code, memberId: member.id, token: member.token, serverEpoch });
    // State is informational. Never replay commit-action when resuming.
    broadcast(room);
  }
  function newMember(name, role) {
    return { id: randomUUID(), nickname: nickname(name), token: randomBytes(24).toString('hex'),
      ensembleRole: role === 'host' ? 'initiator' : 'joiner',
      role, ws: null, ready: false, capabilities: { simulation: true, canStart: false, canStop: false } };
  }
  function prepare(room, msg) {
    if (room.performance && !TERMINAL.has(room.performance.phase)) fail('SESSION_ACTIVE');
    const ids = msg.participantIds;
    if (!MODES.has(msg.mode) || !Array.isArray(ids) || !ids.length || ids.length > MAX_MEMBERS || new Set(ids).size !== ids.length) fail('BAD_PARTICIPANTS');
    if (msg.mode !== 'emote' && !ids.includes(room.leaderId)) fail('LEADER_NOT_PARTICIPATING');
    const countdown = msg.countdownMs ?? 5000;
    if (!Number.isInteger(countdown) || countdown < 1000 || countdown > 60_000) fail('BAD_COUNTDOWN');
    const duration = msg.stopAfterMs;
    const stopRule=msg.stopRule??(duration===undefined?'manual':'duration');
    if (!['manual','duration','first-cycle'].includes(stopRule) || (stopRule==='first-cycle' && duration!==undefined)) fail('BAD_STOP_RULE');
    if (msg.mode==='emote' && stopRule==='first-cycle') fail('STOP_NOT_APPLICABLE');
    if (msg.mode === 'emote' && duration !== undefined) fail('STOP_NOT_APPLICABLE');
    if (duration !== undefined && (!Number.isInteger(duration) || duration < 1 || duration > 86_400_000)) fail('BAD_DURATION');
    const manualJoinerIds = msg.mode === 'ensemble' ? ids.filter(id => room.members.has(id) && ensembleRole(room.members.get(id)) === 'joiner') : [];
    const startParticipantIds = ids.filter(id => !manualJoinerIds.includes(id));
    if (!startParticipantIds.length) fail('NO_ENSEMBLE_INITIATOR');
    const missing = ids.filter(id => {
      const m = room.members.get(id);
      return !m?.ws || (id !== room.hostId && !m.ready) ||
        (m.capabilities.readyMode && m.capabilities.readyMode !== msg.mode) ||
        (startParticipantIds.includes(id) && !m.capabilities.canStart) || (msg.mode !== 'emote' && !m.capabilities.canStop);
    });
    if (missing.length) fail('MEMBER_NOT_READY', { memberIds: missing });
    const leads = startParticipantIds.map(id => room.members.get(id).capabilities.timingOffsetMs ?? 0);
    const advance = Math.max(0, ...leads.map(n => -n)), delay = Math.max(0, ...leads);
    // Leave a full preparation window before the earliest adjusted local input.
    // Large advances expand a short countdown instead of rejecting the round.
    const rtt=Math.max(0,...ids.map(id=>room.members.get(id).capabilities.clockRoundTripMs??0));
    const activation=Math.max(0,...ids.map(id=>room.members.get(id).capabilities.windowActivationMs??0));
    const preparationMs=Math.max(500,activation+Math.min(5000,Math.ceil(rtt*3+250)));
    const effectiveCountdown=Math.max(countdown,advance+preparationMs);
    const id = randomUUID(), target = now() + effectiveCountdown;
    const action = (kind, targetServerMs) => ({ id: randomUUID(), sessionId: id, kind, targetServerMs, serverEpoch });
    const p = { id, mode: msg.mode, phase: 'preparing', countdownMs: effectiveCountdown, requestedCountdownMs:countdown, leaderId: msg.mode === 'emote' ? null : room.leaderId,
      participantIds: [...ids], startParticipantIds, manualJoinerIds, sessionAcks: new Set(),
      startAction: action('start', target), earliestStartServerMs: target - advance, latestStartServerMs: target + delay,
      delayMs: Math.max(0, ...ids.map(id => room.members.get(id).capabilities.timingOffsetMs ?? 0)),
      stopAction: duration === undefined ? undefined : action('stop', target + duration),stopRule,
      cycleStopStatus:stopRule==='first-cycle'?'waiting-for-score':null,
      minimumOffsetMs:Math.min(0,...ids.map(id=>room.members.get(id).capabilities.timingOffsetMs??0)),
      leaderOffsetMs:room.members.get(room.leaderId)?.capabilities.timingOffsetMs??0,
      musicBaseline:[...new Set([...room.members.values()].filter(m=>m.gameObservation?.music).map(m=>m.gameObservation.identity.realmId+':'+m.gameObservation.music.concertId))],
      ensembleRoster: msg.mode === 'ensemble' ? roster(room).filter(m => ids.includes(m.memberId)) : [],
      observationBaseline: [...new Set([...room.members.values()].filter(m => m.gameObservation?.concert).map(m => {
        const o=m.gameObservation; return o.identity.realmId+':'+o.concert.id+':'+o.concert.hostCharacterId;
      }))],
      acks: new Map(), receipts: new Map(), committed: new Set(), timers: [], interruption: null };
    retirePerformance(room); room.performance = p;
    participants(room, p, { type: 'prepare-session', sessionId: p.id, serverEpoch, mode: p.mode }, manualJoinerIds);
    for (const a of actions(p)) {
      p.acks.set(a.id, new Set());
      participants(room, p, { type: 'prepare-action', action: a, mode: p.mode }, actionMembers(p, a));
    }
    p.timers.push(later(() => {
      if (p.phase === 'preparing') { cancel(room, { source: 'prepare-timeout', memberIds: p.participantIds.filter(id => !memberPrepared(p, id)) }); broadcast(room); }
    }, p.earliestStartServerMs - now() - 50));
  }
  function acknowledge(room, member, msg) {
    if (room.retiredActions?.get(msg.actionId)?.members.has(member.id)) return;
    const p = room.performance;
    const a = p && actions(p).find(a => a.id === msg.actionId);
    if (!a && p?.cancelledActions?.has(msg.actionId) && p.participantIds.includes(member.id)) return;
    if (!a || !actionMembers(p, a).includes(member.id)) fail('STALE_ACTION');
    if (p.phase === 'cancelled' && msg.ready === false) return; // Several members can reject the same prepare concurrently.
    if (TERMINAL.has(p.phase)) fail('STALE_ACTION');
    if (p.phase !== 'preparing') {
      if (p.stopRule!=='first-cycle' || a!==p.stopAction || p.committed.has(a.id)) return;
      if (typeof msg.ready!=='boolean') fail('BAD_MESSAGE');
      if (!msg.ready) {abandonCycleStop(room,p,'participant-not-ready');return;}
      p.acks.get(a.id).add(member.id);commitCycleStop(room,p);return;
    }
    if (typeof msg.ready !== 'boolean') fail('BAD_MESSAGE');
    if (!msg.ready) { cancel(room, { source: 'participant-not-ready', memberId: member.id, detail: String(msg.error ?? '').slice(0, 200) }); return; }
    p.acks.get(msg.actionId).add(member.id);
    commitWhenPrepared(room, p);
  }
  function acknowledgeSession(room, member, msg) {
    if (room.retiredSessionAcks?.get(msg.sessionId)?.has(member.id)) return;
    const p = room.performance;
    if (!p || p.id !== msg.sessionId || !p.manualJoinerIds.includes(member.id)) fail('STALE_SESSION');
    if (p.phase === 'cancelled' && msg.ready === false) return;
    if (TERMINAL.has(p.phase)) fail('STALE_SESSION');
    if (p.phase !== 'preparing') return;
    if (typeof msg.ready !== 'boolean') fail('BAD_MESSAGE');
    if (!msg.ready) { cancel(room, { source: 'participant-not-ready', memberId: member.id, detail: String(msg.error ?? '').slice(0, 200) }); return; }
    p.sessionAcks.add(member.id);
    commitWhenPrepared(room, p);
  }
  function commitWhenPrepared(room, p) {
    if (!p.participantIds.every(id => memberPrepared(p, id))) return;
    if (now() + 50 >= p.earliestStartServerMs) { cancel(room, { source: 'prepare-too-late' }); return; }
    clearPerformanceTimers(p); p.phase = 'committed';
    for (const action of actions(p)) {
      p.committed.add(action.id); participants(room, p, { type: 'commit-action', action }, actionMembers(p, action));
    }
    p.timers.push(later(() => { if (p.phase === 'committed') { p.phase = p.mode === 'emote' ? 'stopped' : 'performing'; broadcast(room); } }, p.latestStartServerMs - now()));
    // Service phase is a schedule state, not evidence of physical game success.
    if (p.stopAction) finishScheduledStop(room,p);
  }
  function handle(ws, msg, receivedAt) {
    if (!msg || typeof msg !== 'object' || Array.isArray(msg) || typeof msg.type !== 'string') fail('BAD_MESSAGE');
    if (msg.type === 'hello') {
      if (msg.product !== PRODUCT || msg.protocolVersion !== VERSION) fail('INCOMPATIBLE_CLIENT');
      ws.accepted = true; send(ws, { type: 'hello-accepted', ...identity }); return;
    }
    if (!ws.accepted) fail('INCOMPATIBLE_CLIENT');
    if(msg.type==='room-closed-ack') {
      const closure=closures.get(msg.closureId);
      if(closure?.expected.has(ws)) {
        closure.confirmed.add(ws);
        if(closure.confirmed.size===closure.expected.size)finishClosure(closure);
      }
      return;
    }
    if (msg.type === 'sync') {
      if (!Number.isSafeInteger(msg.id) || !validNumber(msg.t1)) fail('BAD_MESSAGE');
      send(ws, { type: 'sync', id: msg.id, t1: msg.t1, t2: receivedAt, t3: now(), serverEpoch }); return;
    }
    if (['create', 'join', 'resume'].includes(msg.type)) {
      if (ws.room) fail('ALREADY_IN_ROOM');
      if (msg.type === 'create') {
        if (rooms.size >= 1000) fail('SERVER_BUSY');
        const member = newMember(msg.nickname, 'host');
        const alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
        let code;
        do { code = Array.from({ length: 6 }, () => alphabet[randomInt(alphabet.length)]).join(''); } while (rooms.has(code));
        const room = { code, hostId: member.id, leaderId: member.id, members: new Map([[member.id, member]]), performance: null, touched: now() };
        rooms.set(code, room); attach(ws, room, member);
      } else {
        const code = typeof msg.code === 'string' ? msg.code.trim().toUpperCase() : '';
        const room = rooms.get(code);
        if (!room) fail('ROOM_NOT_FOUND');
        if (msg.type === 'resume') {
          const member = [...room.members.values()].find(m => sameToken(msg.token, m.token));
          if (!member) fail('SESSION_EXPIRED');
          attach(ws, room, member);
        } else {
          if (room.members.size >= MAX_MEMBERS) fail('ROOM_FULL');
          const member = newMember(msg.nickname, 'member'); room.members.set(member.id, member); attach(ws, room, member);
        }
      }
      return;
    }
    const { room, member } = ws;
    if (!room || member.ws !== ws) fail('NOT_IN_ROOM');
    const hostOnly = ['select-leader', 'prepare', 'cancel', 'stop-all', 'decide', 'close-room', 'score-timeline',
      'move-ensemble-member', 'set-ensemble-role', 'edit-ensemble-member', 'set-member-selection', 'set-room-mode', 'kick-member', 'bind-game-character'];
    if (hostOnly.includes(msg.type) && room.hostId !== member.id) fail('FORBIDDEN');
    switch (msg.type) {
      case 'set-room-mode':
        if(room.performance && !TERMINAL.has(room.performance.phase))fail('SESSION_ACTIVE');
        if(!['emote','ensemble'].includes(msg.mode))fail('BAD_MESSAGE');
        if(room.mode!==msg.mode){room.mode=msg.mode;for(const m of room.members.values())m.ready=false;}
        break;
      case 'game-observation': acceptObservation(member, msg, now()); observeCycle(room,member); break;
      case 'bind-game-character': bindGameCharacter(room, msg.memberId, msg.characterId, msg.realmId, now()); break;
      case 'select-leader':
        if (room.performance && !TERMINAL.has(room.performance.phase)) fail('SESSION_ACTIVE');
        if (!room.members.has(msg.memberId)) fail('MEMBER_NOT_FOUND');
        room.leaderId = msg.memberId; break;
      case 'set-ready': {
        if (typeof msg.ready !== 'boolean') fail('BAD_CAPABILITIES');
        member.capabilities = capabilities(msg.capabilities);
        member.ready = msg.ready;
        if (!msg.ready) interruption(room, member, 'not-ready');
        break;
      }
      case 'configure-input':
        if (room.performance && !TERMINAL.has(room.performance.phase)) fail('SESSION_ACTIVE');
        member.capabilities = capabilities(msg.capabilities); member.ready = false; break;
      case 'move-ensemble-member': case 'set-ensemble-role': editEnsemble(room, msg); break;
      case 'edit-ensemble-member': case 'set-member-selection': editMember(room,msg); break;
      case 'kick-member': {
        if (msg.memberId === room.hostId) fail('CANNOT_KICK_HOST');
        const kicked = room.members.get(msg.memberId);
        if (!kicked) fail('MEMBER_NOT_FOUND');
        interruption(room, kicked, 'member-kicked');
        if (kicked.ws) { send(kicked.ws, { type: 'kicked', serverEpoch }); kicked.ws.room = kicked.ws.member = null; }
        room.members.delete(kicked.id);
        if (room.leaderId === kicked.id) room.leaderId = room.hostId;
        break;
      }
      case 'prepare': prepare(room, msg); break;
      case 'action-ready': acknowledge(room, member, msg); break;
      case 'session-ready': acknowledgeSession(room, member, msg); break;
      case 'receipt': {
        if (!['executed', 'cancelled', 'missed', 'failed'].includes(msg.status) || (msg.actualServerMs !== undefined && !validNumber(msg.actualServerMs))) fail('BAD_RECEIPT');
        const retired=room.retiredActions?.get(msg.actionId);
        if (retired?.members.has(member.id) && (retired.committed || msg.status==='cancelled')) break;
        const p = room.performance;
        const a = p && actions(p).find(a => a.id === msg.actionId);
        if (!p || !p.participantIds.includes(member.id) ||
            (a && !actionMembers(p, a).includes(member.id)) ||
            (!p.committed.has(msg.actionId) && !(msg.status === 'cancelled' && (actions(p).some(a => a.id === msg.actionId) || p.cancelledActions?.has(msg.actionId))))) fail('STALE_ACTION');
        const key = msg.actionId + ':' + member.id;
        if (!p.receipts.has(key)) {
          p.receipts.set(key, { actionId: msg.actionId, memberId: member.id,
            status: msg.status, actualServerMs: msg.actualServerMs, detail: String(msg.detail ?? '').slice(0, 200),
            simulated: member.capabilities.simulation || msg.simulated !== false, gameResponseVerified: false });
          if (p.mode !== 'emote' && ['failed', 'missed'].includes(msg.status) && !TERMINAL.has(p.phase)) {
            interruption(room, member, 'input-failure');
            if (p.interruption) p.interruption.detail = String(msg.detail ?? '').slice(0, 200);
          }
        }
        break;
      }
      case 'cancel':
        if (room.performance && !TERMINAL.has(room.performance.phase) && now() >= room.performance.latestStartServerMs) fail('USE_STOP_ALL');
        cancel(room, { source: 'host-cancel' }); break;
      case 'stop-all': stopAll(room); break;
      case 'local-stop': member.ready = false; interruption(room, member, 'local-stop'); break;
      case 'leader-status': {
        const p = room.performance;
        if (!p || p.id !== msg.sessionId || member.id !== p.leaderId || TERMINAL.has(p.phase)) fail('STALE_SESSION');
        if (p.mode === 'emote') fail('BAD_MESSAGE');
        if (msg.status === 'natural-end') fail('AUTOMATIC_END_UNVERIFIED');
        if (!['interrupted', 'disconnected'].includes(msg.status)) fail('BAD_MESSAGE');
        interruption(room, member, 'game-observation');
        if (p.interruption) p.interruption.evidenceSource = msg.evidenceSource === 'player-report' ? 'player-report' : 'unverified-report';
        Object.assign(p.interruption, { status: msg.status, detail: String(msg.detail ?? '').slice(0, 200) });
        break;
      }
      case 'decide': {
        const p = room.performance;
        if (!p || p.phase !== 'interrupted') fail('NO_INTERRUPTION');
        if (msg.decision === 'stop') stopAll(room);
        else if (msg.decision === 'continue') { p.phase = now() >= p.startAction.targetServerMs ? 'performing' : 'committed'; p.interruption = null; }
        else fail('BAD_MESSAGE');
        break;
      }
      case 'score-timeline': fail('AUTOMATIC_END_UNVERIFIED'); break;
      case 'leave': detach(ws, true); send(ws, { type: 'left', serverEpoch }); return;
      case 'close-room': closeRoom(room); return;
      default: fail('BAD_MESSAGE');
    }
    room.touched = now(); broadcast(room);
    if (typeof msg.requestId === 'string' && msg.requestId.length <= 64)
      send(ws,{type:'request-complete',requestId:msg.requestId,serverEpoch});
  }
  wss.on('connection', ws => {
    ws.alive = true; ws.accepted = false; ws.rateStart = now(); ws.rateCount = 0;
    ws.on('error', () => {});
    ws.on('pong', () => { ws.alive = true; });
    send(ws, { type: 'hello', ...identity });
    ws.on('message', bytes => {
      const receivedAt = now();
      let msg;
      try {
        try { msg = JSON.parse(bytes.toString('utf-8')); } catch { fail('BAD_MESSAGE'); }
        if (receivedAt - ws.rateStart > 10_000) { ws.rateStart = receivedAt; ws.rateCount = 0; }
        if (++ws.rateCount > 180 && !['stop-all', 'local-stop'].includes(msg?.type)) fail('RATE_LIMIT');
        handle(ws, msg, receivedAt);
      } catch (error) {
        send(ws, { type: 'error', code: error.code ?? 'BAD_MESSAGE', message: error.code ?? 'BAD_MESSAGE',
          ...(typeof msg?.requestId==='string' && msg.requestId.length<=64 ? {requestId:msg.requestId}:{}), ...error.details });
      }
    });
    ws.on('close', () => detach(ws));
  });
  const heartbeat = setInterval(() => {
    for (const ws of wss.clients) { if (!ws.alive) ws.terminate(); else { ws.alive = false; ws.ping(); } }
    for (const room of rooms.values()) if (![...room.members.values()].some(m => m.ws) && now() - room.touched > 1_800_000) closeRoom(room);
  }, 15_000);
  const observationExpiry = setInterval(() => {
    for (const room of rooms.values()) if (expireObservations(room, now())) {
      const leader=room.members.get(room.performance?.leaderId);
      if (leader) observeCycle(room,leader);
      broadcast(room);
    }
  }, 1000);
  observationExpiry.unref();
  heartbeat.unref();
  try { await new Promise((resolve, reject) => { server.once('error', reject); server.listen(port, host, resolve); }); }
  catch (error) { clearInterval(heartbeat); clearInterval(observationExpiry); wss.close(); throw error; }
  const address = server.address();
  return { url: `ws://${host === '0.0.0.0' ? '127.0.0.1' : host}:${address.port}`, port: address.port, serverEpoch,
    async close() {
      closing = true; clearInterval(heartbeat); clearInterval(observationExpiry); for (const timer of timers) clearTimeout(timer); timers.clear();
      closures.clear();
      for (const ws of wss.clients) ws.terminate();
      await new Promise(resolve => wss.close(resolve));
      await new Promise(resolve => server.close(resolve));
    }
  };
}
