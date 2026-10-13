"""Tests of the script that says how much of the hand-written code the tests ran.

The coverage files are written here, small, in the two shapes the two test projects give them:
one root at `backend/` and one at `backend/src/`, the same source named under both. No test
starts .NET or node. The last tests start the script as the CI job does and read its exit code,
because the job trusts nothing else.
"""

import contextlib
import io
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

import coverage_summary as summary

SCRIPT = pathlib.Path(__file__).resolve().parent / 'coverage_summary.py'

# A checkout on Linux and one on Windows, each with the two roots.
LINUX = '/home/runner/work/bank/bank/backend/'
WINDOWS = 'C:\\work\\bank\\backend\\'


def cobertura(source, packages):
    """packages: {assembly: {filename: {line: hits or (hits, 'taken/branches')}}}."""
    text = ['<?xml version="1.0" encoding="utf-8"?>', '<coverage>',
            f'<sources><source>{source}</source></sources>', '<packages>']
    for assembly, files in packages.items():
        text.append(f'<package name="{assembly}"><classes>')
        for filename, lines in files.items():
            text.append(f'<class name="C" filename="{filename}"><lines>')
            for number, hits in lines.items():
                if isinstance(hits, tuple):
                    taken, branches = hits[1].split('/')
                    share = 100 * int(taken) // int(branches)
                    text.append(f'<line number="{number}" hits="{hits[0]}" branch="True" '
                                f'condition-coverage="{share}% ({hits[1]})"/>')
                else:
                    text.append(f'<line number="{number}" hits="{hits}" branch="False"/>')
            text.append('</lines></class>')
        text.append('</classes></package>')
    text += ['</packages>', '</coverage>']
    return '\n'.join(text)


class Folder(unittest.TestCase):
    """A temporary folder that holds the coverage files of one test."""

    def setUp(self):
        holder = tempfile.TemporaryDirectory()
        self.addCleanup(holder.cleanup)
        self.folder = pathlib.Path(holder.name)

    def report(self, name, source, packages):
        target = self.folder / name / summary.REPORT
        target.parent.mkdir(parents=True)
        target.write_text(cobertura(source, packages), encoding='utf-8')
        return target

    def run_main(self, *arguments, environment=None):
        said = io.StringIO()
        with mock.patch.dict(os.environ, environment or {}, clear=False):
            if environment is None:
                os.environ.pop('GITHUB_STEP_SUMMARY', None)
            with contextlib.redirect_stdout(said):
                code = summary.main(list(arguments))
        return code, said.getvalue()


class WholeNames(unittest.TestCase):
    def test_the_two_roots_of_one_checkout_give_one_name(self):
        from_backend = summary.whole(LINUX, 'src/AzureBank.Api/Program.cs')
        from_src = summary.whole(LINUX + 'src/', 'AzureBank.Api/Program.cs')
        self.assertEqual(from_backend, 'src/AzureBank.Api/Program.cs')
        self.assertEqual(from_src, from_backend)

    def test_a_windows_root_gives_the_same_name_as_a_linux_one(self):
        self.assertEqual(summary.whole(WINDOWS + 'src\\', 'AzureBank.Api\\Program.cs'),
                         summary.whole(LINUX, 'src/AzureBank.Api/Program.cs'))

    def test_a_file_named_by_its_full_path_needs_no_root(self):
        self.assertEqual(summary.whole('', LINUX + 'tools/AzureBank.Seeder/Program.cs'),
                         'tools/AzureBank.Seeder/Program.cs')
        self.assertEqual(summary.whole('/elsewhere/', WINDOWS + 'src\\A\\B.cs'), 'src/A/B.cs')

    def test_a_checkout_inside_a_folder_called_backend_is_cut_at_the_right_one(self):
        self.assertEqual(summary.whole('/srv/backend/bank/backend/', 'src/A/B.cs'), 'src/A/B.cs')

    def test_a_source_outside_the_backend_is_refused(self):
        with self.assertRaises(summary.Uncountable):
            summary.whole('/home/runner/.nuget/packages/', 'some.package/1.0.0/File.cs')


