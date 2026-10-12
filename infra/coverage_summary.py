"""Say how much of the hand-written code the tests ran, and fail under a floor.

    python infra/coverage_summary.py backend --floor 90 FOLDER [FOLDER ...]
    python infra/coverage_summary.py frontend --floor 80 coverage/coverage-summary.json

It prints a short Markdown summary and, where GITHUB_STEP_SUMMARY names a file, appends the same
text to it with a notice for the run's page. The figure the floor is held against is LINES; the
branches are printed beside it and hold no floor.

backend reads every coverage.cobertura.xml under the folders, the files that
`dotnet test --collect:"XPlat Code Coverage"` writes, one for each test project:
  - the test projects instrument the assemblies they share, so the files are joined line by
    line and never summed: a line ran when ANY file saw it run;
  - the files name one source with different roots (`backend/` in one, `backend/src/` in
    another), so every name is made whole with the root its own file declares, then cut to the
    path below `backend/`;
  - a file under an `obj/` folder is written by the build (the source generators) and a file
    under a `Migrations/` folder by the EF tool. Both are printed apart and are in no figure:
    together they are larger than the code they sit beside;
  - what is left is hand-written: the application under `backend/src` and the command-line
    tools under `backend/tools`. The floor is held against the two together.
The branches of a line are joined by the best count any file saw, which is exact where one run
took every branch the other took and too low elsewhere: it is printed as "at least".

frontend reads the `json-summary` report of vitest's coverage. What counts as hand-written is
decided there, in frontend/vitest.config.ts.

What the figure does not say: a line that ran is not a line that was checked. And an assembly
that no test project loads is in no coverage file, so its lines are not counted at all.

Exit code 0: the figure is at the floor or above it. Exit code 1: it is under the floor, or
nothing could be counted: no coverage file, a file that cannot be read, a source outside
`backend/`, or no line at all. A run that counted nothing never passes.
"""

import argparse
import hashlib
import json
import math
import os
import pathlib
import re
import sys
import xml.etree.ElementTree as ET

REPORT = 'coverage.cobertura.xml'
CONDITION = re.compile(r'\((\d+)/(\d+)\)')
TOPS = ('src', 'tools', 'tests')

APPLICATION = 'the application (backend/src)'
TOOLS = 'the command-line tools (backend/tools)'
MIGRATIONS = 'EF migrations'
GENERATED = 'written by the build (obj/)'
HAND_WRITTEN = (APPLICATION, TOOLS)
KEPT_APART = (MIGRATIONS, GENERATED)
MEASURES = ('lines', 'branches', 'functions', 'statements')


class Uncountable(Exception):
    """Nothing can be said about the coverage, so the run fails."""


def whole(source, filename):
    """The path of a source file below `backend/`, whatever root its coverage file declares.

    Both separators are read on every system: the files of a Windows run are joined on Linux
    in the tests, and the other way round.
    """
    filename = filename.replace('\\', '/')
    rooted = filename.startswith('/') or re.match(r'^[A-Za-z]:/', filename)
    path = filename if rooted else (source or '').replace('\\', '/').rstrip('/') + '/' + filename
    parts = [part for part in path.split('/') if part]
    for index in range(len(parts) - 1):
        if parts[index].lower() == 'backend' and parts[index + 1] in TOPS:
            return '/'.join(parts[index + 1:])
    raise Uncountable(f'a source file is not under backend/src, backend/tools or backend/tests: '
                      f'{path}')


def kind(file):
    parts = file.split('/')
    if 'obj' in parts[:-1]:
        return GENERATED
    if 'Migrations' in parts[:-1]:
        return MIGRATIONS
    return TOOLS if parts[0] == 'tools' else APPLICATION


def reports_under(folders):
    """Every coverage file under the folders, one for each content.

    A test run that also writes a .trx leaves a second copy of its coverage file beside it.
    Joining a copy changes no figure; it is left out so that the count of files printed is the
    count of test projects.
    """
    found, seen = [], set()
    for folder in folders:
        for report in sorted(pathlib.Path(folder).rglob(REPORT)):
            digest = hashlib.sha256(report.read_bytes()).digest()
            if digest not in seen:
                seen.add(digest)
                found.append(report)
    return found


def join(reports):
    """{(assembly, file): {line: (hits, branches taken, branches)}} over every report."""
    joined = {}
    for report in reports:
        try:
            root = ET.parse(report).getroot()
        except (ET.ParseError, OSError) as error:
            raise Uncountable(f'{report} cannot be read: {error}') from error
        sources = [node.text or '' for node in root.iter('source')]
        if len(sources) > 1 and any(not _rooted(cls.get('filename', ''))
                                    for cls in root.iter('class')):
            raise Uncountable(f'{report} declares {len(sources)} source roots and names a file '
                              'by a relative path: which root it is under is not known')
        source = sources[0] if sources else ''
        for package in root.iter('package'):
            assembly = package.get('name')
            for cls in package.iter('class'):
                lines = joined.setdefault((assembly, whole(source, cls.get('filename'))), {})
                for line in cls.iter('line'):
                    number = int(line.get('number'))
                    hits = int(line.get('hits'))
                    taken, branches = 0, 0
                    found = CONDITION.search(line.get('condition-coverage') or '')
                    if found:
                        taken, branches = int(found.group(1)), int(found.group(2))
                    before = lines.get(number, (0, 0, 0))
                    lines[number] = (max(hits, before[0]), max(taken, before[1]),
                                     max(branches, before[2]))
    return joined


def _rooted(filename):
    filename = filename.replace('\\', '/')
    return filename.startswith('/') or bool(re.match(r'^[A-Za-z]:/', filename))


