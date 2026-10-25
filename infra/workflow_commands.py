"""The lines that the infra scripts print for GitHub's runner to read as its own.

    from workflow_commands import annotation, utf8_stdout

The runner reads a line of standard output that begins with "::error" as an error annotation,
and shows it on the pull request; with a file and a line, it shows it on that line of that file.
In the message it reads %, a carriage return and a line feed as its own: a message that holds a
line break, such as what git said on standard error, would end at it, and the runner would read
what follows as a line of its own. Each script prints its annotations through this module, so
every one is escaped the same way.

What it does not escape: the file of an annotation, which the scripts take from git or from
their arguments as a path.
"""

import sys


def annotation(text, file=None, line=None):
    """One line of an error annotation, on that line of that file when a file is given. Each %,
    carriage return and line feed of the text is written the way the runner unescapes it."""
    text = text.replace('%', '%25').replace('\r', '%0D').replace('\n', '%0A')
    if file is None:
        return f'::error::{text}'
    return f'::error file={file},line={line}::{text}'


def utf8_stdout():
    """Standard output in UTF-8, whatever the code page of the console: a path or a line of a
    message can hold any character. A stream that a test put in its place has no reconfigure and
    is left as it is."""
    if hasattr(sys.stdout, 'reconfigure'):
        sys.stdout.reconfigure(encoding='utf-8', errors='backslashreplace')