class Kinds(unittest.TestCase):
    def test_each_kind(self):
        cases = {
            'src/AzureBank.Api/Services/TransferService.cs': summary.APPLICATION,
            'tools/AzureBank.Seeder/Program.cs': summary.TOOLS,
            'src/AzureBank.Infrastructure/Migrations/20260101_Initial.cs': summary.MIGRATIONS,
            'src/AzureBank.Api/obj/Release/net10.0/Generator/OpenApi.g.cs': summary.GENERATED,
            'tools/AzureBank.Seeder/obj/Release/net10.0/Regex.g.cs': summary.GENERATED,
            'tests/AzureBank.Tests/Fixtures/Rig.cs': summary.TESTS,
            'tests/AzureBank.Tests/obj/Release/net10.0/Regex.g.cs': summary.GENERATED,
        }
        for file, expected in cases.items():
            with self.subTest(file=file):
                self.assertEqual(summary.kind(file), expected)

    def test_a_file_that_is_only_named_like_a_folder_is_hand_written(self):
        self.assertEqual(summary.kind('src/AzureBank.Api/obj'), summary.APPLICATION)
        self.assertEqual(summary.kind('src/AzureBank.Api/Data/Migrations'), summary.APPLICATION)
        self.assertEqual(summary.kind('src/AzureBank.Api/MigrationsHelper/A.cs'),
                         summary.APPLICATION)


class Joining(Folder):
    def test_a_line_ran_when_any_file_saw_it_run_and_is_counted_once(self):
        first = self.report('api', LINUX, {'AzureBank.Api': {
            'src/AzureBank.Api/A.cs': {1: 3, 2: 0, 3: 0, 4: 0}}})
        second = self.report('bff', LINUX + 'src/', {'AzureBank.Api': {
            'AzureBank.Api/A.cs': {1: 0, 2: 5, 3: 0, 4: 0}}})
        assemblies, kinds = summary.count(summary.join([first, second]))
        self.assertEqual(assemblies['AzureBank.Api'][:2], [4, 2])
        self.assertEqual(kinds[summary.APPLICATION][:2], [4, 2])

    def test_branches_take_the_best_count_any_file_saw(self):
        first = self.report('api', LINUX, {'AzureBank.Api': {
            'src/AzureBank.Api/A.cs': {1: (1, '1/4'), 2: (1, '2/2')}}})
        second = self.report('bff', LINUX + 'src/', {'AzureBank.Api': {
            'AzureBank.Api/A.cs': {1: (1, '3/4'), 2: (0, '0/2')}}})
        assemblies, _ = summary.count(summary.join([first, second]))
        self.assertEqual(assemblies['AzureBank.Api'][2:], [6, 5])

    def test_a_copy_of_a_coverage_file_is_not_a_second_file(self):
        first = self.report('api/run', LINUX, {'A': {'src/A/A.cs': {1: 1, 2: 0}}})
        copy = self.folder / 'api' / 'beside-the-trx' / summary.REPORT
        copy.parent.mkdir(parents=True)
        copy.write_bytes(first.read_bytes())
        self.report('bff', LINUX + 'src/', {'A': {'A/A.cs': {1: 0, 2: 0}}})
        self.assertEqual(len(summary.reports_under([self.folder])), 2)
        text, _, _, _ = summary.backend([self.folder], 0)
        self.assertIn('2 coverage file(s), joined line by line', ' '.join(text))

    def test_generated_code_and_migrations_are_in_no_assembly_and_no_total(self):
        self.report('api', LINUX, {
            'AzureBank.Api': {
                'src/AzureBank.Api/A.cs': {1: 1, 2: 0},
                'src/AzureBank.Api/obj/Release/net10.0/G.g.cs': {n: 1 for n in range(1, 101)}},
            'AzureBank.Infrastructure': {
                'src/AzureBank.Infrastructure/Migrations/M.cs': {n: 0 for n in range(1, 201)}},
            'AzureBank.Seeder': {'tools/AzureBank.Seeder/Program.cs': {1: 1, 2: 1}}})
        text, _, failed, figure = summary.backend([self.folder], 75)
        said = '\n'.join(text)
        self.assertEqual(figure, '75.0 %')
        self.assertFalse(failed)
        self.assertIn('| AzureBank.Api | 2 | 1 | 50.0 % |', said)
        self.assertIn('| AzureBank.Seeder | 2 | 2 | 100.0 % |', said)
        self.assertNotIn('| AzureBank.Infrastructure |', said)
        self.assertIn('| **All hand-written code** | 4 | 3 | 75.0 % |', said)
        self.assertIn(f'{summary.MIGRATIONS}, 0 of 200 lines ran', said)
        self.assertIn(f'{summary.GENERATED}, 100 of 100 lines ran', said)

    def test_a_helper_of_the_tests_is_in_no_figure_and_does_not_carry_the_application(self):
        # The application alone is 1 of 4. Counted with the helper it would be 17 of 20, 85.0 %,
        # and pass the floor.
        self.report('api', LINUX, {
            'AzureBank.Api': {'src/AzureBank.Api/A.cs': {1: 1, 2: 0, 3: 0, 4: 0}},
            'AzureBank.Tests': {
                'tests/AzureBank.Tests/Fixtures/Rig.cs': {n: 1 for n in range(1, 17)}}})
        code, said = self.run_main('backend', '--floor', '0', '--floor-application', '85',
                                   str(self.folder))
        self.assertEqual(code, 1)
        self.assertIn('| **All of the application (backend/src)** | 4 | 1 | 25.0 % |', said)
        self.assertIn('| **All hand-written code** | 4 | 1 | 25.0 % |', said)
        self.assertNotIn('| AzureBank.Tests |', said)
        self.assertIn(f'{summary.TESTS}, 16 of 16 lines ran', said)

    def test_two_roots_with_a_relative_name_are_refused(self):
        target = self.report('api', LINUX, {'A': {'src/A/A.cs': {1: 1}}})
        text = target.read_text(encoding='utf-8')
        target.write_text(text.replace('</sources>', '<source>/other/</source></sources>'),
                          encoding='utf-8')
        with self.assertRaises(summary.Uncountable):
            summary.join([target])


