import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { WebSocketServer } from 'ws';

for(const scenario of ['resume-delayed','resume-expired','resume-disconnected'])test(`native ${scenario} cannot complete before room restoration`,async t=>{
  const wss=new WebSocketServer({port:0,host:'127.0.0.1'});await new Promise(r=>wss.once('listening',r));
  t.after(async()=>{for(const ws of wss.clients)ws.terminate();await new Promise(r=>wss.close(r));});
  const epoch=randomUUID();const identity={type:'session',code:'FIXTUR',memberId:'fixture-host',token:'a'.repeat(48),serverEpoch:epoch};
  wss.on('connection',ws=>{
    const send=value=>{if(ws.readyState===1)ws.send(JSON.stringify(value));};
    const state=resumed=>send({type:'state',serverEpoch:epoch,room:{code:'FIXTUR',hostId:identity.memberId,members:[{id:identity.memberId,connected:true}],resumed}});
    send({type:'hello',product:'MabiSync',protocolVersion:1,serverEpoch:epoch});
    ws.on('message',bytes=>{const msg=JSON.parse(bytes);
      if(msg.type==='hello')send({type:'hello-accepted',serverEpoch:epoch});
      if(msg.type==='sync')send({type:'sync',id:msg.id,t1:msg.t1,t2:performance.now(),t3:performance.now(),serverEpoch:epoch});
      if(msg.type==='create'){send(identity);state(false);}
      if(msg.type==='resume')setTimeout(()=>{
        if(scenario==='resume-expired')send({type:'error',code:'SESSION_EXPIRED'});
        else if(scenario==='resume-disconnected')ws.close();
        else{send(identity);state(true);}
      },300).unref();
    });
  });
  const path=fileURLToPath(new URL('./MabiSync.Client.Probe/bin/Release/net8.0/MabiSync.Client.Probe.dll',import.meta.url));
  const child=spawn('dotnet',[path,`ws://127.0.0.1:${wss.address().port}`,scenario],{windowsHide:true,stdio:['ignore','pipe','pipe']});
  t.after(()=>{if(child.exitCode===null)child.kill();});let output='';child.stdout.on('data',b=>output+=b);child.stderr.on('data',b=>output+=b);
  const code=await new Promise((resolve,reject)=>{child.once('error',reject);child.once('exit',resolve);});assert.equal(code,0,output);
});

test('a silent service cannot leave the native client reporting a healthy room',async t=>{
  const wss=new WebSocketServer({port:0,host:'127.0.0.1'});await new Promise(r=>wss.once('listening',r));
  t.after(async()=>{for(const ws of wss.clients)ws.terminate();await new Promise(r=>wss.close(r));});
  const epoch=randomUUID();let silent=false;
  wss.on('connection',ws=>{
    const send=value=>ws.send(JSON.stringify(value));send({type:'hello',product:'MabiSync',protocolVersion:1,serverEpoch:epoch});
    ws.on('message',bytes=>{const msg=JSON.parse(bytes);
      if(msg.type==='hello')send({type:'hello-accepted',serverEpoch:epoch});
      if(msg.type==='sync'&&!silent)send({type:'sync',id:msg.id,t1:msg.t1,t2:performance.now(),t3:performance.now(),serverEpoch:epoch});
      if(msg.type==='create'){send({type:'session',code:'FIXTUR',memberId:'host',token:'a'.repeat(48),serverEpoch:epoch});silent=true;}
    });
  });
  const path=fileURLToPath(new URL('./MabiSync.Client.Probe/bin/Release/net8.0/MabiSync.Client.Probe.dll',import.meta.url));
  const child=spawn('dotnet',[path,`ws://127.0.0.1:${wss.address().port}`,'silent-room'],{windowsHide:true,stdio:['ignore','pipe','pipe']});
  t.after(()=>{if(child.exitCode===null)child.kill();});let output='';child.stdout.on('data',b=>output+=b);child.stderr.on('data',b=>output+=b);
  const code=await new Promise((resolve,reject)=>{child.once('error',reject);child.once('exit',resolve);});assert.equal(code,0,output);
});

