"use strict";

function post(obj) {
  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.postMessage(JSON.stringify(obj));
  }
}

const params = new URLSearchParams(location.search);
const SRC = params.get("src") || "";
const START_T = parseFloat(params.get("t")) || 0;
const MODE = params.get("mode") === "fullscreen" ? "fullscreen" : "inline";
let currentMode = MODE;

const v = document.getElementById("v");
const closeBtn = document.getElementById("closeBtn");
const bigPlay = document.getElementById("bigPlay");
const bigReplay = document.getElementById("bigReplay");
const errorBox = document.getElementById("error");
const retryBtn = document.getElementById("retry");
const playPause = document.getElementById("playPause");
const icPlay = document.getElementById("icPlay");
const icPause = document.getElementById("icPause");
const cur = document.getElementById("cur");
const dur = document.getElementById("dur");
const seek = document.getElementById("seek");
const mute = document.getElementById("mute");
const icWaveLow = document.getElementById("icWaveLow");
const icWaveHigh = document.getElementById("icWaveHigh");
const icMuted = document.getElementById("icMuted");
const vol = document.getElementById("vol");
const speed = document.getElementById("speed");
const fsBtn = document.getElementById("fs");
const icExpand = document.getElementById("icExpand");
const icCompress = document.getElementById("icCompress");
const cropLayer = document.getElementById("cropLayer");
const cropBox = document.getElementById("cropBox");
const cropTag = document.getElementById("cropTag");

function setPlaybackSpeed(rate) {
  v.defaultPlaybackRate = rate;
  v.playbackRate = rate;
  speed.value = String(rate);
}

speed.addEventListener("change", () => setPlaybackSpeed(Number(speed.value)));

let scrubbing = false;
let mutedByUser = null;
let lastAudibleVolume = 1;
let editMode = false;
let trimStart = 0;
let trimEnd = Infinity;
let cropRatio = null;
let crop = { x: 0, y: 0, w: 0, h: 0 };
let cropInitialized = false;
let dragState = null;

function fmt(t) {
  if (!isFinite(t) || t < 0) t = 0;
  t = Math.floor(t);
  const h = Math.floor(t / 3600);
  const m = Math.floor((t % 3600) / 60);
  const s = t % 60;
  const pad = (n) => (n < 10 ? "0" + n : "" + n);
  return h > 0 ? `${h}:${pad(m)}:${pad(s)}` : `${m}:${pad(s)}`;
}

function updateSeekFill() {
  const max = parseFloat(seek.max) || 0;
  const pct = max > 0 ? (parseFloat(seek.value) / max) * 100 : 0;
  seek.style.background =
    `linear-gradient(to right, var(--accent) ${pct}%, rgba(255,255,255,0.25) ${pct}%)`;
}

function updateVolFill() {
  const pct = (parseFloat(vol.value) || 0) * 100;
  vol.style.background =
    `linear-gradient(to right, var(--accent) ${pct}%, rgba(255,255,255,0.25) ${pct}%)`;
}

function updateIcons() {
  icPlay.style.display = v.paused ? "" : "none";
  icPause.style.display = v.paused ? "none" : "";
  const muted = v.muted || v.volume <= 0.004;
  if (v.volume > 0.004) lastAudibleVolume = v.volume;
  icMuted.style.opacity = muted ? "1" : "0";
  icWaveLow.style.opacity = muted ? "0" : String(Math.min(1, v.volume * 2));
  icWaveHigh.style.opacity = muted ? "0" : String(Math.max(0, v.volume * 2 - 1));
  const action = muted ? "unmute" : "mute";
  mute.title = `${action} (m)`;
  mute.setAttribute("aria-label", action);
  mute.setAttribute("aria-pressed", String(muted));

  if (currentMode === "fullscreen") {
    icExpand.style.display = "none";
    icCompress.style.display = "";
  } else {
    icExpand.style.display = "";
    icCompress.style.display = "none";
  }

  bigReplay.hidden = !v.ended;
  bigPlay.hidden = v.ended || !v.paused;
  document.body.classList.toggle("paused", v.paused && !v.ended && !v.srcError);
}