class Figures(unittest.TestCase):
    def test_the_figure_is_cut_and_never_rounded_up(self):
        self.assertEqual(summary.percent(9499, 10000), '94.9 %')
        self.assertEqual(summary.percent(9496, 10000), '94.9 %')
        self.assertEqual(summary.percent(1, 1), '100.0 %')
        self.assertEqual(summary.percent(0, 0), 'n/a')

    def test_the_floor_is_held_against_the_counts_and_not_the_printed_figure(self):
        self.assertTrue(summary.under(9499, 10000, 95))
        self.assertFalse(summary.under(9500, 10000, 95))
        self.assertTrue(summary.under(94999, 100000, 95))


class BackendRuns(Folder):
    def three_of_four(self):
        self.report('api', LINUX, {'AzureBank.Api': {
            'src/AzureBank.Api/A.cs': {1: 1, 2: 1, 3: 1, 4: 0}}})

    def test_at_the_floor_it_passes_and_prints_the_figure(self):
        self.three_of_four()
        code, said = self.run_main('backend', '--floor', '75', str(self.folder))
        self.assertEqual(code, 0)
        self.assertIn('**75.0 % of lines** (3 of 4)', said)
        self.assertIn('A line that ran is not a line that was checked.', said)
        self.assertNotIn('::error::', said)

    def test_under_the_floor_it_fails_and_says_both_numbers(self):
        self.three_of_four()
        code, said = self.run_main('backend', '--floor', '75.1', str(self.folder))
        self.assertEqual(code, 1)
        self.assertIn('::error::75.0 % of the lines of hand-written backend code ran, under '
                      'the floor of 75.1 %', said)

    def test_an_empty_folder_fails(self):
        code, said = self.run_main('backend', '--floor', '0', str(self.folder))
        self.assertEqual(code, 1)
        self.assertIn('::error::', said)
        self.assertIn(f'no {summary.REPORT} under', said)

    def test_a_folder_that_does_not_exist_fails(self):
        code, _ = self.run_main('backend', '--floor', '0', str(self.folder / 'missing'))
        self.assertEqual(code, 1)

    def test_a_file_with_no_line_fails_even_with_a_floor_of_zero(self):
        self.report('api', LINUX, {'AzureBank.Api': {}})
        code, said = self.run_main('backend', '--floor', '0', str(self.folder))
        self.assertEqual(code, 1)
        self.assertIn('no line of hand-written code', said)

    def test_only_generated_lines_fail_even_with_a_floor_of_zero(self):
        self.report('api', LINUX, {'AzureBank.Api': {
            'src/AzureBank.Api/obj/Release/G.g.cs': {1: 1, 2: 1}}})
        code, _ = self.run_main('backend', '--floor', '0', str(self.folder))
        self.assertEqual(code, 1)

    def test_a_file_that_is_not_xml_fails(self):
        target = self.folder / summary.REPORT
        target.write_text('not a report', encoding='utf-8')
        code, said = self.run_main('backend', '--floor', '0', str(self.folder))
        self.assertEqual(code, 1)
        self.assertIn('cannot be read', said)

    def test_the_summary_file_gets_the_same_text_and_the_run_a_notice(self):
        self.three_of_four()
        step_summary = self.folder / 'step-summary.md'
        step_summary.write_text('what an earlier step wrote\n', encoding='utf-8')
        code, said = self.run_main('backend', '--floor', '50', str(self.folder),
                                   environment={'GITHUB_STEP_SUMMARY': str(step_summary)})
        self.assertEqual(code, 0)
        written = step_summary.read_text(encoding='utf-8')
        self.assertTrue(written.startswith('what an earlier step wrote\n'))
        self.assertIn('**75.0 % of lines** (3 of 4)', written)
        self.assertIn('::notice title=Backend coverage::75.0 % of the lines of hand-written '
                      'code (3 of 4)', said)

    def test_without_a_summary_file_no_notice_is_printed(self):
        self.three_of_four()
        _, said = self.run_main('backend', '--floor', '50', str(self.folder))
        self.assertNotIn('::notice', said)


