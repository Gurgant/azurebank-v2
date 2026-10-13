"""Read the prose of the repository and fail on an American spelling.

    python infra/check_british_spelling.py [FILE ...]

In CI the job "Prose spelling (British)" runs it. The rule set of main does not require that
job: a finding is a red mark on the pull request and blocks no merge.

With no argument it reads every Markdown file that git tracks and the English half of
site/index.html. With arguments it reads those files instead: a name that ends in ".html" is
read as the entry page is, anything else as Markdown.

What it looks for is in british_spelling_words.txt, beside this script: a list of American
forms, each with its British form. A word that is not in the list is not looked for. No rule
guesses from an ending, because "size", "prize" and "seize" end like "organize" and are right.

What it reads as prose, in a Markdown file: everything except a fenced block, a code span, an
HTML comment, an HTML tag with its attributes, the content of a code, pre, kbd or samp element,
the target of a link, and an address. A tag ends at a blank line at the latest, so a "<" in a
sentence hides no more than its own paragraph. In the entry page: the text of every element
whose language is English, which leaves out the Italian half, the attributes, the scripts, the
styles and the code elements. A paragraph or a list item with no end tag ends where the next
one starts, as a browser reads it.

What it reads as a name, and passes: a word in mixed letter case, such as "NormalizeEmail"; a
word joined to a digit, or by an underscore to a letter or a digit, as in "color_scheme"; a
word that follows a dot, a backslash, a hash, a dollar or an equals sign; a word that follows
the slash of a path, as in "/authorize", "docs/adr/color" and "src/color"; a word that is
followed by an opening bracket, an equals sign or a dot and a small letter, as in
"normalize.css". A phrase of the list has the same rules, and its last word must be in small
letters: "the LICENSE file" and "the License" of a title pass.

What is not a name, and is read: a word in underscore emphasis, "_color_"; a word before
"(s)"; a word after a slash that joins two words, "request/behavior"; a word before a dot and
a capital letter, where a space is missing after a full stop. That last rule has a cost: a bare
name of two parts, such as "Color.Red", is read too, and needs backticks.

A name that is none of those and must stay goes in backticks. Where backticks cannot be used,
as for "Organization" in the title of someone else's page, the name is in
british_spelling_allowed.txt, beside this script: the word, the file, a text that the line
holds, and the reason. The word is matched in its exact letter case; a star in its place
stands for every listed word on that line, as for a quoted title. When the whole repository
is read, a line of that file that allows nothing is an error too: the name it was for is gone,
and it would let a later mistake through.

Each finding is one line that GitHub shows as an error annotation on the file and the line: the
word and its British form. Exit code 0: no finding; the line printed says how many files were
read. Exit code 1: at least one finding. Exit code 2: nothing could be judged, because git
could not list the files, a file could not be read, or one of the two lists has a line that
cannot be understood.

What it does not read: a code comment, a commit message, the title and the text of a pull
request, an attribute such as an alternative text, a file that git does not track, and a page
or a part of a page that names no language or a language other than English.

What it lets through: every American form that the list does not name; the noun "license"
anywhere but after one of the words listed with it ("the license" is found, "licenses are
listed" is not), and the verb "to practice", because the same two words are right in "from
theory to practice"; "The License" where it is a title or a heading, because its last word
has a capital letter; a word after a slash whose left side is a code span, because the code
span is blanked first and the word then looks like the start of a path; a phrase of the list
that is broken over two lines; a word that is split over a line end with a hyphen, or by a
mark of emphasis inside it; a word that stands after a "<" and a letter in the same paragraph,
up to the next ">"; an indented code block with no fence around it, which is read as prose.
"""

import fnmatch
import html.parser
import pathlib
import re
import subprocess
import sys

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parent
WORDS = HERE / 'british_spelling_words.txt'
ALLOWED = HERE / 'british_spelling_allowed.txt'
ENTRY_PAGE = 'site/index.html'

