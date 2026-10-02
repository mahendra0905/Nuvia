/* Nuvia product-film animatic — single master clock drives every beat as a
   pure function of time t (0..30s), so scrubbing is exact and deterministic.
   Mograph beats (pain / wipe / hero / end) animate; REC beats just show. */
"use strict";

const DURATION = 30.0;
const F = 0.22;                 // crossfade seconds at beat boundaries

const BEATS = [
  { id: "b-pain",    start: 0.00,  end: 3.00,  name: "1 · Pain — storage fills", kind: "mg"  },
  { id: "b-wipe",    start: 3.00,  end: 4.40,  name: "2 · Lime “n” wipe",         kind: "mg"  },
  { id: "b-splash",  start: 4.40,  end: 5.40,  name: "3 · Splash (REC)",          kind: "rec" },
  { id: "b-browse",  start: 5.40,  end: 8.80,  name: "4 · Explorer browse (REC)", kind: "rec" },
  { id: "b-upload",  start: 8.80,  end: 15.00, name: "5 · Upload to Nuvia (REC)", kind: "rec" },
  { id: "b-control", start: 15.00, end: 19.00, name: "6 · Pause/resume (REC)",    kind: "rec" },
  { id: "b-hero",    start: 19.00, end: 25.00, name: "7 · Hero — never fills",    kind: "mg"  },
  { id: "b-end",     start: 25.00, end: 30.00, name: "8 · End card",              kind: "mg"  },
];

// Easing (motion-design tokens)
const clamp01 = x => x < 0 ? 0 : x > 1 ? 1 : x;
const easeOutQuart = x => 1 - Math.pow(1 - clamp01(x), 4);
const easeOutQuint = x => 1 - Math.pow(1 - clamp01(x), 5);
const easeOutExpo  = x => (x = clamp01(x)) >= 1 ? 1 : 1 - Math.pow(2, -10 * x);
const easeOutQuad  = x => 1 - Math.pow(1 - clamp01(x), 2);
const easeInOutQuint = x => (x = clamp01(x)) < 0.5
  ? 16 * x * x * x * x * x : 1 - Math.pow(-2 * x + 2, 5) / 2;

const $ = id => document.getElementById(id);
const local = (t, s, e) => clamp01((t - s) / (e - s));

// cached nodes
const el = {};
["pain-fill","pain-warn","wipe-panel","hero-fill","hero-flow","hero-cap",
 "hero-cap-label","end-n","end-word","end-tag","end-disc",
 "hud-tc","hud-bn","tc","scrub","play","restart","loop","guides","hudbtn",
 "markbar","stage"].forEach(k => el[k] = $(k));
const beatNodes = BEATS.map(b => $(b.id));

// ---- per-beat opacity: incoming (later in DOM) crossfades on top of outgoing
function beatAlpha(b, t) {
  const vis = b.start - F;                 // start fading in F before start
  if (t < vis || t >= b.end) return 0;
  return clamp01((t - vis) / F);
}

// ---- Beat 1: storage meter creeps toward "almost full" (no numbers)
function renderPain(t) {
  const p = local(t, 0, 3);
  const w = 8 + 88 * easeOutQuad(p);       // decel = "straining" near the top
  el["pain-fill"].style.width = w.toFixed(2) + "%";
  el["pain-fill"].style.background =
    p < 0.55 ? "#7CB342" : p < 0.82 ? "#E6A700" : "#E5484D";
  el["pain-warn"].style.opacity = clamp01((p - 0.72) / 0.2).toFixed(3);
}

// ---- Beat 2: lime "n" panel sweeps across the frame
function renderWipe(t) {
  const p = local(t, 3.0, 4.4);
  const x = -100 + 200 * easeInOutQuint(p); // -100% → +100%
  el["wipe-panel"].style.transform = `translateX(${x.toFixed(2)}%)`;
}

// ---- Beat 7: the meter strains, the FULL cap dissolves, flow never stops
function renderHero(t) {
  const p = local(t, 19.0, 25.0);
  const strain = easeOutExpo(clamp01(p / 0.5));          // fast push to the cap
  const w = 8 + strain * 78 + clamp01((p - 0.5) / 0.5) * 6; // then gentle drift
  el["hero-fill"].style.width = Math.min(w, 92).toFixed(2) + "%";
  const capGone = clamp01((p - 0.4) / 0.2);
  el["hero-cap"].style.opacity = (1 - capGone).toFixed(3);
  el["hero-cap-label"].style.opacity = (1 - capGone).toFixed(3);
  const sheen = ((t * 90) % 160) - 30;                   // endless travelling flow
  el["hero-flow"].style.backgroundPosition = sheen.toFixed(1) + "% 0";
}