class TheFloorOnTheApplication(Folder):
    """The application 3 of 4 lines, the tools 4 of 4: 75.0 % and, together, 87.5 %."""

    def setUp(self):
        super().setUp()
        self.report('api', LINUX, {
            'AzureBank.Api': {'src/AzureBank.Api/A.cs': {1: 1, 2: 1, 3: 1, 4: 0}},
            'AzureBank.Seeder': {'tools/AzureBank.Seeder/Program.cs': {1: 1, 2: 1, 3: 1, 4: 1}}})

    def test_at_its_floor_it_passes_and_the_summary_names_both_floors(self):
        code, said = self.run_main('backend', '--floor', '87.5', '--floor-application', '75',
                                   str(self.folder))
        self.assertEqual(code, 0)
        self.assertIn('| **All of the application (backend/src)** | 4 | 3 | 75.0 % |', said)
        self.assertIn('Floors on lines: 87.5 % on all hand-written code, 75 % on the application '
                      '(backend/src). 1 coverage file(s)', said)
        self.assertNotIn('::error::', said)

    def test_under_its_floor_it_fails_and_says_which_figure_under_which_floor(self):
        code, said = self.run_main('backend', '--floor', '0', '--floor-application', '75.1',
                                   str(self.folder))
        self.assertEqual(code, 1)
        self.assertIn('::error::75.0 % of the lines of the application (backend/src) ran, under '
                      'its floor of 75.1 %', said)

    def test_the_whole_above_its_floor_does_not_carry_the_application_under_its_own(self):
        code, said = self.run_main('backend', '--floor', '87.5', '--floor-application', '80',
                                   str(self.folder))
        self.assertEqual(code, 1)
        self.assertIn('**87.5 % of lines** (7 of 8)', said)
        self.assertEqual(said.count('::error::'), 1)
        self.assertIn('of the application (backend/src) ran, under its floor of 80 %', said)

    def test_the_application_above_its_floor_does_not_carry_the_whole_under_its_own(self):
        code, said = self.run_main('backend', '--floor', '90', '--floor-application', '75',
                                   str(self.folder))
        self.assertEqual(code, 1)
        self.assertEqual(said.count('::error::'), 1)
        self.assertIn('::error::87.5 % of the lines of hand-written backend code ran, under '
                      'the floor of 90 %', said)

    def test_both_under_their_floors_give_a_line_each(self):
        code, said = self.run_main('backend', '--floor', '90', '--floor-application', '80',
                                   str(self.folder))
        self.assertEqual(code, 1)
        self.assertEqual(said.count('::error::'), 2)

    def test_without_the_argument_no_floor_is_held_on_the_application(self):
        code, said = self.run_main('backend', '--floor', '87.5', str(self.folder))
        self.assertEqual(code, 0)
        self.assertIn('Floor on lines: 87.5 %. 1 coverage file(s), joined line by line.', said)
        self.assertNotIn('Floors on lines', said)

    def test_with_a_floor_on_it_the_notice_names_the_application_too(self):
        # A page that shows the application's figure links the run: a visitor who is not signed
        # in sees the notice and not the table, so the notice carries both figures.
        step_summary = self.folder / 'step-summary.md'
        code, said = self.run_main('backend', '--floor', '87.5', '--floor-application', '75',
                                   str(self.folder),
                                   environment={'GITHUB_STEP_SUMMARY': str(step_summary)})
        self.assertEqual(code, 0)
        self.assertIn('::notice title=Backend coverage::87.5 % of the lines of hand-written '
                      'code (7 of 8); the application (backend/src) 75.0 % (3 of 4)', said)

    def test_without_a_floor_on_it_the_notice_is_the_one_line_it_was(self):
        step_summary = self.folder / 'step-summary.md'
        _, said = self.run_main('backend', '--floor', '87.5', str(self.folder),
                                environment={'GITHUB_STEP_SUMMARY': str(step_summary)})
        self.assertIn('::notice title=Backend coverage::87.5 % of the lines of hand-written '
                      'code (7 of 8)\n', said)

    def test_a_floor_on_an_application_with_no_line_fails_even_at_zero(self):
        other = self.folder / 'only-the-tools'
        target = other / summary.REPORT
        target.parent.mkdir(parents=True)
        target.write_text(cobertura(LINUX, {'AzureBank.Seeder': {
            'tools/AzureBank.Seeder/Program.cs': {1: 1, 2: 1}}}), encoding='utf-8')
        code, _ = self.run_main('backend', '--floor', '0', str(other))
        self.assertEqual(code, 0)
        code, said = self.run_main('backend', '--floor', '0', '--floor-application', '0',
                                   str(other))
        self.assertEqual(code, 1)
        self.assertIn('a floor is held on the application (backend/src) and no line of it', said)


