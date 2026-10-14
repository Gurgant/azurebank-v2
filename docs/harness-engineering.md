# Harness engineering on AzureBank

AzureBank is a banking application (a .NET API, a React frontend, SQL Server) that I build
alone, with four AI coding agents working under my direction. I designed it and I read every
change I merge; 64 [decision records][adr] say why it is built the way it is. Harness
engineering is the part of that work that is not the application: what I build around agents
made by others. It has three parts:

- **what they read:** I give every agent the same public [instruction file][ag], with the
  commands, what "done" means and [what an agent never does][never];
- **what runs on their changes:** the jobs of two workflows, [CI][ci] and the
  [contract tests][bru], a security scan and an AI review; eleven of those checks are
  [required][rs] today, and `main` does not take a change that has not passed them
  ([that is recent](#what-this-does-not-show));
- **what limits them:** on my computer the orchestrator's merge command is refused before it
  runs, and the folders of the other three agents refuse a push; on GitHub a deployment waits
  for my approval. I merge every pull request myself.

I change the harness when a mistake shows a gap in its checks or its instructions. When a
program can detect the mistake, I add an automated check for that case. When only a reader can
judge it, I add a written rule, and that rule has no automated check behind it. One example: a
mistake with dates could not show in UTC, and every CI job ran in UTC. I added a job that runs
the frontend's tests in two other time zones; six hours after it reached `main` it failed on
another test with the same kind of mistake. [Three episodes](#three-episodes) tell that one and
two more in full.

This page takes the public part of the harness piece by piece, each with the file, the job or
the rule that shows it, and says where a part rests on my word alone. How I lead the work is in
the [Engineering Playbook][pb]; its [table][who] names the agents. The numbers in
[the last table](#the-numbers) were counted on 12 October 2026, and the first of them again on
14 October 2026; it says where each can be counted again.

## One change, start to finish

In [#283][p283] I corrected two code comments that described the build wrongly and, after the
review, the message one test prints when it fails. I chose it because it is small: the whole
path fits on one screen. No test checks anything different after it, and no step of a workflow
changes.
The test is the one of the [third episode](#a-defect-the-build-did-not-report-september-2026).

1. **Before the pull request.** The change touches two files, a test and a CI workflow. For
   those two kinds of file [`AGENTS.md`][ag] lists what to run first: the Release build, the
   backend tests and the format check, then the tests of the deployment scripts and the lint of
   the workflows. The pull request's text reports the numbers as read: 2,192 and 697 backend
   tests passed; 371 were skipped on my machine because they need a SQL Server, and CI runs
   them.
2. **On GitHub.** The last commit has 17 results: twelve jobs of the two workflows, four of the
   security scan and the status of the AI review. On that day nine of the jobs were the required
   checks, and the security scan had a rule of its own; the other three jobs and the review's
   status were not required.
3. **The AI review, first round.** One finding, marked minor, and it was right. The comment had
   been corrected, but the message the test prints when it fails still gave the old explanation
   in other words. Before the pull request I had searched the code for the comment's own words,
   not for other places that said the same wrong thing differently. One commit fixed it. The
   reply in the review thread says what changed, and that the test was made to fail once on
   purpose, to see the new message printed.
4. **The second round.** No finding. One required job had been stopped by its own 15-minute
   limit while its containers were still starting, before it had fetched the change: a failure
   that had nothing to do with the change. It was run again once and passed.
5. **The merge.** I read the diff, 16 lines added and 14 removed in two files, and merged.

My search found the comment's wording. It missed the same wrong explanation in the test's
failure message, and on this pull request the AI review found it.

## Three episodes

For each episode: the mistake, what I changed in the harness, and what the new check found.

### A mock that agreed only with itself (July and August 2026)

For the frontend's tests I used a simulated API, a mock, as the only reference for what the
server does. The mock had been written by reading the server's code. Twice the tests and the
server turned out to disagree: the mock answered 422 where the API answers 400, and a test
asserted an error code the API never sends ([#67][p67] names both). The rule is a decision of
[ADR-0029][a29]: every expected value is measured on the real stack, never inferred. Today it
is a line of [`AGENTS.md`][ag]: "The running API is the oracle for what the server does, never
the mock." The check is in the same record: one suite of contract assertions that runs against
the mock and against the real stack. On its first run, in [#67][p67], the suite found six
differences, and the backend was right in each. [#72][p72] then put that stack in CI, and the
job "Real-stack layers" is now a required check.

### A test that could not fail where CI ran (September 2026)

The mock dated its rows from UTC midnight on the first of the month; the dashboard asks for local
midnight. My test checked the UTC month too, so it passed although the window was wrong for a
viewer west of Greenwich ([#145][p145]). The corrected test could not fail in UTC either, where
the two midnights are the same instant, and every CI job ran in UTC. So the same pull request
added a job ([commit][c145]) that runs the frontend's unit suite in two other zones
([`ci.yml`][ci], `frontend-tz`). Six hours after that merge the Los Angeles run went [red][run]
on a pull request that had not yet touched the frontend. The failure exposed a wrong expectation
in another test already on `main`, which named a day in UTC where the page names it in local
time; the application itself was right ([the fix][fix]). The failure depends on the hour:
the same job had passed four times on that branch in the ninety minutes before. The job was not
yet a required check then; both zones have been required since 25 September.

### A defect the build did not report (September 2026)

In [#198][p198] two edits each left two documentation summaries on one member: the first when a
method body became a call to another method, the second when a helper was inserted between a
comment and its method. The build had zero warnings both times, and the AI review caught both. I
added an automated check, a rule in an architecture test: [`SourceHygieneTests`][sh] fails on a
second summary in one comment. On its first run it found four more places, in code the pull
request had not touched, one of them in production code. The pull request lists the four. The
test's own comment explained the compiler's silence wrongly until [#283][p283] corrected it: the
check was right and its explanation was not.

## The parts

The words in brackets are borrowed; [where the words come from](#where-the-words-come-from)
gives the sources. A guide tells an agent what to do before it acts; a sensor checks the result
afterwards. A sensor is computational when a program runs it, such as a test or a scan, and
inferential when an AI model judges the result.

| Part | What is here, and where it shows |
|---|---|
| Instructions (guides) | One [instruction file][ag] for all agents, two procedures ([gate][gate], [review round][round]), 64 [decisions][adr], the [traps][tr] |
| Checks a program runs (computational sensors) | [CI jobs][ci]; a security scan; 24 [architecture-test][arch] files |
| Checks a model judges (inferential sensors) | An AI review on pull requests; a [second reader][who] |
| Limits and my checkpoints | The [rule set][rs], 11 required checks; my merge; a [deploy][dep] |
| Isolation | A separate folder and branch per agent ([the path of a change][path]): it separates files, nothing else |
| Memory | The [decision records][adr], cited in the code by number; a page of [traps][tr] |
| The correction loop (steering loop) | A mistake becomes a check when a program can detect it: [three episodes](#three-episodes) |

- **Instructions.** `AGENTS.md` holds the commands, what "done" means and [what an agent never
  does][never]. Two procedures give the steps in order: [the gate before a pull request][gate]
  and [the review round][round]. Before I published them I tried each twice: an AI model was
  given the file and nothing else and answered fixed scenarios, a second model scored the
  answers against what I require, and I corrected the text after the first trial (my own
  statement: the record is not public). These files give instructions; nothing automatic checks
  that they were followed.
- **Checks a program runs.** A green job does not show that any test passed. So 9 steps in
  [`ci.yml`][ci] and [`contract-tests.yml`][bru] count what ran and fail their job when the
  suite beside them passed nothing, skipped a test or fell under a minimum. Three jobs run on a
  pull request and are not required: the documentation site, the check on spelling and the test
  coverage, which holds one of the counting steps.
- **Checks a model judges.** The second reader is another agent with one task, to find what is
  wrong, and only where a mistake is expensive: money, access, a database migration. Its
  findings are not in the pull requests: that it ran is my own statement.
- **Limits.** The rule set, the merge and the approval of a deployment are described in
  [the playbook][guards]. The bypass list of the rule set is empty, so the required checks apply
  to my account too. A visitor cannot see that list on GitHub: it is my own statement. The
  approval of a deployment is a rule of the environment `demo`: its settings page is closed to a
  visitor, and GitHub's public API [lists the rule][env].
- **Isolation.** A separate folder separates files, not services, and the agents run on my
  computer, not in a closed environment. No agent runs in a workflow. The two test workflows
  hold a read-only token. The [deploy][dep] and [pages][pg] workflows begin without permissions,
  and each job names the ones it needs.

## The private part

Part of the harness is not here, and for this part a visitor has my word alone. Beside this
repository I keep a second, private one for my working material: notes on where the work stands,
the plans, the brief each agent receives, a short file that only the orchestrator reads on top
of `AGENTS.md`, the blocks that refuse a merge command or a push, and the scripts that run the
checks before a pull request and after a merge. It is private because it holds drafts,
unfinished decisions and paths of my computer. The two procedures are public:
[the gate before a pull request][gate] and [the review round][round]. Beside each I keep, in
private, the steps of my own machine and a file of dated cases. What the repository asks of a
change, from anyone's agent, is public: [`AGENTS.md`][ag], the checks in [CI][ci] and the
[decision records][adr] in this repository, and the [rule set][rs] on GitHub.

## What this does not show

- **Eleven required checks is recent.** The rule set on `main` required two checks from 14 July
  2026, eight and the code-scanning rule from 25 September, nine from 3 October and eleven from 14
  October. 202 of the 273 pull requests merged up to [#283][p283] were merged before 25 September.
  Fewer jobs existed then: [#26][p26], merged on 22 July, ran three CI jobs, and [#208][p208],
  merged on 24 September, ran eight. Outside the two required ones, a red job would not have blocked
  the merge. I counted how often that happened: of those 273, one was merged with a check red on its
  last commit, the security scan on [#7][p7], in the scan's first week. The checks of every pull
  request are public and can be counted again. The dates of 25 September and 3 October, and what
  each version of the rule set required, are my own statement, because a visitor cannot read the
  rule set's history; [GitHub's API][rsapi] shows when the rule set was created and when it last
  changed, and each pull request shows which checks ran on it. `AGENTS.md` itself entered the
  repository on 11 October ([history][agh]); the written gates and merge rules have been in
  [Engineering practices][ep] since 25 July.
- **No comparison with and without the harness exists, and the accuracy of the AI review is
  unknown.** I have not measured the work with and without any part of it. The episodes show
  what a new check caught on its first run; they do not show a rate. Each finding of the AI
  review is checked against the code and fixed, declined or set aside, with a written answer
  on the pull request, but I have not counted how many were real defects. Some pull requests
  were merged without a review coming back, and the rule set requires no approving review.
- **The blocks on my machine are not a lock.** They refuse a merge command or a push
  ([the playbook][guards] describes them) and stop a habit or a mistake, not an agent set on
  getting around them. Some rules have no check at all: not rewriting a pushed branch is
  [one][never], because the rule set covers `main` alone. And the repository is mine, so I can
  still edit the rule set.
- **One person, one account.** GitHub shows one account opening and merging every pull request.
  That shows which account merged, not who pressed the button: that no agent merges is my own
  statement, as the [playbook][pb] says of its count. And nothing here says the same practice
  works in a team.
- **No automated check tells whether a change made a check weaker.** An edited workflow is
  linted and scanned, and an edited test runs; neither shows whether the edit made a check
  easier to pass. What there is instead: a written rule (no
  [weakening a check to reach green][never]); a list of the test and workflow files a change
  edits, made on my machine before a pull request, which I read with the diff; and my reading of
  the diff. That the list exists is my own statement.

## Finding who uses a piece of code

For "which files use this member or type" the orchestrator starts from one command that takes
the name: it runs a few syntax patterns and a text search on that name, and both lists are
read. That rule is in the private file that only the orchestrator reads, not in `AGENTS.md`:
that it is in force is my own statement. One measurement of that question follows: four ways to
search, and no other part of the harness.

The measurement had ten such questions, all about a member and none about a type, on this
repository's C# backend, at one commit of 12 October 2026: among them a method name that exists
on unrelated types, one overload of two, an extension method, a generic method, a constructor
and a constant. The right answers came from the compiler: the member was marked obsolete, the
solution was built, and the right files are those in which the build warned, 63 in all, a file
counted once for each question it answers. Questions and answers were fixed before any scored
run. So was one condition, written before any result came back: a tool's figures are printed
only if its three runs agree with one another. Each tool was used by a separate agent that had
not been shown the answers, in three runs per tool, all with one AI model.

| Tool | The tool's own output: right files, of files returned | The agent's list after reading that output: right files of 63 | Questions the agent answered exactly, of 10 |
|---|---|---|---|
| Text search (`git grep -w` on the name) | 63 of 205 | 63 in each run | 10 in each run |
| Syntax patterns (ast-grep 0.50.0, a few patterns of mine on the name) | 63 of 128 | 63 in each run | 10 in each run |
| Language server (csharp-ls 0.28.0, asked for references by a script) | 63 of 65 | 63 in each run | 10 in each run |

After reading, the agents with these three listed exactly the compiler's 63 files in all nine
runs. The language server's own output was the nearest to that list: its 2 extra files name the
member only inside a documentation comment, where the compiler does not warn. The fourth way was
a code graph (graphify 0.9.86, built beforehand and asked to explain the name). Its three runs
did not agree with one another, so it has no row: in none of them did the agent's list hold all
63 files, and no file on those lists was wrong. The measurement does not choose among the first
three, and the command the rule names runs the first two together. The graph is kept, on trial,
for another question, how one piece of code reaches another, which this did not measure.

Limits. One repository, one language, ten questions of one kind, chosen by an agent that read
the code with a text search: eight to a list of kinds set in its brief, two of its own choosing.
Two were swapped in during that choice so that a file naming the member only in a documentation
comment existed: the language server's 2 extra files are those. One AI model; three runs per
tool. "Right" means that the compiler warns there, so a use inside a comment or a string does
not count. The agents were told not to look for the answers; nothing made it impossible. The row
of the syntax patterns measures my patterns, tried beforehand on this same repository and run by
a script that is not in it, which prints beside them the lines a text search finds; and it is a
second pass: in a first one my instructions asked for the constructor with the pattern for
calls, and the tool's own output held 51 of the 63. The questions, the compiler's answers and
the scoring are not in this repository, so every figure here is my own statement, and none is in
the table of numbers below.

## Where I differ from OpenAI's post

I use the same term as [OpenAI's post][oai] and the opposite merge policy. The post describes a
repository with minimal blocking merge gates, where a person may review a pull request but does
not have to, and where agents "often squash and merge their own pull requests". Here eleven
required checks and a code-scanning rule block a merge, and I merge every pull request myself.

This is a different position, not a correction of theirs. The post says its minimal gates fit a
repository where agent throughput is far above human attention and a correction is cheap, and
that an agent merging its own change depends on structure and tooling that should not be assumed
elsewhere. I keep the merge decision because these changes can affect rules about money.

## Where the words come from

- **Harness engineering** is in the title of [OpenAI's post of 11 February 2026][oai]. I do not
  know who used the words first.
- **Guides and sensors**, **computational and inferential** and **the steering loop** are
  Birgitta Böckeler's words, from [Harness engineering for coding agent users][bb]
  (martinfowler.com, 2 April 2026). The table of parts gives her words in brackets. The other
  names in it (limits, isolation, memory) are plain words of mine.
- **The repository as the system of record, with `AGENTS.md` as the map**, is from OpenAI's
  post, where a short `AGENTS.md` is mainly a map into the documentation. Mine has a table of
  where things are and also holds rules. That post does not use the words guides and sensors:
  putting the two sources side by side is my own reading.

## The numbers

| Number | Where it can be counted again |
|--:|---|
| 11 | The required checks listed on the [rule set][rs] page |
| 64 | The numbered files in [`docs/adr/`][adrdir]: 65, one of which is the template |
| 24 | The files in the [architecture tests' folder][arch]: 25, one of which holds no test |
| 9 | Eight steps of [`ci.yml`][ci] and one of [`contract-tests.yml`][bru], named below |
| 17 | The results on the last commit of [#283][p283]: 16 check runs and one status |
| 273, of which 202 before 25 September | GitHub's list of [merged pull requests][merged] up to #283; [the same list before that day][early] |
| 1 | The checks on the last commit of each merged pull request; the one is on [#7][p7] |

The nine: in `ci.yml`, the two "Test totals", "Report the total" and the five steps whose name
starts with "Assert", other than "Assert the zone reached Node"; in `contract-tests.yml`, "Fail
if the run sent no requests".

[pb]: engineering-playbook.md
[who]: engineering-playbook.md#who-does-what
[path]: engineering-playbook.md#the-path-of-a-change
[guards]: engineering-playbook.md#what-stands-between-an-agent-and-main
[ep]: engineering-practices.md
[ag]: ../AGENTS.md
[never]: ../AGENTS.md#what-an-agent-never-does
[gate]: ../.agents/skills/pre-pr-gate/SKILL.md
[round]: ../.agents/skills/review-round/SKILL.md
[agh]: https://github.com/Gurgant/azurebank-v2/commits/main/AGENTS.md
[adr]: adr/README.md
[adrdir]: https://github.com/Gurgant/azurebank-v2/tree/main/docs/adr
[a29]: adr/0029-contract-conformance-gate.md
[tr]: engineering-traps.md
[ci]: ../.github/workflows/ci.yml
[bru]: ../.github/workflows/contract-tests.yml
[dep]: ../.github/workflows/deploy.yml
[pg]: ../.github/workflows/pages.yml
[arch]: ../backend/tests/AzureBank.Tests/Architecture
[sh]: ../backend/tests/AzureBank.Tests/Architecture/SourceHygieneTests.cs
[rs]: https://github.com/Gurgant/azurebank-v2/rules/18930321
[rsapi]: https://api.github.com/repos/Gurgant/azurebank-v2/rulesets/18930321
[env]: https://api.github.com/repos/Gurgant/azurebank-v2/environments/demo
[merged]: https://github.com/Gurgant/azurebank-v2/pulls?q=is%3Apr+is%3Amerged
[early]: https://github.com/Gurgant/azurebank-v2/pulls?q=is%3Apr+is%3Amerged+merged%3A%3C2026-09-25
[p7]: https://github.com/Gurgant/azurebank-v2/pull/7
[p26]: https://github.com/Gurgant/azurebank-v2/pull/26
[p67]: https://github.com/Gurgant/azurebank-v2/pull/67
[p72]: https://github.com/Gurgant/azurebank-v2/pull/72
[p145]: https://github.com/Gurgant/azurebank-v2/pull/145
[c145]: https://github.com/Gurgant/azurebank-v2/commit/6ad4c798
[fix]: https://github.com/Gurgant/azurebank-v2/commit/2c0ed30b
[p198]: https://github.com/Gurgant/azurebank-v2/pull/198
[p208]: https://github.com/Gurgant/azurebank-v2/pull/208
[p283]: https://github.com/Gurgant/azurebank-v2/pull/283
[run]: https://github.com/Gurgant/azurebank-v2/actions/runs/33705993646
[oai]: https://openai.com/index/harness-engineering/
[bb]: https://martinfowler.com/articles/harness-engineering.html
