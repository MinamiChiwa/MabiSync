import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { WebSocket } from 'ws';

test('a departing conductor falls back to the host and does not block the next music round',async t=>{
  const {client}=await setup(t);const host=await client(),member=await client();
  await host.create();await member.join(host.identity.code);await host.ready();
  host.send({type:'select-leader',memberId:member.identity.memberId});
  await host.wait(m=>m.type==='state' && m.room.leaderId===member.identity.memberId);
  while(host.pending('state').length)await host.next('state');
  member.send({type:'leave'});await member.next('left');
  const state=await host.wait(m=>m.type==='state' && m.room.members.length===1);
  assert.equal(state.room.leaderId,host.identity.memberId);
  host.send({type:'prepare',mode:'ensemble',participantIds:[host.identity.memberId]});
  const next=await host.wait(m=>m.type==='prepare-action'||m.type==='error');
  assert.equal(next.type,'prepare-action');
});

test('host directly edits group, order, role, instrument and selection, broadcasting one authoritative roster', async t => {
  const {client}=await setup(t); const host=await client(),a=await client(),b=await client();
  await host.create();await a.join(host.identity.code);await b.join(host.identity.code);
  host.send({type:'edit-ensemble-member',memberId:a.identity.memberId,group:2,order:1,instrument:'自定义竖琴'});
  for (const peer of [host,a,b]) {
    const state=await peer.wait(m=>m.type==='state' && m.room.members.find(x=>x.id===a.identity.memberId)?.instrument==='自定义竖琴');
    const row=state.room.members.find(x=>x.id===a.identity.memberId);assert.equal(row.ensembleGroup,2);assert.equal(row.ensembleOrder,1);
  }
  host.send({type:'edit-ensemble-member',memberId:b.identity.memberId,group:2,order:1,musicRole:'initiator',instrument:'钢琴'});
  const final=await a.wait(m=>m.type==='state' && m.room.members.find(x=>x.id===b.identity.memberId)?.instrument==='钢琴');
  assert.equal(final.room.members.find(x=>x.id===b.identity.memberId).ensembleOrder,1);
  assert.equal(final.room.members.find(x=>x.id===a.identity.memberId).ensembleOrder,2);
  host.send({type:'set-member-selection',memberId:a.identity.memberId,selected:false});
  await b.wait(m=>m.type==='state' && m.room.members.find(x=>x.id===a.identity.memberId)?.selected===false);
});

test('only host can edit roster metadata; invalid edits are atomic', async t=>{
  const {client}=await setup(t);const host=await client(),a=await client();await host.create();await a.join(host.identity.code);
  a.send({type:'edit-ensemble-member',memberId:a.identity.memberId,instrument:'长笛'});
  assert.equal((await a.next('error')).code,'FORBIDDEN');
  a.send({type:'set-member-selection',memberId:host.identity.memberId,selected:false});
  assert.equal((await a.next('error')).code,'FORBIDDEN');
  host.send({type:'edit-ensemble-member',memberId:a.identity.memberId,group:2,instrument:'x'.repeat(41)});
  assert.equal((await host.next('error')).code,'BAD_INSTRUMENT');
  host.send({type:'edit-ensemble-member',memberId:a.identity.memberId,order:0});
  assert.equal((await host.next('error')).code,'BAD_ENSEMBLE_ORDER');
  host.send({type:'select-leader',memberId:a.identity.memberId});
  const unchanged=await host.wait(m=>m.type==='state' && m.room.leaderId===a.identity.memberId);
  assert.equal(unchanged.room.members.find(x=>x.id===a.identity.memberId).ensembleGroup,1);
});

test('host starts emotes without explicit ready after configuring its local input',async t=>{
  const {client}=await setup(t);const host=await client();await host.create();
  host.send({type:'configure-input',capabilities:{simulation:true,canStart:true,canStop:false,readyMode:'emote'}});
  await host.wait(m=>m.type==='state' && m.room.members[0].capabilities.canStart);
  host.send({type:'prepare',mode:'emote',participantIds:[host.identity.memberId]});
  const next=await host.wait(m=>m.type==='prepare-action'||m.type==='error');assert.equal(next.type,'prepare-action');
});

test('closing room ejects every peer and revokes disconnected resume tokens',async t=>{
  const {client}=await setup(t);const host=await client(),a=await client(),b=await client();
  await host.create();await a.join(host.identity.code);await b.join(host.identity.code);
  const code=host.identity.code,token=b.identity.token;b.ws.close();
  await host.wait(m=>m.type==='state' && m.room.members.some(x=>x.id===b.identity.memberId && !x.connected));
  host.send({type:'close-room'});await host.next('room-closed');await a.next('room-closed');
  a.send({type:'set-ready',ready:true,capabilities:{simulation:true,canStart:true,canStop:true}});assert.equal((await a.next('error')).code,'NOT_IN_ROOM');
  const rejoin=await client();rejoin.send({type:'resume',code,token});assert.equal((await rejoin.next('error')).code,'ROOM_NOT_FOUND');
});
test('host mode choice reaches every member and invalidates readiness for the previous mode',async t=>{
  const {client}=await setup(t);const host=await client(),a=await client();await host.create();await a.join(host.identity.code);await a.ready();
  a.send({type:'set-room-mode',mode:'ensemble'});assert.equal((await a.next('error')).code,'FORBIDDEN');
  host.send({type:'set-room-mode',mode:'ensemble'});
  for(const peer of [host,a]){const state=await peer.wait(m=>m.type==='state' && m.room.mode==='ensemble');assert.equal(state.room.members.find(x=>x.id===a.identity.memberId).ready,false);}
});
test('closing a room before the first start cancels inputs without clicking a nonexistent stop button',async t=>{
  const {client}=await setup(t);const host=await client();await host.create();await host.ready();
  host.send({type:'prepare',mode:'ensemble',participantIds:[host.identity.memberId]});await host.next('prepare-action');
  host.send({type:'close-room'});await host.next('room-closed');assert.equal(host.pending('stop-now').length,0);
});
test('host close completion waits until every online peer has acknowledged leaving',async t=>{
  const {client}=await setup(t);const host=await client(),a=await client();await host.create();await a.join(host.identity.code);
  host.send({type:'close-room'});const h=await host.next('room-closed'),g=await a.next('room-closed');
  assert.equal(typeof h.closureId,'string');assert.equal(g.closureId,h.closureId);
  host.send({type:'room-closed-ack',closureId:h.closureId});host.send({type:'sync',id:333,t1:1});await host.next('sync');
  assert.equal(host.pending('room-close-complete').length,0);
  a.send({type:'room-closed-ack',closureId:g.closureId});
  const completed=await host.next('room-close-complete');assert.equal(completed.unconfirmedMembers,0);
});
test('close reports unconfirmed offline members while preventing old room recovery',async t=>{
  const {client}=await setup(t);const host=await client(),a=await client();await host.create();await a.join(host.identity.code);
  const code=host.identity.code;a.ws.close();await host.wait(m=>m.type==='state' && m.room.members.some(x=>!x.connected));
  host.send({type:'close-room'});const closed=await host.next('room-closed');host.send({type:'room-closed-ack',closureId:closed.closureId});
  assert.equal((await host.next('room-close-complete')).unconfirmedMembers,1);
  const retry=await client();retry.send({type:'join',nickname:'Retry',code});assert.equal((await retry.next('error')).code,'ROOM_NOT_FOUND');
});

