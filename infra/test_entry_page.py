"""Tests of the one number the entry page reads from GitHub when it opens.

The entry page (site/) shows how many pull requests were merged. The number is written in the page
with its date, and site/entry.js asks GitHub's search API for today's and writes it in place of
the written one, in English and in Italian, when the answer is a whole number that is not smaller.

The page is read as text: the policy that names the hosts it may reach, and the places the
script writes to. The script is run in Node with no page: the file hands its count to whoever
loads it where there is no document. Each case gives it two figures and two notes that hold what
the page holds, and a stand-in for fetch that answers, refuses or stays silent. No test sends a
request, so what a browser does with the real one is not tested here: the policy the browser
enforces, and GitHub's answer to a page of another origin.

On a developer's machine a missing Node skips the tests that need it; in CI it is an error.
"""

import json
import os
import pathlib
import re
import shutil
import subprocess
import unittest

SITE = pathlib.Path(__file__).resolve().parent.parent / 'site'
SCRIPT = SITE / 'entry.js'
PAGE = (SITE / 'index.html').read_text(encoding='utf-8')
ADDRESS = ('https://api.github.com/search/issues'
           '?q=repo:Gurgant/azurebank-v2+is:pr+is:merged&per_page=1')
NOTES = ['; the pull requests, as this page opened',
         "; le pull request, all'apertura di questa pagina"]


def tool(name):
    path = shutil.which(name)
    if not path and os.environ.get('CI') == 'true':
        raise RuntimeError(f'{name} is not installed on this runner; in CI these tests may not be skipped.')
    return path


NODE = tool('node')

# What stands in for the page: an element is its text and the one attribute that names its
# language. A timer never fires, except the one that ends a request nobody answers.
HARNESS = r'''
const entry = require(process.argv[1]);
const cases = JSON.parse(require('fs').readFileSync(0, 'utf8'));
const realTimeout = setTimeout;
const element = (name, language, text) => ({
  textContent: text,
  getAttribute: (asked) => (asked === name ? language : null),
});
(async () => {
  const seen = [];
  for (const one of cases) {
    const figures = [
      element('data-merged-count', 'en', one.written[0]),
      element('data-merged-count', 'it', one.written[1]),
    ];
    const notes = [element('data-count-note', 'en', ''), element('data-count-note', 'it', '')];
    const requests = [];
    const waits = [];
    globalThis.setTimeout = (task, wait) => {
      waits.push(wait);
      return one.reply === 'silence' ? realTimeout(task, 0) : 0;
    };
    globalThis.fetch =
      one.reply === 'no fetch'
        ? undefined
        : (address, options) => {
            requests.push({
              address: String(address),
              options: Object.fromEntries(
                Object.entries(options || {}).filter(([name]) => name !== 'signal'),
              ),
            });
            if (one.reply === 'refused') return Promise.reject(new TypeError('Failed to fetch'));
            if (one.reply === 'silence')
              return new Promise((resolve, reject) => {
                options.signal.addEventListener('abort', () => reject(new Error('aborted')));
              });
            return Promise.resolve({
              ok: one.status >= 200 && one.status < 300,
              status: one.status,
              json: async () => JSON.parse(one.reply),
            });
          };
    await entry.countMerged(figures, notes);
    seen.push({
      figures: figures.map((figure) => figure.textContent),
      notes: notes.map((note) => note.textContent),
      requests: requests,
      waits: waits,
    });
  }
  globalThis.setTimeout = realTimeout;
  console.log(JSON.stringify(seen));
})();
'''


def answer(total, complete=True):
    return json.dumps({'total_count': total, 'incomplete_results': not complete, 'items': []})


def run(*cases):
    """What the page shows after each case: its figures, its notes, the requests it sent."""
    asked = [{'written': ['296', '296'], 'status': 200, **case} for case in cases]
    done = subprocess.run([NODE, '-e', HARNESS, str(SCRIPT)], input=json.dumps(asked),
                          capture_output=True, text=True, encoding='utf-8', timeout=60, check=False)
    if done.returncode != 0:
        raise AssertionError(f'node exited {done.returncode}: {done.stderr.strip()}')
    return json.loads(done.stdout)


