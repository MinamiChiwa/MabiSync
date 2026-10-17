import test from 'node:test';
import assert from 'node:assert/strict';
import { acceptObservation, clearObservation, expireObservations, ensembleFeedback, bindGameCharacter, memberMusic } from '../server/ensemble-observation.js';

test('score summaries remain local, bounded and informational, and expire with observation', () => {
  const s=setup(); const m=s.members.get('a');
  const music={concertId:'7',performerCharacterId:'101',hostCharacterId:'101',title:'Fixture',isLoop:true,
    selectedChannels:[0,1],scoreSha256:'a'.repeat(64),wholeScoreDurationSeconds:34,selectedDurationSeconds:30,
    issue:'起点待核对',automaticStopAvailable:true,gameStartLocalMs:1,mml:'PRIVATE'};
  const msg={generation:'g',sequence:2,status:'identified',identity:m.gameObservation.identity,music};
  acceptObservation(m,msg,100);
  assert.equal(memberMusic(m,100).wholeScoreDurationSeconds,34);
  assert.equal(memberMusic(m,100).automaticStopAvailable,false);
  assert.equal(memberMusic(m,100).gameStartLocalMs,null);
  assert.equal(memberMusic(m,100).mml,undefined);
  assert.equal(memberMusic(m,15101),null);
  for (const bad of [{performerCharacterId:'102'},{selectedChannels:[0,0]},{wholeScoreDurationSeconds:Infinity},{title:'a'.repeat(513)}]) {
    assert.throws(()=>acceptObservation(m,{...msg,sequence:3,music:{...music,...bad}},100),/BAD_OBSERVATION/);
    assert.equal(m.gameObservation.sequence,2);
  }
  acceptObservation(m,{...msg,sequence:3,music:null},100); assert.equal(memberMusic(m,100),null);
});

test('long score durations above 200 seconds survive room observation without truncation', () => {
  const s=setup(), m=s.members.get('a');
  const music={concertId:'7',performerCharacterId:'101',hostCharacterId:'101',title:'Long fixture',isLoop:true,
    selectedChannels:[0],scoreSha256:'a'.repeat(64),wholeScoreDurationSeconds:601.25,selectedDurationSeconds:200.5,
    estimatedStartServerMs:100,timingSource:'tw-receive-offset-v1'};
  acceptObservation(m,{generation:'g',sequence:2,status:'identified',identity:m.gameObservation.identity,music},100);
  assert.equal(memberMusic(m,100).wholeScoreDurationSeconds,601.25);
  assert.equal(memberMusic(m,100).selectedDurationSeconds,200.5);
  assert.equal(memberMusic(m,100).automaticStopAvailable,true);
});