function postState() {
  post({
    type: "state",
    width: v.videoWidth,
    height: v.videoHeight,
    playing: !v.paused && !v.ended && v.readyState >= 2,
    t: v.currentTime,
    d: isFinite(v.duration) ? v.duration : 0,
    v: v.volume,
    m: v.muted || v.volume <= 0.004,
    ended: v.ended,
    seeking: v.seeking,
    ready: v.readyState >= 1 || (isFinite(v.duration) && v.duration > 0),
    err: v.srcError ? "video-error" : null,
  });
}

let progressRafId = 0;
function progressLoop() {
  progressRafId = 0;
  if (!scrubbing && isFinite(v.duration) && v.duration > 0) {
    seek.value = v.currentTime;
    updateSeekFill();
    cur.textContent = fmt(v.currentTime);
  }
  if (!v.paused && !v.ended && !v.srcError) {
    progressRafId = requestAnimationFrame(progressLoop);
  }
}
function startProgressLoop() {
  if (!progressRafId && !v.paused && !v.ended && !v.srcError) {
    progressRafId = requestAnimationFrame(progressLoop);
  }
}
function stopProgressLoop() {
  if (progressRafId) {
    cancelAnimationFrame(progressRafId);
    progressRafId = 0;
  }
}

v.addEventListener("play", () => { postState(); updateIcons(); activity(); startProgressLoop(); });
v.addEventListener("pause", () => { stopProgressLoop(); postState(); updateIcons(); clearIdle(); });
v.addEventListener("ended", () => { stopProgressLoop(); postState(); updateIcons(); clearIdle(); });
v.addEventListener("volumechange", () => { postState(); updateVolFill(); updateIcons(); });
v.addEventListener("loadedmetadata", () => {
  if (isFinite(v.duration) && v.duration > 0) {
    seek.max = v.duration;
    dur.textContent = fmt(v.duration);
  } else {
    seek.disabled = true;
    dur.textContent = "--:--";
  }
  resetCropState(false);
  if (START_T > 0) v.currentTime = START_T;
  updateSeekFill();
  updateIcons();
  postState();
  if (editMode) {
    syncCropLayout();
    postCrop();
  }
});
v.addEventListener("durationchange", () => {
  if (isFinite(v.duration) && v.duration > 0) {
    seek.disabled = false;
    seek.max = v.duration;
    dur.textContent = fmt(v.duration);
    updateSeekFill();
  }
});
let frameSignaled = false;
function signalVideoReady() {
  if (frameSignaled) return;
  frameSignaled = true;
  requestAnimationFrame(() => {
    document.body.classList.add("video-ready");
    post({ type: "videoReady" });
    activity();
  });
}

v.addEventListener("loadeddata", signalVideoReady);
v.addEventListener("playing", signalVideoReady);
v.addEventListener("timeupdate", () => {
  if (v.readyState >= 2) signalVideoReady();
  if (editMode && Number.isFinite(trimEnd) && v.currentTime >= trimEnd - 0.015) {
    if (!v.paused) v.pause();
    if (Math.abs(v.currentTime - trimEnd) > 0.02) v.currentTime = trimEnd;
    postState();
  }
  if (!scrubbing) {
    if (isFinite(v.duration) && v.duration > 0) seek.value = v.currentTime;
    updateSeekFill();
    cur.textContent = fmt(v.currentTime);
  }
});
v.addEventListener("error", () => {
  v.srcError = true;
  const code = v.error ? v.error.code : 0;
  const message = v.error ? (v.error.message || "") : "";
  errorBox.hidden = false;
  updateIcons();
  post({ type: "videoError", code, message, src: v.currentSrc || v.src || "" });
  postState();
  signalVideoReady();
});

setInterval(() => {
  if (editMode && !v.paused) {
    if (Number.isFinite(trimEnd) && v.currentTime >= trimEnd - 0.015) {
      v.pause();
      v.currentTime = trimEnd;
    }
    postState();
  }
}, 60);

setInterval(postState, 250);