@unittest.skipUnless(NODE, 'Node is not installed')
class LiveCountTests(unittest.TestCase):
    def test_a_larger_count_is_written_in_both_languages_with_its_note(self):
        seen, = run({'reply': answer(301)})
        self.assertEqual(seen['figures'], ['301', '301'])
        self.assertEqual(seen['notes'], NOTES)

    def test_the_same_count_is_today_s_count_too(self):
        seen, = run({'reply': answer(296)})
        self.assertEqual(seen['figures'], ['296', '296'])
        self.assertEqual(seen['notes'], NOTES)

    def test_thousands_are_grouped_as_each_language_groups_them(self):
        small, large = run({'reply': answer(1234)},
                           {'reply': answer(1234567), 'written': ['4,741', '4.741']})
        self.assertEqual(small['figures'], ['1,234', '1.234'])
        self.assertEqual(large['figures'], ['1,234,567', '1.234.567'])

    def test_a_written_number_with_its_thousands_is_read_as_the_number_it_is(self):
        smaller, larger = run({'reply': answer(4740), 'written': ['4,741', '4.741']},
                              {'reply': answer(4742), 'written': ['4,741', '4.741']})
        self.assertEqual(smaller['figures'], ['4,741', '4.741'])
        self.assertEqual(larger['figures'], ['4,742', '4.742'])

    def test_one_request_goes_to_github_s_search_and_carries_nothing_of_the_visitor(self):
        seen, = run({'reply': answer(301)})
        self.assertEqual(seen['requests'], [{
            'address': ADDRESS,
            'options': {'credentials': 'omit', 'referrerPolicy': 'no-referrer'},
        }])
        self.assertEqual(seen['waits'], [4000])

    def test_the_written_number_stays_when_the_answer_is_not_a_count_that_can_be_shown(self):
        replies = {
            'a smaller count': answer(295),
            'zero': answer(0),
            'a negative number': answer(-301),
            'a fraction': answer(301.5),
            'a number as text': answer('301'),
            'null': answer(None),
            'no count': json.dumps({'incomplete_results': False, 'items': []}),
            'a count GitHub calls incomplete': answer(301, complete=False),
            'a count that does not say whether it is complete': json.dumps({'total_count': 301}),
            'a number too large to be exact': '{"total_count": 9007199254740993, "incomplete_results": false}',
            'a number past every number': '{"total_count": 1e999, "incomplete_results": false}',
            'a list': '[301]',
            'a bare number': '301',
            'the word null': 'null',
            'a page of HTML': '<!doctype html><title>301</title>',
            'nothing': '',
        }
        results = run(*({'reply': reply} for reply in replies.values()))
        for name, seen in zip(replies, results):
            with self.subTest(answer=name):
                self.assertEqual(seen['figures'], ['296', '296'])
                self.assertEqual(seen['notes'], ['', ''])
                self.assertEqual(len(seen['requests']), 1)

    def test_the_written_number_stays_when_github_refuses_or_fails(self):
        statuses = (403, 429, 404, 500, 503, 304)
        results = run(*({'reply': answer(301), 'status': status} for status in statuses))
        for status, seen in zip(statuses, results):
            with self.subTest(status=status):
                self.assertEqual(seen['figures'], ['296', '296'])
                self.assertEqual(seen['notes'], ['', ''])

    def test_the_written_number_stays_when_the_request_cannot_be_sent(self):
        seen, = run({'reply': 'refused'})
        self.assertEqual(len(seen['requests']), 1)
        self.assertEqual(seen['figures'], ['296', '296'])
        self.assertEqual(seen['notes'], ['', ''])

    def test_a_request_nobody_answers_is_ended_and_the_written_number_stays(self):
        seen, = run({'reply': 'silence'})
        self.assertEqual(len(seen['requests']), 1)
        self.assertEqual(seen['waits'], [4000])
        self.assertEqual(seen['figures'], ['296', '296'])
        self.assertEqual(seen['notes'], ['', ''])

    def test_a_browser_with_no_fetch_shows_the_written_number(self):
        seen, = run({'reply': 'no fetch'})
        self.assertEqual(seen['figures'], ['296', '296'])
        self.assertEqual(seen['notes'], ['', ''])

    def test_nothing_is_asked_when_the_page_s_own_number_cannot_be_read(self):
        for written in (['296', 'molti'], ['', ''], ['296 ', '296'], ['0296', '0296'], ['2,96', '296']):
            with self.subTest(written=written):
                seen, = run({'reply': answer(301), 'written': written})
                self.assertEqual(seen['requests'], [])
                self.assertEqual(seen['figures'], written)
                self.assertEqual(seen['notes'], ['', ''])

    def test_two_written_numbers_that_differ_are_both_a_floor(self):
        between, above = run({'reply': answer(298), 'written': ['296', '299']},
                             {'reply': answer(299), 'written': ['296', '299']})
        self.assertEqual(between['figures'], ['296', '299'])
        self.assertEqual(between['notes'], ['', ''])
        self.assertEqual(above['figures'], ['299', '299'])