class FrontendRuns(Folder):
    def report_of(self, lines=(90, 100), **others):
        total = {'lines': lines, 'statements': (180, 200), 'functions': (30, 40),
                 'branches': (50, 80), **others}
        report = {'total': {name: {'total': pair[1], 'covered': pair[0], 'skipped': 0,
                                   'pct': 'Unknown'} for name, pair in total.items()},
                  '/repo/frontend/src/App.tsx': {}}
        target = self.folder / 'coverage-summary.json'
        target.write_text(json.dumps(report), encoding='utf-8')
        return str(target)

    def test_it_prints_the_four_measures_and_holds_the_floor_on_lines(self):
        code, said = self.run_main('frontend', '--floor', '90', self.report_of())
        self.assertEqual(code, 0)
        self.assertIn('**90.0 % of lines** (90 of 100) in 1 files', said)
        self.assertIn('| Lines | 100 | 90 | 90.0 % |', said)
        self.assertIn('| Branches | 80 | 50 | 62.5 % |', said)
        self.assertIn('| Functions | 40 | 30 | 75.0 % |', said)
        self.assertIn('| Statements | 200 | 180 | 90.0 % |', said)

    def test_low_branches_do_not_fail_and_low_lines_do(self):
        code, _ = self.run_main('frontend', '--floor', '90',
                                self.report_of(branches=(0, 80)))
        self.assertEqual(code, 0)
        code, said = self.run_main('frontend', '--floor', '90', self.report_of(lines=(89, 100)))
        self.assertEqual(code, 1)
        self.assertIn('under the floor of 90 %', said)

    def test_a_missing_report_fails(self):
        code, said = self.run_main('frontend', '--floor', '0', str(self.folder / 'none.json'))
        self.assertEqual(code, 1)
        self.assertIn('cannot be read', said)

    def test_a_report_with_no_line_fails_even_with_a_floor_of_zero(self):
        code, _ = self.run_main('frontend', '--floor', '0', self.report_of(lines=(0, 0)))
        self.assertEqual(code, 1)

    def test_a_report_of_another_shape_fails(self):
        target = self.folder / 'other.json'
        target.write_text('{"numPassedTests": 3}', encoding='utf-8')
        code, said = self.run_main('frontend', '--floor', '0', str(target))
        self.assertEqual(code, 1)
        self.assertIn('not a json-summary report', said)