for (const changedEndpoint of [false, true]) test(changedEndpoint ? 'changing endpoint clears a native room even when service epoch matches' : 'new service epoch clears native room identity and stale room presentation', async t => {
  const wss = new WebSocketServer({ port: 0, host: '127.0.0.1' });
  await new Promise(resolve => wss.once('listening', resolve));
  t.after(async () => { for (const ws of wss.clients) ws.terminate(); await new Promise(resolve => wss.close(resolve)); });
  const sharedEpoch = randomUUID();
  wss.on('connection', ws => {
    const epoch = changedEndpoint ? sharedEpoch : randomUUID(), send = msg => ws.send(JSON.stringify(msg));
    send({ type: 'hello', product: 'MabiSync', protocolVersion: 1, serverEpoch: epoch });
    ws.on('message', bytes => {
      const msg = JSON.parse(bytes);
      if (msg.type === 'hello') send({ type: 'hello-accepted', serverEpoch: epoch });
      if (msg.type === 'sync') send({ type: 'sync', id: msg.id, t1: msg.t1, t2: performance.now(), t3: performance.now(), serverEpoch: epoch });
      if (msg.type === 'create') {
        send({ type: 'session', code: 'FIXTUR', memberId: 'old-member', token: 'a'.repeat(48), serverEpoch: epoch });
        send({ type: 'state', serverEpoch: epoch, room: { code: 'FIXTUR', members: [], performance: null } });
      }
    });
  });
  const path = fileURLToPath(new URL('./MabiSync.Client.Probe/bin/Release/net8.0/MabiSync.Client.Probe.dll', import.meta.url));
  const child = spawn('dotnet', [path, `ws://127.0.0.1:${wss.address().port}`, changedEndpoint ? 'switch-endpoint' : 'reset-identity'], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  t.after(() => { if (child.exitCode === null) child.kill(); });
  let output = ''; child.stdout.on('data', bytes => output += bytes); child.stderr.on('data', bytes => output += bytes);
  const code = await new Promise((resolve, reject) => { child.once('error', reject); child.once('exit', resolve); });
  assert.equal(code, 0, output);
});

for (const cancel of [false, true]) test(cancel ? 'native cancellation receipt omits absent dispatch time' : 'native duplicate prepare and commit cannot cancel or falsely fail an action', async t => {
  const wss = new WebSocketServer({ port: 0, host: '127.0.0.1' });
  await new Promise(resolve => wss.once('listening', resolve));
  t.after(async () => { for (const ws of wss.clients) ws.terminate(); await new Promise(resolve => wss.close(resolve)); });
  const epoch = randomUUID(); let invalidTimestamp = false;
  wss.on('connection', ws => {
    const send = value => ws.send(JSON.stringify(value));
    const member = { id: 'member-1', nickname: 'Fixture', ready: false, connected: true, role: 'host', capabilities: { simulation: true, canStart: true, canStop: true } };
    const room = { code: 'FIXTUR', hostId: member.id, leaderId: member.id, members: [member], performance: null };
    const state = () => send({ type: 'state', room, serverEpoch: epoch, serverNowMs: performance.now() });
    send({ type: 'hello', product: 'MabiSync', protocolVersion: 1, serverEpoch: epoch });
    let actions = [], acks = new Set();
    ws.on('message', bytes => {
      const msg = JSON.parse(bytes);
      if (msg.type === 'hello') send({ type: 'hello-accepted', product: 'MabiSync', protocolVersion: 1, serverEpoch: epoch });
      if (msg.type === 'sync') { const t2 = performance.now(); send({ type: 'sync', id: msg.id, t1: msg.t1, t2, t3: performance.now(), serverEpoch: epoch }); }
      if (msg.type === 'create') { send({ type: 'session', code: room.code, memberId: member.id, token: '1'.repeat(48), serverEpoch: epoch }); state(); }
      if (msg.type === 'set-ready') { member.ready = true; state(); }
      if (msg.type === 'prepare') {
        const target = performance.now() + 1000;
        actions = ['start', 'stop'].map((kind, index) => ({ id: randomUUID(), sessionId: 'session-1', kind, targetServerMs: target + index * 200, serverEpoch: epoch }));
        room.performance = { id: 'session-1', mode: 'independent', phase: 'preparing', leaderId: member.id, participantIds: [member.id], startAction: actions[0], stopAction: actions[1], receipts: [], interruption: null };
        for (const action of actions) { send({ type: 'prepare-action', action, mode: 'independent' }); send({ type: 'prepare-action', action, mode: 'independent' }); }
        state();
      }
      if (msg.type === 'action-ready') {
        if (!msg.ready) return; acks.add(msg.actionId);
        if (acks.size === 2 && room.performance.phase === 'preparing') {
          room.performance.phase = 'committed';
          for (const action of actions) { send({ type: 'commit-action', action }); send({ type: 'commit-action', action }); }
          if (cancel) for (const action of actions) send({ type: 'cancel-action', actionId: action.id, sessionId: action.sessionId, serverEpoch: epoch });
          state();
        }
      }
      if (msg.type === 'receipt') {
        if ('actualServerMs' in msg && msg.actualServerMs === null) invalidTimestamp = true;
        if (!room.performance.receipts.some(r => r.actionId === msg.actionId)) room.performance.receipts.push({ ...msg, memberId: member.id, gameResponseVerified: false });
        state();
      }
    });
  });
  const path = fileURLToPath(new URL('./MabiSync.Client.Probe/bin/Release/net8.0/MabiSync.Client.Probe.dll', import.meta.url));
  const child = spawn('dotnet', [path, `ws://127.0.0.1:${wss.address().port}`, ...(cancel ? ['cancel'] : [])], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  t.after(() => { if (child.exitCode === null) child.kill(); });
  let output = ''; child.stdout.on('data', bytes => output += bytes); child.stderr.on('data', bytes => output += bytes);
  const code = await new Promise((resolve, reject) => { child.once('error', reject); child.once('exit', resolve); });
  assert.equal(code, 0, output);
  assert.equal(invalidTimestamp, false, 'Cancelled receipt serialized a nonexistent input time as null');
});
