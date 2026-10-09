/*
 * The entry page's one script: the language switch and the start panel.
 *
 * It is loaded from <head> as a blocking classic script, so that the language is chosen before
 * the first paint. It is a file because the page's Content-Security-Policy allows no inline script.
 *
 * THE START PANEL. The demo scales to zero: its first request after an idle spell waits while the
 * server starts. The panel sends that request itself and enables the link when the server answers.
 *  - The probe is an Image pointed at the demo's /favicon.ico. The demo sends no CORS headers, so
 *    fetch could read nothing from it; an image needs none. crossOrigin is never set: it would
 *    turn the probe into a CORS request, and that one fails.
 *  - The link in the HTML works without this script. While a probe is out the script takes its
 *    href away, so that a click does not open a blank tab, and gives it back at the answer.
 *  - Nothing is ever sent to keep the server awake. An answer is trusted for four minutes. After
 *    that the page probes again when the tab comes back, or when the link is pressed: the press
 *    wakes the server first and opens the demo at the answer.
 */
(function () {
  'use strict';

  var ESTIMATE_MS = 25000; // a start from zero takes about this long
  var QUIET_MS = 600; // an answer inside it comes from a server that was awake: no bar is shown
  var BLOCKED_MS = 3000; // an error inside it: the probe cannot work from this browser
  var RETRY_MS = 3000; // after a later error the probe asks again
  var GIVE_UP_MS = 60000; // with no answer the link is enabled anyway
  var TRUST_MS = 240000; // the server goes back to sleep after about five idle minutes
  var FULL = 0.95; // the bar never looks finished before the answer
  var LANGUAGE_KEY = 'azurebank-entry-language';

  var TEXT = {
    en: {
      starting: 'Starting the server...',
      count: '{n} s of about {t}',
      late: 'Almost there...',
      lateCount: '{n} s',
      ready: 'The server is ready',
      anyway: 'The server has not answered yet: the first page may still be starting.',
    },
    it: {
      starting: 'Avvio del server...',
      count: '{n} s di circa {t}',
      late: 'Ci siamo quasi...',
      lateCount: '{n} s',
      ready: 'Il server è pronto',
      anyway: 'Il server non ha ancora risposto: la prima pagina potrebbe essere ancora in avvio.',
    },
  };

  var root = document.documentElement;
  var language = chooseLanguage();
  root.lang = language;
  root.classList.add('js');

  // The address may name the language (#it, #en); then the visitor's own choice; then the browser.
  function chooseLanguage() {
    if (location.hash === '#it') return 'it';
    if (location.hash === '#en') return 'en';
    try {
      var saved = localStorage.getItem(LANGUAGE_KEY);
      if (saved === 'it') return 'it';
      if (saved === 'en') return 'en';
    } catch (error) {
      // No storage in this browser: the page works without it.
    }
    return (navigator.language || '').toLowerCase().indexOf('it') === 0 ? 'it' : 'en';
  }

  function start() {
    var panel = document.getElementById('start');
    var link = document.getElementById('open-demo');
    var status = document.getElementById('wake-status');
    var count = document.getElementById('wake-count');
    var fill = document.getElementById('wake-fill');
    var switches = {
      en: document.getElementById('lang-en'),
      it: document.getElementById('lang-it'),
    };

    // The demo's address is written once, in the link.
    var demo = link.href;
    var favicon = new URL('/favicon.ico', demo).href;

    var state = ''; // checking, waiting, ready, anyway, idle: entry.css says what each one shows
    var cycle = 0; // an answer to an earlier probe is ignored
    var began = 0; // performance.now() when the cycle started: timers slow down in a hidden tab
    var settled = 0; // Date.now() when the last cycle ended: it goes on while the computer sleeps
    var openAtAnswer = false; // the cycle was started by a press of the link
    var timers = [];

    function later(task, delay) {
      timers.push(setTimeout(task, delay));
    }

    function forget() {
      timers.forEach(clearTimeout);
      timers = [];
    }

    function show(next) {
      if (next) {
        state = next;
        panel.setAttribute('data-state', next);
      }
      var text = TEXT[language];
      var elapsed = performance.now() - began;
      var late = elapsed >= ESTIMATE_MS;
      var message = '';
      var counter = '';
      var filled = 0;
      if (state === 'waiting') {
        message = late ? text.late : text.starting;
        counter = (late ? text.lateCount : text.count)
          .replace('{n}', String(Math.max(1, Math.floor(elapsed / 1000))))
          .replace('{t}', String(ESTIMATE_MS / 1000));
        filled = Math.min(elapsed / ESTIMATE_MS, 1) * FULL;
      } else if (state === 'ready') {
        message = text.ready;
      } else if (state === 'anyway') {
        message = text.anyway;
      }
      // The status is a live region: it is written when its words change, never on a tick.
      if (status.textContent !== message) status.textContent = message;
      count.textContent = counter;
      fill.style.transform = 'scaleX(' + filled + ')';
    }

    function tick() {
      show();
      later(tick, Math.max(50, 1000 - ((performance.now() - began) % 1000)));
    }

    function probe(pressed) {
      forget();
      cycle += 1;
      var mine = cycle;
      began = performance.now();
      openAtAnswer = pressed === true;
      // Not a link while it waits, and still where the keyboard's focus can stay.
      link.tabIndex = 0;
      link.setAttribute('role', 'link');
      link.setAttribute('aria-disabled', 'true');
      link.removeAttribute('href');
      show('checking');
      later(function () {
        show('waiting');
        tick();
      }, QUIET_MS);
      later(function () {
        settle(mine, 'anyway');
      }, GIVE_UP_MS);
      afterLoad(function () {
        if (mine === cycle) ask(mine);
      });
    }

    // An image asked for before the page's load event holds that event back, and the tab would
    // show a page still loading for as long as the server takes to start.
    function afterLoad(task) {
      if (document.readyState === 'complete') task();
      else window.addEventListener('load', function () { setTimeout(task, 0); }, { once: true });
    }

    function ask(mine) {
      var image = new Image();
      image.onload = function () {
        settle(mine, 'ready');
      };
      image.onerror = function () {
        if (mine !== cycle || state === 'ready' || state === 'anyway') return;
        if (performance.now() - began < BLOCKED_MS) settle(mine, 'anyway');
        else later(function () { ask(mine); }, RETRY_MS);
      };
      image.src = favicon + '?wake=' + Date.now();
    }

    // An image that loads after the link was enabled anyway still turns the panel to ready.
    function settle(mine, outcome) {
      if (mine !== cycle || state === 'ready' || state === outcome) return;
      forget();
      settled = Date.now();
      link.setAttribute('href', demo);
      link.removeAttribute('aria-disabled');
      link.removeAttribute('role');
      link.removeAttribute('tabindex');
      show(outcome);
      if (outcome === 'ready') {
        later(function () {
          show('idle');
        }, TRUST_MS);
      }
      if (openAtAnswer) {
        openAtAnswer = false;
        link.click();
        return;
      }
      var focused = document.activeElement;
      if (outcome === 'ready' && (!focused || focused === document.body || focused === link)) {
        link.focus({ preventScroll: true });
      }
    }

    // A plain press of the link after the answer has gone stale: wake first, open at the answer.
    link.addEventListener('click', function (event) {
      var stale = state === 'idle' || (state === 'ready' && Date.now() - settled > TRUST_MS);
      var plain =
        event.button === 0 && !event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey;
      if (!stale || !plain) return;
      event.preventDefault();
      probe(true);
    });

    function comeBack() {
      var running = state === 'checking' || state === 'waiting';
      if (document.visibilityState === 'visible' && !running && Date.now() - settled > TRUST_MS) {
        probe();
      }
    }
    document.addEventListener('visibilitychange', comeBack);
    window.addEventListener('pageshow', function (event) {
      if (event.persisted) comeBack();
    });

    function speak(next, remember) {
      language = next;
      root.lang = next;
      switches.en.setAttribute('aria-pressed', String(next === 'en'));
      switches.it.setAttribute('aria-pressed', String(next === 'it'));
      if (remember) {
        try {
          localStorage.setItem(LANGUAGE_KEY, next);
        } catch (error) {
          // The choice lasts for this visit only.
        }
      }
      show();
    }
    switches.en.addEventListener('click', function () {
      speak('en', true);
    });
    switches.it.addEventListener('click', function () {
      speak('it', true);
    });
    speak(language, false);

    // A page the browser loads ahead of a visit must not start the server for nobody.
    if (document.prerendering) {
      document.addEventListener('prerenderingchange', function () { probe(); }, { once: true });
    } else {
      probe();
    }
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
  else start();
})();