test('live observation messages update only the sender and clear feedback on disconnect', async t => {
  const { client } = await setup(t); const host=await client(), member=await client();
  await host.create(); await member.join(host.identity.code); await host.ready(); await member.ready();
  const report=(peer, characterId, nickname, sequence, concert) => peer.send({type:'game-observation',generation:'capture-'+characterId,sequence,status:'identified',identity:{characterId,realmId:3,nickname},concert});
  report(host,'101','自动本机',1); report(member,'102','自动成员',1);
  await host.wait(m=>m.type==='state' && m.room.members.find(p=>p.id===member.identity.memberId)?.gameIdentity?.characterId==='102');
  report(host,'101','自动本机',2,{id:'7',hostCharacterId:'101',orderedCharacterIds:['101','102']});
  const good=await host.wait(m=>m.type==='state' && m.room.members.every(p=>p.ensembleFeedback.state==='correct'));
  assert.ok(good.room.members.every(m=>m.ready));
  assert.equal(good.room.members.find(m=>m.id===member.identity.memberId).nickname,'自动成员');
  report(host,'101','错误重播昵称',1);
  assert.equal((await host.next('error')).code,'STALE_OBSERVATION');
  member.ws.close();
  const disconnected=await host.wait(m=>m.type==='state' && m.room.members.some(p=>!p.connected));
  assert.ok(disconnected.room.members.every(p=>p.ensembleFeedback.state==='unconfirmed'));
  assert.equal(disconnected.room.members.find(p=>p.id===member.identity.memberId).gameIdentity,null);
});

test('room distributes sanitized score summaries without accepting automatic timing or score content', async t => {
  const {client}=await setup(t); const host=await client(), member=await client();
  await host.create(); await member.join(host.identity.code);
  member.send({type:'game-observation',generation:'music',sequence:1,status:'identified',identity:{characterId:'101',realmId:3,nickname:'主奏'},
    music:{concertId:'7',performerCharacterId:'101',hostCharacterId:'101',title:'生日快乐',isLoop:true,selectedChannels:[0,1],scoreSha256:'a'.repeat(64),
      wholeScoreDurationSeconds:33.6,selectedDurationSeconds:33.6,issue:'实际起点待核对',automaticStopAvailable:true,gameStartLocalMs:100,mml:'PRIVATE'}});
  const result=await host.wait(m=>m.type==='state' && m.room.members.some(p=>p.music?.title==='生日快乐'));
  const score=result.room.members.find(p=>p.id===member.identity.memberId).music;
  assert.equal(score.automaticStopAvailable,false); assert.equal(score.gameStartLocalMs,null); assert.equal(score.mml,undefined);
  assert.equal(result.room.performance,null);
  member.send({type:'game-observation',generation:'music',sequence:2,status:'unavailable'});
  await host.wait(m=>m.type==='state' && m.room.members.every(p=>p.music===null));
});
test('observation capabilities are advertised and an initiator stays first in its own group', async t => {
  const {client}=await setup(t); const host=await client(),member=await client();
  assert.equal(host.hello.features.gameObservation,1);
  await host.create();await member.join(host.identity.code);
  host.send({type:'set-ensemble-role',memberId:member.identity.memberId,musicRole:'initiator'});
  const state=await host.wait(m=>m.type==='state' && m.room.members.find(p=>p.id===member.identity.memberId)?.ensembleRole==='initiator');
  assert.equal(state.room.members.find(p=>p.id===member.identity.memberId).ensembleOrder,1);
  assert.equal(state.room.members.find(p=>p.id===host.identity.memberId).ensembleOrder,2);
});

test('host edits ensemble groups and join order while emote readiness is preserved', async t => {
  const { client } = await setup(t); const host = await client(), a = await client(), b = await client();
  await host.create(); await a.join(host.identity.code); await b.join(host.identity.code);
  a.send({ type: 'set-ready', ready: true, capabilities: { simulation: true, canStart: true, canStop: false, readyMode: 'emote' } });
  await host.wait(m => m.type === 'state' && m.room.members.some(p => p.id === a.identity.memberId && p.ready));
  b.send({ type: 'move-ensemble-member', memberId: a.identity.memberId, group: 2 });
  assert.equal((await b.next('error')).code, 'FORBIDDEN');
  host.send({ type: 'move-ensemble-member', memberId: a.identity.memberId, group: 2 });
  const moved = await host.wait(m => m.type === 'state' && m.room.members.find(p => p.id === a.identity.memberId)?.ensembleGroup === 2);
  assert.equal(moved.room.members.find(p => p.id === a.identity.memberId).ready, true);
  host.send({ type: 'move-ensemble-member', memberId: b.identity.memberId, targetMemberId: a.identity.memberId, placement: 'before' });
  const ordered = await host.wait(m => m.type === 'state' && m.room.members.find(p => p.id === b.identity.memberId)?.ensembleGroup === 2);
  assert.equal(ordered.room.members.find(p => p.id === b.identity.memberId).ensembleOrder, 1);
  assert.equal(ordered.room.members.find(p => p.id === a.identity.memberId).ensembleOrder, 2);
  host.send({ type: 'set-ensemble-role', memberId: b.identity.memberId, musicRole: 'initiator' });
  const role = await host.wait(m => m.type === 'state' && m.room.members.find(p => p.id === b.identity.memberId)?.ensembleRole === 'initiator' && m.room.members.find(p => p.id === b.identity.memberId)?.ensembleGroup === 2 && m.room.members.find(p => p.id === a.identity.memberId)?.ensembleOrder === 2);
  assert.equal(role.room.members.find(p => p.id === a.identity.memberId).ready, true);
  host.send({ type: 'move-ensemble-member', memberId: a.identity.memberId, group: 0 });
  assert.equal((await host.next('error')).code, 'BAD_ENSEMBLE_GROUP');
});

test('host skips ready button but still needs input configured for the chosen mode and acknowledges preparation', async t => {
  const { client } = await setup(t); const host = await client(); await host.create();
  host.send({ type: 'configure-input', capabilities: { simulation: true, canStart: true, canStop: true, readyMode: 'ensemble' } });
  const configured = await host.wait(m => m.type === 'state' && m.room.members[0].capabilities.canStart);
  assert.equal(configured.room.members[0].ready, false);
  host.send({ type: 'prepare', mode: 'emote', participantIds: [host.identity.memberId] });
  assert.equal((await host.next('error')).code, 'MEMBER_NOT_READY');
  host.send({ type: 'prepare', mode: 'ensemble', participantIds: [host.identity.memberId] });
  const first = await host.wait(m => m.type === 'prepare-action' || m.type === 'error'); assert.equal(first.type, 'prepare-action');
  assert.equal(host.pending('commit-action').length, 0);
  host.send({ type: 'action-ready', actionId: first.action.id, ready: false, error: 'No start button' });
  await host.wait(m => m.type === 'state' && m.room.performance?.phase === 'cancelled');
});

