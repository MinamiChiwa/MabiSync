import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const read = relative => fs.readFileSync(path.join(root, relative), 'utf8');

test('music UI exposes one ensemble mode and no local MML estimator', () => {
  const xaml = read('src/MabiSync.App/MainWindow.xaml');
  assert.match(xaml, /ComboBoxItem Content="表情动作" Tag="emote"/);
  assert.match(xaml, /ComboBoxItem Content="合奏" Tag="ensemble"/);
  assert.doesNotMatch(xaml, /Tag="independent"/);
  assert.doesNotMatch(xaml, /乐谱时长估算|导入 MML|采用估算时长/);
});

test('observation actions are accessible next to the nickname before joining a room', () => {
  const xaml = read('src/MabiSync.App/MainWindow.xaml');
  const connectionArea = xaml.slice(xaml.indexOf('x:Name="RoomControls"'), xaml.indexOf('x:Name="AdvancedConnection"'));
  assert.match(connectionArea, /x:Name="CaptureChannelsButton" Content="抓包校对"/);
  assert.match(connectionArea, /x:Name="RefreshObservationButton" Content="更新昵称"/);
  assert.doesNotMatch(connectionArea, /x:Name="ObservationControls" Visibility="Collapsed"/);
});

test('preview build discovers an available cloudflared runtime', () => {
  const script = read('tools/Build-Preview.ps1');
  assert.match(script, /Get-Command cloudflared/);
  assert.match(script, /outputs[\\/]previews/);
});