class AsTheJobStartsIt(Folder):
    def start(self, *arguments):
        environment = {name: value for name, value in os.environ.items()
                       if name != 'GITHUB_STEP_SUMMARY'}
        return subprocess.run([sys.executable, str(SCRIPT), *arguments], capture_output=True,
                              text=True, check=False, env=environment)

    def test_exit_codes(self):
        self.report('api', LINUX, {'AzureBank.Api': {'src/AzureBank.Api/A.cs': {1: 1, 2: 0}}})
        empty = self.folder / 'empty'
        empty.mkdir()
        self.assertEqual(self.start('backend', '--floor', '50', str(self.folder)).returncode, 0)
        self.assertEqual(self.start('backend', '--floor', '51', str(self.folder)).returncode, 1)
        self.assertEqual(self.start('backend', '--floor', '0', str(empty)).returncode, 1)

    def test_a_run_without_a_floor_is_refused(self):
        self.assertNotEqual(self.start('backend', str(self.folder)).returncode, 0)

    def test_the_floor_on_the_application_decides_the_exit_code(self):
        self.report('api', LINUX, {
            'AzureBank.Api': {'src/AzureBank.Api/A.cs': {1: 1, 2: 0}},
            'AzureBank.Seeder': {'tools/AzureBank.Seeder/Program.cs': {1: 1, 2: 1}}})
        held = ('backend', '--floor', '75', '--floor-application')
        self.assertEqual(self.start(*held, '50', str(self.folder)).returncode, 0)
        fell = self.start(*held, '51', str(self.folder))
        self.assertEqual(fell.returncode, 1)
        self.assertIn('under its floor of 51 %', fell.stdout)

    def test_the_frontend_refuses_a_floor_on_the_application(self):
        report = self.folder / 'coverage-summary.json'
        pair = {'total': 100, 'covered': 100}
        report.write_text(json.dumps({'total': {name: pair for name in summary.MEASURES},
                                      '/repo/frontend/src/App.tsx': {}}), encoding='utf-8')
        self.assertEqual(self.start('frontend', '--floor', '90', str(report)).returncode, 0)
        refused = self.start('frontend', '--floor', '90', '--floor-application', '90',
                             str(report))
        self.assertEqual(refused.returncode, 2)
        self.assertIn('--floor-application is held on the backend only', refused.stderr)
        self.assertNotIn('Frontend coverage', refused.stdout)


if __name__ == '__main__':
    unittest.main()