function togglePlay() {
  if (editMode) {
    if (v.paused || v.ended) {
      const startBound = Math.max(0, trimStart || 0);
      const endBound = Number.isFinite(trimEnd) ? trimEnd : (v.duration || 0);
      if (v.currentTime < startBound || v.currentTime >= endBound - 0.04) {
        v.currentTime = startBound;
      }
      v.play();
    } else {
      v.pause();
    }
    return;
  }
  if (v.ended) {
    v.currentTime = 0;
    v.play();
  } else if (v.paused) {
    v.play();
  } else {
    v.pause();
  }
}

playPause.addEventListener("click", togglePlay);
bigPlay.addEventListener("click", togglePlay);
bigReplay.addEventListener("click", togglePlay);

document.addEventListener("click", (e) => {
  if (editMode) return;
  if (e.target.closest("#bar, #closeBtn, .big, #error, #cropLayer")) return;
  togglePlay();
});

retryBtn.addEventListener("click", () => {
  errorBox.hidden = true;
  delete v.srcError;
  v.load();
  tryPlay();
});

closeBtn.addEventListener("click", () => {
  postState();
  post({ type: "close" });
});

[playPause, bigPlay, bigReplay, mute, fsBtn, closeBtn, retryBtn].forEach((btn) => {
  if (!btn) return;
  btn.addEventListener("pointerup", () => { try { btn.blur(); } catch (_) { } });
  btn.addEventListener("click", (e) => {
    if (e.detail > 0) try { btn.blur(); } catch (_) { }
  });
});

let scrubTarget = null;
let scrubTimer = null;
let lastScrubSeekAt = 0;
let resumeAfterScrub = false;
let pendingEditSeek = null;
let pendingEditPreview = false;
const hasFastSeek = typeof v.fastSeek === "function";

function cancelScrubState() {
  if (scrubTimer) { clearTimeout(scrubTimer); scrubTimer = null; }
  scrubbing = false;
  scrubTarget = null;
  resumeAfterScrub = false;
  pendingEditSeek = null;
}

function flushScrubPreview() {
  scrubTimer = null;
  if (!scrubbing || scrubTarget === null) return;
  if (v.seeking) return;
  const t = scrubTarget;
  if (Math.abs(v.currentTime - t) < 0.03) return;
  lastScrubSeekAt = performance.now();
  if (hasFastSeek && isFinite(v.duration) && v.duration > 6) {
    try { v.fastSeek(t); } catch (_) { v.currentTime = t; }
  } else {
    v.currentTime = t;
  }
}

function beginScrub() {
  if (scrubbing) return;
  scrubbing = true;
  stopProgressLoop();
  resumeAfterScrub = !v.paused && !v.ended;
  if (resumeAfterScrub) {
    try { v.pause(); } catch (_) { }
  }
}

function finishScrub() {
  if (!scrubbing) return;
  if (scrubTimer) { clearTimeout(scrubTimer); scrubTimer = null; }
  const target = scrubTarget !== null ? scrubTarget : parseFloat(seek.value);
  const shouldResume = resumeAfterScrub;
  scrubbing = false;
  scrubTarget = null;
  resumeAfterScrub = false;
  if (Number.isFinite(target)) {
    if (Math.abs(v.currentTime - target) >= 0.01) v.currentTime = target;
    if (isFinite(v.duration) && v.duration > 0) seek.value = target;
    updateSeekFill();
    cur.textContent = fmt(target);
  }
  if (shouldResume && !editMode) {
    tryPlay();
  } else {
    startProgressLoop();
  }
  postState();
}

