import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { createRoomServer } from '../server/room-server.js';

test('two native music clients complete timed songs, ensemble decisions, disconnect stops and cancellation', async t => {
  const server = await createRoomServer({ port: 0 }); t.after(() => server.close());
  const path = fileURLToPath(new URL('./MabiSync.Client.Probe/bin/Release/net8.0/MabiSync.Client.Probe.dll', import.meta.url));
  const child = spawn('dotnet', [path, server.url, 'music-flow'], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  t.after(() => { if (child.exitCode === null) child.kill(); });
  let output = ''; child.stdout.on('data', bytes => output += bytes); child.stderr.on('data', bytes => output += bytes);
  const result = await new Promise((resolve, reject) => { child.once('error', reject); child.once('exit', resolve); });
  assert.equal(result, 0, output);
  const summary = JSON.parse(output.trim().split(/\r?\n/).at(-1));
  assert.equal(summary.completedMusicRounds, 5); assert.equal(summary.cancelledRounds, 1);
  assert.equal(summary.desktopInputs, 0);
});

test('single invitation room setup recovers from bad code and transmits a fractional countdown', async t => {
  const server = await createRoomServer({ port: 0 }); t.after(() => server.close());
  const path = fileURLToPath(new URL('./MabiSync.Client.Probe/bin/Release/net8.0/MabiSync.Client.Probe.dll', import.meta.url));
  const child = spawn('dotnet', [path, server.url, 'room-setup'], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  t.after(() => { if (child.exitCode === null) child.kill(); });
  let output = ''; child.stdout.on('data', bytes => output += bytes); child.stderr.on('data', bytes => output += bytes);
  const result = await new Promise((resolve, reject) => { child.once('error', reject); child.once('exit', resolve); });
  assert.equal(result, 0, output);
});

test('tunnel readiness probe verifies HTTP plus real WebSocket handshake and rejects a different service epoch', async t => {
  const server = await createRoomServer({ port: 0 }); t.after(() => server.close());
  const path = fileURLToPath(new URL('./MabiSync.Client.Probe/bin/Release/net8.0/MabiSync.Client.Probe.dll', import.meta.url));
  const child = spawn('dotnet', [path, server.url, 'probe-tunnel'], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  t.after(() => { if (child.exitCode === null) child.kill(); });
  let output = ''; child.stdout.on('data', bytes => output += bytes); child.stderr.on('data', bytes => output += bytes);
  const result = await new Promise((resolve, reject) => { child.once('error', reject); child.once('exit', resolve); });
  assert.equal(result, 0, output);
});

test('two native clients repeat prefilled emotes, rearm, cancel and reconnect without duplicate input', async t => {
  const server = await createRoomServer({ port: 0 }); t.after(() => server.close());
  const path = fileURLToPath(new URL('./MabiSync.Client.Probe/bin/Release/net8.0/MabiSync.Client.Probe.dll', import.meta.url));
  const child = spawn('dotnet', [path, server.url, 'emote-flow'], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  t.after(() => { if (child.exitCode === null) child.kill(); });
  let output = ''; child.stdout.on('data', bytes => output += bytes); child.stderr.on('data', bytes => output += bytes);
  const result = await new Promise((resolve, reject) => { child.once('error', reject); child.once('exit', resolve); });
  assert.equal(result, 0, output);
  const summary = JSON.parse(output.trim().split(/\r?\n/).at(-1));
  assert.equal(summary.completedRounds, 3);
  assert.equal(summary.cancelledRounds, 2);
  assert.equal(summary.osInputFaked, true);
  assert.equal(summary.desktopInputs, 0);
});

for (const urgent of [false, true]) test(urgent ? 'native immediate stop is executed and accepted by the real Node service' : 'real .NET client completes prepare commit and two simulated inputs through Node service', async t => {
  const server = await createRoomServer({ port: 0 }); t.after(() => server.close());
  const path = fileURLToPath(new URL('./MabiSync.Client.Probe/bin/Release/net8.0/MabiSync.Client.Probe.dll', import.meta.url));
  const child = spawn('dotnet', [path, server.url, ...(urgent ? ['stopnow'] : [])], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  t.after(() => { if (child.exitCode === null) child.kill(); });
  let output = '', error = '';
  child.stdout.on('data', bytes => output += bytes); child.stderr.on('data', bytes => error += bytes);
  const result = await new Promise((resolve, reject) => { child.once('error', reject); child.once('exit', resolve); });
  assert.equal(result, 0, output + error);
  const summary = JSON.parse(output.trim().split(/\r?\n/).at(-1));
  assert.equal(summary.inputCount, 2);
  assert.equal(summary.receipts, 2);
  assert.equal(summary.gameResponseVerified, false);
  assert.equal(summary.simulated, true);
});

test('native local-only music stop executes once and never sends an unknown action receipt to the room', async t => {
  const server = await createRoomServer({ port: 0 }); t.after(() => server.close());
  const path = fileURLToPath(new URL('./MabiSync.Client.Probe/bin/Release/net8.0/MabiSync.Client.Probe.dll', import.meta.url));
  const child = spawn('dotnet', [path, server.url, 'localstop'], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  t.after(() => { if (child.exitCode === null) child.kill(); });
  let output = ''; child.stdout.on('data', bytes => output += bytes); child.stderr.on('data', bytes => output += bytes);
  const result = await new Promise((resolve, reject) => { child.once('error', reject); child.once('exit', resolve); });
  assert.equal(result, 0, output);
  const summary = JSON.parse(output.trim().split(/\r?\n/).at(-1));
  assert.equal(summary.receipts, 1);
  assert.equal(summary.inputCount, 2);
  assert.equal(summary.localStopReported, true);
});
