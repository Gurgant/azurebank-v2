# AGENTS.md

For a coding agent that starts a task in this repository. It is the short form of
[Engineering practices](docs/engineering-practices.md): where the two differ, that page is right.
How the work is led, and by whom, is in the [Engineering Playbook](docs/engineering-playbook.md).

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
| `docs/engineering-traps.md` | what fails silently here, and how each was caught |

## Commands

Backend, from `backend/`:

```bash
dotnet build AzureBank.slnx
dotnet test AzureBank.slnx
dotnet format AzureBank.slnx --verify-no-changes
```

Frontend, from `frontend/`:

```bash
npm run format:check && npm run lint && npm run build && npm test && npm run test:contract:mock
```

Deployment scripts and templates, from the root:

```bash
python -m unittest discover -s infra -p 'test_*.py'
```

Name the solution: a bare or a filtered `dotnet test` under-reports. The type gate is
`npm run build`, not `tsc --noEmit`. The tests that need a SQL Server skip themselves unless
`AZUREBANK_TEST_SQLSERVER` holds a connection string; CI runs them on a real one.

## Before a change is called done

- Run the commands above for every part you touched and report the numbers you read: passed,
  failed, skipped. "Should pass" is not a result.
- A check that prints nothing proves nothing until you have seen it fail. Break the thing once on
  purpose and watch the check go red.
- The running API is the oracle, never the mock. Where the two disagree, the mock is wrong.
- The contract is generated, and so are the frontend's types and validators. To change them,
  change the API and regenerate; the steps are under
  [Merging](docs/engineering-practices.md#merging).
- A test that a decision record names is holding that decision. If it is in your way, the
  decision is what to question, in words, before any edit.
- A change that moves money gets a test against a real host: a unit test cannot see a
  transaction boundary.

## Pull requests and review

- One topic per pull request, in fewer than 100 files. Past 100, CodeRabbit refuses the whole
  pull request.
- A contract change is two pull requests: the endpoint with the regenerated contract and types
  first, the screen that uses them second.
- The commands above are green before a review is asked for. A second, adversarial reader is
  added only where a mistake is expensive: money, access, a database migration.
- Each finding of a review round is checked against the code, then fixed or declined with the
  reason. One commit answers the round, and every thread gets its reply.
- A merge is proposed after a round with no finding, or after two rounds whose findings were all
  minor and are fixed.

## Writing

- A title says the outcome in plain, imperative words, in 72 characters or fewer:
  `Reject negative balances on concurrent withdrawals`. No `type(scope):` prefix. A branch is
  named the same way.
- A document says what is true now. A sentence that turned out wrong is corrected in place, with
  no note about what it used to say.
- A comment explains a constraint the code cannot show. It does not restate the next line and it
  does not argue that the change is right.
- No at-sign in front of a name in any text bound for GitHub: it notifies a real account.
- A commit message ends with its last sentence, with no trailer of any kind.
- No secret, account identifier, server name or address in a file, a message or a command line.
  Secrets come from user-secrets or from the environment.

## What an agent never does

- **Merge.** I merge every pull request myself, and I approve every deployment.
- **Push, or open a pull request, unless the task says so in words.** Otherwise stop at the local
  commit and hand back what the task asks for.
- Write outside the folder it was started in, or switch that folder to another branch.
- Rewrite history that has left the machine: no force-push, no rebase of a pushed branch. A
  branch that is behind takes a merge of `main`.
- Delete a branch: decision records cite merged branches as evidence.
- Dismiss a finding without a reason that will still convince in six months, skip a test, or
  weaken a check to reach green.
- Install a tool, add a dependency or move a pinned version without asking first, naming the
  tool, its source and its size.
- Change a setting of the repository, its rule set or its deployment environment.