seek.addEventListener("pointerdown", () => {
  beginScrub();
});
seek.addEventListener("input", () => {
  beginScrub();
  const target = parseFloat(seek.value);
  if (!Number.isFinite(target)) return;
  scrubTarget = target;
  updateSeekFill();
  cur.textContent = fmt(target);
  if (!v.seeking && performance.now() - lastScrubSeekAt >= 40) {
    if (scrubTimer) { clearTimeout(scrubTimer); scrubTimer = null; }
    flushScrubPreview();
  } else if (!scrubTimer && !v.seeking) {
    scrubTimer = setTimeout(flushScrubPreview, 32);
  }
});
seek.addEventListener("change", () => {
  finishScrub();
  try { seek.blur(); } catch (_) { }
});
v.addEventListener("seeked", () => {
  if (scrubbing && scrubTarget !== null) {
    if (scrubTimer) { clearTimeout(scrubTimer); scrubTimer = null; }
    flushScrubPreview();
  } else if (editMode && pendingEditSeek !== null) {
    const nextT = pendingEditSeek;
    const isPrev = pendingEditPreview;
    pendingEditSeek = null;
    applyEditSeek(nextT, isPrev);
  }
});
window.addEventListener("pointerup", finishScrub);
window.addEventListener("pointercancel", finishScrub);
window.addEventListener("blur", finishScrub);

function applyEditSeek(targetSec, isPreview) {
  const clamped = Math.max(0, Math.min(isFinite(v.duration) ? v.duration : targetSec, targetSec));
  if (v.seeking) {
    pendingEditSeek = clamped;
    pendingEditPreview = !!isPreview;
    return;
  }
  if (isPreview && hasFastSeek && isFinite(v.duration) && v.duration > 6) {
    try { v.fastSeek(clamped); } catch (_) { v.currentTime = clamped; }
  } else {
    v.currentTime = clamped;
  }
}

vol.addEventListener("input", () => {
  v.volume = parseFloat(vol.value);
  if (v.volume > 0) {
    if (v.muted) v.muted = false;
    mutedByUser = false;
  } else {
    mutedByUser = true;
  }
  updateVolFill();
  updateIcons();
  try { localStorage.setItem("ir_vol", String(v.volume)); localStorage.setItem("ir_muted", v.muted ? "1" : "0"); } catch (_) { }
  postState();
});

function toggleMute() {
  const wasMuted = v.muted || v.volume <= 0.004;
  if (wasMuted) {
    if (v.volume <= 0.004) { v.volume = lastAudibleVolume > 0.004 ? lastAudibleVolume : 1; vol.value = v.volume; }
    v.muted = false;
  } else v.muted = true;
  mutedByUser = v.muted;
  try { localStorage.setItem("ir_muted", v.muted ? "1" : "0"); localStorage.setItem("ir_vol", String(v.volume)); } catch (_) { }
  updateVolFill(); updateIcons(); postState();
}
mute.addEventListener("click", toggleMute);
fsBtn.addEventListener("click", toggleFullscreen);

function toggleFullscreen() {
  postState();
  if (currentMode === "fullscreen") post({ type: "fs", act: "exit" });
  else post({ type: "fs", act: "enter" });
}

document.addEventListener("keydown", (e) => {
  if (e.target === speed) return;
  if (e.key === "Escape") {
    e.preventDefault();
    post({ type: "close" });
    return;
  }

  const tag = (e.target && e.target.tagName) || "";
  if (tag === "INPUT" || tag === "TEXTAREA") return;
  switch (e.key.toLowerCase()) {
    case " ":
    case "k":
      e.preventDefault();
      togglePlay();
      break;
    case "arrowright":
      e.preventDefault();
      cancelScrubState();
      v.currentTime = Math.min(v.currentTime + 5, isFinite(v.duration) ? v.duration : v.currentTime + 5);
      if (isFinite(v.duration) && v.duration > 0) seek.value = v.currentTime;
      updateSeekFill();
      cur.textContent = fmt(v.currentTime);
      break;
    case "arrowleft":
      e.preventDefault();
      cancelScrubState();
      v.currentTime = Math.max(v.currentTime - 5, 0);
      if (isFinite(v.duration) && v.duration > 0) seek.value = v.currentTime;
      updateSeekFill();
      cur.textContent = fmt(v.currentTime);
      break;
    case "m":
      mute.click();
      break;
    case "f":
      toggleFullscreen();
      break;
  }
});

