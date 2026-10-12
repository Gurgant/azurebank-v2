"""Tests of the check that reads the prose of the repository for American spellings.

Each kind of text has a line that fails and the nearest lines that pass: a word in prose, the
same word where it is a name. The last tests start the script as the CI step does, on files in
a temporary directory and on the repository as it is.
"""

import contextlib
import io
import pathlib
import subprocess
import sys
import tempfile
import unittest

import check_british_spelling as check

SCRIPT = pathlib.Path(__file__).resolve().parent / 'check_british_spelling.py'
WORDS, PHRASES = check.read_words(check.WORDS.read_text(encoding='utf-8'))

PAGE = '''<!doctype html>
<html lang="en">
<head>
  <title>A page</title>
  <style>.fig { color: red; }</style>
</head>
<body>
  <p lang="en" data-lang="en" title="the behavior of an attribute">It is organized
    by <code>color</code> and by behavior.</p>
  <p lang="it" data-lang="it">Il color rosso, il favor del pubblico.</p>
  <img alt="a gray cat" src="cat.png">
  <p>After an image, the center holds.</p>
  <script>var behavior = "organized";</script>
</body>
</html>
'''


def markdown(text):
    return [(word, british) for _, word, british
            in check.findings(check.prose_of_markdown(text), WORDS, PHRASES)]


def page(text):
    return check.findings(check.prose_of_page(text), WORDS, PHRASES)


def run(*arguments):
    said = io.StringIO()
    with contextlib.redirect_stdout(said):
        code = check.main(list(arguments))
    return code, said.getvalue()


class ProseTests(unittest.TestCase):
    def test_an_american_word_in_prose_is_found_with_its_line_and_its_british_form(self):
        found = check.findings(check.prose_of_markdown('# A title\n\nThe body is serialized once.\n'),
                               WORDS, PHRASES)
        self.assertEqual(found, [(3, 'serialized', 'serialised')])

    def test_each_family_of_the_list_is_found(self):
        for american, british in (('organize', 'organise'), ('organization', 'organisation'),
                                  ('deserializes', 'deserialises'), ('analyzing', 'analysing'),
                                  ('behavior', 'behaviour'), ('colors', 'colours'),
                                  ('center', 'centre'), ('centered', 'centred'),
                                  ('catalog', 'catalogue'), ('artifacts', 'artefacts'),
                                  ('judgment', 'judgement'), ('fulfill', 'fulfil'),
                                  ('canceled', 'cancelled'), ('defense', 'defence'),
                                  ('licencing', 'licensing'), ('gray', 'grey')):
            with self.subTest(american=american):
                self.assertEqual(markdown(f'One {american} here.\n'), [(american, british)])

    def test_the_british_form_and_the_words_that_only_end_alike_pass(self):
        for line in ('It is organised, serialised and normalised.',
                     'The size of the prize: seize it, or capsize.',
                     'An artefact, a judgement, a licence and a centre.',
                     'The meter of the logs, a program, a dialog, an error, an author.',
                     'A software license is not a noun this check can tell from the verb.'):
            with self.subTest(line=line):
                self.assertEqual(markdown(line + '\n'), [])

    def test_the_letter_case_of_the_word_is_kept(self):
        self.assertEqual(markdown('Organization of the ARTIFACTS.\n'),
                         [('Organization', 'Organisation'), ('ARTIFACTS', 'ARTEFACTS')])

    def test_the_noun_license_is_found_only_in_a_phrase_of_the_list(self):
        self.assertEqual(markdown('Read the license first.\n'), [('the license', 'the licence')])
        self.assertEqual(markdown('I license the code, and they license theirs.\n'), [])

    def test_a_word_is_found_in_a_heading_a_list_a_table_a_quote_and_a_link_text(self):
        for line in ('## The behavior', '- one color', '| a | center |', '> the favor',
                     '[the behavior](https://example.invalid/behavior)', '**artifact**'):
            with self.subTest(line=line):
                self.assertEqual(len(markdown(line + '\n')), 1)


