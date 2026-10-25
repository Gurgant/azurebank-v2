"""Tests of the module through which the infra scripts print their annotations.

What the runner does with a line is not asked here: the escapes are the ones its toolkit writes,
and each script's own tests check its lines as a whole.
"""

import unittest

from workflow_commands import annotation


class AnnotationTests(unittest.TestCase):
    def test_what_the_runner_would_read_as_its_own_is_escaped(self):
        self.assertEqual(annotation('100% of a line\r\n::warning::second'),
                         '::error::100%25 of a line%0D%0A::warning::second')

    def test_a_file_and_a_line_go_in_front_and_the_text_is_escaped_the_same_way(self):
        self.assertEqual(annotation('100% of a line\r\n::warning::second', file='docs/a.md', line=2),
                         '::error file=docs/a.md,line=2::100%25 of a line%0D%0A::warning::second')

    def test_a_file_without_a_line_is_an_annotation_on_the_file(self):
        self.assertEqual(annotation('no such page', file='docs/a.md'),
                         '::error file=docs/a.md::no such page')


if __name__ == '__main__':
    unittest.main()