test('host can kick another identity and its old resume token expires', async t => {
  const { client } = await setup(t); const host = await client(), member = await client();
  await host.create(); await member.join(host.identity.code);
  member.send({ type: 'kick-member', memberId: host.identity.memberId });
  assert.equal((await member.next('error')).code, 'FORBIDDEN');
  host.send({ type: 'kick-member', memberId: host.identity.memberId });
  assert.equal((await host.next('error')).code, 'CANNOT_KICK_HOST');
  host.send({ type: 'kick-member', memberId: member.identity.memberId });
  await member.next('kicked');
  await host.wait(m => m.type === 'state' && m.room.members.length === 1);
  const reconnect = await client(); reconnect.send({ type: 'resume', code: host.identity.code, token: member.identity.token });
  assert.equal((await reconnect.next('error')).code, 'SESSION_EXPIRED');
});

test('ensemble joiners prepare only their stop path and never receive a start', async t => {
  const { client } = await setup(t); const host = await client(), joiner = await client();
  await host.create(); await joiner.join(host.identity.code); await host.ready();
  joiner.send({ type: 'set-ready', ready: true, capabilities: { simulation: true, canStart: false, canStop: true, musicRole: 'joiner' } });
  await host.wait(m => m.type === 'state' && m.room.members.some(p => p.id === joiner.identity.memberId && p.ready));
  host.send({ type: 'prepare', mode: 'ensemble', participantIds: [host.identity.memberId, joiner.identity.memberId] });
  const first = await host.wait(m => m.type === 'prepare-action' || m.type === 'error');
  assert.equal(first.type, 'prepare-action', 'A manual joiner must not need start capability');
  const context = await joiner.next('prepare-session');
  host.send({ type: 'action-ready', actionId: first.action.id, ready: true });
  host.send({ type: 'sync', id: 200, t1: 1 }); await host.next('sync');
  assert.equal(host.pending('commit-action').length, 0, 'Joiner stop validation belongs in the barrier');
  joiner.send({ type: 'session-ready', sessionId: context.sessionId, ready: true });
  await host.next('commit-action');
  joiner.send({ type: 'sync', id: 201, t1: 1 }); await joiner.next('sync');
  assert.equal(joiner.pending('prepare-action').length + joiner.pending('commit-action').length, 0);
  joiner.send({ type: 'receipt', actionId: first.action.id, status: 'executed' });
  assert.equal((await joiner.next('error')).code, 'STALE_ACTION');
  host.send({ type: 'stop-all' });
  const stop = await joiner.next('stop-now');
  assert.equal(stop.sessionId, context.sessionId);
});

test('timed ensemble sends its stop to both roles and rejects a failed joiner preparation', async t => {
  const { client } = await setup(t); const host = await client(), joiner = await client();
  await host.create(); await joiner.join(host.identity.code); await host.ready();
  joiner.send({ type: 'set-ready', ready: true, capabilities: { simulation: true, canStart: false, canStop: true, musicRole: 'joiner' } });
  await host.wait(m => m.type === 'state' && m.room.members.some(p => p.id === joiner.identity.memberId && p.ready));
  host.send({ type: 'prepare', mode: 'ensemble', participantIds: [host.identity.memberId, joiner.identity.memberId], stopAfterMs: 8000 });
  const first = await host.wait(m => m.type === 'prepare-action' || m.type === 'error'); assert.equal(first.type, 'prepare-action');
  const hostStop = await host.next('prepare-action'), joinerStop = await joiner.next('prepare-action');
  assert.equal(joinerStop.action.kind, 'stop'); assert.equal(joinerStop.action.id, hostStop.action.id);
  const context = await joiner.next('prepare-session');
  joiner.send({ type: 'session-ready', sessionId: context.sessionId, ready: false, error: 'Stop button not calibrated' });
  await host.wait(m => m.type === 'state' && m.room.performance?.phase === 'cancelled');
  assert.equal((await joiner.next('cancel-session')).sessionId, context.sessionId);
  assert.equal(host.pending('commit-action').length + joiner.pending('commit-action').length, 0);
});

test('ensemble needs an initiator and every music member needs a stop binding', async t => {
  const { client } = await setup(t); const host = await client(); await host.create();
  host.send({ type: 'set-ensemble-role', memberId: host.identity.memberId, musicRole: 'joiner' });
  await host.wait(m => m.type === 'state' && m.room.members[0].ensembleRole === 'joiner');
  host.send({ type: 'set-ready', ready: true, capabilities: { simulation: true, canStart: false, canStop: true, musicRole: 'joiner' } });
  await host.wait(m => m.type === 'state' && m.room.members[0].ready);
  host.send({ type: 'prepare', mode: 'ensemble', participantIds: [host.identity.memberId] });
  assert.equal((await host.next('error')).code, 'NO_ENSEMBLE_INITIATOR');
  host.send({ type: 'set-ensemble-role', memberId: host.identity.memberId, musicRole: 'initiator' });
  host.send({ type: 'set-ready', ready: true, capabilities: { simulation: true, canStart: true, canStop: false, musicRole: 'initiator' } });
  await host.wait(m => m.type === 'state' && m.room.members[0].capabilities.canStart);
  host.send({ type: 'prepare', mode: 'independent', participantIds: [host.identity.memberId] });
  assert.equal((await host.next('error')).code, 'MEMBER_NOT_READY');
});

test('failed music input alerts the host without silently stopping or restarting others', async t => {
  const { client } = await setup(t); const host = await client(), member = await client();
  await host.create(); await member.join(host.identity.code); await host.ready(); await member.ready();
  host.send({ type: 'prepare', mode: 'independent', participantIds: [host.identity.memberId, member.identity.memberId] });
  const { action } = await host.next('prepare-action'); await member.next('prepare-action');
  host.send({ type: 'action-ready', actionId: action.id, ready: true }); member.send({ type: 'action-ready', actionId: action.id, ready: true });
  await host.next('commit-action'); await member.next('commit-action');
  member.send({ type: 'receipt', actionId: action.id, status: 'failed', detail: 'Game window lost focus' });
  const state = await host.wait(m => m.type === 'state' && m.room.performance?.receipts.length === 1);
  assert.equal(state.room.performance.phase, 'interrupted');
  assert.equal(state.room.performance.interruption.source, 'input-failure');
  assert.equal(state.room.performance.interruption.memberId, member.identity.memberId);
  assert.equal(host.pending('stop-now').length, 0); assert.equal(member.pending('stop-now').length, 0);
});

