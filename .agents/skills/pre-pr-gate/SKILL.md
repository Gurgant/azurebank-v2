---
name: pre-pr-gate
description: Checks that a branch of AzureBank (Gurgant/azurebank-v2) is ready to become a pull request, and reports the numbers it read. Use before opening a pull request, before pushing a fix made in a review round, and whenever the question is whether a change is ready for review or would pass CI. It covers the scope, the checks for what changed, the generated API contract, one check seen failing, and the title, text and commit messages.
compatibility: Needs git, gh, the .NET SDK, Node.js and Python 3; actionlint and ShellCheck for a workflow change. A missing tool is never installed; the step is reported as not run.
---

# The gate before a pull request

A change becomes a pull request only when every step below has a result that was read, not
expected. Report numbers, copied from the output of each command. A step that could not run is
reported as not run, with the reason, in the report and in the pull request's text: it is never
a pass.

## 1. Scope

```bash
git fetch origin
git status --short                                    # empty: nothing uncommitted rides along
git rev-list --left-right --count origin/main...HEAD  # left number above 0: the branch is behind
git diff --stat origin/main...HEAD | tail -1          # the file count
git diff --name-only origin/main...HEAD               # read the list: one topic
```

- One topic, in fewer than 100 files: past that, the review bot has refused the whole pull
  request on this repository. A file the title does not explain is a second topic: split before
  anything else.
- Behind, on a branch no review has read yet: `git merge origin/main`, resolve any conflict in
  that merge commit, then run step 2 again (and step 3 if it applied) before a push: results
  from before the merge no longer count. A conflict in a generated file is resolved by
  generating it again (step 3), never by hand. Never a rebase or a force-push of a pushed
  branch. A branch that a review has read is not updated for being behind alone: the merge would
  be a new head with no review of its own. The review-round skill, step 6, says what is done
  instead.

## 2. The checks for what changed

`git diff --name-only origin/main...HEAD` decides which rows run. The commands are the ones under
"Commands" in `AGENTS.md`; where the two differ, `AGENTS.md` is right.

| Changed | Run | From |
|---|---|---|
| `backend/` | the three backend commands on `AzureBank.slnx`, with no `--filter` and no `--include`: a filter drops a test project, and a format check that matches no file passes. Two "Test run for" lines are due, one for each test project | `backend/` |
| `frontend/` | the frontend line (`npm ci` first in a fresh checkout) | `frontend/` |
| `infra/`, `.github/workflows/` | `python -m unittest discover -s infra -p 'test_*.py'` | the root |
| `.github/workflows/` | also `actionlint`, with `shellcheck` installed: the infrastructure job runs it, and it reads the shell of every `run:` block | the root |
| any Markdown | every relative link and anchor still resolves. For a page under `docs/`, the root `README.md` or `SECURITY.md`: `npm ci --ignore-scripts && npm run build` from `docs-site/`, whose last line must read `check-links: <n> references in <m> pages, none broken`. The site build does not read Markdown outside `docs/` other than those two (`AGENTS.md`, a README in `backend/` or `frontend/`): open each link the diff adds or changes | the root |
| Markdown under `docs/`, or a README | also the backend row: architecture tests read the pages, so a sentence can turn a test red | `backend/` |
| a DTO, a route, a status code | the contract, step 3 | |

Write down passed, failed and skipped for each suite. A skipped count that differs from the last
CI run on `main` needs its reason (the backend job prints its counts in the step "Test totals").

What these commands do not run. CI runs all of it on every pull request: say which were left to
CI, and never count them as passed.

- The suites that need a SQL Server: they skip themselves without `AZUREBANK_TEST_SQLSERVER`.
- Four frontend suites that need a stack: `npm run test:contract:real`, `npm run test:integration`
  and `npm run test:e2e` (the real stack), and `npm run test:e2e:demo` (the stack in containers,
  with the demo on).