function setup() {
  const members = new Map(['a','b','c'].map(id => [id, { id, nickname: id, ws: {}, ready: true }]));
  const room = { members, performance: null };
  const roster = ['a','b','c'].map((memberId,i) => ({ memberId, group: 1, role: i === 0 ? 'initiator' : 'joiner', order:i+1 }));
  const report = (id, ordered, sequence=1, generation='g') => acceptObservation(members.get(id), { type:'game-observation', generation, sequence, status:'identified',
    identity:{characterId:String({a:101,b:102,c:103}[id]),realmId:3,nickname:'SameName'},
    ...(ordered ? { concert:{id:'7',hostCharacterId:'101',orderedCharacterIds:ordered.map(String)} } : {}) }, 100);
  ['a','b','c'].forEach(id => report(id));
  return { room, roster, report, members, states: () => ensembleFeedback(room, roster, 100) };
}
test('automatic identity remains member-owned and does not consume emote readiness', () => {
  const s = setup(); assert.equal(s.members.get('a').nickname,'SameName');
  assert.ok([...s.members.values()].every(m=>m.ready));
  assert.throws(()=>s.report('a',[101,102,102],2),/BAD_OBSERVATION/);
  assert.equal(s.members.get('a').gameObservation.sequence,1);
});
test('join prefixes, wrong order and manual join waiting are distinct', () => {
  const s=setup(); s.report('a',[101,102],2);
  assert.equal(s.states().get('a').state,'correct'); assert.equal(s.states().get('b').state,'correct'); assert.equal(s.states().get('c').state,'pending');
  s.report('a',[101,103,102],3);
  assert.equal(s.states().get('a').state,'correct'); assert.equal(s.states().get('b').state,'wrong'); assert.equal(s.states().get('c').state,'wrong');
});
test('foreign actors, conflicting reports and duplicate persistent identities cannot stay green', () => {
  const s=setup(); s.report('a',[101,102],2); s.report('b',[101,103,102],2);
  assert.equal(s.states().get('a').state,'unconfirmed');
  s.report('b',undefined,3); s.report('a',[101,999,102],3); assert.equal(s.states().get('b').state,'unconfirmed');
  s.report('a',[101,102],4); s.members.get('c').gameObservation.identity.characterId='102';
  assert.equal(s.states().get('b').state,'unconfirmed');
});
test('new generation supersedes old messages, unavailable and disconnect clear sources', () => {
  const s=setup(); s.report('a',[101,102],2,'new');
  assert.throws(()=>s.report('a',[101,102],1,'g'),/STALE_OBSERVATION/);
  assert.throws(()=>s.report('a',[101,102],99,'g'),/STALE_OBSERVATION/);
  acceptObservation(s.members.get('a'),{generation:'new',sequence:3,status:'unavailable'},100);
  assert.equal(s.states().get('b').state,'unconfirmed');
  s.report('a',[101,102],4,'new'); clearObservation(s.members.get('a')); assert.equal(s.states().get('b').state,'unconfirmed');
});
test('new room round cannot reuse a concert already observed before preparation', () => {
  const s=setup(); s.report('a',[101,102],2);
  s.room.performance={mode:'ensemble',phase:'performing',ensembleRoster:s.roster, observationBaseline:['3:7:101']};
  assert.equal(s.states().get('a').state,'unconfirmed');
});
test('stale observation expires and frozen session roster ignores later edits', () => {
  const s=setup(); s.room.performance={mode:'ensemble',phase:'performing',ensembleRoster:s.roster};
  s.report('a',[101,103,102],2); const changed=s.roster.map(r=>({...r,order:4-r.order}));
  assert.equal(ensembleFeedback(s.room,changed,100).get('b').state,'wrong');
  assert.ok(expireObservations(s.room,15101)); assert.equal(s.states().get('a').state,'unconfirmed');
});
test('separate groups do not share a concert and emotes have no join feedback', () => {
  const s=setup(); s.roster[2]={...s.roster[2],group:2,role:'initiator',order:1};
  s.report('a',[101,102],2); assert.equal(s.states().get('c').state,'unconfirmed');
  s.room.performance={mode:'emote',phase:'committed'};
  assert.ok([...s.states().values()].every(v=>v.state==='unconfirmed'));
});
test('host explicitly associates a driver-free participant using observed identity, never name matching', () => {
  const s=setup(); clearObservation(s.members.get('b')); clearObservation(s.members.get('c'));
  acceptObservation(s.members.get('a'),{generation:'g',sequence:2,status:'identified',identity:{characterId:'101',realmId:3,nickname:'Local'},
    concert:{id:'7',hostCharacterId:'101',orderedCharacterIds:['101','102'],members:[{characterId:'101',realmId:3,nickname:'Local'},{characterId:'102',realmId:3,nickname:'Other'}]}},100);
  s.roster=s.roster.slice(0,2);
  assert.equal(ensembleFeedback(s.room,s.roster,100).get('b').state,'unconfirmed');
  bindGameCharacter(s.room,'b','102',3,100);
  assert.equal(ensembleFeedback(s.room,s.roster,100).get('b').state,'correct');
  assert.equal(s.members.get('b').nickname,'Other');
  clearObservation(s.members.get('a'));
  assert.equal(ensembleFeedback(s.room,s.roster,100).get('b').state,'unconfirmed');
  assert.throws(()=>bindGameCharacter(s.room,'c','999',3,100),/CHARACTER_NOT_OBSERVED/);
});