test('music completion consumes participant readiness before another start can be prepared', async t => {
  const { client } = await setup(t); const host = await client(); await host.create(); await host.ready();
  host.send({ type: 'prepare', mode: 'independent', participantIds: [host.identity.memberId] });
  const { action } = await host.next('prepare-action');
  host.send({ type: 'action-ready', actionId: action.id, ready: true }); await host.next('commit-action');
  host.send({ type: 'stop-all' }); await host.next('stop-now');
  const ended = await host.wait(m => m.type === 'state' && m.room.performance?.phase === 'stopped');
  assert.equal(ended.room.members[0].ready, false);
  host.send({ type: 'prepare', mode: 'independent', participantIds: [host.identity.memberId] });
  assert.equal((await host.next('error')).code, 'MEMBER_NOT_READY');
});

test('manual lead is bounded and the shared round waits for the latest local trigger', async t => {
  const { client } = await setup(t); const host = await client(); await host.create();
  const caps = { simulation: true, canStart: true, canStop: false };
  host.send({ type: 'set-ready', ready: true, capabilities: { ...caps, timingOffsetMs: -2001 } });
  assert.equal((await host.next('error')).code, 'BAD_CAPABILITIES');
  host.send({ type: 'set-ready', ready: true, capabilities: { ...caps, timingOffsetMs: -1500 } });
  await host.wait(m => m.type === 'state' && m.room.members[0].ready);
  host.send({ type: 'prepare', mode: 'emote', countdownMs: 1000, participantIds: [host.identity.memberId] });
  assert.equal((await host.next('error')).code, 'COUNTDOWN_BEFORE_LOCAL_TRIGGER');
  host.send({ type: 'set-ready', ready: true, capabilities: { ...caps, timingOffsetMs: -200 } });
  await host.wait(m => m.type === 'state' && m.room.members[0].capabilities.timingOffsetMs === -200);
  const guest = await client(); await guest.join(host.identity.code);
  guest.send({ type: 'set-ready', ready: true, capabilities: { ...caps, timingOffsetMs: 500 } });
  await host.wait(m => m.type === 'state' && m.room.members.some(p => p.id === guest.identity.memberId && p.ready));
  host.send({ type: 'prepare', mode: 'emote', countdownMs: 1000, participantIds: [host.identity.memberId, guest.identity.memberId] });
  const pending = await host.next('prepare-action');
  await guest.next('prepare-action'); guest.send({ type: 'action-ready', actionId: pending.action.id, ready: true });
  host.send({ type: 'action-ready', actionId: pending.action.id, ready: true });
  await host.next('commit-action');
  const state = await host.wait(m => m.type === 'state' && m.room.performance?.phase === 'committed');
  assert.equal(state.room.performance.latestStartServerMs, pending.action.targetServerMs + 500);
  host.send({ type: 'receipt', actionId: pending.action.id, status: 'executed', simulated: true });
  host.send({ type: 'set-ready', ready: false, capabilities: { ...caps, timingOffsetMs: -200 } });
  const consumed = await host.wait(m => m.type === 'state' && m.room.performance?.id === pending.action.sessionId && !m.room.members.find(p => p.id === host.identity.memberId).ready);
  assert.equal(consumed.room.performance.phase, 'committed', 'Earlier participant consuming its chat draft must not cancel a later participant');
  await new Promise(resolve => setTimeout(resolve, 1100));
  host.send({ type: 'prepare', mode: 'emote', participantIds: [host.identity.memberId] });
  assert.equal((await host.next('error')).code, 'SESSION_ACTIVE');
  await host.wait(m => m.type === 'state' && m.room.performance?.phase === 'stopped');
});

async function setup(t) {
  assert.ok(existsSync(new URL('../server/room-server.js', import.meta.url)), 'MabiSync room service is missing');
  const { createRoomServer } = await import('../server/room-server.js');
  const server = await createRoomServer({ port: 0 });
  const clients = [];
  t.after(async () => { for (const client of clients) client.ws.terminate(); await server.close(); });
  async function client(handshake = true) {
    const ws = new WebSocket(server.url);
    const queue = [], waiters = [];
    ws.on('error', () => {});
    ws.on('message', bytes => {
      const value = JSON.parse(bytes.toString());
      const at = waiters.findIndex(w => w.predicate(value));
      if (at >= 0) { const [w] = waiters.splice(at, 1); clearTimeout(w.timer); w.resolve(value); }
      else queue.push(value);
    });
    const peer = {
      ws, send: payload => ws.send(JSON.stringify(payload)),
      wait(predicate) {
        const at = queue.findIndex(predicate);
        if (at >= 0) return Promise.resolve(queue.splice(at, 1)[0]);
        return new Promise((resolve, reject) => {
          const item = { predicate, resolve };
          item.timer = setTimeout(() => { const at = waiters.indexOf(item); if (at >= 0) waiters.splice(at, 1); reject(new Error('Message timeout')); }, 2500);
          waiters.push(item);
        });
      },
      next(type) { return this.wait(msg => msg.type === type); },
      pending(type) { return queue.filter(msg => msg.type === type); },
      async create() { this.send({ type: 'create', nickname: 'Host' }); return this.identity = await this.next('session'); },
      async join(code) { this.send({ type: 'join', code, nickname: 'Member' }); return this.identity = await this.next('session'); },
      async ready() {
        this.send({ type: 'set-ready', ready: true, capabilities: { simulation: true, canStart: true, canStop: true } });
        await this.wait(msg => msg.type === 'state' && msg.room.members.some(m => m.id === this.identity.memberId && m.ready));
      }
    };
    clients.push(peer);
    peer.hello = await peer.next('hello');
    if (handshake) {
      peer.send({ type: 'hello', product: 'MabiSync', protocolVersion: 1 });
      await peer.next('hello-accepted');
    }
    return peer;
  }
  return { server, client };
}

test('product handshake rejects an old SyncCue client and health identifies the service', async t => {
  const { server, client } = await setup(t);
  const peer = await client(false);
  peer.send({ type: 'create', nickname: 'Old' });
  assert.equal((await peer.next('error')).code, 'INCOMPATIBLE_CLIENT');
  peer.send({ type: 'hello', product: 'SyncCue', protocolVersion: 1 });
  assert.equal((await peer.next('error')).code, 'INCOMPATIBLE_CLIENT');
  const health = await (await fetch(server.url.replace('ws:', 'http:') + '/health')).json();
  assert.equal(health.product, 'MabiSync');
  assert.equal(health.protocolVersion, 1);
  assert.equal(health.serverEpoch, peer.hello.serverEpoch);
});