- The frontend unit suite under two other time zones, the run of Schemathesis against the running
  API, the Bruno collection, the check that every model change has a migration, and the compile
  and lint of the deployment templates.

Where the change touches what only one of these can see (a status code, an error body, a flow in
the browser, a query, a migration), bring the stack up (`AGENTS.md` points to the commands) and
run it before the pull request. If that cannot be done, say in the text that the change touches
it and that it was not run here: do not count the suite.

## 3. The contract

Do this when `CommittedOpenApiDocumentTests` failed in the backend row, or when the change is
meant to move the API's surface. Otherwise skip it and say so. The contract and the frontend's
types and validators are generated, never edited.

```bash
# from backend/: writes docs/api/openapiv1.json, then FAILS on purpose
AZUREBANK_REGENERATE_OPENAPI=1 dotnet test --filter "FullyQualifiedName~CommittedOpenApiDocumentTests"
# again without the flag: must pass
dotnet test --filter "FullyQualifiedName~CommittedOpenApiDocumentTests"
# from frontend/
npm run generate:api && npm run generate:zod
```

Read the diff of the contract: every changed path is one the change meant. If the same branch
changes the API's surface and a screen that uses it, stop: that is two pull requests. The first
holds the endpoint with the regenerated contract, types and validators; the screen comes after
the first is merged.

## 4. One check seen failing

For the main claim of the change, make its check fail once. Commit first. Then, as an uncommitted
edit, undo the fix or break the thing, run the test, read the failure, and restore with
`git restore .` from the root, so that `git status --short` is empty again (a file the break
created is deleted by hand). Run the test once more: green. Report what was broken and the line
that went red. A check never seen red has proved nothing yet.

A change with no check of its own (wording in a page, a rename) says so in the text: do not
invent a failure.

## 5. A second reader, only where a mistake is expensive

When the diff touches money movement, access control or a database migration, one independent
reader (another agent, in a fresh session) gets the diff and the decision records it touches
(`docs/adr/`), and not the reasoning behind the change. Its one task: find what is wrong. One
round. Each finding gets an outcome before the pull request opens: fixed (the commit), declined
(the line, the test or the source that shows why), or left for later. A second round only if the
first found a true defect, and only on the lines changed since. Everything else goes without a
second reader. If no second reader can be started, report this step as not run: reading your own
diff again does not count.

## 6. Title and text

- The branch and the title say the outcome, not the problem, in plain imperative words, 72
  characters or fewer: `Reject negative balances on concurrent withdrawals`. No `type(scope):`
  prefix and no `(#N)`.
- The text says why, what changes, what does not change, what was run with the numbers that were
  read, what was left to CI, and what is left for later.
- The text is written to a file and posted with `--body-file`: a file can be read again before
  it is public, and a shell cannot mangle its quotes.
- No at-sign in front of a name: GitHub links it to a real account and notifies it. The one
  exception is the review bot's handle in a request for review.
- `python infra/check_commit_messages.py origin/main HEAD` exits 0: no commit message has a line
  that credits a second author, or an at-sign before a name. A pushed commit is not rewritten,
  so run it before the first push.

## 7. Open, read the checks, stop

Only when the task says to push or to open a pull request (`AGENTS.md`, "What an agent never
does"). Otherwise stop here: report the results of steps 1 to 6 and hand back the local commit,
the title and the text.

```bash
git push -u origin <branch>
gh pr create --title "<title>" --body-file <file>
gh pr checks <number> --watch     # ends when the checks end, not when they pass
gh pr checks <number>             # read every row
```

A pull request is called green only when every check has finished and every row reads `pass`.
Read the description of the review bot's row too: `pass` beside "Review skipped" is not a
review. A failed check goes to the review-round skill. Then the review is asked for (that skill,
step 5) and the work stops. Asked whether it can be merged, give every row and the state of the
review: the decision and the merge are never the agent's.

Every step above is complete on a fresh clone. Where a file named `LOCAL.md` exists beside this
one, it holds the steps of that machine: follow it too.
