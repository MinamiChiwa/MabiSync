// Passive evidence reported by clients. Host-selected session policy controls stop scheduling.
const MAX_ID = 18446744073709551615n;
const bad = code => { throw Object.assign(new Error(code), { code }); };
function id(value) {
  if (typeof value !== 'string' || !/^[1-9][0-9]{0,19}$/.test(value) || BigInt(value) > MAX_ID) bad('BAD_OBSERVATION');
  return value;
}
function identityValue(value) {
  if (!value || !Number.isInteger(value.realmId) || value.realmId < 0 || value.realmId > 2147483647 ||
      typeof value.nickname !== 'string' || !value.nickname.trim() || [...value.nickname.trim()].length > 20 || /[\u0000-\u001f\u007f]/u.test(value.nickname)) bad('BAD_OBSERVATION');
  return {characterId:id(value.characterId),realmId:value.realmId,nickname:value.nickname.trim()};
}
function musicValue(value, identity) {
  if (value === undefined || value === null) return null;
  const duration = v => {
    if (v === null || v === undefined) return null;
    if (typeof v !== 'number' || !Number.isFinite(v) || v < 0 || v > 7*24*3600) bad('BAD_OBSERVATION');
    return v;
  };
  if (id(value.performerCharacterId)!==identity.characterId || typeof value.title!=='string' || value.title.length>512 ||
      /[\u0000-\u001f\u007f]/u.test(value.title) || typeof value.isLoop!=='boolean' ||
      !Array.isArray(value.selectedChannels) || value.selectedChannels.length>128 ||
      new Set(value.selectedChannels).size!==value.selectedChannels.length || value.selectedChannels.some(i=>!Number.isInteger(i)||i<0||i>=128) ||
      (value.scoreSha256!=null && (typeof value.scoreSha256!=='string' || !/^[a-f0-9]{64}$/.test(value.scoreSha256))) ||
      (value.issue!=null && (typeof value.issue!=='string' || value.issue.length>256))) bad('BAD_OBSERVATION');
  const whole=duration(value.wholeScoreDurationSeconds), selected=duration(value.selectedDurationSeconds);
  if (selected!==null && (whole===null || selected>whole+.000001)) bad('BAD_OBSERVATION');
  const estimated=value.estimatedStartServerMs??null;
  if (estimated!==null && (!Number.isFinite(estimated) || typeof estimated!=='number' || value.timingSource!=='tw-receive-offset-v1')) bad('BAD_OBSERVATION');
  return {concertId:id(value.concertId),performerCharacterId:identity.characterId,hostCharacterId:id(value.hostCharacterId),
    title:value.title,isLoop:value.isLoop,selectedChannels:[...value.selectedChannels],scoreSha256:value.scoreSha256??null,
    wholeScoreDurationSeconds:whole,selectedDurationSeconds:selected,issue:value.issue??null,
    estimatedStartServerMs:estimated,timingSource:estimated===null?null:'tw-receive-offset-v1',
    automaticStopAvailable:estimated!==null && whole>0 && whole<=86400 && selected!==null,gameStartLocalMs:null};
}
export function clearObservation(member) { member.gameObservation = null; member.gameBinding=null; member.observationSequence = 0; member.observationGeneration = null; member.retiredObservationGenerations = new Set(); }
export function acceptObservation(member, msg, now) {
  if (typeof msg.generation !== 'string' || !msg.generation.length || msg.generation.length > 64 ||
      !Number.isSafeInteger(msg.sequence) || msg.sequence < 1 || !['identified','unavailable'].includes(msg.status)) bad('BAD_OBSERVATION');
  // The transport sequence is monotonic across all capture generations, bounded O(1).
  // Reset only on a new WebSocket attachment, which discards all previous observation.
  if (msg.sequence <= (member.observationSequence ?? 0)) bad('STALE_OBSERVATION');
  const retired = member.retiredObservationGenerations ?? new Set();
  if (retired.has(msg.generation)) bad('STALE_OBSERVATION');
  if (member.observationGeneration && member.observationGeneration !== msg.generation && retired.size >= 1024) {
    member.gameObservation = null; bad('OBSERVATION_RECONNECT_REQUIRED');
  }
  let observation = null;
  if (msg.status === 'identified') {
    const identity = identityValue(msg.identity);
    let concert = null;
    if (msg.concert !== undefined && msg.concert !== null) {
      const c = msg.concert; const ordered = c.orderedCharacterIds;
      if (!Array.isArray(ordered) || ordered.length < 1 || ordered.length > 128 || new Set(ordered).size !== ordered.length) bad('BAD_OBSERVATION');
      concert = { id:id(c.id), hostCharacterId:id(c.hostCharacterId), orderedCharacterIds:ordered.map(id) };
      if (ordered[0] !== concert.hostCharacterId || !ordered.includes(identity.characterId)) bad('BAD_OBSERVATION');
      if (c.members !== undefined) {
        if (!Array.isArray(c.members) || c.members.length!==ordered.length) bad('BAD_OBSERVATION');
        concert.members=c.members.map(identityValue);
        if (concert.members.some((m,i)=>m.characterId!==ordered[i] || m.realmId!==identity.realmId)) bad('BAD_OBSERVATION');
      }
    }
    if (msg.sessionId!=null && (typeof msg.sessionId!=='string' || msg.sessionId.length>64)) bad('BAD_OBSERVATION');
    observation = { generation:msg.generation, sequence:msg.sequence, at:now, identity, concert, music:musicValue(msg.music,identity),sessionId:msg.sessionId??null };
  }
  // Commit after every field has passed validation. Cannot rename another member.
  if (member.observationGeneration && member.observationGeneration !== msg.generation) retired.add(member.observationGeneration);
  member.retiredObservationGenerations = retired;
  member.observationSequence = msg.sequence; member.observationGeneration = msg.generation; member.gameObservation = observation;
  if (observation) member.nickname = observation.identity.nickname;
}
const fresh = (member, now) => !!member.ws && member.gameObservation && now - member.gameObservation.at <= 15000 && now >= member.gameObservation.at;
export function memberMusic(member, now) { return fresh(member,now) ? member.gameObservation.music??null : null; }
export function observedCharacters(room, now) {
  const found=new Map(), conflicts=new Set();
  for (const m of room.members.values()) if (fresh(m,now)) for (const identity of m.gameObservation.concert?.members??[]) {
    const key=identity.realmId+':'+identity.characterId;
    if (found.has(key) && found.get(key).nickname!==identity.nickname) conflicts.add(key);
    found.set(key,identity);
  }
  return [...found].filter(([key])=>!conflicts.has(key)).map(([,value])=>value).slice(0,128);
}
export function memberIdentity(member, room, now, observed=observedCharacters(room,now)) {
  if (!member.ws) return null;
  if (fresh(member,now)) return member.gameObservation.identity;
  const binding=member.gameBinding;
  return binding ? observed.find(i=>i.characterId===binding.characterId && i.realmId===binding.realmId)??null : null;
}
export function bindGameCharacter(room, memberId, characterId, realmId, now) {
  const member=room.members.get(memberId); if (!member) bad('MEMBER_NOT_FOUND');
  if (characterId===null) { member.gameBinding=null; return; }
  const identity=observedCharacters(room,now).find(i=>i.characterId===characterId && i.realmId===realmId);
  if (!identity) bad('CHARACTER_NOT_OBSERVED');
  if (fresh(member,now) && (member.gameObservation.identity.characterId!==characterId || member.gameObservation.identity.realmId!==realmId)) bad('CAPTURE_IDENTITY_CONFLICT');
  member.gameBinding={characterId:identity.characterId,realmId:identity.realmId}; member.nickname=identity.nickname;
}
export function expireObservations(room, now) {
  let changed = false;
  for (const m of room.members.values()) if (m.gameObservation && !fresh(m,now)) { m.gameObservation=null; changed=true; }
  return changed;
}
export function ensembleFeedback(room, layout, now) {
  const result = new Map([...room.members.keys()].map(key => [key,{state:'unconfirmed',text:'等待识别加入顺序'}]));
  const p = room.performance;
  const active = p && !['cancelled','stopped'].includes(p.phase);
  if (active && p.mode !== 'ensemble') return result;
  const planned = active ? p.ensembleRoster ?? [] : layout;
  const observed=observedCharacters(room,now);
  const identities=new Map([...room.members.values()].map(m=>[m.id,memberIdentity(m,room,now,observed)]));
  const identityMembers = new Map();
  for (const m of room.members.values()) if (identities.get(m.id)) {
    const i=identities.get(m.id), key=i.realmId+':'+i.characterId;
    const owners=identityMembers.get(key)??[]; owners.push(m); identityMembers.set(key,owners);
  }
  for (const group of new Set(planned.map(r=>r.group))) {
    const rows=planned.filter(r=>r.group===group).sort((a,b)=>(a.role==='initiator'?0:1)-(b.role==='initiator'?0:1)||a.order-b.order);
    if (rows.filter(r=>r.role==='initiator').length!==1) continue;
    const host=room.members.get(rows[0].memberId);
    if (!host || !identities.get(host.id)) continue;
    const identity=identities.get(host.id), realm=identity.realmId;
    if (identityMembers.get(realm+':'+identity.characterId)?.length!==1) continue;
    const expected=[];
    for (const row of rows) {
      const m=room.members.get(row.memberId);
      const actualIdentity=m ? identities.get(m.id) : null;
      if (!actualIdentity || actualIdentity.realmId!==realm || identityMembers.get(realm+':'+actualIdentity.characterId)?.length!==1) { expected.length=0; break; }
      expected.push(actualIdentity.characterId);
    }
    if (!expected.length) continue;
    const reports=rows.map(r=>room.members.get(r.memberId)).filter(m=>m && fresh(m,now)).map(m=>m.gameObservation).filter(o=>o?.concert);
    if (!reports.length) continue;
    // Even an in-group report of another concert is conflicting evidence, not a vote.
    if (reports.some(o=>o.identity.realmId!==realm || o.concert.hostCharacterId!==identity.characterId)) continue;
    const key=o=>JSON.stringify([o.concert.id,o.concert.hostCharacterId,o.concert.orderedCharacterIds]);
    const signature=key(reports[0]);
    if (reports.some(o=>key(o)!==signature)) continue;
    const actual=reports[0].concert.orderedCharacterIds;
    if (p?.observationBaseline?.includes(realm+':'+reports[0].concert.id+':'+reports[0].concert.hostCharacterId)) continue;
    if (actual.some(value=>!expected.includes(value))) continue;
    rows.forEach((row,index)=> {
      const at=actual.indexOf(expected[index]);
      result.set(row.memberId, at<0 ? {state:'pending',text:'尚未加入'} :
        {state:at===index?'correct':'wrong',text:at===index?'加入顺序正确':'加入顺序不符',actualOrder:at+1});
    });
  }
  return result;
}
