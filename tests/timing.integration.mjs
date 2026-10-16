import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { WebSocket, WebSocketServer } from 'ws';
import { createRoomServer } from '../server/room-server.js';

test('background members restore before readiness acknowledgment and cancellation remains responsive',async t=>{
  const service=await createRoomServer({port:0});t.after(()=>service.close());
  const probe=fileURLToPath(new URL('./MabiSync.Client.Probe/bin/Release/net8.0/MabiSync.Client.Probe.dll',import.meta.url));
  const child=spawn('dotnet',[probe,service.url,'focus-flow'],{windowsHide:true});
  t.after(()=>{if(child.exitCode===null)child.kill();});let output='';child.stdout.on('data',b=>output+=b);child.stderr.on('data',b=>output+=b);
  const code=await new Promise((r,j)=>{child.once('error',j);child.once('exit',r);});assert.equal(code,0,output);t.diagnostic(output.trim());
});

test('jittered WAN relay preserves early music members and synchronizes tool starts',async t=>{
  const service=await createRoomServer({port:0});t.after(()=>service.close());
  const relay=new WebSocketServer({port:0,host:'127.0.0.1'});await new Promise(r=>relay.once('listening',r));
  const timers=new Set();const sockets=new Set();let connection=0;
  t.after(async()=>{for(const timer of timers)clearTimeout(timer);for(const ws of sockets)ws.terminate();await new Promise(r=>relay.close(r));});
  relay.on('connection',down=>{
    const up=new WebSocket(service.url);sockets.add(down);sockets.add(up);const base=connection++?45:15;let sent=0;
    function forward(target){let last=0;return bytes=>{
      const jitter=[0,8,24,3,12,0][sent++%6];const due=Math.max(performance.now()+base+jitter,last);last=due;
      const timer=setTimeout(()=>{timers.delete(timer);if(target.readyState===WebSocket.OPEN)target.send(bytes.toString());},Math.max(0,due-performance.now()));timers.add(timer);
    };}
    const uplink=forward(up),downlink=forward(down);
    down.on('message',bytes=>{if(up.readyState===WebSocket.OPEN)uplink(bytes);else up.once('open',()=>uplink(bytes));});
    up.on('message',downlink);up.on('error',()=>{});down.on('error',()=>{});
    up.on('close',()=>down.close());down.on('close',()=>up.close());
  });
  const probe=fileURLToPath(new URL('./MabiSync.Client.Probe/bin/Release/net8.0/MabiSync.Client.Probe.dll',import.meta.url));
  const child=spawn('dotnet',[probe,`ws://127.0.0.1:${relay.address().port}`,'timing-flow'],{windowsHide:true});
  t.after(()=>{if(child.exitCode===null)child.kill();});let output='';child.stdout.on('data',b=>output+=b);child.stderr.on('data',b=>output+=b);
  const code=await new Promise((r,j)=>{child.once('error',j);child.once('exit',r);});assert.equal(code,0,output);
  const result=JSON.parse(output.trim().split(/\r?\n/).at(-1));assert.equal(result.rounds,3);assert.equal(result.desktopInputs,0);
  assert.deepEqual(result.adjustments,[-125,-2000,125]);
  assert.ok(result.correctedToolSkewMs.every(ms=>ms<100),output);t.diagnostic(output.trim());
});