test('only all selected participants acknowledging commits exactly once', async t => {
  const { client } = await setup(t);
  const host = await client(), member = await client();
  const session = await host.create(); await member.join(session.code);
  await host.ready(); await member.ready();
  host.send({ type: 'prepare', mode: 'emote', participantIds: [host.identity.memberId, member.identity.memberId] });
  const a = await host.next('prepare-action'), b = await member.next('prepare-action');
  assert.equal(a.action.id, b.action.id);
  host.send({ type: 'action-ready', actionId: a.action.id, ready: true });
  const preparing = await host.wait(msg => msg.type === 'state' && msg.room.performance?.phase === 'preparing' && msg.room.performance.preparedMemberIds.includes(host.identity.memberId));
  assert.equal(preparing.room.performance.preparedMemberIds.length, 1);
  member.send({ type: 'action-ready', actionId: a.action.id, ready: true });
  assert.equal((await host.next('commit-action')).action.id, a.action.id);
  assert.equal((await member.next('commit-action')).action.id, a.action.id);
  member.send({ type: 'action-ready', actionId: a.action.id, ready: true });
  member.send({ type: 'sync', id: 91, t1: 1 });
  await member.next('sync');
  assert.equal(member.pending('commit-action').length, 0);
  const committed = await member.wait(msg => msg.type === 'state' && msg.room.performance?.phase === 'committed');
  assert.equal(committed.room.performance.startAction.id, a.action.id);
});

test('unready member is identified and a member cannot prepare or stop the room', async t => {
  const { client } = await setup(t);
  const host = await client(), member = await client();
  const room = await host.create(); await member.join(room.code); await host.ready();
  host.send({ type: 'prepare', mode: 'emote', participantIds: [member.identity.memberId] });
  const error = await host.next('error');
  assert.equal(error.code, 'MEMBER_NOT_READY');
  assert.deepEqual(error.memberIds, [member.identity.memberId]);
  member.send({ type: 'stop-all' });
  assert.equal((await member.next('error')).code, 'FORBIDDEN');
  member.send({ type: 'prepare', mode: 'emote', participantIds: [member.identity.memberId] });
  assert.equal((await member.next('error')).code, 'FORBIDDEN');
});

test('cancelling an emote before dispatch never sends a stop operation', async t => {
  const { client } = await setup(t);
  const host = await client(), member = await client();
  const room = await host.create(); await member.join(room.code); await member.ready();
  host.send({ type: 'prepare', mode: 'emote', participantIds: [member.identity.memberId] });
  const pending = await member.next('prepare-action');
  host.send({ type: 'stop-all' });
  const cancelled = await member.next('cancel-action');
  assert.equal(cancelled.sessionId, pending.action.sessionId);
  member.send({ type: 'sync', id: 97, t1: 1 }); await member.next('sync');
  assert.equal(member.pending('stop-now').length, 0);
  await host.wait(msg => msg.type === 'state' && msg.room.performance?.phase === 'cancelled');
  member.send({ type: 'action-ready', actionId: pending.action.id, ready: true });
  assert.equal((await member.next('error')).code, 'STALE_ACTION');
});

test('emote flow rejects duration and has no leader or ending semantics', async t => {
  const { client } = await setup(t); const host = await client(); await host.create(); await host.ready();
  host.send({ type: 'prepare', mode: 'emote', participantIds: [host.identity.memberId], stopAfterMs: 1000 });
  const rejected = await host.wait(msg => msg.type === 'error' || msg.type === 'prepare-action');
  assert.equal(rejected.type, 'error');
  assert.equal(rejected.code, 'STOP_NOT_APPLICABLE');
  host.send({ type: 'prepare', mode: 'emote', participantIds: [host.identity.memberId] });
  const { action } = await host.next('prepare-action');
  host.send({ type: 'action-ready', actionId: action.id, ready: true }); await host.next('commit-action');
  const state = await host.wait(msg => msg.type === 'state' && msg.room.performance?.phase === 'committed');
  assert.equal(state.room.performance.leaderId, null);
  assert.equal(state.room.performance.normalEndRule, 'none');
  assert.equal(state.room.performance.stopTimingUnavailable, null);
});

for (const signal of ['set-ready', 'local-stop', 'disconnect']) test(`committed emote ${signal} cancels the pending trigger without entering music interruption`, async t => {
  const { client } = await setup(t); const host = await client(), member = await client();
  const room = await host.create(); await member.join(room.code); await member.ready();
  host.send({ type: 'prepare', mode: 'emote', participantIds: [member.identity.memberId] });
  const { action } = await member.next('prepare-action');
  member.send({ type: 'action-ready', actionId: action.id, ready: true });
  await host.wait(m => m.type === 'state' && m.room.performance?.phase === 'committed');
  if (signal === 'disconnect') member.ws.close();
  else member.send({ type: signal, ready: false, capabilities: { simulation: true, canStart: false, canStop: false } });
  const changed = await host.wait(m => m.type === 'state' && ['cancelled', 'interrupted'].includes(m.room.performance?.phase));
  assert.equal(changed.room.performance.phase, 'cancelled', 'Emote has no music continue/stop UI to resolve an interrupted state');
  assert.equal(host.pending('stop-now').length, 0);
  if (signal !== 'disconnect') {
    assert.equal((await member.next('cancel-action')).actionId, action.id);
    await member.ready();
    host.send({ type: 'prepare', mode: 'emote', participantIds: [member.identity.memberId] });
    assert.notEqual((await member.next('prepare-action')).action.sessionId, action.sessionId);
  }
});

test('concurrent negative preparation acknowledgments cancel once without stale-action errors', async t => {
  const { client } = await setup(t); const host = await client(), member = await client();
  const room = await host.create(); await member.join(room.code); await host.ready(); await member.ready();
  host.send({ type: 'prepare', mode: 'emote', participantIds: [host.identity.memberId, member.identity.memberId] });
  const { action } = await host.next('prepare-action'); await member.next('prepare-action');
  for (const peer of [host, member]) {
    peer.send({ type: 'action-ready', actionId: action.id, ready: false, error: 'Local binding changed' });
    peer.send({ type: 'sync', id: 101, t1: 1 });
  }
  await Promise.all([host.next('sync'), member.next('sync')]);
  assert.equal(host.pending('error').length + member.pending('error').length, 0);
  await host.wait(m => m.type === 'state' && m.room.performance?.phase === 'cancelled');
  assert.equal(host.pending('commit-action').length + member.pending('commit-action').length, 0);
});

test('immediate stop receipts are accepted and deduplicated after host stop', async t => {
  const { client } = await setup(t); const host = await client(); await host.create(); await host.ready();
  host.send({ type: 'prepare', mode: 'independent', participantIds: [host.identity.memberId] });
  const { action } = await host.next('prepare-action');
  host.send({ type: 'action-ready', actionId: action.id, ready: true }); await host.next('commit-action');
  host.send({ type: 'stop-all' }); const stop = await host.next('stop-now');
  for (let i = 0; i < 2; i++) host.send({ type: 'receipt', actionId: stop.id, status: 'executed', actualServerMs: 123 });
  host.send({ type: 'sync', id: 99, t1: 1 }); await host.next('sync');
  assert.equal(host.pending('error').length, 0);
  const state = host.pending('state').at(-1);
  assert.equal(state.room.performance.receipts.length, 1);
  assert.equal(state.room.performance.receipts[0].actionId, stop.id);
  assert.equal(state.room.performance.receipts[0].gameResponseVerified, false);
});

