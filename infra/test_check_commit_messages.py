"""Tests of the check that reads the commit messages of a branch.

Each rule has messages that fail and the nearest messages that pass, because a pushed commit is
not rewritten: a rule that fails on an innocent line costs a new branch. The last tests build a
small repository in a temporary directory and start the script on a range of it, as the CI job
does.

What GitHub does with a message is not asked here. On its page of a commit it links a name that
follows an at-sign to the account of that name, and does not link one inside backticks.
"""

import os
import pathlib
import subprocess
import sys
import tempfile
import unittest

import check_commit_messages as check

SCRIPT = pathlib.Path(__file__).resolve().parent / 'check_commit_messages.py'
CREDIT = 'a line that credits someone'
MENTION = 'an at-sign before a name, outside backticks'
SIGN_OFF = 'Signed-off-by: Someone <someone@example.invalid>'

# The shape of a dependency update's message: a line of dashes, a block that is not prose, and a
# sign-off under it. To git nothing under the dashes is a trailer.
DEPENDENCY_UPDATE = ('Bump the actions group with 1 update\n\n'
                     'Bumps the actions group with 1 update: an-action.\n\n'
                     '---\n'
                     'updated-dependencies:\n'
                     '- dependency-name: an-action\n'
                     '  dependency-version: 2.0.0\n'
                     '...\n\n'
                     'Signed-off-by: an-app[bot] <an-app@example.invalid>\n')


def message(*paragraphs):
    return '\n\n'.join(('Say what the commit does',) + paragraphs) + '\n'


class CreditTests(unittest.TestCase):
    def test_a_sign_off_fails(self):
        self.assertEqual(check.offences(message('A body.', SIGN_OFF)), [(CREDIT, SIGN_OFF)])

    def test_any_key_that_ends_in_by_fails_in_any_letter_case_and_however_it_is_spaced(self):
        for line in ('Reviewed-by: Someone', 'Co-written-by: Someone', 'signed-off-by: Someone',
                     'SIGNED-OFF-BY: Someone', 'Signed-off-by : Someone', '  Signed-off-by: Someone',
                     '\tSigned-off-by:Someone'):
            with self.subTest(line=line):
                self.assertEqual(check.offences(message('A body.', line)), [(CREDIT, line)])

    def test_it_fails_wherever_the_line_stands_trailer_to_git_or_not(self):
        for text in (message('A body.', '---\n' + SIGN_OFF),
                     message('A body.', SIGN_OFF + '\nthanks for the pairing'),
                     message('A body.', SIGN_OFF, 'One more paragraph.'),
                     message('A body.\n' + SIGN_OFF),
                     'Say what the commit does\n' + SIGN_OFF + '\n'):
            with self.subTest(text=text):
                self.assertEqual(check.offences(text), [(CREDIT, SIGN_OFF)])

    def test_the_sign_off_of_a_dependency_update_fails_under_its_line_of_dashes(self):
        self.assertEqual(check.offences(DEPENDENCY_UPDATE),
                         [(CREDIT, 'Signed-off-by: an-app[bot] <an-app@example.invalid>')])

    def test_each_credit_is_its_own_line(self):
        found = check.offences(message('A body.', 'Reviewed-by: One\nReviewed-by: Another'))
        self.assertEqual(found, [(CREDIT, 'Reviewed-by: One'), (CREDIT, 'Reviewed-by: Another')])

    def test_a_closing_line_with_another_key_is_not_a_credit(self):
        for line in ('Gate: 12 passed', 'Refs: 12', 'Change-Id: I0123456789', 'Measured: 12 of 12.',
                     'Backend: 12 passed\nFrontend: 7 passed'):
            with self.subTest(line=line):
                self.assertEqual(check.offences(message('A body.', line)), [])

    def test_a_credit_that_does_not_begin_its_line_passes(self):
        for line in ('The line Signed-off-by: is refused.', '- Signed-off-by: in a list',
                     '`Signed-off-by:` in backticks', '"Signed-off-by:" in quote marks',
                     'Written by: Someone', 'Stood by: nobody'):
            with self.subTest(line=line):
                self.assertEqual(check.offences(message('A body.', line)), [])

    def test_a_subject_with_a_colon_and_the_subject_git_writes_for_a_merge_pass(self):
        self.assertEqual(check.offences('Docs: say what the squash keeps\n'), [])
        self.assertEqual(check.offences("Merge branch 'main' into a-topic\n"), [])
        self.assertEqual(check.offences("Merge remote-tracking branch 'origin/main' into a-topic\n\n"
                                        '# Conflicts:\n#\tREADME.md\n'), [])


