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
- Agents then work autonomously inside that scope. Deviations and new decisions come back to me,
  and sensitive work gets closer supervision, with checkpoints.
- I merge every pull request myself, and nothing reaches Azure without my approval.
- Every commit on `main` carries my name: I answer for all of it, whoever or whatever wrote it.

## In numbers

| Number | What it counts |
|--:|---|
| **[244](https://github.com/Gurgant/azurebank-v2/pulls?q=is%3Apr+is%3Amerged)** | pull requests merged: each one by me, each one through the automated tests |
| **[9](https://github.com/Gurgant/azurebank-v2/rules)** | checks a pull request must pass today before `main` takes it, plus a security scan |
| **[2,846 · 1,405 · 89](https://github.com/Gurgant/azurebank-v2/actions/workflows/ci.yml?query=branch%3Amain)** | backend tests, frontend tests, and browser tests on the real stack |
| **[64](adr/README.md)** | decisions written down as architecture decision records |
| **4** | agents coordinated: one orchestrator and three working in parallel |
| **0** | merges made by an agent |

Counted on 9 October 2026. Each number links to where it can be counted again.

## The path of a change

`plan → brief → development → checks → pull request → review → merge and deploy`

1. **Plan.** Every change starts from an approach I have approved; larger work gets a written plan
   that shows the end result and what will not break. Nothing is touched before that.
2. **Brief.** I decide what is handed to an agent. Each task becomes a brief that stands on its
   own: the result wanted, what was measured beforehand, the files the agent may touch, the
   checks it must run and what it hands back.
3. **Development.** Each agent works in its own git worktree, on its own branch, so two agents
   never write in the same checkout at the same time. A worktree separates files, not services:
   the brief also names the database the agent may use. It commits locally and stops.
4. **Checks.** Before a pull request exists, the gate runs on what the change touches: format,
   lint, build, the test suites that need no running stack, and the generated contract. The rule
   for every agent: report what was measured, never what was expected. A new or changed check
   counts only after it has been seen to fail.
5. **Pull request.** One topic, fewer than 100 files, and a title that says the outcome. CI then
   runs everything, including the suites that need a real SQL Server, the running stack and a
   browser.
6. **Review.** Every pull request goes through CI, a security scan and an AI review before I
   merge it. Each finding is checked against the code, then fixed or declined with the reason
   written in its thread.
7. **Merge and deploy.** I merge: a green pipeline is necessary, and it does not replace my
   reading of the change. When several pull requests go in together, their combination is built
   and tested first. A merge does not deploy the application: a deployment to Azure is a run
   started by hand that waits for my approval.

## Who does what

| Who | What |
|---|---|
| **Claude Code** | The orchestrator. It drafts the plan I approve, writes the briefs, runs the gate, opens the pull request and works through each review round. Then it stops: the merge is mine. |
| **Codex, Cursor, Antigravity** | Three agents working in parallel, each in its own folder, from its own brief. I start each one myself. The orchestrator reviews each of their branches before it leaves the machine. |
| **A second reader** | Where a mistake is expensive (money, access, a database migration), another agent reads the change with one task: to find what is wrong. |
| **CodeRabbit** | AI review, asked for on every pull request. |
| **CodeQL** | Security scan of the C#, the TypeScript and the workflows. |

Models are chosen per task: the most capable one orchestrates and verifies, a cheaper one does the
mechanical work. One [`AGENTS.md`](../AGENTS.md) gives every agent the same commands, checks and
rules. Two agent skills hold the repeatable procedures: the gate before a pull request and the
review round. Where a check needs a real browser outside the test suite, an agent drives Chrome
through an MCP server and reports what it read.

## What stands between an agent and `main`

- **The rule set.** `main` takes a change only as a squash-merged pull request that has passed
  nine required checks and a CodeQL scan. The rule set has no bypass list, so it binds my own
  account too.
- **No agent merges: I do.** The orchestrator is stopped by a permission rule and a hook that
  refuse the merge command before it runs. The other three are held one step earlier: their
  folders refuse a push, and their brief ends at the local commit.
- **Nothing reaches Azure without me.** A deployment is a run started by hand, and the live
  environment has a required reviewer and no admin bypass.

An instruction in a prompt is not a permission boundary. That is why each of these is a
mechanism, and why the page says where each one stops.

## The contract between backend and frontend

The API contract is generated by the backend, never written by hand, and a test fails whenever
the committed contract is not exactly what the API generates. The frontend's types and its
runtime validators for the API are generated from that contract, and CI fails if they drift from
it. The mock is held to the real API: one suite of contract assertions runs against both, and
the client's data layer and the browser suite run against the real stack. A contract change ships
first; the screen that uses it follows in a second pull request.

## Where to check

What happens on GitHub can be checked there: the numbers above link to the merged pull requests
with their review threads, to the rule set and to the CI runs. The rules are in
[Engineering practices](engineering-practices.md) and [`AGENTS.md`](../AGENTS.md), the decisions
in the [decision records](adr/README.md), and the system in
[How AzureBank works](architecture/overview.md).