FENCE = re.compile(r'^\s*(?:>\s*)*(`{3,}|~{3,})(.*)$')
COMMENT = re.compile(r'<!--.*?-->', re.DOTALL)
BLANK_LINE = re.compile(r'(\n[ \t]*\n)')
SPAN = re.compile(r'(?<!`)(`+)(?!`)(.+?)(?<!`)\1(?!`)', re.DOTALL)
CODE_ELEMENT = re.compile(r'<(code|pre|kbd|samp)\b[^<>]*>.*?</\1\s*>', re.DOTALL | re.IGNORECASE)
TAG = re.compile(r'</?[A-Za-z](?:[^<>\n]|\n(?![ \t]*\n))*>')
LINK_TARGET = re.compile(r'(?<=\])\([^()\s]*(?:\([^()\s]*\)[^()\s]*)*')
REFERENCE = re.compile(r'^(\s*\[[^\]\n]+\]:\s*)(\S+)', re.MULTILINE)
ADDRESS = re.compile(r'(?:https?://|mailto:|www\.)[^\s<>\]]+', re.IGNORECASE)
WORD = re.compile(r'(?<![A-Za-z0-9])[A-Za-z]+(?![A-Za-z0-9])')
NAME_BEFORE = tuple('.\\#$=')
NAME_AFTER = re.compile(r'\((?!s\))|=|\.[a-z]')
BEFORE_A_SLASH = re.compile(r'(\S*)/$')
# The folders a path in this repository starts with. A word after a slash is part of a path
# when what stands before the slash is nothing ("/authorize"), holds a dot or another slash
# ("docs/adr/color"), or is one of these ("src/color"). Any other slash joins two words of a
# sentence ("request/behavior"), and both are read.
FOLDERS = {'backend', 'design-tokens', 'docs', 'docs-site', 'frontend', 'infra', 'observability',
           'scripts', 'site', 'tests', 'src', 'e2e', 'public'}
OPENING = '([{<"\'*_'
# An element that ends an open paragraph when it starts, as HTML says.
ENDS_A_PARAGRAPH = {'address', 'article', 'aside', 'blockquote', 'details', 'div', 'dl',
                    'fieldset', 'figcaption', 'figure', 'footer', 'form', 'h1', 'h2', 'h3', 'h4',
                    'h5', 'h6', 'header', 'hr', 'li', 'main', 'menu', 'nav', 'ol', 'p', 'pre',
                    'section', 'table', 'ul'}
LISTS = {'ul', 'ol', 'menu'}
NOT_READ = {'script', 'style', 'code', 'pre', 'kbd', 'samp'}
VOID = {'area', 'base', 'br', 'col', 'embed', 'hr', 'img', 'input', 'link', 'meta', 'source',
        'track', 'wbr'}


class Unreadable(Exception):
    """Something the check needs could not be read, so nothing is known about the prose."""


def blank(text):
    """The same text with a space in place of every character that is not a line break, so that
    what is left keeps its line and its column."""
    return re.sub(r'[^\n]', ' ', text)


def read_words(text):
    """The list of American forms: (single words, phrases). The first maps a word in small
    letters to its British form; the second holds (pattern, american, british)."""
    words, phrases, endings = {}, [], ['']
    for number, line in enumerate(text.splitlines(), 1):
        line = line.strip()
        if not line or line.startswith('#'):
            continue
        if line.startswith('endings:'):
            endings = ['' if ending == '-' else ending for ending in line[len('endings:'):].split()]
            if not endings:
                raise Unreadable(f'{WORDS.name} line {number} names no ending.')
            continue
        american, arrow, british = (part.strip() for part in line.partition('->'))
        if not arrow or not american or not british or american != american.lower():
            raise Unreadable(f'{WORDS.name} line {number} is not "american -> british" in small '
                             f'letters: {line}')
        if ' ' in american:
            pattern = re.compile(r'(?<![A-Za-z0-9])' + r'\s+'.join(map(re.escape, american.split()))
                                 + r'(?![A-Za-z0-9])', re.IGNORECASE)
            phrases.append((pattern, american, british))
            continue
        for ending in endings:
            words[american + ending] = british + ending
    return words, phrases


def read_allowed(text):
    """The names that stay: a list of (word, file pattern, text on the line, reason, line number)."""
    allowed = []
    for number, line in enumerate(text.splitlines(), 1):
        if not line.strip() or line.lstrip().startswith('#'):
            continue
        parts = [part.strip() for part in line.split('|')]
        if len(parts) != 4 or not all(parts):
            raise Unreadable(f'{ALLOWED.name} line {number} is not "word | file | text on the '
                             f'line | reason": {line}')
        if parts[0] == '*' and parts[2] == '*':
            raise Unreadable(f'{ALLOWED.name} line {number} allows every word on every line of a '
                             f'file: name the word or a text of the line: {line}')
        allowed.append((*parts, number))
    return allowed