// ---- Beat 8: lockup reveal (mark → wordmark → tagline → disclosure, held)
function renderEnd(t) {
  const ls = t - 25.0;                      // local seconds, 0..5
  const e1 = easeOutExpo(clamp01((ls - 0.0) / 0.6));
  const e2 = easeOutExpo(clamp01((ls - 0.5) / 0.9));
  const e3 = easeOutQuart(clamp01((ls - 1.4) / 0.6));
  const e4 = clamp01((ls - 2.2) / 0.7);
  el["end-n"].style.opacity = e1.toFixed(3);
  el["end-n"].style.transform = `scale(${(0.9 + 0.1 * e1).toFixed(3)})`;
  el["end-word"].style.opacity = e2.toFixed(3);
  el["end-word"].style.transform =
    `translateY(${((1 - e2) * 14).toFixed(2)}px) scale(${(0.96 + 0.04 * e2).toFixed(3)})`;
  el["end-tag"].style.opacity = e3.toFixed(3);
  el["end-tag"].style.transform = `translateY(${((1 - e3) * 10).toFixed(2)}px)`;
  el["end-disc"].style.opacity = e4.toFixed(3);
}

// ---- master render: every beat recomputed from t (scrub-safe both directions)
let playhead = null;
function render(t) {
  t = Math.max(0, Math.min(DURATION, t));
  BEATS.forEach((b, i) => { beatNodes[i].style.opacity = beatAlpha(b, t); });
  renderPain(t); renderWipe(t); renderHero(t); renderEnd(t);

  // active beat = last one whose window contains t
  let active = BEATS[0];
  for (const b of BEATS) if (t >= b.start) active = b;
  el["hud-tc"].textContent = t.toFixed(1) + "s";
  el["hud-bn"].textContent = active.name;
  el["tc"].textContent = `${t.toFixed(1)} / 30.0s`;
  if (document.activeElement !== el["scrub"]) el["scrub"].value = t;
  if (playhead) playhead.style.left = (t / DURATION * 100) + "%";
}

// ---- markbar: proportional segments + playhead
function buildMarkbar() {
  BEATS.forEach(b => {
    const seg = document.createElement("div");
    seg.className = "seg " + b.kind;
    seg.style.left = (b.start / DURATION * 100) + "%";
    seg.style.width = ((b.end - b.start) / DURATION * 100) + "%";
    seg.textContent = b.name.split("·")[0].trim();
    seg.title = b.name;
    seg.onclick = () => { t = b.start; render(t); };
    el["markbar"].appendChild(seg);
  });
  playhead = document.createElement("div");
  playhead.className = "play";
  el["markbar"].appendChild(playhead);
}

// ---- clock + transport
let t = 0, playing = false, looping = false, last = 0;
function setPlay(on) {
  const was = playing;
  playing = on;
  el["play"].textContent = on ? "❚❚ Pause" : "▶ Play";
  last = performance.now();
  if (on && !was) requestAnimationFrame(tick);   // loop runs only while playing
}
function tick(now) {
  t += (now - last) / 1000;
  last = now;
  if (t >= DURATION) {
    if (looping) t -= DURATION;
    else { t = DURATION; render(t); setPlay(false); return; }
  }
  render(t);
  if (playing) requestAnimationFrame(tick);
}

el["play"].onclick = () => { if (t >= DURATION) t = 0; setPlay(!playing); };
el["restart"].onclick = () => { t = 0; render(t); };
el["scrub"].oninput = e => { t = parseFloat(e.target.value); render(t); };
el["loop"].onclick = () => { looping = !looping; el["loop"].classList.toggle("active", looping); };
el["guides"].onclick = () => el["guides"].classList.toggle("active",
  el["stage"].classList.toggle("guides-on"));
el["hudbtn"].onclick = () => el["hudbtn"].classList.toggle("active",
  !el["stage"].classList.toggle("no-hud"));
document.addEventListener("keydown", e => {
  if (e.code === "Space") { e.preventDefault(); if (t >= DURATION) t = 0; setPlay(!playing); }
});

buildMarkbar();
el["hudbtn"].classList.add("active");
render(0);