class NameTests(unittest.TestCase):
    def test_a_code_span_is_not_read(self):
        for line in ('Set `color` to red.', 'Set ``the `color` of it`` to red.',
                     'A span that crosses `a line\nbreak with color` in it.'):
            with self.subTest(line=line):
                self.assertEqual(markdown(line + '\n'), [])

    def test_what_follows_a_code_span_is_read(self):
        self.assertEqual(markdown('Set `color` to a color.\n'), [('color', 'colour')])

    def test_a_backtick_with_no_pair_in_its_paragraph_hides_nothing(self):
        self.assertEqual(markdown('A stray ` and a color.\n\nThen a ` and no more.\n'),
                         [('color', 'colour')])

    def test_a_fenced_block_is_not_read_and_the_line_after_it_is(self):
        for fence in ('```', '~~~', '````', '  ```', '> ```'):
            with self.subTest(fence=fence):
                text = f'Before.\n\n{fence}css\ncolor: gray;\n{fence}\n\nThe color after.\n'
                found = check.findings(check.prose_of_markdown(text), WORDS, PHRASES)
                self.assertEqual(found, [(7, 'color', 'colour')])

    def test_a_shorter_fence_inside_a_longer_one_does_not_close_it(self):
        self.assertEqual(markdown('````\n```\ncolor\n```\ncolor\n````\n'), [])

    def test_an_address_is_not_read(self):
        for line in ('See [the page](https://example.invalid/color/organize.html).',
                     'See [the page](../adr/0001-normalize-the-color.md#behavior).',
                     'See https://example.invalid/the-color-of-it for more.',
                     'See <https://example.invalid/color>.',
                     '[the page]: https://example.invalid/color',
                     'See [a page](https://example.invalid/a_(color)_page).',
                     '![a picture](images/color.png)'):
            with self.subTest(line=line):
                self.assertEqual(markdown(line + '\n'), [])

    def test_an_html_attribute_a_comment_and_a_code_element_are_not_read(self):
        for line in ('<img alt="the color" src="color.png">',
                     '<a href="color.html"\n   title="color">it</a>',
                     '<!-- the color -->', '<!-- the\ncolor\nof it -->',
                     'Set <code>color</code> to red.', '<pre>\ncolor\n</pre>',
                     'Press <kbd>color</kbd>.'):
            with self.subTest(line=line):
                self.assertEqual(markdown(line + '\n'), [])

    def test_the_text_between_two_tags_is_read(self):
        self.assertEqual(markdown('<summary>The color</summary>\n'), [('color', 'colour')])

    def test_a_word_shaped_like_an_identifier_or_a_file_is_not_read(self):
        for line in ('NormalizeEmail runs first.', 'The authorizeUser hook.', 'It is color_scheme.',
                     'It is color2.', 'Open normalize.css.', 'Call normalize() first.',
                     'Call JSON.serialize first.', 'Pass color=red.', 'The #color anchor.',
                     'Under src/color it lives.', 'The $color variable.'):
            with self.subTest(line=line):
                self.assertEqual(markdown(line + '\n'), [])

    def test_a_word_before_a_full_stop_a_comma_or_a_bracket_that_closes_is_read(self):
        for line in ('It has a color.', 'It has a color, and more.', '(It has a color)',
                     'It has a "color".', "The color's name.", 'A well-organized color-blind page'):
            with self.subTest(line=line):
                self.assertIn(('color', 'colour'), markdown(line + '\n'))