test('cancellation before commit accepts an inert task receipt without allowing execution claims', async t => {
  const { client } = await setup(t); const host = await client(); await host.create(); await host.ready();
  host.send({ type: 'prepare', mode: 'independent', participantIds: [host.identity.memberId] });
  const { action } = await host.next('prepare-action');
  host.send({ type: 'cancel' }); await host.next('cancel-action');
  host.send({ type: 'receipt', actionId: action.id, status: 'executed', actualServerMs: 123 });
  assert.equal((await host.next('error')).code, 'STALE_ACTION');
  host.send({ type: 'receipt', actionId: action.id, status: 'cancelled' });
  host.send({ type: 'sync', id: 98, t1: 1 }); await host.next('sync');
  assert.equal(host.pending('error').length, 0);
  assert.equal(host.pending('state').at(-1).room.performance.receipts[0].status, 'cancelled');
});

test('tool disconnection never silently transfers host and resume does not replay a start', async t => {
  const { client } = await setup(t);
  const host = await client(), member = await client();
  const room = await host.create(); await member.join(room.code); await host.ready();
  host.send({ type: 'prepare', mode: 'emote', participantIds: [host.identity.memberId] });
  const pending = await host.next('prepare-action');
  host.send({ type: 'action-ready', actionId: pending.action.id, ready: true });
  await host.next('commit-action');
  host.ws.terminate();
  const offline = await member.wait(msg => msg.type === 'state' && msg.room.members.find(m => m.id === room.memberId)?.connected === false);
  assert.equal(offline.room.hostId, room.memberId);
  assert.equal(offline.room.performance.interruption.source, 'tool-connection');
  const resumed = await client();
  resumed.send({ type: 'resume', code: room.code, token: room.token });
  const identity = await resumed.next('session');
  assert.equal(identity.memberId, room.memberId);
  const state = await resumed.next('state');
  assert.equal(state.room.performance.startAction.id, pending.action.id);
  assert.equal(state.room.members.find(m => m.id === room.memberId).ready, false);
  resumed.send({ type: 'sync', id: 67, t1: 1 }); await resumed.next('sync');
  assert.equal(resumed.pending('commit-action').length, 0);
});

test('disconnect during preparation cancels instead of partially committing', async t => {
  const { client } = await setup(t);
  const host = await client(), member = await client();
  const room = await host.create(); await member.join(room.code); await member.ready();
  host.send({ type: 'prepare', mode: 'emote', participantIds: [member.identity.memberId] });
  const pending = await member.next('prepare-action'); member.ws.terminate();
  const state = await host.wait(msg => msg.type === 'state' && msg.room.performance?.phase === 'cancelled');
  assert.equal(state.room.performance.id, pending.action.sessionId);
});

test('manual stop duration prepares both actions and preserves committed stop on disconnect', async t => {
  const { client } = await setup(t);
  const host = await client(), member = await client();
  const room = await host.create(); await member.join(room.code); await host.ready(); await member.ready();
  host.send({ type: 'prepare', mode: 'independent', participantIds: [room.memberId, member.identity.memberId], stopAfterMs: 8000 });
  for (const peer of [host, member]) {
    const a = await peer.next('prepare-action'), b = await peer.next('prepare-action');
    assert.ok(Math.abs((b.action.targetServerMs - a.action.targetServerMs) - 8000) < 0.000001);
    peer.send({ type: 'action-ready', actionId: a.action.id, ready: true });
    peer.send({ type: 'action-ready', actionId: b.action.id, ready: true });
  }
  const start = await host.next('commit-action'), stop = await host.next('commit-action');
  assert.equal(start.action.kind, 'start'); assert.equal(stop.action.kind, 'stop');
  member.ws.terminate();
  const state = await host.wait(msg => msg.type === 'state' && msg.room.performance?.interruption?.source === 'tool-connection');
  assert.equal(state.room.performance.stopAction.id, stop.action.id);
  assert.equal(state.room.performance.stopTimingSource, 'user-duration');
});

test('four timestamp sync echoes the request and new service boot gets a new epoch', async t => {
  const first = await setup(t), second = await setup(t);
  const a = await first.client(), b = await second.client();
  assert.notEqual(a.hello.serverEpoch, b.hello.serverEpoch);
  a.send({ type: 'sync', id: 1, t1: 1234.5 });
  const reply = await a.next('sync');
  assert.equal(reply.t1, 1234.5); assert.ok(reply.t3 >= reply.t2);
  assert.equal(reply.serverEpoch, a.hello.serverEpoch);
});

test('a verified boolean cannot enable unvalidated automatic score endpoints', async t => {
  const { client } = await setup(t); const host = await client(); await host.create();
  host.send({ type: 'score-timeline', sessionId: 'fake', verified: true, durationMs: 100 });
  assert.equal((await host.next('error')).code, 'AUTOMATIC_END_UNVERIFIED');
});

test('24 clients join and a 25th is rejected without leaking session tokens', async t => {
  const { client } = await setup(t); const host = await client(); const room = await host.create();
  for (let i = 1; i < 24; i++) { const member = await client(); await member.join(room.code); }
  const state = await host.wait(msg => msg.type === 'state' && msg.room.members.length === 24);
  assert.ok(state.room.members.every(m => !('token' in m)));
  const overflow = await client(); overflow.send({ type: 'join', code: room.code, nickname: 'Overflow' });
  assert.equal((await overflow.next('error')).code, 'ROOM_FULL');
});

test('only chosen leader may report interruption and host explicitly chooses continue or stop', async t => {
  const { client } = await setup(t); const host = await client(), member = await client();
  const room = await host.create(); await member.join(room.code); await host.ready(); await member.ready();
  host.send({ type: 'select-leader', memberId: member.identity.memberId });
  await host.wait(msg => msg.type === 'state' && msg.room.leaderId === member.identity.memberId);
  host.send({ type: 'prepare', mode: 'independent', participantIds: [member.identity.memberId] });
  const action = (await member.next('prepare-action')).action;
  member.send({ type: 'action-ready', actionId: action.id, ready: true }); await member.next('commit-action');
  host.send({ type: 'leader-status', sessionId: action.sessionId, status: 'interrupted' });
  assert.equal((await host.next('error')).code, 'STALE_SESSION');
  member.send({ type: 'leader-status', sessionId: action.sessionId, status: 'interrupted' });
  const interrupted = await host.wait(msg => msg.type === 'state' && msg.room.performance?.phase === 'interrupted');
  assert.equal(interrupted.room.performance.interruption.source, 'game-observation');
  host.send({ type: 'decide', decision: 'continue' });
  await host.wait(msg => msg.type === 'state' && msg.room.performance?.phase === 'committed' && !msg.room.performance.interruption);
  member.send({ type: 'leader-status', sessionId: action.sessionId, status: 'disconnected' });
  await host.wait(msg => msg.type === 'state' && msg.room.performance?.interruption?.status === 'disconnected');
  host.send({ type: 'decide', decision: 'stop' }); await member.next('stop-now');
});

