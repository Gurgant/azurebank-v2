# AGENTS.md

What a coding agent needs at the start of a task in this repository. The reasons and the local
setup are in [Engineering practices](docs/engineering-practices.md): where the two differ, that
page is right. How the work is led, and by whom, is in the
[Engineering Playbook](docs/engineering-playbook.md). Where this page says "I", it is me,
Vladislav Aleshaev, the author of the repository.

## What this is

AzureBank is a banking application that runs as a live demo on Azure. A React single-page app
talks to a BFF that owns the browser session; the BFF is the only client of the API; the API holds
the money rules and the audit trail. [How AzureBank works](docs/architecture/overview.md) is the
one document to read first.

| Path | What is there |
|---|---|
| `backend/src/AzureBank.Api` | the API |
| `backend/src/AzureBank.Bff` | the BFF |
| `backend/tests` | the two test projects that `backend/AzureBank.slnx` names |
| `frontend` | React, TypeScript, Vite; its own rules are in `frontend/CONVENTIONS.md` |
| `infra` | the Azure deployment: templates, scripts and their tests |
| `docs/api/openapiv1.json` | the API contract. It is generated: never edit it by hand |
| `docs/adr` | the decision records. Read the one a file cites before changing what it holds |
| `docs/engineering-traps.md` | what fails silently here, why nothing reports it, and what to do |

## Commands

Backend, from `backend/`:

```bash
dotnet build AzureBank.slnx --configuration Release
dotnet test AzureBank.slnx --configuration Release --no-build
dotnet format AzureBank.slnx --verify-no-changes
```

Frontend, from `frontend/`:

```bash
npm run format:check && npm run lint && npm run build && npm test && npm run test:contract:mock
```

Deployment scripts, templates and workflows, from the root:

```bash
python -m unittest discover -s infra -p 'test_*.py'
actionlint        # for a change to a workflow; with ShellCheck installed, as in CI
```

Build in Release: warnings are errors there, as in CI. Name the solution and do not filter: a
filter on `AzureBank.Tests` drops the BFF's suite and still reports success. The type gate is
`npm run build`, not `tsc --noEmit`.

These run without a stack. The tests that need a SQL Server skip themselves unless
`AZUREBANK_TEST_SQLSERVER` holds a connection string, and three frontend suites need the real
stack up: `npm run test:contract:real`, `npm run test:integration` and `npm run test:e2e`
(`frontend/CONVENTIONS.md` says what that stack must be, and
[Local setup](docs/engineering-practices.md#local-setup) has the commands). A fourth,
`npm run test:e2e:demo`, needs the stack in containers with the demo on. CI runs all of them on
every pull request.

## Before a change is called done

- Run the commands above for every part you touched and report the numbers you read: passed,
  failed, skipped. "Should pass" is not a result.
- A new or changed check proves nothing until you have seen it fail. Break the thing once on
  purpose and watch the check go red.
- A change to a page under `docs/` also runs the backend commands: architecture tests read the
  pages.
- The running API is the oracle for what the server does, never the mock. Where the two disagree,
  read the contract and the decision record first: if the API is right the mock changes, and if
  the API is wrong that is a defect to report, not a mock to bend.
- The contract is generated, and so are the frontend's types and validators. To change them,
  change the API and regenerate; the steps are under
  [Merging](docs/engineering-practices.md#merging).
- A test that a decision record names is holding that decision. If it is in your way, the
  decision is what to question, in writing, before any edit.
- A change that moves money gets a test against a real host: a unit test cannot see a
  transaction boundary.

## Pull requests and review

- One topic per pull request, in fewer than 100 files. Past 100, CodeRabbit refuses the whole
  pull request on this repository.
- A contract change is two pull requests: the endpoint with the regenerated contract and types
  first, the screen that uses them second.
- The commands above are green before a pull request is opened. A second, adversarial reader is
  added only where a mistake is expensive: money, access, a database migration.
- Each finding of a review round is checked against the code, then fixed or declined with the
  reason. One commit answers the round, and every thread gets its reply.
- A merge is proposed when every required check is green on the last head, and either the last
  round has no finding or two rounds in a row had only findings the reviewer marked minor, all
  fixed.
- When several pull requests are proposed together, their combination is checked first: merged
  in the proposed order on a scratch tree, then built and tested.
- The steps in full are in two files:
  [the gate before a pull request](.agents/skills/pre-pr-gate/SKILL.md) and
  [one review round](.agents/skills/review-round/SKILL.md).

## Writing

- A title says the outcome in plain, imperative words, in 72 characters or fewer:
  `Reject negative balances on concurrent withdrawals`. No `type(scope):` prefix. A branch is
  named the same way.
- A document says what is true now. A sentence that turned out wrong is corrected in place, with
  no note about what it used to say.
- A comment explains a constraint the code cannot show. It does not restate the next line and it
  does not argue that the change is right.
- No at-sign in front of a name in a commit message, a pull request, a comment or a review:
  GitHub links it to the real account of that name, and outside a commit message notifies it.
  The one exception is the review bot's own handle in a request for review.
- A commit message has no line that credits a second author or a signer: one author answers
  for `main`, and the tools are named in the Engineering Playbook.
- No real secret, subscription or tenant identifier, database server name, mailbox or public IP
  address in a file, a message or a command line. Secrets come from user-secrets or from the
  environment.

## What an agent never does

- **Merge.** I merge every pull request myself, and I approve every deployment of the application.
- **Push, or open a pull request, unless the task says so explicitly.** Otherwise stop at the
  local commit and hand back what the task asks for.
- Write outside the folder it was started in, or switch that folder to another branch.
- Rewrite history that has left the machine: no force-push, no rebase of a pushed branch. A
  branch that is behind takes a merge of `main`.
- Delete a branch: a squash leaves one commit on `main`, and the branch is where the commits
  behind it stay.
- Dismiss a finding without a reason that will still convince in six months, skip a test, or
  weaken a check to reach green.
- Install a tool, add a dependency or move a pinned version without asking first, naming the
  tool, its source and its size.
- Change a setting of the repository, its rule set or its deployment environment.
