/*
 * The entry page's one script: the language switch, the start panel and the door at the thumb.
 *
 * It is loaded from <head> as a blocking classic script, so that the language is chosen before
 * the first paint. It is a file because the page's Content-Security-Policy allows no inline script.
 * The page is complete without it: English, with the key of the first screen a plain link.
 *
 * THE START PANEL. The demo scales to zero: its first request after an idle spell waits while the
 * server starts. The panel sends that request itself and opens the way in when the server answers.
 *  - The probe is an Image pointed at the demo's /favicon.ico. The demo sends no CORS headers, so
 *    fetch could read nothing from it; an image needs none. crossOrigin is never set: it would
 *    turn the probe into a CORS request, and that one fails.
 *  - A key is every element with data-door: the one of the first screen, the one under the three
 *    steps and the one of the dock. While a probe is out the script takes their address away, so
 *    that a press does not open a blank tab, and makes them buttons. A press is answered at once:
 *    the status says that the demo opens when the server answers, and it does. A second press, or
 *    Escape, takes the request back.
 *  - Nothing is ever sent to keep the server awake. An answer is trusted for four minutes. After
 *    that the page probes again when the tab comes back, or when a key is pressed: the press
 *    wakes the server first and opens the demo at the answer.
 *
 * The state is written on <html data-wake>, and entry.css says what each one shows:
 *   checking  a probe is out and may still come back at once: the status line is empty
 *   starting  no answer after the first moment: the lamps and the seconds
 *   ready     the server answered
 *   late      no answer in a minute: the keys work, with no promise
 *   unknown   the probe cannot work from this browser: the keys work, with no promise
 *   asleep    the last answer is old: as without the script, and a press wakes the server first
 * <html data-queued> is there while a press waits for the answer.
 */
