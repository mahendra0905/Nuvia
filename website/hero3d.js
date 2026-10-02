/* Nuvia hero — full-bleed 3D background (WebGL via three.js, ES module).
   A deep VOLUME of lime light points fills the ENTIRE hero and, as you scroll,
   the camera flies forward into it — files streaming up into the cloud. Purely
   decorative: the page is fully readable with no WebGL and no JS, and with
   "reduce motion" the field is drawn once, frozen. three.js is loaded from a
   pinned CDN; if that or WebGL is unavailable this module simply no-ops. */
import * as THREE from "https://unpkg.com/three@0.160.0/build/three.module.js";

(function () {
  "use strict";

  var canvas = document.getElementById("hero-canvas");
  var hero = document.getElementById("hero");
  if (!canvas || !hero) return;

  var reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  var finePointer = window.matchMedia("(pointer: fine)").matches;
  var mobile = window.matchMedia("(max-width: 640px)").matches;

  // Bail politely if WebGL can't start — the page already reads without it.
  var renderer;
  try {
    renderer = new THREE.WebGLRenderer({
      canvas: canvas, antialias: !mobile, alpha: true, powerPreference: "low-power"
    });
  } catch (e) { return; }
  renderer.setClearAlpha(0);                        // let the white page show through
  renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, mobile ? 1.5 : 2));

  var scene = new THREE.Scene();
  scene.fog = new THREE.Fog(0xffffff, 150, 520);    // far points dissolve into white

  var camera = new THREE.PerspectiveCamera(70, hero.clientWidth / hero.clientHeight, 0.1, 1000);
  var BASE_Z = 70;                                  // base eye; scroll + pointer nudge it
  camera.position.set(0, 0, BASE_Z);

  // A soft round dot, so every point reads as a puff of light, not a square.
  var sprite = (function () {
    var cv = document.createElement("canvas"); cv.width = cv.height = 64;
    var ctx = cv.getContext("2d");
    var g = ctx.createRadialGradient(32, 32, 0, 32, 32, 32);
    g.addColorStop(0, "rgba(255,255,255,1)");
    g.addColorStop(0.35, "rgba(255,255,255,0.9)");
    g.addColorStop(1, "rgba(255,255,255,0)");
    ctx.fillStyle = g; ctx.fillRect(0, 0, 64, 64);
    return new THREE.CanvasTexture(cv);
  })();

  // A deep volumetric cloud of points that fills the whole frame (not a flat
  // floor) — wide in X, tall in Y, and layered in Z so dots sit behind every
  // part of the headline, thinning into fog toward the back.
  var count = mobile ? 2800 : 6200;
  var SPREAD_X = 340, SPREAD_Y = 210, Z_NEAR = 40, Z_FAR = -260;
  var positions = new Float32Array(count * 3);
  var colors = new Float32Array(count * 3);
  var basePos = new Float32Array(count * 3);        // rest position of each point
  var phase = new Float32Array(count);              // per-point drift phase

  var cDark = new THREE.Color(0x497a1e);            // --accent
  var cMid = new THREE.Color(0x6aa331);             // --accent-ic
  var cLime = new THREE.Color(0xa3e635);            // --lime (scattered sparkles)
  for (var i = 0; i < count; i++) {
    var x = (Math.random() * 2 - 1) * SPREAD_X;
    var y = (Math.random() * 2 - 1) * SPREAD_Y;
    var z = Z_NEAR + Math.random() * (Z_FAR - Z_NEAR);
    basePos[i * 3] = x; basePos[i * 3 + 1] = y; basePos[i * 3 + 2] = z;
    positions[i * 3] = x; positions[i * 3 + 1] = y; positions[i * 3 + 2] = z;
    phase[i] = Math.random() * Math.PI * 2;
    var pick = Math.random();                       // mostly green so it reads on white
    var col = pick > 0.82 ? cLime : (pick > 0.44 ? cMid : cDark);
    colors[i * 3] = col.r; colors[i * 3 + 1] = col.g; colors[i * 3 + 2] = col.b;
  }

  var geo = new THREE.BufferGeometry();
  geo.setAttribute("position", new THREE.BufferAttribute(positions, 3));
  geo.setAttribute("color", new THREE.BufferAttribute(colors, 3));

  var mat = new THREE.PointsMaterial({
    size: mobile ? 2.8 : 2.2, map: sprite, vertexColors: true,
    transparent: true, opacity: 0.62, depthWrite: false,
    sizeAttenuation: true, blending: THREE.NormalBlending
  });
  var cloud = new THREE.Points(geo, mat);
  scene.add(cloud);

  function resize() {
    var w = hero.clientWidth, h = hero.clientHeight;
    renderer.setSize(w, h, false);
    camera.aspect = w / h; camera.updateProjectionMatrix();
  }
  resize();
  window.addEventListener("resize", resize, { passive: true });

  // Subtle pointer parallax (fine pointers + motion only).
  var px = 0, py = 0;
  if (finePointer && !reduceMotion) {
    window.addEventListener("pointermove", function (e) {
      px = (e.clientX / window.innerWidth - 0.5) * 2;
      py = (e.clientY / window.innerHeight - 0.5) * 2;
    }, { passive: true });
  }

  // How far the hero has scrolled away (0..1.2) — flies the camera forward.
  var scrollP = 0;
  function readScroll() {
    var h = hero.offsetHeight || window.innerHeight;
    scrollP = Math.min(1.2, Math.max(0, (window.scrollY || window.pageYOffset) / h));
  }
  readScroll();
  window.addEventListener("scroll", readScroll, { passive: true });

  // Idle the loop while the hero is off screen.
  var visible = true;
  if ("IntersectionObserver" in window) {
    new IntersectionObserver(function (en) { visible = en[0].isIntersecting; },
      { threshold: 0 }).observe(hero);
  }

  // Pose the camera from scroll (fly straight into the cloud), eased toward the
  // pointer. Looking down -Z keeps the volume centred and filling the frame.
  var cx = 0, cy = 0;
  function pose() {
    var flyZ = BASE_Z - scrollP * 190;              // scroll pushes INTO the field
    cx += (px * 14 - cx) * 0.05;
    cy += (-py * 10 - cy) * 0.05;
    camera.position.set(cx, cy, flyZ);
    camera.lookAt(cx * 0.35, cy * 0.35, flyZ - 220);
  }

  // Breathe the whole volume with a gentle per-point drift — always alive.
  function wave(t) {
    var pos = geo.attributes.position.array;
    for (var k = 0; k < count; k++) {
      var ph = phase[k];
      pos[k * 3]     = basePos[k * 3]     + Math.sin(t * 0.00035 + ph) * 6;
      pos[k * 3 + 1] = basePos[k * 3 + 1] + Math.cos(t * 0.00045 + ph * 1.3) * 6;
      pos[k * 3 + 2] = basePos[k * 3 + 2] + Math.sin(t * 0.00030 + ph * 0.7) * 5;
    }
    geo.attributes.position.needsUpdate = true;
  }

  function frame(ts) {
    requestAnimationFrame(frame);
    if (!visible) return;                           // nothing to draw off-screen
    wave(ts); pose(); renderer.render(scene, camera);
  }

  if (reduceMotion) {
    wave(0); pose(); renderer.render(scene, camera);  // one calm, frozen frame
  } else {
    requestAnimationFrame(frame);
  }
})();