class EntryPageTests(unittest.TestCase):
    def test_the_english_text_is_read_with_its_line(self):
        self.assertEqual(page(PAGE), [(8, 'organized', 'organised'), (9, 'behavior', 'behaviour'),
                                      (12, 'center', 'centre')])

    def test_the_italian_half_is_not_read_as_english(self):
        self.assertIn('Il color rosso', PAGE.split('\n')[9])
        self.assertEqual(check.prose_of_page(PAGE)[9].strip(), '')

    def test_the_same_italian_line_is_found_once_it_is_marked_english(self):
        english = PAGE.replace('<p lang="it" data-lang="it">', '<p lang="en" data-lang="en">')
        self.assertIn((10, 'color', 'colour'), page(english))
        self.assertIn((10, 'favor', 'favour'), page(english))

    def test_an_attribute_a_script_a_style_and_a_code_element_are_not_read(self):
        kept = '\n'.join(check.prose_of_page(PAGE))
        for name in ('attribute', 'color', 'red', 'gray', 'var'):
            with self.subTest(name=name):
                self.assertNotIn(name, kept)

    def test_every_character_that_is_kept_stands_where_it_stood(self):
        for kept, whole in zip(check.prose_of_page(PAGE), PAGE.split('\n'), strict=True):
            self.assertEqual(len(kept), len(whole))
            for at, character in enumerate(kept):
                if character != ' ':
                    self.assertEqual(character, whole[at])


class ListTests(unittest.TestCase):
    def test_a_stem_is_read_with_each_ending_and_a_dash_is_no_ending(self):
        words, phrases = check.read_words('endings: - s ed\ncolor -> colour\n'
                                          'endings: -\na license -> a licence\n')
        self.assertEqual(words, {'color': 'colour', 'colors': 'colours', 'colored': 'coloured'})
        self.assertEqual([(american, british) for _, american, british in phrases],
                         [('a license', 'a licence')])

    def test_the_list_holds_no_word_that_is_right_as_it_is(self):
        for word in ('size', 'sized', 'prize', 'seize', 'capsize', 'program', 'meter', 'dialog',
                     'license', 'practice', 'error', 'author', 'number', 'enrolled'):
            with self.subTest(word=word):
                self.assertNotIn(word, WORDS)

    def test_no_british_form_of_the_list_is_itself_an_american_form(self):
        self.assertEqual([british for british in WORDS.values() if british in WORDS], [])

    def test_a_line_that_cannot_be_understood_is_refused(self):
        for text in ('color colour\n', 'Color -> Colour\n', 'color ->\n', 'endings:\n'):
            with self.subTest(text=text):
                with self.assertRaises(check.Unreadable):
                    check.read_words(text)
        for text in ('color | docs/a.md | no reason\n', 'color | docs/a.md | text |\n'):
            with self.subTest(text=text):
                with self.assertRaises(check.Unreadable):
                    check.read_allowed(text)