(function () {
  'use strict';

  var ESTIMATE_MS = 25000; // a start from zero takes about this long
  var QUIET_MS = 600; // an answer inside it comes from a server that was awake: no lamp is lit
  var BLOCKED_MS = 3000; // an error inside it: the probe cannot work from this browser
  var RETRY_MS = 3000; // after a later error the probe asks again
  var GIVE_UP_MS = 60000; // with no answer the keys are given back anyway
  var TRUST_MS = 240000; // the server goes back to sleep after about five idle minutes
  var LANGUAGE_KEY = 'azurebank-entry-language';
  // On a developer's own machine ?state=<one of these> shows that state and sends nothing.
  var PREVIEW = ['asleep', 'starting', 'pressed', 'ready', 'late', 'unknown'];
  var PREVIEW_MS = 12000; // the second a preview shows

  var TEXT = {
    en: {
      asleep: 'The demo is asleep. Starting it takes about {t} seconds.',
      starting: 'Starting the server…',
      almost: 'Almost there…',
      queued: 'The demo opens when the server answers',
      count: '{n} s of about {t}',
      lateCount: '{n} s',
      ready: 'The demo is awake.',
      late: 'This is taking longer than usual.',
      unknown:
        'This browser cannot check the server. If the demo is asleep, the first page takes about {t} seconds.',
    },
    it: {
      title: 'AzureBank: una banca che funziona, online. Di Vladislav Aleshaev',
      asleep: 'La demo è spenta. Per avviarla servono circa {t} secondi.',
      starting: 'Avvio del server…',
      almost: 'Ci siamo quasi…',
      queued: 'La demo si apre appena il server risponde',
      count: '{n} s di circa {t}',
      lateCount: '{n} s',
      ready: 'La demo è pronta.',
      late: 'Ci sta mettendo più del solito.',
      unknown:
        'Da questo browser non si può controllare il server. Se la demo è spenta, la prima pagina richiede circa {t} secondi.',
    },
  };

  var root = document.documentElement;
  var language = chooseLanguage();
  root.lang = language;
  root.classList.add('js');

  // The address may name the language (#it, #en); then the visitor's own choice. Otherwise the
  // page is English, whatever the browser's language is.
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
    return 'en';
  }

  function all(selector, scope) {
    return Array.prototype.slice.call((scope || document).querySelectorAll(selector));
  }

  function start() {
    var first = document.getElementById('open-demo');
    var doors = all('[data-door]');
    var texts = all('[data-wake-text]');
    var counts = all('[data-wake-count]');
    var meters = all('[data-meter]').map(function (meter) {
      return all('i', meter);
    });
    var switches = all('[data-set-lang]');
    var dock = document.getElementById('dock');
    var englishTitle = document.title;

    // What a picture or a list is called is an attribute, and an attribute holds one language:
    // the English one is in the page, the Italian one beside it.
    var twins = [];
    [
      ['alt', 'data-alt-it'],
      ['aria-label', 'data-label-it'],
    ].forEach(function (pair) {
      all('[' + pair[1] + ']').forEach(function (element) {
        twins.push({
          element: element,
          name: pair[0],
          en: element.getAttribute(pair[0]),
          it: element.getAttribute(pair[1]),
        });
      });
    });

    var demo = ''; // the demo's address: written once, in the first key
    var favicon = '';
    var state = '';
    var cycle = 0; // an answer to an earlier probe is ignored
    var began = 0; // performance.now() when the cycle started: timers slow down in a hidden tab
    var settled = 0; // Date.now() when the last cycle ended: it goes on while the computer sleeps
    var queued = false; // a press is waiting for the answer
    var previewing = false;
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
        root.setAttribute('data-wake', next);
      }
      if (queued) root.setAttribute('data-queued', '');
      else root.removeAttribute('data-queued');

      var words = TEXT[language];
      var estimate = String(ESTIMATE_MS / 1000);
      var elapsed = previewing ? PREVIEW_MS : performance.now() - began;
      var past = elapsed >= ESTIMATE_MS;
      var message = '';
      var counter = '';
      var lit = -1;
      if (state === 'starting') {
        counter = (past ? words.lateCount : words.count)
          .replace('{n}', String(Math.max(1, Math.floor(elapsed / 1000))))
          .replace('{t}', estimate);
        message = queued ? words.queued + ':' : past ? words.almost : words.starting;
      } else if (state === 'checking') {
        message = queued ? words.queued + '.' : '';
      } else if (state) {
        message = words[state].replace('{t}', estimate);
      }
      // The status is a live region: it is written when its words change, never on a tick.
      texts.forEach(function (text) {
        if (text.textContent !== message) text.textContent = message;
      });
      counts.forEach(function (count) {
        if (count.textContent !== counter) count.textContent = counter;
      });
      // One lamp for each second of the estimate. The last one never lights before the answer.
      meters.forEach(function (lamps) {
        if (state === 'starting') {
          lit = Math.min(Math.floor((elapsed / ESTIMATE_MS) * lamps.length), lamps.length - 1);
        }
        lamps.forEach(function (lamp, index) {
          var wanted = index < lit ? 'on' : index === lit ? 'now' : '';
          if (lamp.className !== wanted) lamp.className = wanted;
        });
      });
    }

    function tick() {
      show();
      later(tick, Math.max(50, 1000 - ((performance.now() - began) % 1000)));
    }

    // Not links while a probe is out, and still where the keyboard's focus can stay.
    function hold() {
      doors.forEach(function (door) {
        door.removeAttribute('href');
        door.setAttribute('role', 'button');
        door.setAttribute('aria-pressed', String(queued));
        door.tabIndex = 0;
      });
    }

    function release() {
      doors.forEach(function (door) {
        door.setAttribute('href', demo);
        door.removeAttribute('role');
        door.removeAttribute('aria-pressed');
        door.removeAttribute('tabindex');
      });
    }

    function queue(next) {
      queued = next;
      doors.forEach(function (door) {
        if (!door.hasAttribute('href')) door.setAttribute('aria-pressed', String(next));
      });
      show();
    }

    function probe(pressed) {
      forget();
      cycle += 1;
      var mine = cycle;
      began = performance.now();
      queued = pressed === true;
      hold();
      show('checking');
      later(function () {
        show('starting');
        tick();
      }, QUIET_MS);
      later(function () {
        settle(mine, 'late');
      }, GIVE_UP_MS);
      afterLoad(function () {
        if (mine === cycle) ask(mine);
      });
    }

    // An image asked for before the page's load event holds that event back, and the tab would
    // show a page still loading for as long as the server takes to start.
    function afterLoad(task) {
      if (document.readyState === 'complete') task();
      else
        window.addEventListener(
          'load',
          function () {
            setTimeout(task, 0);
          },
          { once: true },
        );
    }

    function ask(mine) {
      var image = new Image();
      image.onload = function () {
        settle(mine, 'ready');
      };
      image.onerror = function () {
        if (mine !== cycle || state === 'ready' || state === 'late' || state === 'unknown') return;
        if (performance.now() - began < BLOCKED_MS) settle(mine, 'unknown');
        else
          later(function () {
            ask(mine);
          }, RETRY_MS);
      };
      image.src = favicon + '?wake=' + Date.now();
    }

    // An image that loads after the keys were given back anyway still turns the panel to ready.
    function settle(mine, outcome) {
      if (mine !== cycle || state === 'ready' || state === outcome) return;
      forget();
      settled = Date.now();
      var open = queued;
      queued = false;
      release();
      show(outcome);
      if (outcome === 'ready') {
        later(function () {
          show('asleep');
        }, TRUST_MS);
      }
      if (open) {
        first.click();
        return;
      }
      var focused = document.activeElement;
      if (outcome === 'ready' && (!focused || focused === document.body || focused === first)) {
        first.focus({ preventScroll: true });
      }
    }

    function press(event) {
      if (state === 'checking' || state === 'starting') {
        event.preventDefault();
        queue(!queued);
        return;
      }
      if (previewing) return;
      // A plain press after the answer has gone stale: wake first, open at the answer.
      var stale = state === 'asleep' || (state === 'ready' && Date.now() - settled > TRUST_MS);
      var plain =
        event.button === 0 && !event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey;
      if (!stale || !plain) return;
      event.preventDefault();
      probe(true);
    }

    // A key without its address is a button, and a button answers Enter and the space bar.
    function type(event) {
      if (event.currentTarget.hasAttribute('href')) return;
      if (event.key === 'Enter' || event.key === ' ') {
        event.preventDefault();
        queue(!queued);
      }
    }

    function comeBack() {
      var running = state === 'checking' || state === 'starting';
      if (document.visibilityState === 'visible' && !running && Date.now() - settled > TRUST_MS) {
        probe();
      }
    }

    function speak(next, remember) {
      language = next;
      root.lang = next;
      document.title = TEXT[next].title || englishTitle;
      switches.forEach(function (button) {
        button.setAttribute('aria-pressed', String(button.getAttribute('data-set-lang') === next));
      });
      twins.forEach(function (twin) {
        if (twin[next] !== null) twin.element.setAttribute(twin.name, twin[next]);
      });
      if (remember) {
        try {
          localStorage.setItem(LANGUAGE_KEY, next);
        } catch (error) {
          // The choice lasts for this visit only.
        }
        // An address that names a language wins at the next load: it follows the choice.
        if (location.hash === '#it' || location.hash === '#en') {
          try {
            history.replaceState(null, '', '#' + next);
          } catch (error) {
            // The address stays as it is.
          }
        }
      }
      show();
    }

    // Which state the address asks to be shown, on a developer's own machine and nowhere else.
    function preview() {
      var local =
        location.protocol === 'file:' ||
        /^(localhost|127\.0\.0\.1|\[::1\])$/.test(location.hostname);
      if (!local) return '';
      var asked = '';
      try {
        asked = new URLSearchParams(location.search).get('state') || '';
      } catch (error) {
        // An old browser: no preview.
      }
      return PREVIEW.indexOf(asked) === -1 ? '' : asked;
    }

    switches.forEach(function (button) {
      button.addEventListener('click', function () {
        speak(button.getAttribute('data-set-lang'), true);
      });
    });
    speak(language, false);

    // Without the first key there is nothing to wake and nothing to open.
    if (!first || !first.href) return;
    demo = first.href;
    favicon = new URL('/favicon.ico', demo).href;
    doors.concat(all('[data-demo-link]')).forEach(function (link) {
      link.setAttribute('href', demo);
    });
    doors.forEach(function (door) {
      door.addEventListener('click', press);
      door.addEventListener('keydown', type);
    });
    document.addEventListener('keydown', function (event) {
      if (event.key === 'Escape' && queued) queue(false);
    });

    // The dock stands at the bottom of a phone's screen while the first key is above the screen.
    if (dock && 'IntersectionObserver' in window) {
      new IntersectionObserver(function (entries) {
        var entry = entries[entries.length - 1];
        var away = !entry.isIntersecting && entry.boundingClientRect.bottom <= 0;
        dock.classList.toggle('is-on', away);
        root.classList.toggle('has-dock', away);
      }).observe(first);
    }

    var asked = preview();
    if (asked) {
      previewing = true;
      if (asked === 'starting' || asked === 'pressed') {
        queued = asked === 'pressed';
        hold();
        show('starting');
      } else {
        show(asked);
      }
      return;
    }

    document.addEventListener('visibilitychange', comeBack);
    window.addEventListener('pageshow', function (event) {
      if (event.persisted) comeBack();
    });

    // A page the browser loads ahead of a visit must not start the server for nobody.
    if (document.prerendering) {
      document.addEventListener(
        'prerenderingchange',
        function () {
          probe();
        },
        { once: true },
      );
    } else {
      probe();
    }
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
  else start();
})();