test('duplicate receipts are immutable and never claim a verified game response', async t => {
  const { client } = await setup(t); const host = await client(); await host.create(); await host.ready();
  host.send({ type: 'prepare', mode: 'emote', participantIds: [host.identity.memberId] });
  const { action } = await host.next('prepare-action');
  host.send({ type: 'action-ready', actionId: action.id, ready: true }); await host.next('commit-action');
  host.send({ type: 'receipt', actionId: action.id, status: 'executed', actualServerMs: 100, simulated: false });
  host.send({ type: 'receipt', actionId: action.id, status: 'failed', actualServerMs: 200 });
  host.send({ type: 'sync', id: 88, t1: 1 }); await host.next('sync');
  const state = host.pending('state').at(-1);
  assert.equal(state.room.performance.receipts.length, 1);
  assert.equal(state.room.performance.receipts[0].status, 'executed');
  assert.equal(state.room.performance.receipts[0].simulated, true);
  assert.equal(state.room.performance.receipts[0].gameResponseVerified, false);
});

test('malformed multibyte resume token is rejected as expired identity', async t => {
  const { client } = await setup(t); const host = await client(), other = await client();
  const room = await host.create();
  other.send({ type: 'resume', code: room.code, token: '\u4e2d'.repeat(48) });
  assert.equal((await other.next('error')).code, 'SESSION_EXPIRED');
});

test('leader interruption before commit cancels the inert task', async t => {
  const { client } = await setup(t); const host = await client(); await host.create(); await host.ready();
  host.send({ type: 'prepare', mode: 'independent', participantIds: [host.identity.memberId] });
  const { action } = await host.next('prepare-action');
  host.send({ type: 'leader-status', sessionId: action.sessionId, status: 'interrupted' });
  const state = await host.wait(msg => msg.type === 'state' && msg.room.performance?.id === action.sessionId && msg.room.performance.phase !== 'preparing');
  assert.equal(state.room.performance.phase, 'cancelled');
  host.send({ type: 'decide', decision: 'continue' });
  assert.equal((await host.next('error')).code, 'NO_INTERRUPTION');
});

test('moving a live identity to a second socket reports the interrupted input owner', async t => {
  const { client } = await setup(t); const host = await client(), member = await client();
  const room = await host.create(); await member.join(room.code); await member.ready();
  host.send({ type: 'prepare', mode: 'emote', participantIds: [member.identity.memberId] });
  const { action } = await member.next('prepare-action');
  member.send({ type: 'action-ready', actionId: action.id, ready: true }); await member.next('commit-action');
  const replacement = await client(); replacement.send({ type: 'resume', code: room.code, token: member.identity.token });
  await replacement.next('session');
  host.send({ type: 'sync', id: 66, t1: 1 }); await host.next('sync');
  const state = host.pending('state').at(-1);
  assert.equal(state.room.performance.interruption?.source, 'tool-session-replaced');
});

test('first-cycle stop is prepared after leader capture, all acknowledge it, and duplicates cannot move it', async t => {
  const {client}=await setup(t); const host=await client(),other=await client();
  await host.create(); await other.join(host.identity.code); await host.ready(); await other.ready();
  host.send({type:'prepare',mode:'independent',countdownMs:1000,stopRule:'first-cycle',participantIds:[host.identity.memberId,other.identity.memberId]});
  const {action}=await host.next('prepare-action'); await other.next('prepare-action');
  host.send({type:'action-ready',actionId:action.id,ready:true}); other.send({type:'action-ready',actionId:action.id,ready:true});
  await host.next('commit-action'); await other.next('commit-action');
  await new Promise(resolve=>setTimeout(resolve,1050));
  const report={type:'game-observation',sessionId:action.sessionId,generation:'cycle',sequence:1,status:'identified',
    identity:{characterId:'101',realmId:3,nickname:'Leader'},music:{concertId:'7',hostCharacterId:'101',performerCharacterId:'101',title:'Fixture',
      isLoop:true,selectedChannels:[0],scoreSha256:'a'.repeat(64),wholeScoreDurationSeconds:2,selectedDurationSeconds:2,issue:null,
      timingSource:'tw-receive-offset-v1',estimatedStartServerMs:action.targetServerMs+100}};
  host.send(report);
  const stop=(await host.next('prepare-action')).action; await other.next('prepare-action');
  assert.equal(stop.kind,'stop'); assert.equal(stop.targetServerMs,action.targetServerMs+2100);
  host.send({type:'action-ready',actionId:stop.id,ready:true});
  host.send({type:'sync',id:997,t1:1}); await host.next('sync'); assert.equal(host.pending('commit-action').length,0);
  other.send({type:'action-ready',actionId:stop.id,ready:true}); await host.next('commit-action'); await other.next('commit-action');
  host.send({...report,sequence:2}); host.send({type:'sync',id:998,t1:1}); await host.next('sync');
  assert.equal(host.pending('prepare-action').length,0);
  const state=host.pending('state').at(-1).room.performance;
  assert.equal(state.stopTimingSource,'leader-capture-estimate'); assert.equal(state.firstCycleTimingVerified,false);
  host.send({...report,sequence:3,generation:'new-connection',music:{...report.music,concertId:'8'}});
  await host.wait(m=>m.type==='state' && m.room.performance?.phase==='interrupted');
  const changed=host.pending('state').at(-1).room.performance;
  assert.equal(changed.stopAction.id,stop.id); assert.equal(changed.stopCommitted,true);
  assert.equal(host.pending('prepare-action').length,0);
  host.send({type:'decide',decision:'continue'});
  await host.wait(m=>m.type==='state' && m.room.performance?.phase==='performing');
  host.send({type:'stop-all'}); await host.next('stop-now');
});