def prose_of_markdown(text):
    """The lines of a Markdown file with spaces in place of everything that is not prose."""
    lines, fence = text.split('\n'), None
    for index, line in enumerate(lines):
        mark = FENCE.match(line)
        if fence is None:
            if mark:
                fence = mark.group(1)
                lines[index] = blank(line)
        else:
            if mark and mark.group(1)[0] == fence[0] and len(mark.group(1)) >= len(fence) \
                    and not mark.group(2).strip():
                fence = None
            lines[index] = blank(line)
    text = COMMENT.sub(lambda found: blank(found.group(0)), '\n'.join(lines))
    parts = BLANK_LINE.split(text)
    parts[::2] = [SPAN.sub(lambda found: blank(found.group(0)), part) for part in parts[::2]]
    text = ''.join(parts)
    for pattern in (CODE_ELEMENT, TAG, LINK_TARGET, ADDRESS):
        text = pattern.sub(lambda found: blank(found.group(0)), text)
    text = REFERENCE.sub(lambda found: found.group(1) + blank(found.group(2)), text)
    return text.split('\n')


class EnglishText(html.parser.HTMLParser):
    """Keeps the text of the elements whose language is English, each character where it stood."""

    def __init__(self, text):
        super().__init__(convert_charrefs=False)
        self.kept = [[' '] * len(line) for line in text.split('\n')]
        self.open = [('', '', True)]
        self.feed(text)
        self.close()

    def end_what_has_no_end_tag(self, tag):
        """A paragraph ends where an element starts that cannot stand inside one, and a list
        item where the next item of the same list starts."""
        if tag in ENDS_A_PARAGRAPH:
            for index in range(len(self.open) - 1, 0, -1):
                if self.open[index][0] == 'p':
                    del self.open[index:]
                    break
        if tag == 'li':
            for index in range(len(self.open) - 1, 0, -1):
                if self.open[index][0] in LISTS:
                    break
                if self.open[index][0] == 'li':
                    del self.open[index:]
                    break

    def handle_starttag(self, tag, attrs):
        self.end_what_has_no_end_tag(tag)
        if tag in VOID:
            return
        _, language, read = self.open[-1]
        language = dict(attrs).get('lang') or language
        self.open.append((tag, language, read and tag not in NOT_READ))

    def handle_endtag(self, tag):
        for index in range(len(self.open) - 1, 0, -1):
            if self.open[index][0] == tag:
                del self.open[index:]
                return

    def handle_data(self, data):
        _, language, read = self.open[-1]
        if not read or not language.lower().startswith('en'):
            return
        line, column = self.getpos()
        for character in data:
            if character == '\n':
                line, column = line + 1, 0
                continue
            self.kept[line - 1][column] = character
            column += 1

    def lines(self):
        return [''.join(line) for line in self.kept]


def prose_of_page(text):
    """The lines of an HTML page with spaces in place of everything that is not English text."""
    return EnglishText(text).lines()


def in_the_case_of(word, british):
    if word.isupper():
        return british.upper()
    return british.capitalize() if word[0].isupper() else british


def is_name(line, start, end):
    """Whether what stands from start to end of the line is part of an identifier, of a file
    name or of a path, by what stands before it and after it."""
    before, after = line[:start], line[end:]
    bare = before.rstrip('_')
    if bare != before and bare[-1:].isalnum():
        return True
    bare = after.lstrip('_')
    if bare != after and bare[:1].isalnum():
        return True
    if before.endswith(NAME_BEFORE) or NAME_AFTER.match(after):
        return True
    slash = BEFORE_A_SLASH.search(before)
    if not slash:
        return False
    path = slash.group(1).lstrip(OPENING)
    return not path or '.' in path or '/' in path or path.lower() in FOLDERS