class ScriptTests(unittest.TestCase):
    def setUp(self):
        scratch = tempfile.TemporaryDirectory()
        self.addCleanup(scratch.cleanup)
        self.folder = pathlib.Path(scratch.name)
        self.allowed = self.folder / 'allowed.txt'
        self.allowed.write_text('', encoding='utf-8')
        kept = check.ALLOWED
        check.ALLOWED = self.allowed
        self.addCleanup(setattr, check, 'ALLOWED', kept)

    def write(self, name, text):
        path = self.folder / name
        path.write_text(text, encoding='utf-8', newline='')
        return str(path)

    def test_a_finding_is_one_annotation_with_file_line_word_and_british_form_and_exit_1(self):
        path = self.write('a.md', 'One line.\r\nThe Authorization header is recognized.\r\n')
        code, said = run(path)
        name = path.replace('\\', '/')
        self.assertEqual(code, 1)
        self.assertEqual(said.splitlines(), [
            f'::error file={name},line=2::{name}:2: "Authorization" is "Authorisation" in British '
            'spelling',
            f'::error file={name},line=2::{name}:2: "recognized" is "recognised" in British spelling',
            '2 American spelling(s) in 1 file(s), and 0 line(s) of allowed.txt that allow nothing.'])

    def test_a_clean_file_exits_0_and_says_what_was_read(self):
        code, said = run(self.write('a.md', 'It is recognised, in `color`.\n'))
        self.assertEqual(code, 0)
        self.assertIn('Read the prose of 1 file(s): no American spelling', said)

    def test_the_allow_list_passes_the_word_in_that_file_on_that_line_only(self):
        path = self.write('a.md', 'The Authorization header.\n\nAn Authorization of mine.\n')
        self.allowed.write_text('# a note\nAuthorization | *a.md | Authorization header | the name '
                                'of an HTTP header\n', encoding='utf-8')
        code, said = run(path)
        self.assertEqual(code, 1)
        self.assertEqual(len(said.splitlines()), 2)
        self.assertIn(':3: "Authorization" is "Authorisation"', said)

    def test_the_allow_list_asks_for_the_exact_letter_case_and_for_the_file(self):
        path = self.write('a.md', 'The authorization header.\n')
        for line in ('Authorization | *a.md | * | the name of an HTTP header',
                     'authorization | *b.md | * | the name of an HTTP header'):
            with self.subTest(line=line):
                self.allowed.write_text(line + '\n', encoding='utf-8')
                self.assertEqual(run(path)[0], 1)
        self.allowed.write_text('authorization | *a.md | * | the name of an HTTP header\n',
                                encoding='utf-8')
        self.assertEqual(run(path)[0], 0)

    def test_a_page_is_read_as_a_page_by_its_name(self):
        code, said = run(self.write('index.html', PAGE))
        self.assertEqual(code, 1)
        self.assertEqual(len(said.splitlines()), 4)
        self.assertNotIn(':10:', said)

    def test_a_file_that_is_not_there_and_a_list_that_cannot_be_read_exit_2(self):
        code, said = run(str(self.folder / 'missing.md'))
        self.assertEqual(code, 2)
        self.assertIn('::error::The spelling was not checked:', said)
        self.allowed.write_text('color | no reason\n', encoding='utf-8')
        self.assertEqual(run(self.write('a.md', 'Fine.\n'))[0], 2)


class RepositoryTests(unittest.TestCase):
    def test_the_repository_as_it_is_passes_started_as_the_ci_step_starts_it(self):
        done = subprocess.run([sys.executable, str(SCRIPT)], capture_output=True, check=False)
        said = done.stdout.decode('utf-8', 'replace')
        self.assertEqual(done.returncode, 0, said + done.stderr.decode('utf-8', 'replace'))
        self.assertIn('no American spelling', said)

    def test_it_reads_the_entry_page_and_more_than_a_handful_of_markdown_files(self):
        paths = check.tracked()
        self.assertIn(check.ENTRY_PAGE, paths)
        self.assertGreater(len([path for path in paths if path.endswith('.md')]), 50)

    def test_the_entry_page_gives_english_text_and_none_of_its_italian(self):
        text = (check.ROOT / check.ENTRY_PAGE).read_text(encoding='utf-8')
        kept = ' '.join(' '.join(check.prose_of_page(text)).split())
        self.assertIn('Skip to the content', kept)
        self.assertNotIn('Vai al contenuto', kept)
        self.assertGreater(len(kept.split()), 500)

    def test_a_line_of_the_allow_list_that_allows_nothing_fails_the_whole_run(self):
        kept = check.ALLOWED
        self.addCleanup(setattr, check, 'ALLOWED', kept)
        with tempfile.TemporaryDirectory() as scratch:
            check.ALLOWED = pathlib.Path(scratch) / 'allowed.txt'
            check.ALLOWED.write_text(kept.read_text(encoding='utf-8')
                                     + 'flavor | README.md | * | a name that is gone\n',
                                     encoding='utf-8')
            code, said = run()
        self.assertEqual(code, 1)
        self.assertIn('allows "flavor" in README.md', said)
        self.assertIn('0 American spelling(s)', said)


if __name__ == '__main__':
    unittest.main()