test('first-cycle ignores other members, old rounds, old concerts and an unconfirmed start', async t => {
  const {client}=await setup(t); const host=await client(), other=await client();
  await host.create(); await other.join(host.identity.code); await host.ready(); await other.ready();
  const report={type:'game-observation',generation:'isolation',sequence:1,status:'identified',
    identity:{characterId:'101',realmId:3,nickname:'Leader'},music:{concertId:'7',hostCharacterId:'101',performerCharacterId:'101',title:'Fixture',
      isLoop:true,selectedChannels:[0],scoreSha256:'a'.repeat(64),wholeScoreDurationSeconds:4,selectedDurationSeconds:4,
      timingSource:'tw-receive-offset-v1',estimatedStartServerMs:0}};
  host.send(report);
  await host.wait(m=>m.type==='state' && m.room.members.some(p=>p.music?.concertId==='7'));
  host.send({type:'prepare',mode:'independent',countdownMs:1000,stopRule:'first-cycle',participantIds:[host.identity.memberId,other.identity.memberId]});
  const {action}=await host.next('prepare-action'); await other.next('prepare-action');
  host.send({type:'action-ready',actionId:action.id,ready:true}); other.send({type:'action-ready',actionId:action.id,ready:true});
  await host.next('commit-action'); await other.next('commit-action');
  const fresh={...report,sessionId:action.sessionId,music:{...report.music,estimatedStartServerMs:action.targetServerMs+100}};
  other.send(fresh); // A valid score on a different room member is not the selected leader.
  host.send({...fresh,sequence:2,sessionId:'old-round',music:{...fresh.music,concertId:'8'}});
  host.send({...fresh,sequence:3}); // Present before preparation: never bind it to a new round.
  host.send({...fresh,sequence:4,music:{...fresh.music,concertId:'8',estimatedStartServerMs:undefined,timingSource:undefined,automaticStopAvailable:true}});
  host.send({type:'sync',id:1001,t1:1}); await host.next('sync');
  assert.equal(host.pending('prepare-action').length,0);
  host.send({...fresh,sequence:5,music:{...fresh.music,concertId:'8'}});
  const stop=(await host.next('prepare-action')).action;
  assert.equal(stop.kind,'stop'); assert.equal(stop.targetServerMs,action.targetServerMs+4100);
});

for (const rejection of [true,false]) test(rejection?'a rejected late stop leaves music under manual control and tolerates late acknowledgments':'a missed first-cycle endpoint keeps music active without a late stop', async t=>{
  const {client}=await setup(t);const host=await client(),other=await client();
  await host.create();await other.join(host.identity.code);await host.ready();await other.ready();
  host.send({type:'prepare',mode:'independent',countdownMs:1000,stopRule:'first-cycle',participantIds:[host.identity.memberId,other.identity.memberId]});
  const {action}=await host.next('prepare-action');await other.next('prepare-action');
  host.send({type:'action-ready',actionId:action.id,ready:true});other.send({type:'action-ready',actionId:action.id,ready:true});
  await host.next('commit-action');await other.next('commit-action');
  if (!rejection) await new Promise(resolve=>setTimeout(resolve,1100));
  host.send({type:'game-observation',sessionId:action.sessionId,generation:'fallback',sequence:1,status:'identified',
    identity:{characterId:'101',realmId:3,nickname:'Leader'},music:{concertId:'7',performerCharacterId:'101',hostCharacterId:'101',title:'Fixture',
      isLoop:true,selectedChannels:[0],scoreSha256:'a'.repeat(64),wholeScoreDurationSeconds:rejection?4:.001,selectedDurationSeconds:rejection?4:.001,
      timingSource:'tw-receive-offset-v1',estimatedStartServerMs:action.targetServerMs}});
  if (rejection) {
    const stop=(await host.next('prepare-action')).action;await other.next('prepare-action');
    host.send({type:'action-ready',actionId:stop.id,ready:false,error:'Missing stop binding'});
    await host.next('cancel-action');await other.next('cancel-action');
    other.send({type:'action-ready',actionId:stop.id,ready:true});
    other.send({type:'receipt',actionId:stop.id,status:'cancelled',simulated:true});
  }
  const status=rejection?'participant-not-ready':'too-late';
  const result=await host.wait(m=>m.type==='state' && m.room.performance?.cycleStopStatus===status);
  assert.ok(['committed','performing'].includes(result.room.performance.phase));
  assert.equal(result.room.performance.stopAction,undefined);
  other.send({type:'sync',id:1002,t1:1});await other.next('sync');assert.equal(other.pending('error').length,0);
  host.send({type:'stop-all'});await host.next('stop-now');await other.next('stop-now');
});

test('music uses first-cycle intent without inventing a stop when timing has not been validated', async t => {
  const { client } = await setup(t); const host = await client(); await host.create(); await host.ready();
  host.send({ type: 'prepare', mode: 'independent', participantIds: [host.identity.memberId] });
  const { action } = await host.next('prepare-action');
  host.send({ type: 'action-ready', actionId: action.id, ready: true }); await host.next('commit-action');
  const state = await host.wait(msg => msg.type === 'state' && msg.room.performance?.phase === 'committed');
  assert.equal(state.room.performance.normalEndRule, 'leader-first-cycle');
  assert.equal(state.room.performance.firstCycleTimingVerified, false);
  assert.equal(state.room.performance.stopAction, undefined);
  assert.equal(state.room.performance.stopTimingUnavailable, 'playback-timing-unverified');
});

test('a prior round cleanup receipt is isolated from a newly prepared round', async t=>{
  const {client}=await setup(t);const host=await client();await host.create();await host.ready();
  host.send({type:'prepare',mode:'emote',participantIds:[host.identity.memberId]});
  const old=(await host.next('prepare-action')).action;
  host.send({type:'cancel'});await host.next('cancel-action');
  host.send({type:'prepare',mode:'emote',participantIds:[host.identity.memberId]});
  const current=(await host.next('prepare-action')).action;
  host.send({type:'receipt',actionId:old.id,status:'cancelled',simulated:true});
  host.send({type:'action-ready',actionId:old.id,ready:false});
  host.send({type:'sync',id:1101,t1:1});await host.next('sync');
  assert.equal(host.pending('error').length,0);
  const state=host.pending('state').at(-1).room.performance;
  assert.equal(state.id,current.sessionId);assert.equal(state.receipts.length,0);assert.equal(state.phase,'preparing');
  host.send({type:'receipt',actionId:old.id,status:'executed',simulated:true});
  assert.equal((await host.next('error')).code,'STALE_ACTION'); // An uncommitted input cannot be laundered as executed.
});

test('a manual joiner late session acknowledgment cannot disturb the next round', async t=>{
  const {client}=await setup(t);const host=await client(),joiner=await client();
  await host.create();await joiner.join(host.identity.code);await host.ready();
  joiner.send({type:'set-ready',ready:true,capabilities:{simulation:true,canStart:false,canStop:true,musicRole:'joiner'}});
  await host.wait(m=>m.type==='state' && m.room.members.every(p=>p.ready));
  const prepare=()=>host.send({type:'prepare',mode:'ensemble',participantIds:[host.identity.memberId,joiner.identity.memberId]});
  prepare();await host.next('prepare-action');const old=await joiner.next('prepare-session');
  host.send({type:'cancel'});await joiner.next('cancel-session');
  await host.ready();joiner.send({type:'set-ready',ready:true,capabilities:{simulation:true,canStart:false,canStop:true,musicRole:'joiner'}});
  await host.wait(m=>m.type==='state' && m.room.members.every(p=>p.ready));
  prepare();const current=await host.next('prepare-action');await joiner.next('prepare-session');
  joiner.send({type:'session-ready',sessionId:old.sessionId,ready:true});
  joiner.send({type:'sync',id:1102,t1:1});await joiner.next('sync');
  assert.equal(joiner.pending('error').length,0);
  assert.equal(joiner.pending('state').at(-1).room.performance.id,current.action.sessionId);
  assert.equal(host.pending('commit-action').length,0);
});