let idleTimer = null;
function clearIdle() {
  if (idleTimer) {
    clearTimeout(idleTimer);
    idleTimer = null;
  }
  document.body.classList.remove("idle");
}
function activity() {
  document.body.classList.remove("idle");
  if (idleTimer) clearTimeout(idleTimer);
  idleTimer = setTimeout(() => {
    if (!v.paused && !v.ended && !v.srcError && errorBox.hidden && !scrubbing && !editMode &&
        !document.querySelector("#bar:hover, #bar :focus-visible, #closeBtn:hover, #closeBtn:focus-visible")) {
      document.body.classList.add("idle");
    }
  }, 2000);
}
["pointermove", "pointerdown", "keydown", "wheel", "focusin", "focusout"].forEach((ev) =>
  document.addEventListener(ev, activity, { passive: true })
);

function applyStoredVolume() {
  try {
    const sv = parseFloat(localStorage.getItem("ir_vol") || "1");
    if (isFinite(sv) && sv >= 0 && sv <= 1) {
      v.volume = sv;
      vol.value = sv;
      updateVolFill();
    }
    if (localStorage.getItem("ir_muted") === "1") {
      v.muted = true;
      mutedByUser = true;
    }
  } catch (_) { }
  updateIcons();
}

function tryPlay() {
  const p = v.play();
  if (!p) return;
  p.catch(() => {
    if (mutedByUser === true) return;
    v.muted = true;
    const retry = v.play();
    if (!retry) return;
    retry
      .then(() => {
        if (mutedByUser !== true) {
          v.muted = false;
          v.dispatchEvent(new Event("volumechange"));
          updateVolFill();
        }
      })
      .catch(() => { });
  });
}

document.body.classList.add(MODE);
try { window.focus(); } catch (_) { }
updateSeekFill();
updateVolFill();
updateIcons();

if (!SRC) {
  errorBox.querySelector("p").textContent = "no media source was provided.";
  errorBox.hidden = false;
} else {
  v.src = SRC;
  applyStoredVolume();
  updateIcons();
  const volume = Number(params.get("volume"));
  if (params.has("volume") && Number.isFinite(volume)) { v.volume = Math.max(0, Math.min(1, volume)); vol.value = v.volume; }
  if (params.has("muted")) { v.muted = params.get("muted") === "1"; mutedByUser = v.muted; }
  updateVolFill(); updateIcons();
  if (params.get("autoplay") !== "0") tryPlay();
}