class MentionTests(unittest.TestCase):
    def test_an_at_sign_before_a_name_fails_wherever_it_stands_on_the_line(self):
        for line in ('@someone asked for this', 'Thanks to @someone for the report.', 'Seen by (@someone).',
                     'Uses @types/node 24.', 'For @some-team/reviewers: read this.', 'Ask @0x'):
            with self.subTest(line=line):
                self.assertEqual(check.offences(message(line, 'The end.')), [(MENTION, line)])

    def test_it_fails_in_the_subject_too(self):
        self.assertEqual(check.offences('Thank @someone\n'), [(MENTION, 'Thank @someone')])

    def test_an_at_sign_after_a_letter_a_digit_or_an_underscore_is_an_address_not_a_mention(self):
        self.assertEqual(check.offences(message('Write to someone@example.invalid or s_1@example.invalid.',
                                                'The action checkout@v6, the pair 3@2, the name some_@one.')), [])

    def test_an_accented_letter_before_the_at_sign_does_not_make_an_address(self):
        line = 'Seen at the caff' + chr(232) + '@someone.'
        self.assertEqual(check.offences(message(line, 'The end.')), [(MENTION, line)])

    def test_a_name_in_backticks_is_not_a_mention(self):
        self.assertEqual(check.offences(message('The handle `@someone` and the parameter `@accountId` stay; '
                                                '`WHERE Id = @id AND Owner = @owner` too.', 'The end.')), [])

    def test_a_backtick_without_its_pair_hides_nothing(self):
        line = 'The handle `@someone stays.'
        self.assertEqual(check.offences(message(line, 'The end.')), [(MENTION, line)])
        line = 'The parameter `@id` is typed, and @someone said so.'
        self.assertEqual(check.offences(message(line, 'The end.')), [(MENTION, line)])

    def test_doubled_backticks_hide_nothing(self):
        line = 'The parameter ``@id`` is typed.'
        self.assertEqual(check.offences(message(line, 'The end.')), [(MENTION, line)])

    def test_a_span_that_crosses_a_line_break_hides_neither_what_is_in_it_nor_what_follows_it(self):
        found = check.offences(message('The span `starts here\nand ends` @someone `again`.', 'The end.'))
        self.assertEqual(found, [(MENTION, 'and ends` @someone `again`.')])
        found = check.offences(message('The handle `@someone\nstays` in its span.', 'The end.'))
        self.assertEqual(found, [(MENTION, 'The handle `@someone')])

    def test_backticks_pair_within_a_paragraph_and_not_across_a_blank_line(self):
        self.assertEqual(check.offences(message('A backtick ` with no pair.', 'The handle `@someone` stays.')), [])

    def test_an_at_sign_before_anything_but_a_letter_or_a_digit_is_not_a_mention(self):
        self.assertEqual(check.offences(message('The hunk @@ -1,3 +1,4 @@, the ref HEAD@{1}, @{upstream}, 3 @ 2.',
                                                'The end.')), [])

    def test_a_message_with_carriage_returns_is_read_like_any_other(self):
        text = 'Say what the commit does\n\nThanks to @someone.\n'
        self.assertEqual(check.offences(text.replace('\n', '\r\n')), [(MENTION, 'Thanks to @someone.')])
        text = 'Say what the commit does\n\nA backtick ` with no pair.\n\nThe handle `@someone` stays.\n'
        self.assertEqual(check.offences(text.replace('\n', '\r\n')), [])


class AnnotationTests(unittest.TestCase):
    def test_what_the_runner_would_read_as_its_own_is_escaped(self):
        self.assertEqual(check.annotation('100% of a line\r\n::warning::second'),
                         '::error::100%25 of a line%0D%0A::warning::second')


