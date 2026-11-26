// Yappest Stream Deck plugin.
// Talks to Stream Deck over its plugin socket and to the Yappest app over a local WebSocket (127.0.0.1 only).

const YAPPEST_PORTS = [59712, 59713, 59714, 59715, 59716];

const A = {
  sound: 'com.yappest.sound',
  clip: 'com.yappest.clip',
  mute: 'com.yappest.mute',
  stop: 'com.yappest.stop',
  voicechanger: 'com.yappest.voicechanger',
  effect: 'com.yappest.effect',
  hear: 'com.yappest.hear',
};

let sd = null;               // Stream Deck socket
let pluginUUID = null;
const contexts = {};         // context -> { action, settings }
let yp = null;               // Yappest socket
let ypPortIndex = 0;
let ypRetry = null;
let state = { muted: false, voiceChanger: false, effect: 'pitch', hearMyself: false };
let boards = [];
let effects = [];            // [{ id, name }] sent by Yappest

// ---------------- Stream Deck side ----------------
function connectElgatoStreamDeckSocket(inPort, inPluginUUID, inRegisterEvent, inInfo) {
  pluginUUID = inPluginUUID;
  sd = new WebSocket('ws://127.0.0.1:' + inPort);
  sd.onopen = () => {
    sd.send(JSON.stringify({ event: inRegisterEvent, uuid: inPluginUUID }));
    connectYappest();
  };
  sd.onmessage = (evt) => onSdMessage(JSON.parse(evt.data));
}

function sdSend(obj) { if (sd && sd.readyState === 1) sd.send(JSON.stringify(obj)); }
const setState = (context, s) => sdSend({ event: 'setState', context, payload: { state: s } });
const setTitle = (context, title) => sdSend({ event: 'setTitle', context, payload: { title, target: 0 } });
const setImage = (context, image) => sdSend({ event: 'setImage', context, payload: { image: image || null, target: 0 } });
const showAlert = (context) => sdSend({ event: 'showAlert', context });
const toPI = (context, action, payload) => sdSend({ event: 'sendToPropertyInspector', action, context, payload });

function onSdMessage(m) {
  switch (m.event) {
    case 'willAppear':
      contexts[m.context] = { action: m.action, settings: (m.payload && m.payload.settings) || {} };
      refresh(m.context);
      break;
    case 'willDisappear':
      delete contexts[m.context];
      break;
    case 'didReceiveSettings':
      if (contexts[m.context]) { contexts[m.context].settings = m.payload.settings || {}; refresh(m.context); }
      break;
    case 'keyDown':
      onKeyDown(m.context, m.action, (m.payload && m.payload.settings) || {});
      break;
    case 'sendToPlugin':
      if (m.payload && m.payload.request === 'sounds') toPI(m.context, m.action, { boards, effects, connected: !!(yp && yp.readyState === 1) });
      break;
  }
}

function onKeyDown(context, action, settings) {
  if (!yp || yp.readyState !== 1) { showAlert(context); return; }
  switch (action) {
    case A.sound:
      if (settings.soundId) ypSend({ action: 'playSound', id: settings.soundId }); else showAlert(context);
      break;
    case A.clip: ypSend({ action: 'clip' }); break;
    case A.mute: ypSend({ action: 'toggleMute' }); break;
    case A.stop: ypSend({ action: 'stopSounds' }); break;
    case A.voicechanger: ypSend({ action: 'toggleVoiceChanger' }); break;
    case A.effect: ypSend({ action: 'selectEffect', id: settings.effect || 'pitch' }); break;
    case A.hear: ypSend({ action: 'toggleHearMyself' }); break;
  }
}

function effectName(id) {
  const e = effects.find(x => x.id === id);
  return e ? e.name : (id === 'barrier' ? 'Sound Barrier' : 'Pitch');
}

function findSound(id) {
  for (const b of boards) for (const s of b.sounds) if (s.id === id) return s;
  return null;
}

function refresh(context) {
  const c = contexts[context];
  if (!c) return;
  switch (c.action) {
    case A.sound: {
      const s = c.settings.soundId ? findSound(c.settings.soundId) : null;
      if (s) { setTitle(context, s.name); setImage(context, s.icon); }
      else { setImage(context, null); setTitle(context, c.settings.soundId ? 'Missing' : 'Pick a sound'); }
      break;
    }
    case A.mute: setState(context, state.muted ? 1 : 0); break;
    case A.voicechanger: setState(context, state.voiceChanger ? 1 : 0); break;
    case A.hear: setState(context, state.hearMyself ? 1 : 0); break;
    case A.effect: {
      const id = c.settings.effect || 'pitch';
      setTitle(context, effectName(id));
      setState(context, state.voiceChanger && state.effect === id ? 1 : 0);
      break;
    }
  }
}

function refreshAll() { Object.keys(contexts).forEach(refresh); }

// ---------------- Yappest side ----------------
function ypSend(obj) { if (yp && yp.readyState === 1) yp.send(JSON.stringify(obj)); }

function connectYappest() {
  clearTimeout(ypRetry);
  const port = YAPPEST_PORTS[ypPortIndex % YAPPEST_PORTS.length];
  let ws;
  try { ws = new WebSocket('ws://127.0.0.1:' + port); } catch (e) { return scheduleRetry(); }
  let opened = false;
  ws.onopen = () => { opened = true; yp = ws; ws.send(JSON.stringify({ action: 'getState' })); ws.send(JSON.stringify({ action: 'getSounds' })); };
  ws.onmessage = (evt) => {
    const m = JSON.parse(evt.data);
    if (m.event === 'state') {
      state = { muted: m.muted, voiceChanger: m.voiceChanger, effect: m.effect, hearMyself: m.hearMyself };
      if (m.effects) effects = m.effects;
      refreshAll();
    } else if (m.event === 'sounds') {
      boards = m.boards || [];
      refreshAll();
    }
  };
  ws.onclose = () => { if (yp === ws) yp = null; if (!opened) ypPortIndex++; scheduleRetry(); };
  ws.onerror = () => { try { ws.close(); } catch (e) {} };
}

function scheduleRetry() { clearTimeout(ypRetry); ypRetry = setTimeout(connectYappest, 2000); }