window.receiveCommand = function (cmd) {
  if (!cmd) return;
  try {
    switch (cmd.c) {
      case "load":
        frameSignaled = false;
        dragState = null;
        cancelScrubState();
        stopProgressLoop();
        editMode = false;
        trimStart = 0;
        trimEnd = Infinity;
        cropRatio = null;
        cropInitialized = false;
        clearIdle();
        document.body.classList.remove("video-ready", "paused", "idle", "editing");
        cropLayer.hidden = true;
        errorBox.hidden = true;
        delete v.srcError;
        seek.value = 0;
        seek.max = 0;
        cur.textContent = "0:00";
        dur.textContent = "0:00";
        updateSeekFill();
        if (typeof cmd.src === "string" && cmd.src.length > 0) {
          setPlaybackSpeed(1);
          v.src = cmd.src;
          if (Number.isFinite(cmd.v)) { v.volume = Math.max(0, Math.min(1, cmd.v)); vol.value = v.volume; }
          if (typeof cmd.m === "boolean") { v.muted = cmd.m; mutedByUser = cmd.m; }
          updateVolFill();
          v.load();
          if (cmd.autoplay !== false) { tryPlay(); activity(); }
          else updateIcons();
        }
        break;
      case "unload":
        frameSignaled = false;
        dragState = null;
        cancelScrubState();
        stopProgressLoop();
        editMode = false;
        trimStart = 0;
        trimEnd = Infinity;
        cropRatio = null;
        cropInitialized = false;
        clearIdle();
        document.body.classList.remove("video-ready", "paused", "idle", "editing");
        cropLayer.hidden = true;
        try { v.pause(); } catch (_) { }
        v.removeAttribute("src");
        v.load();
        break;
      case "fsState":
        currentMode = cmd.fullscreen ? "fullscreen" : "inline";
        document.body.classList.remove("inline", "fullscreen");
        document.body.classList.add(currentMode);
        if (typeof closeBtn !== "undefined" && closeBtn) closeBtn.style.display = cmd.fullscreen ? "flex" : "none";
        updateIcons();
        if (document.activeElement && document.activeElement.blur) try { document.activeElement.blur(); } catch (_) { }
        try { window.focus(); } catch (_) { }
        break;
      case "restore":
        if (Number.isFinite(cmd.t)) v.currentTime = cmd.t;
        if (Number.isFinite(cmd.v)) { v.volume = Math.max(0, Math.min(1, cmd.v)); vol.value = v.volume; }
        v.muted = !!cmd.m; mutedByUser = v.muted;
        if (cmd.playing) v.play().catch(() => {}); else v.pause();
        updateVolFill(); updateSeekFill(); updateIcons();
        break;
      case "play":
        v.play();
        break;
      case "pause":
        v.pause();
        break;
      case "seek":
        if (isFinite(cmd.t)) v.currentTime = cmd.t;
        break;
      case "seekplay":
        if (isFinite(cmd.t)) v.currentTime = cmd.t;
        v.play();
        break;
      case "fullscreen":
        toggleFullscreen();
        break;
      case "close":
        post({ type: "close" });
        break;
      case "accent":
        if (typeof cmd.v === "string" && /^#[0-9a-fA-F]{6,8}$/.test(cmd.v)) {
          document.documentElement.style.setProperty("--accent", cmd.v);
        }
        break;
      case "editMode":
        setEditMode(!!cmd.on);
        break;
      case "trimRange":
        if (Number.isFinite(cmd.start)) trimStart = Math.max(0, cmd.start);
        if (Number.isFinite(cmd.end)) trimEnd = Math.max(trimStart + 0.1, cmd.end);
        if (Number.isFinite(cmd.seek)) {
          try { if (!v.paused) v.pause(); } catch (_) { }
          applyEditSeek(cmd.seek, !!cmd.preview);
        }
        break;
      case "cropAspect":
        applyCropAspect(cmd.ratio);
        break;
      case "cropReset":
        cropRatio = null;
        resetCropState(true);
        break;
      case "togglePlay":
        togglePlay();
        break;
    }
  } catch (_) { }
};

function getVideoDisplayRect() {
  const cw = window.innerWidth;
  const ch = window.innerHeight;
  const vw = v.videoWidth || 0;
  const vh = v.videoHeight || 0;
  if (!vw || !vh || !cw || !ch) return { left: 0, top: 0, width: cw, height: ch };
  const scale = Math.min(cw / vw, ch / vh);
  const width = vw * scale;
  const height = vh * scale;
  return {
    left: (cw - width) / 2,
    top: (ch - height) / 2,
    width,
    height,
  };
}

function snapEvenFloor(n) {
  return Math.max(0, Math.floor(n / 2) * 2);
}

function snapEven(n, min, max) {
  let c = Math.max(min, Math.min(max, Math.round(n / 2) * 2));
  if (c > max) c = Math.floor(max / 2) * 2;
  return Math.max(min, c);
}

function resetCropState(shouldPost) {
  const vw = v.videoWidth || 0;
  const vh = v.videoHeight || 0;
  if (!vw || !vh) {
    crop = { x: 0, y: 0, w: 0, h: 0 };
    cropInitialized = false;
    return;
  }
  crop = { x: 0, y: 0, w: snapEvenFloor(vw), h: snapEvenFloor(vh) };
  cropInitialized = true;
  if (editMode) syncCropLayout();
  if (shouldPost) postCrop();
}

function parseAspect(ratioSpec) {
  switch (ratioSpec) {
    case "16:9": return 16 / 9;
    case "4:3": return 4 / 3;
    case "9:16": return 9 / 16;
    case "1:1": return 1;
    default: return null;
  }
}

