"""Tests that read the pages of the repository as they are, for the spelling check.

    python -m unittest discover -s infra -p 'prose_test_*.py'

These are the tests that a word in a page can turn red, so they are not in
test_check_british_spelling.py: the name of this file keeps it out of the discovery of
'test_*.py', which the Infrastructure job runs and the rule set of main requires. The job
"Prose spelling (British)" of CI runs this file, and the rule set does not require that job: a
spelling is reported there and blocks no merge.

The first test runs the check on the repository. The second asks that every word in -ise or
-yse that a page writes has its American twin in british_spelling_words.txt, so that the list
follows what the pages write; its message says what to do with the words it names.
"""

import pathlib
import re
import subprocess
import sys
import unittest

import check_british_spelling as check
import test_check_british_spelling as fixed

SCRIPT = pathlib.Path(__file__).resolve().parent / 'check_british_spelling.py'


def written():
    """Every word of the prose that the check reads, in small letters."""
    words = set()
    for path in check.tracked():
        text = (check.ROOT / path).read_text(encoding='utf-8').replace('\r\n', '\n')
        lines = check.prose_of_page(text) if path.endswith('.html') \
            else check.prose_of_markdown(text)
        words.update(word.lower() for line in lines for word in re.findall('[A-Za-z]+', line))
    return words


class ProseOfTheRepositoryTests(unittest.TestCase):
    def test_the_repository_as_it_is_passes_started_as_the_ci_step_starts_it(self):
        done = subprocess.run([sys.executable, str(SCRIPT)], capture_output=True, check=False)
        said = done.stdout.decode('utf-8', 'replace')
        self.assertEqual(done.returncode, 0, said + done.stderr.decode('utf-8', 'replace'))
        self.assertIn('no American spelling', said)

    def test_every_word_in_ise_that_the_prose_writes_has_its_american_twin_in_the_list(self):
        words = written()
        self.assertGreater(len([word for word in words if fixed.twin(word)[0]]), 20)
        missing = fixed.without_a_twin(words)
        self.assertEqual(
            missing, [],
            f'A page writes {", ".join(missing)}, and infra/british_spelling_words.txt has no '
            'American twin for it, so the check would not find the form with a z. If the word is '
            'a verb in -ise or -yse, add its stem to that list, as the line "organiz -> organis" '
            'does for "organise". If it only ends alike and is spelt with an s everywhere, as '
            '"franchise", add it to ONLY_END_ALIKE in infra/test_check_british_spelling.py.')


if __name__ == '__main__':
    unittest.main()