def count(joined):
    """(per assembly of hand-written code, per kind): name -> [lines, ran, branches, taken]."""
    assemblies, kinds = {}, {}
    for (assembly, file), lines in joined.items():
        row = [len(lines),
               sum(1 for hits, _, _ in lines.values() if hits > 0),
               sum(branches for _, _, branches in lines.values()),
               sum(taken for _, taken, _ in lines.values())]
        rows = [kinds.setdefault(kind(file), [0, 0, 0, 0])]
        if kind(file) in HAND_WRITTEN:
            rows.append(assemblies.setdefault(assembly, [0, 0, 0, 0]))
        for target in rows:
            for index, value in enumerate(row):
                target[index] += value
    return assemblies, kinds


def percent(part, total):
    """One decimal, cut and not rounded: the figure shown is never above the figure held."""
    if not total:
        return 'n/a'
    return f'{math.floor(1000 * part / total) / 10:.1f} %'


def under(part, total, floor):
    return 100 * part < floor * total


def backend(folders, floor):
    reports = reports_under(folders)
    if not reports:
        raise Uncountable(f'no {REPORT} under: ' + ', '.join(str(folder) for folder in folders))
    assemblies, kinds = count(join(reports))
    total = [sum(kinds.get(name, [0, 0, 0, 0])[index] for name in HAND_WRITTEN)
             for index in range(4)]
    if not total[0]:
        raise Uncountable(f'{len(reports)} coverage file(s) and no line of hand-written code '
                          'in them')

    def cells(name, row):
        return (f'| {name} | {row[0]:,} | {row[1]:,} | {percent(row[1], row[0])} '
                f'| {row[2]:,} | {percent(row[3], row[2])} |')

    text = ['### Backend coverage, hand-written code',
            '',
            f'**{percent(total[1], total[0])} of lines** ({total[1]:,} of {total[0]:,}); '
            f'branches at least {percent(total[3], total[2])} ({total[3]:,} of {total[2]:,}). '
            f'Floor on lines: {floor:g} %. {len(reports)} coverage file(s), joined line by line.',
            '',
            '| Assembly | Lines | Ran | Lines ran | Branches | Taken, at least |',
            '|---|---:|---:|---:|---:|---:|']
    text += [cells(name, row) for name, row in sorted(assemblies.items())]
    text += [cells(f'**All of {name}**', kinds[name]) for name in HAND_WRITTEN if name in kinds]
    text += [cells('**All hand-written code**', total), '']
    text += [f'Kept apart, in no figure above: {name}, {kinds[name][1]:,} of {kinds[name][0]:,} '
             f'lines ran.' for name in KEPT_APART if name in kinds]
    text += ['', 'A line that ran is not a line that was checked.']
    notice = (f'Backend coverage::{percent(total[1], total[0])} of the lines of hand-written '
              f'code ({total[1]:,} of {total[0]:,})')
    return text, notice, under(total[1], total[0], floor), percent(total[1], total[0])


def frontend(summary, floor):
    try:
        with open(summary, encoding='utf-8') as handle:
            report = json.load(handle)
    except (OSError, ValueError) as error:
        raise Uncountable(f'{summary} cannot be read: {error}') from error
    try:
        total = {name: (int(report['total'][name]['covered']), int(report['total'][name]['total']))
                 for name in MEASURES}
    except (KeyError, TypeError, ValueError) as error:
        raise Uncountable(f'{summary} is not a json-summary report: no {error}') from error
    files = len(report) - 1
    ran, lines = total['lines']
    if not lines or files < 1:
        raise Uncountable(f'{summary} counts no line in no file')
    text = ['### Frontend coverage, hand-written code',
            '',
            f'**{percent(ran, lines)} of lines** ({ran:,} of {lines:,}) in {files:,} files, '
            f'by the unit and component tests. Floor on lines: {floor:g} %.',
            '',
            '| Measure | Total | Ran | Share |',
            '|---|---:|---:|---:|']
    text += [f'| {name.capitalize()} | {total[name][1]:,} | {total[name][0]:,} '
             f'| {percent(*total[name])} |' for name in MEASURES]
    text += ['', 'A line that ran is not a line that was checked.']
    notice = (f'Frontend coverage::{percent(ran, lines)} of the lines of hand-written code '
              f'({ran:,} of {lines:,})')
    return text, notice, under(ran, lines, floor), percent(ran, lines)


def main(arguments=None):
    parser = argparse.ArgumentParser(description=__doc__.split('\n', 1)[0])
    parser.add_argument('side', choices=('backend', 'frontend'))
    parser.add_argument('--floor', type=float, required=True,
                        help='the share of lines, in percent, under which the run fails')
    parser.add_argument('paths', nargs='+',
                        help='backend: folders that hold the Cobertura files; frontend: the '
                             'json-summary file')
    options = parser.parse_args(arguments)
    try:
        if options.side == 'backend':
            text, notice, failed, figure = backend(options.paths, options.floor)
        elif len(options.paths) != 1:
            raise Uncountable('frontend reads exactly one json-summary file')
        else:
            text, notice, failed, figure = frontend(options.paths[0], options.floor)
    except Uncountable as error:
        print(f'::error::coverage of the {options.side} could not be counted: {error}')
        return 1
    print('\n'.join(text))
    step_summary = os.environ.get('GITHUB_STEP_SUMMARY')
    if step_summary:
        with open(step_summary, 'a', encoding='utf-8', newline='\n') as handle:
            handle.write('\n'.join(text) + '\n\n')
        print(f'::notice title={notice}')
    if failed:
        print(f'::error::{figure} of the lines of hand-written {options.side} code ran, under '
              f'the floor of {options.floor:g} %')
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