function applyCropAspect(ratioSpec) {
  const vw = snapEvenFloor(v.videoWidth || 0);
  const vh = snapEvenFloor(v.videoHeight || 0);
  cropRatio = parseAspect(ratioSpec);
  if (!vw || !vh) return;
  if (cropRatio === null) {
    renderCropBox();
    postCrop();
    return;
  }

  const cx = cropInitialized ? crop.x + crop.w / 2 : vw / 2;
  const cy = cropInitialized ? crop.y + crop.h / 2 : vh / 2;

  let w = vw;
  let h = Math.round(w / cropRatio);
  if (h > vh) {
    h = vh;
    w = Math.round(h * cropRatio);
  }
  w = snapEven(w, 16, vw);
  h = snapEven(Math.round(w / cropRatio), 16, vh);
  w = snapEven(Math.round(h * cropRatio), 16, vw);

  const x = Math.max(0, Math.min(vw - w, snapEvenFloor(cx - w / 2)));
  const y = Math.max(0, Math.min(vh - h, snapEvenFloor(cy - h / 2)));
  crop = { x, y, w, h };
  cropInitialized = true;
  syncCropLayout();
  postCrop();
}

function setEditMode(on) {
  editMode = !!on;
  cancelCropDrag();
  document.body.classList.toggle("editing", editMode);
  cropLayer.hidden = !editMode;
  if (editMode) {
    clearIdle();
    try { if (!v.paused) v.pause(); } catch (_) { }
    if (!cropInitialized) resetCropState(false);
    syncCropLayout();
    postCrop();
  } else {
    activity();
  }
}

function syncCropLayout() {
  if (!editMode) return;
  const d = getVideoDisplayRect();
  cropLayer.style.left = d.left + "px";
  cropLayer.style.top = d.top + "px";
  cropLayer.style.width = d.width + "px";
  cropLayer.style.height = d.height + "px";
  renderCropBox(d);
}

function renderCropBox(disp) {
  const d = disp || getVideoDisplayRect();
  const vw = v.videoWidth || 0;
  const vh = v.videoHeight || 0;
  if (!vw || !vh || !cropInitialized || !d.width || !d.height) return;

  const sx = d.width / vw;
  const sy = d.height / vh;
  cropBox.style.left = (crop.x * sx) + "px";
  cropBox.style.top = (crop.y * sy) + "px";
  cropBox.style.width = (crop.w * sx) + "px";
  cropBox.style.height = (crop.h * sy) + "px";
  cropTag.textContent = `${crop.w} × ${crop.h}`;
}

function postCrop() {
  const vw = v.videoWidth || 0;
  const vh = v.videoHeight || 0;
  if (!vw || !vh || !cropInitialized) return;
  post({
    type: "crop",
    x: crop.x,
    y: crop.y,
    w: crop.w,
    h: crop.h,
    srcW: vw,
    srcH: vh,
  });
}

window.addEventListener("resize", () => {
  if (editMode) syncCropLayout();
});

function cancelCropDrag(pointerId) {
  if (!dragState) return;
  const id = pointerId !== undefined ? pointerId : dragState.pointerId;
  try {
    if (id != null && cropBox.hasPointerCapture(id)) cropBox.releasePointerCapture(id);
  } catch (_) { }
  dragState = null;
}

cropBox.addEventListener("pointerdown", (e) => {
  if (!editMode || e.button !== 0 || !v.videoWidth || !v.videoHeight) return;
  const d = getVideoDisplayRect();
  if (!d.width || !d.height) return;
  e.preventDefault();
  e.stopPropagation();

  const handleEl = e.target.closest("[data-handle]");
  const handle = handleEl ? handleEl.dataset.handle : "move";
  dragState = {
    pointerId: e.pointerId,
    handle,
    startX: e.clientX,
    startY: e.clientY,
    startCrop: { ...crop },
    disp: d,
  };
  try { cropBox.setPointerCapture(e.pointerId); } catch (_) { }
});

