/* Nuvia landing - progressive enhancement only. The page is fully readable
   with JavaScript disabled; this just adds reveal-on-scroll and pointer polish. */
(function () {
  "use strict";

  var reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  var finePointer = window.matchMedia("(pointer: fine)").matches;

  /* Reveal on scroll via IntersectionObserver (no scroll listeners). */
  var animated = document.querySelectorAll("[data-animate], [data-reveal], .sr");
  if ("IntersectionObserver" in window && !reduceMotion) {
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (entry.isIntersecting) {
          entry.target.classList.add("in");
          io.unobserve(entry.target);
        }
      });
    }, { threshold: 0.16, rootMargin: "0px 0px -8% 0px" });
    animated.forEach(function (el) { io.observe(el); });
  } else {
    animated.forEach(function (el) { el.classList.add("in"); });
  }

  /* The hero's full-bleed 3D background (WebGL) lives in hero3d.js and owns its
     own pointer parallax + scroll travel; nothing here touches it. */

  /* Magnetic pull on primary buttons (pointer only, motion allowed). */
  if (finePointer && !reduceMotion) {
    document.querySelectorAll(".magnetic").forEach(function (btn) {
      var strength = 0.28;
      btn.addEventListener("pointermove", function (e) {
        var r = btn.getBoundingClientRect();
        var x = e.clientX - (r.left + r.width / 2);
        var y = e.clientY - (r.top + r.height / 2);
        btn.style.setProperty("--tx", (x * strength).toFixed(1) + "px");
        btn.style.setProperty("--ty", (y * strength).toFixed(1) + "px");
      });
      btn.addEventListener("pointerleave", function () {
        btn.style.setProperty("--tx", "0px");
        btn.style.setProperty("--ty", "0px");
      });
    });
  }

  /* FAQ: keep one answer open at a time. */
  var items = document.querySelectorAll(".accordion .qa");
  items.forEach(function (item) {
    item.addEventListener("toggle", function () {
      if (item.open) {
        items.forEach(function (other) { if (other !== item) other.open = false; });
      }
    });
  });

  /* Honest count-up for the stat band — real numbers only, animated once. */
  var suffixHtml = function (el) {
    var s = el.getAttribute("data-suffix") || "";
    return s ? '<span class="u">' + s + "</span>" : "";
  };
  var finalize = function (el) {
    el.innerHTML = (parseFloat(el.getAttribute("data-count")) || 0) + suffixHtml(el);
  };
  var countUp = function (el) {
    var target = parseFloat(el.getAttribute("data-count")) || 0;
    if (target <= 0) { finalize(el); return; }
    var dur = 1100, start = null;
    var step = function (ts) {
      if (start === null) start = ts;
      var p = Math.min(1, (ts - start) / dur);
      var eased = 1 - Math.pow(1 - p, 3);
      el.innerHTML = Math.round(eased * target) + suffixHtml(el);
      if (p < 1) requestAnimationFrame(step);
    };
    requestAnimationFrame(step);
  };
  var counters = document.querySelectorAll("[data-count]");
  if (counters.length) {
    if (!("IntersectionObserver" in window) || reduceMotion) {
      counters.forEach(finalize);
    } else {
      var cio = new IntersectionObserver(function (entries) {
        entries.forEach(function (entry) {
          if (entry.isIntersecting) { countUp(entry.target); cio.unobserve(entry.target); }
        });
      }, { threshold: 0.6 });
      counters.forEach(function (el) { cio.observe(el); });
    }
  }
})();
