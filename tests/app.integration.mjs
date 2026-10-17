import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { createRoomServer } from '../server/room-server.js';

test('real WPF rendering survives room transitions and uses one room snapshot per frame', async t => {
  const server=await createRoomServer({port:0});t.after(()=>server.close());
  const probe=fileURLToPath(new URL('./MabiSync.App.Probe/bin/Release/net8.0-windows/MabiSync.App.Probe.dll',import.meta.url));
  const app=process.env.MABISYNC_TEST_APP_ASSEMBLY ?? fileURLToPath(new URL('../src/MabiSync.App/bin/Release/net8.0-windows/MabiSync.dll',import.meta.url));
  const child=spawn('dotnet',[probe,app,server.url],{windowsHide:true,stdio:['ignore','pipe','pipe']});
  t.after(()=>{if(child.exitCode===null)child.kill();});
  let output='';child.stdout.on('data',b=>output+=b);child.stderr.on('data',b=>output+=b);
  // Includes 24-row layout and role-switch checks as well as live room flows.
  const timeout=setTimeout(()=>child.kill(),40000);timeout.unref();
  const code=await new Promise((resolve,reject)=>{child.once('error',reject);child.once('exit',resolve);});clearTimeout(timeout);
  assert.equal(code,0,output);assert.match(output,/PASS WPF member arrival/);assert.match(output,/PASS WPF create/);
  t.diagnostic(output.trim());
});