class RangeTests(unittest.TestCase):
    """The script, started as the CI job starts it, on a repository made here: `main` with one
    commit, and `topic` grown from it."""

    def setUp(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.repository = pathlib.Path(folder.name) / 'repository'
        self.repository.mkdir()
        # Git's own defaults, and nothing of the machine: no signing key, no hook path, no template.
        self.environment = dict(os.environ, GIT_CONFIG_GLOBAL=os.devnull, GIT_CONFIG_NOSYSTEM='1',
                                GIT_AUTHOR_NAME='Someone', GIT_AUTHOR_EMAIL='someone@example.invalid',
                                GIT_COMMITTER_NAME='Someone', GIT_COMMITTER_EMAIL='someone@example.invalid')
        self.git('init', '--quiet', '--initial-branch=main')
        self.commit('Start the repository')
        self.git('checkout', '--quiet', '-b', 'topic')

    def git(self, *arguments, text=None, cwd=None, author=None):
        environment = dict(self.environment, GIT_AUTHOR_NAME=author) if author else self.environment
        done = subprocess.run(['git', *arguments], input=None if text is None else text.encode('utf-8'),
                              capture_output=True, env=environment, cwd=cwd or self.repository, check=False)
        self.assertEqual(done.returncode, 0, done.stderr.decode('utf-8', 'replace'))
        return done.stdout.decode('utf-8', 'replace').strip()

    def commit(self, text, author=None):
        self.git('commit', '--quiet', '--allow-empty', '--file=-', text=text, author=author)
        return self.git('rev-parse', '--short=9', 'HEAD')

    def run_check(self, base='main', head='topic', cwd=None):
        done = subprocess.run([sys.executable, str(SCRIPT), base, head], capture_output=True,
                              env=self.environment, cwd=cwd or self.repository, check=False)
        return done.returncode, done.stdout.decode('utf-8'), done.stderr.decode('utf-8', 'replace')

    def test_clean_commits_pass_and_the_run_says_how_many_it_read(self):
        self.commit(message('A body with someone@example.invalid and `@handle`.', 'Gate: 12 passed'))
        self.commit('Add the second thing\n')
        code, out, error = self.run_check()
        self.assertEqual((code, error), (0, ''), out)
        self.assertEqual(out.strip(), 'Read the messages of 2 commit(s) on topic and not on main: no line that '
                                      'credits someone, no at-sign before a name.')

    def test_each_offence_is_an_error_annotation_with_the_short_hash_and_the_line(self):
        self.commit('Add the clean thing\n')
        credit = self.commit(message('A body.', SIGN_OFF))
        both = self.commit(message('Reviewed-by: Someone, 100% of it', 'Thanks to @someone.'))
        code, out, error = self.run_check()
        self.assertEqual((code, error), (1, ''), out)
        self.assertEqual(out.splitlines(), [
            f'::error::commit {both} has a line that credits someone: Reviewed-by: Someone, 100%25 of it',
            f'::error::commit {both} has an at-sign before a name, outside backticks: Thanks to @someone.',
            f'::error::commit {credit} has a line that credits someone: {SIGN_OFF}',
            '3 line(s) in the messages of 3 commit(s) break a rule.'])

    def test_a_commit_an_app_wrote_is_not_read_and_the_run_says_so(self):
        self.commit(DEPENDENCY_UPDATE, author='an-app[bot]')
        code, out, _ = self.run_check()
        self.assertEqual(code, 0, out)
        self.assertEqual(out.strip(), 'Read the messages of 0 commit(s) on topic and not on main: no line that '
                                      'credits someone, no at-sign before a name. '
                                      'Not read, because an app wrote them: 1.')
        credit = self.commit(DEPENDENCY_UPDATE)
        code, out, _ = self.run_check()
        self.assertEqual(code, 1, out)
        self.assertEqual(out.splitlines(), [
            f'::error::commit {credit} has a line that credits someone: '
            'Signed-off-by: an-app[bot] <an-app@example.invalid>',
            '1 line(s) in the messages of 1 commit(s) break a rule. Not read, because an app wrote them: 1.'])

    def test_a_commit_that_is_on_the_base_is_not_read_and_the_merge_that_brings_it_is(self):
        self.commit('Add the clean thing\n')
        self.git('checkout', '--quiet', 'main')
        self.commit(message('A body.', SIGN_OFF))
        self.git('checkout', '--quiet', 'topic')
        self.git('merge', '--quiet', '--no-ff', '--no-edit', 'main')
        self.assertEqual(self.git('log', '-1', '--format=%s'), "Merge branch 'main' into topic")
        code, out, _ = self.run_check()
        self.assertEqual(code, 0, out)
        self.assertIn('Read the messages of 2 commit(s)', out)
        self.git('commit', '--quiet', '--allow-empty', '--file=-', text=message('Thanks to @someone.'))
        code, out, _ = self.run_check()
        self.assertEqual(code, 1, out)
        self.assertEqual(out.splitlines()[1:], ['1 line(s) in the messages of 3 commit(s) break a rule.'])

    def test_a_range_with_no_commit_is_an_error_and_not_a_pass(self):
        code, out, _ = self.run_check()
        self.assertEqual(code, 2, out)
        self.assertEqual(out.strip(), '::error::The commit messages were not checked: no commit is on topic and '
                                      'not on main: nothing was read.')

    def test_a_ref_git_does_not_know_is_an_error_and_not_a_pass(self):
        self.commit('Add the clean thing\n')
        code, out, _ = self.run_check(base='origin/main')
        self.assertEqual(code, 2, out)
        self.assertTrue(out.startswith('::error::The commit messages were not checked: git rev-list '
                                       'origin/main..topic -- exited '), out)
        self.assertEqual(len(out.splitlines()), 1, out)

    def test_a_shallow_history_is_an_error_and_not_a_pass(self):
        self.commit('Add the clean thing\n')
        self.commit('Add the second thing\n')
        shallow = self.repository.parent / 'shallow'
        self.git('clone', '--quiet', '--depth=1', '--branch=topic', self.repository.as_uri(), str(shallow))
        self.assertEqual(self.git('rev-parse', '--is-shallow-repository', cwd=shallow), 'true')
        code, out, _ = self.run_check(base='HEAD~1', head='HEAD', cwd=shallow)
        self.assertEqual(code, 2, out)
        self.assertIn('the history is shallow', out)


if __name__ == '__main__':
    unittest.main()
