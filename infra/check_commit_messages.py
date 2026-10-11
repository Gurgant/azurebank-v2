"""Read the message of every commit of a branch and fail on what a commit message never carries.

    python infra/check_commit_messages.py BASE HEAD

It reads the commits that are on HEAD and not on BASE, merges included, and fails on a line that:
  1. credits someone: it begins with a key that ends in "-by" and a colon, in any letter case.
     That is the form in which git names a signer, a reviewer or a second author, and GitHub
     shows the last as an author of the commit. The key is looked for on every line, wherever
     the line stands, and is not asked of git as a trailer: a credit under a line of dashes,
     above a last paragraph or indented is no trailer to git and still reads as one. A closing
     line with any other key, such as "Gate: 12 passed", is not a credit and passes;
  2. has an at-sign directly before a letter or a digit, outside a pair of backticks that opens
     and closes on that line. An e-mail address is not one: its at-sign follows a letter, a
     digit or an underscore.

A squash puts one commit on main; the commits of the branch stay public, on the pull request's
page and on the branch. There GitHub links a name that follows an at-sign to the account of that
name, and shows a second author for a line that credits one.

The rules are narrow on purpose. A pushed commit is not rewritten, so a message that fails by
mistake costs a new branch: each rule fails on what it is for and on as little else as it can.

A commit whose author's name ends in "[bot]" is not read: an app wrote it, and its words are
not chosen here. The run says how many it left out.

Each offence is one line that GitHub shows as an error annotation, with the short hash of the
commit and the line as the message has it. Exit code 1: at least one offence. Exit code 2:
nothing could be judged, because the range holds no commit, a ref is not known, or the history
is shallow, where git cannot tell which commits are on HEAD and not on BASE. Exit code 0: the
count of commits read.

What it does not read: the title and the text of the pull request, a comment, a review, the name
of the branch, the e-mail address of the author, the name and the address of the committer, a
commit that is already on BASE. What it lets through: a credit that does not begin its line, a
credit in other words, a name in backticks.
"""

import argparse
import re
import subprocess
import sys

CREDIT = 'a line that credits someone'
MENTION = 'an at-sign before a name, outside backticks'

CREDITS = re.compile(r'^\s*[A-Za-z][A-Za-z-]*-by\s*:', re.IGNORECASE)
SPAN = re.compile(r'`[^`]*`')
BLANK_LINE = re.compile(r'(\n[ \t]*\n)')
MENTIONS = re.compile(r'(?<![A-Za-z0-9_])@[A-Za-z0-9]')
APP = '[bot]'


class Unreadable(Exception):
    """The range could not be read, so nothing is known about its messages."""


def git(*arguments):
    done = subprocess.run(['git', *arguments], capture_output=True, check=False)
    if done.returncode != 0:
        said = done.stderr.decode('utf-8', 'replace').strip()
        raise Unreadable(f'git {" ".join(arguments)} exited {done.returncode}: {said}')
    return done.stdout.decode('utf-8', 'replace')


def outside_backticks(message):
    """The lines of a message with a space in place of each backtick span that opens and closes
    on one line, so that what stands on either side of a span stays apart.

    Backticks pair over a paragraph, in the order a reader meets them. A span that crosses a line
    break uses up its two backticks and hides nothing: what stands in it is read, and so is what
    follows it. A backtick with no pair in its paragraph hides nothing either."""
    parts = BLANK_LINE.split(message)
    parts[::2] = [SPAN.sub(lambda span: span.group(0) if '\n' in span.group(0) else ' ', part)
                  for part in parts[::2]]
    return ''.join(parts).splitlines()


def offences(message):
    """What is wrong with one message: (rule, line) pairs, in the order of the two rules."""
    message = message.replace('\r\n', '\n').replace('\r', '\n')
    lines = message.splitlines()
    found = [(CREDIT, line) for line in lines if CREDITS.search(line)]
    found += [(MENTION, line) for line, read in zip(lines, outside_backticks(message))
              if MENTIONS.search(read)]
    return found


def commits(base, head):
    """The full hashes of the commits on head and not on base."""
    if git('rev-parse', '--is-shallow-repository').strip() != 'false':
        raise Unreadable('the history is shallow: which commits are on the head and not on the base '
                         'is not known. Fetch the whole history first.')
    hashes = git('rev-list', f'{base}..{head}', '--').split()
    if not hashes:
        raise Unreadable(f'no commit is on {head} and not on {base}: nothing was read.')
    return hashes


def annotation(text):
    """One line of an error annotation: GitHub reads %, a carriage return and a line feed as its
    own, so each is written the way its runner unescapes it."""
    return '::error::' + text.replace('%', '%25').replace('\r', '%0D').replace('\n', '%0A')


def main(arguments=None):
    parser = argparse.ArgumentParser(description='Fail on a line that credits someone or on an '
                                                 'at-sign before a name in a commit message.')
    parser.add_argument('base', help='the ref whose commits are not read, such as origin/main')
    parser.add_argument('head', help='the ref whose commits are read, such as HEAD')
    options = parser.parse_args(arguments)
    if hasattr(sys.stdout, 'reconfigure'):
        sys.stdout.reconfigure(encoding='utf-8', errors='backslashreplace')
    read, left_out, found = 0, 0, []
    try:
        for commit in commits(options.base, options.head):
            author, _, message = git('log', '-1', '--format=%an%x00%B', commit, '--').partition('\x00')
            if author.endswith(APP):
                left_out += 1
                continue
            read += 1
            found += [(commit[:9], rule, line) for rule, line in offences(message)]
    except Unreadable as error:
        print(annotation(f'The commit messages were not checked: {error}'))
        return 2
    for commit, rule, line in found:
        print(annotation(f'commit {commit} has {rule}: {line}'))
    apps = f' Not read, because an app wrote them: {left_out}.' if left_out else ''
    if found:
        print(f'{len(found)} line(s) in the messages of {read} commit(s) break a rule.{apps}')
        return 1
    print(f'Read the messages of {read} commit(s) on {options.head} and not on {options.base}: '
          f'no line that credits someone, no at-sign before a name.{apps}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
