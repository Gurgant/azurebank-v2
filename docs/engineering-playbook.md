# AzureBank Engineering Playbook

**Human-led, multi-agent-assisted software development.**

I build AzureBank alone, with four AI coding agents working under my direction. Agents propose and
build, mechanisms hold the line, and I decide. This page is the playbook: what I decide, the path
a change takes from a plan to the live demo, and what stands between an agent and `main`.

## My role

> I read the code I merge, and I do not merge what I cannot explain.

- I designed the system and wrote part of the code by hand.
- I decide what is built and in which order, and I set the rules the agents work under.
- Before any implementation I investigate the problem, set the requirements and constraints, and
  approve the approach; larger changes get a written plan.
- Agents then work autonomously inside those limits. Deviations and new decisions come back to
  me, and I supervise sensitive work more closely, at set checkpoints.
- I merge every pull request myself, and nothing reaches Azure without my approval.
- Every commit on `main` carries my name: I answer for all of it, whoever or whatever wrote it.

## In numbers

| Number | What it counts |
|--:|---|
| **[323](https://github.com/Gurgant/azurebank-v2/pulls?q=is%3Apr+is%3Amerged)** | pull requests merged: each one by me, each one through the automated tests |
| **[9](https://github.com/Gurgant/azurebank-v2/rules/18930321)** | checks now required before `main` takes a pull request, plus a security scan |
| **[3,243](https://github.com/Gurgant/azurebank-v2/actions/runs/38551127398/job/115809063440)** | backend tests that run with no database |
| **[494](https://github.com/Gurgant/azurebank-v2/actions/runs/38551127398/job/115809063267)** | backend proofs on a real SQL Server, in a job of their own |
| **[1,561](https://github.com/Gurgant/azurebank-v2/actions/runs/38551127398/job/115809063155)** | frontend tests |
| **[90](https://github.com/Gurgant/azurebank-v2/actions/runs/38551127398/job/115809063331)** | browser tests on the real stack |
| **[64](adr/README.md)** | decisions written down as architecture decision records |
| **4** | agents coordinated: one orchestrator and three working in parallel |
| **0** | merges made by an agent |

Counted on 14 October 2026. The four test numbers are those of one CI run on `main`,
[run 38551127398](https://github.com/Gurgant/azurebank-v2/actions/runs/38551127398), each linked to
its job, whose page shows it; the other linked numbers lead to where they can be counted again.
The two without a link, 4 and 0, are my own statement: on GitHub every merge shows one account,
mine.

## The path of a change

`plan → brief → development → checks → pull request → review → merge → deploy`

1. **Plan.** Work starts from an approach I have approved. Larger work gets a written plan that
   shows the end result and what will not break, and nothing is touched until I approve it.
2. **Brief.** I decide what is handed to an agent. Each task becomes a brief that stands on its
   own: the result wanted, what was measured beforehand, the files the agent may touch, the
   checks it must run and what it hands back.
3. **Development.** Each agent works in its own git worktree, on its own branch, so two agents
   never write in the same checkout at the same time. A worktree separates files, not services:
   where a task needs a database, the brief names one of its own. The agent commits locally and
   stops.
4. **Checks.** Before a pull request exists, the gate runs on what the change touches: format,
   lint, build, the test suites that need no running stack, and the generated contract. The rule
   for every agent: report what was measured, never what was expected. A new or changed check
   must be seen to fail on the defect it is meant to detect.
5. **Pull request.** One topic, fewer than 100 files, and a title that says the outcome. CI then
   runs every suite, including those that need a real SQL Server, the running stack and a
   browser, and the demo's, which restarts two containers of the stack.
6. **Review.** Every pull request goes through CI and a security scan, and an AI review starts
   by itself or is asked for on each, before I merge it. Each finding is checked against the
   code, then fixed, declined with the reason, or set aside for a change of its own, with a
   written answer on the pull request.
7. **Merge.** I merge: a green pipeline is necessary, but it does not replace my reading of the
   change. When several pull requests go in together, their combination is built and tested
   first.
8. **Deploy.** A merge does not deploy the application: a deployment to Azure is a run started
   by hand that waits for my approval.

## Who does what

| Who | What |
|---|---|
| **Claude Code** | The orchestrator. It drafts the plan I approve, writes the briefs, runs the gate, opens the pull request and works through each review round. Then it stops: the merge is mine. |
| **Codex, Cursor, Antigravity** | Three agents working in parallel, each in its own folder, from its own brief. I start each one myself. The orchestrator reviews each of their branches before it leaves the machine. |
| **A second reader** | Where a mistake is expensive (money, access, a database migration), another agent reads the change with one task: to find what is wrong. |
| **CodeRabbit** | AI review on pull requests. |
| **CodeQL** | Security scan of the C#, the TypeScript and the workflows. |

Models are chosen per task: the most capable one orchestrates and verifies, a cheaper one does the
mechanical work. One [`AGENTS.md`](../AGENTS.md) gives every agent the same commands, checks and
rules. Two agent skills, each a file of numbered steps, hold the repeatable procedures:
[the gate before a pull request](../.agents/skills/pre-pr-gate/SKILL.md) and
[the review round](../.agents/skills/review-round/SKILL.md). Where a check needs a real browser
outside the test suite, an agent drives Chrome through an MCP server and reports what it read.

## What stands between an agent and `main`

- **The rule set.** `main` takes a change only as a squash-merged pull request that has passed
  nine required checks and a CodeQL scan. The rule set has no bypass list, so it binds my own
  account too; the repository is mine, so I can still edit the rule set.
- **No agent merges: I do.** The orchestrator is stopped by a permission rule and a hook that
  refuse the merge command before it runs. The other three are held one step earlier: their
  folders refuse a push, and their brief ends at the local commit.
- **Nothing reaches Azure without my approval.** A merge does not deploy the application: a
  deployment is a separate run, started by hand, that stops until I approve it.

An instruction in a prompt is not a permission boundary: that is why each of these is a
mechanism. Each has its limit. The hook and the folders that refuse a push run on my machine:
they stop a habit or a mistake, not an agent set on getting around them. The rule set and the
reviewer on the live environment are enforced by GitHub.

## Harness engineering

The instructions I give the agents, the checks that run on their changes and the limits on what
they may do are, together, the harness. Building it and correcting it is harness engineering:
the agents are products made by others, and the harness is the part I build. I merge every pull
request myself.

I revise the harness when a mistake shows a missing check or rule. When a program can detect the
mistake, I add an automated check for that case; when only a reader can judge it, I add a written
rule. [Harness engineering on AzureBank](harness-engineering.md) follows one pull request from
start to finish, tells three episodes in which a mistake led to a new check, and says what I have
not measured.

## The contract between backend and frontend

The API contract is generated by the backend, never written by hand, and a test fails whenever
the committed contract is not what the API generates. The frontend's types and its
runtime validators for the API are generated from that contract, and CI fails if they drift from
it. One case shows why both checks exist: with a property added to the API and the committed
contract left as it was, the frontend's two checks stay green, because both regenerate from that
same file. The backend's test, which compares the committed contract with what the API
generates, is what catches it
([ADR-0053](adr/0053-the-committed-contract-is-what-the-api-generates.md)). The mock is held to
the real API: one suite of contract assertions runs against both, and
the client's data layer and the browser suite run against the real stack. The rule for a contract
change: it ships first, and the screen that uses it follows in a second pull request.

## Where to check

What happens on GitHub can be checked there: the numbers above link to the merged pull requests
with their review threads, to the rule set and to a CI run. The rules are in
[Engineering practices](engineering-practices.md) and [`AGENTS.md`](../AGENTS.md), the decisions
in the [decision records](adr/README.md), and the system in
[How AzureBank works](architecture/overview.md).