class EntryPageTests(unittest.TestCase):
    def test_the_policy_lets_the_page_ask_github_s_api_and_no_other_host(self):
        policy, = re.findall(r'http-equiv="Content-Security-Policy"\s+content="([^"]*)"', PAGE)
        rules = dict(rule.strip().split(' ', 1) for rule in policy.split(';'))
        self.assertEqual(rules['default-src'], "'none'")
        self.assertEqual(rules['connect-src'], 'https://api.github.com')
        hosts = re.findall(r'https?://[^\s;]+', policy)
        self.assertEqual(len(hosts), 2)
        self.assertIn('https://api.github.com', hosts)
        self.assertRegex(rules['img-src'], r"^'self' https://[a-z0-9.-]+\.azurecontainerapps\.io$")

    def test_the_script_names_one_address_and_it_is_the_search(self):
        script = SCRIPT.read_text(encoding='utf-8')
        self.assertEqual(re.findall(r'''https?://[^'"\s]+''', script), [ADDRESS])
        self.assertEqual(script.count('fetch('), 1)
        self.assertNotIn('XMLHttpRequest', script)
        self.assertNotIn('sendBeacon', script)
        self.assertNotIn('WebSocket', script)
        self.assertNotIn('EventSource', script)

    def test_the_number_is_written_once_for_each_language_in_the_link_to_the_list(self):
        links = re.findall(r'<a href="https://github\.com/Gurgant/azurebank-v2/pulls\?q=is%3Apr\+is%3Amerged">'
                           r'(.*?)</a>', PAGE, flags=re.S)
        link, = [link for link in links if 'data-merged-count' in link]
        figures = re.findall(r'<span class="fig" lang="(\w+)" data-lang="\1"><b data-merged-count="\1">'
                             r'([^<]*)</b>', link)
        self.assertEqual([language for language, _ in figures], ['en', 'it'])
        self.assertEqual(PAGE.count('data-merged-count'), 2)
        for _, written in figures:
            self.assertRegex(written, r'^[1-9]\d{0,2}([.,]\d{3})*$')
        self.assertEqual(figures[0][1].replace(',', ''), figures[1][1].replace('.', ''))

    def test_the_note_is_empty_in_the_page_and_follows_the_date_of_the_four_figures(self):
        date, = re.findall(r'<p class="proof__date">(.*?)</p>', PAGE, flags=re.S)
        notes = re.findall(r'<span lang="(\w+)" data-lang="\1">[^<]*<time datetime="[\d-]+">[^<]*</time>'
                           r'<span data-count-note="\1"></span></span>', date)
        self.assertEqual(notes, ['en', 'it'])
        self.assertEqual(PAGE.count('data-count-note'), 2)

    def test_the_script_and_the_style_sheet_are_asked_for_under_one_version(self):
        versions = re.findall(r'(?:href|src)="entry\.(?:css|js)\?v=(\d+)"', PAGE)
        self.assertEqual(len(versions), 2)
        self.assertEqual(versions[0], versions[1])


if __name__ == '__main__':
    unittest.main()