def findings(lines, words, phrases):
    """What is American in the prose: (line number, word, British form), in reading order."""
    found = []
    for number, line in enumerate(lines, 1):
        on_line = []
        for match in WORD.finditer(line):
            word = match.group(0)
            british = words.get(word.lower())
            if british is None or not (word.islower() or word.isupper() or word.istitle()):
                continue
            if is_name(line, match.start(), match.end()):
                continue
            on_line.append((match.start(), word, in_the_case_of(word, british)))
        for pattern, _, british in phrases:
            for match in pattern.finditer(line):
                if not match.group(0).split()[-1].islower() \
                        or is_name(line, match.start(), match.end()):
                    continue
                on_line.append((match.start(), match.group(0), british))
        found += [(number, word, british) for _, word, british in sorted(on_line)]
    return found


def read_file(path, text, words, phrases):
    text = text.replace('\r\n', '\n').replace('\r', '\n')
    lines = prose_of_page(text) if path.lower().endswith(('.html', '.htm')) else prose_of_markdown(text)
    whole = text.split('\n')
    return [(path, number, word, british, whole[number - 1])
            for number, word, british in findings(lines, words, phrases)]


def tracked():
    """The Markdown files that git tracks, and the entry page."""
    done = subprocess.run(['git', 'ls-files', '-z', '--', '*.md', ENTRY_PAGE], cwd=ROOT,
                          capture_output=True, check=False)
    if done.returncode != 0:
        said = done.stderr.decode('utf-8', 'replace').strip()
        raise Unreadable(f'git ls-files exited {done.returncode}: {said}')
    paths = [path for path in done.stdout.decode('utf-8').split('\0') if path]
    if ENTRY_PAGE not in paths or len(paths) < 2:
        raise Unreadable(f'git lists {len(paths)} file(s) and the entry page is '
                         f'{"among them" if ENTRY_PAGE in paths else "not among them"}: '
                         'the repository is not what this check was written for.')
    return paths


def annotation(path, line, text):
    """One line of an error annotation: GitHub reads %, a carriage return and a line feed as its
    own, so each is written the way its runner unescapes it."""
    text = text.replace('%', '%25').replace('\r', '%0D').replace('\n', '%0A')
    return f'::error file={path},line={line}::{text}'


def main(arguments=None):
    arguments = sys.argv[1:] if arguments is None else arguments
    if hasattr(sys.stdout, 'reconfigure'):
        sys.stdout.reconfigure(encoding='utf-8', errors='backslashreplace')
    found, used = [], set()
    try:
        words, phrases = read_words(WORDS.read_text(encoding='utf-8'))
        allowed = read_allowed(ALLOWED.read_text(encoding='utf-8'))
        paths = arguments or tracked()
        for path in paths:
            file = pathlib.Path(path) if arguments else ROOT / path
            for finding in read_file(path.replace('\\', '/'), file.read_text(encoding='utf-8'),
                                     words, phrases):
                path_read, _, word, _, line = finding
                reasons = [number for name, pattern, text, _, number in allowed
                           if name in (word, '*') and fnmatch.fnmatchcase(path_read, pattern)
                           and (text == '*' or text in line)]
                used.update(reasons)
                if not reasons:
                    found.append(finding)
    except (Unreadable, OSError, UnicodeError) as error:
        print(f'::error::The spelling was not checked: {error}')
        return 2
    for path, number, word, british, _ in found:
        print(annotation(path, number, f'{path}:{number}: "{word}" is "{british}" in British spelling'))
    idle = [] if arguments else [entry for entry in allowed if entry[-1] not in used]
    for word, pattern, text, _, number in idle:
        print(annotation(f'infra/{ALLOWED.name}', number,
                         f'infra/{ALLOWED.name}:{number}: allows "{word}" in {pattern} on a line '
                         f'with "{text}", and no such word is there any more: delete the line'))
    if found or idle:
        advice = (' If a word is a name, put it in backticks, or add its line to '
                  'infra/british_spelling_allowed.txt.') if found else ''
        print(f'{len(found)} American spelling(s) in {len(paths)} file(s), and {len(idle)} line(s) '
              f'of {ALLOWED.name} that allow nothing.{advice}')
        return 1
    print(f'Read the prose of {len(paths)} file(s): no American spelling among the '
          f'{len(words) + len(phrases)} forms of {WORDS.name}. '
          f'Names that stay, by {ALLOWED.name}: {len(used)} line(s) used.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