window.addEventListener("pointermove", (e) => {
  if (!dragState || e.pointerId !== dragState.pointerId) return;
  if (!editMode || (e.buttons & 1) === 0) {
    cancelCropDrag(e.pointerId);
    return;
  }
  const vw = snapEvenFloor(v.videoWidth || 0);
  const vh = snapEvenFloor(v.videoHeight || 0);
  if (!vw || !vh) return;

  const scaleX = vw / dragState.disp.width;
  const scaleY = vh / dragState.disp.height;
  const dx = (e.clientX - dragState.startX) * scaleX;
  const dy = (e.clientY - dragState.startY) * scaleY;
  const s = dragState.startCrop;
  const minSz = Math.max(32, snapEven(Math.min(vw, vh) * 0.08, 16, Math.min(vw, vh)));

  if (dragState.handle === "move") {
    const nx = Math.max(0, Math.min(vw - s.w, snapEvenFloor(s.x + dx)));
    const ny = Math.max(0, Math.min(vh - s.h, snapEvenFloor(s.y + dy)));
    crop = { x: nx, y: ny, w: s.w, h: s.h };
  } else {
    let left = s.x;
    let right = s.x + s.w;
    let top = s.y;
    let bottom = s.y + s.h;
    const hnd = dragState.handle;

    if (cropRatio === null) {
      if (hnd.includes("w")) left = Math.max(0, Math.min(right - minSz, s.x + dx));
      if (hnd.includes("e")) right = Math.min(vw, Math.max(left + minSz, s.x + s.w + dx));
      if (hnd.includes("n")) top = Math.max(0, Math.min(bottom - minSz, s.y + dy));
      if (hnd.includes("s")) bottom = Math.min(vh, Math.max(top + minSz, s.y + s.h + dy));
      const x = snapEvenFloor(left);
      const y = snapEvenFloor(top);
      const w = snapEven(right - x, minSz, vw - x);
      const h = snapEven(bottom - y, minSz, vh - y);
      crop = { x, y, w, h };
    } else {
      const r = cropRatio;
      const minW = Math.max(minSz, Math.ceil(minSz * r));
      const minH = Math.max(minSz, Math.ceil(minSz / r));
      let anchorX = hnd.includes("w") ? right : (hnd.includes("e") ? left : (left + right) / 2);
      let anchorY = hnd.includes("n") ? bottom : (hnd.includes("s") ? top : (top + bottom) / 2);

      let candW = s.w;
      let candH = s.h;
      if (hnd.includes("w") || hnd.includes("e")) {
        candW = hnd.includes("w") ? (right - (s.x + dx)) : (s.w + dx);
        candH = candW / r;
      } else {
        candH = hnd.includes("n") ? (bottom - (s.y + dy)) : (s.h + dy);
        candW = candH * r;
      }

      if (candW < minW) { candW = minW; candH = candW / r; }
      if (candH < minH) { candH = minH; candW = candH * r; }

      const maxW = hnd.includes("w") ? anchorX : (hnd.includes("e") ? vw - anchorX : Math.min(anchorX, vw - anchorX) * 2);
      const maxH = hnd.includes("n") ? anchorY : (hnd.includes("s") ? vh - anchorY : Math.min(anchorY, vh - anchorY) * 2);
      if (candW > maxW) { candW = maxW; candH = candW / r; }
      if (candH > maxH) { candH = maxH; candW = candH * r; }

      let w = snapEven(candW, 16, vw);
      let h = snapEven(Math.round(w / r), 16, vh);
      w = snapEven(Math.round(h * r), 16, vw);

      let nx = hnd.includes("w") ? anchorX - w : (hnd.includes("e") ? anchorX : anchorX - w / 2);
      let ny = hnd.includes("n") ? anchorY - h : (hnd.includes("s") ? anchorY : anchorY - h / 2);
      nx = Math.max(0, Math.min(vw - w, snapEvenFloor(nx)));
      ny = Math.max(0, Math.min(vh - h, snapEvenFloor(ny)));
      crop = { x: nx, y: ny, w, h };
    }
  }

  renderCropBox(dragState.disp);
  postCrop();
});

function endCropDrag(e) {
  if (!dragState) return;
  if (e && e.pointerId !== undefined && e.pointerId !== dragState.pointerId) return;
  cancelCropDrag(e ? e.pointerId : dragState.pointerId);
}

window.addEventListener("pointerup", endCropDrag);
window.addEventListener("pointercancel", endCropDrag);
window.addEventListener("blur", () => cancelCropDrag());
cropBox.addEventListener("lostpointercapture", () => cancelCropDrag());
