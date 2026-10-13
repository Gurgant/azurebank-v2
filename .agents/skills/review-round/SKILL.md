---
name: review-round
description: Works through one round of review on an open AzureBank pull request (Gurgant/azurebank-v2). It reads every finding, checks each against the code, fixes it, declines it or sets it aside, replies in each thread, asks for the next review, and says whether a merge can be proposed. It never merges. Use when CodeRabbit, CodeQL or CI has answered on a pull request, when a check failed, when asked to address review comments, and when the question is whether a pull request is ready to merge.
compatibility: Needs git 2.38 or newer and gh signed in with access to the pull request.
---

# One review round

A round is everything the reviewers said about one head. It ends with at most one commit of
fixes, after a merge of `main` where the branch needed one (step 3), one reply in each thread,
and the next request, the proposal of a merge, or a report of what the pull request waits for.
A round pushes and posts: work it only when the task says so (`AGENTS.md`, "What an agent never
does"). Otherwise stop after step 2 and hand back what each finding needs. Asked only whether a
merge can be proposed, answer from step 6 as well, and push and post nothing.

## 1. Read the whole round before touching anything

```bash
gh pr view <number> --json headRefOid,statusCheckRollup,reviews,comments
gh pr checks <number> --required     # the checks that step 6 calls required
gh api graphql -f query='query($n:Int!){repository(owner:"Gurgant",name:"azurebank-v2"){pullRequest(number:$n){reviewThreads(first:100){pageInfo{hasNextPage endCursor} nodes{id isResolved isOutdated comments(first:100){nodes{databaseId author{login} path line originalLine body createdAt}}}}}}}' -F n=<number>
gh api --paginate "repos/Gurgant/azurebank-v2/code-scanning/alerts?state=open&pr=<number>&per_page=100"
```

- A verdict counts only when it is about the last head: the commit the review names
  (`reviews[].commit.oid`, or the commit in the reviewer's summary) equals `headRefOid`. A `gh`
  that prints no `commit` for a review is too old for the first command: read `commit_id` from
  `gh api --paginate "repos/Gurgant/azurebank-v2/pulls/<number>/reviews?per_page=100"`. An
  acknowledgement is not a verdict, and neither is "currently processing", a review that was
  skipped, a green tick on the reviewer's check, the reviewer's reply in a thread, or a thread
  marked resolved.
- Read the summary, every thread of the round, the failed checks and the open alerts. A finding
  outside the diff has no thread: it is only in the text of the review. Note the severity the
  reviewer gave each finding. `hasNextPage` true means threads were cut: ask again with
  `reviewThreads(first:100, after:"<endCursor>")` until it is false. A thread that shows 100
  comments was cut too: read it on the pull request's page before judging it.

## 2. Judge each finding against the code

Open the file at the line (`line`; a comment on lines that have since changed has only
`originalLine`, the line in the commit it was made on). Then:

- true, or partly true: what is true is to be fixed (step 3), and the reply says what is not;
- false: decline it, with the line, the test or the primary source that shows why;
- true but outside the topic: note it for a change of its own (in the report of the task, or
  in an issue when the task allows opening one) and say so in the reply. Do not widen this one.

A suggested patch is a claim like any other: it can be partial or wrong. Where a reviewer
challenges a statement, check the primary source, then concede or answer on evidence. A finding
from a review of an older head is still judged, against the code of the last head: if a later
commit already fixed it, the reply names that commit. An open code-scanning alert is judged the
same way, before the commit of step 3.

## 3. One commit for the round

All fixes of the round go in one commit whose message says the outcome: the reviewer then reads
one new head, and each reply can name one commit. The order is:

- First, when the round has a fix to commit: `git fetch origin`, then, where the branch is
  behind, `git merge origin/main`. Without the fetch the merge takes main as this machine last
  saw it, and a merge made after the checks would leave them run on another tree.
- Then fix, and commit.
- Then run the pre-pr-gate skill's step 2 on that commit and read the numbers. Its list of what
  to run is read from the committed diff: a fix that is not committed yet, in a folder the pull
  request had not touched, would select none of that folder's checks.
- Then the check of the commit messages (that skill, step 6): a pushed message is not rewritten.
- Then push, and reply.

A check that fails there is corrected before the push, in the same commit while it has not left
the machine, and the checks run again on the corrected commit: what is pushed has passed them.
A round with nothing true to fix has no commit and no merge of `main`: nothing is pushed. Go to
step 4. The one exception is a branch that conflicts with `main` (step 6).

## 4. One reply in each thread

When the round has a commit, push it first. Each thread then gets what was done, with the
commit, why it was declined, or where it was noted for a change of its own. Write each reply to
a file and post it from the file, to the FIRST comment of the thread (its `databaseId` from
step 1; a reply to a reply is refused):

```bash
gh api repos/Gurgant/azurebank-v2/pulls/<number>/comments/<databaseId>/replies -F body=@<file>
```

Findings that are only in the text of a review have no thread: answer all of them in one comment,
one line for each, `gh pr comment <number> --body-file <file>`. A file the pull request does not
change may be fixed when the finding is inside the topic.

A code-scanning alert is fixed, or dismissed as a false positive with a reason that will still
convince in six months.

## 5. Ask for the next round

A round that pushed nothing asks for no review when a review has already read its head, or when
a request on that head still waits for its answer: go to step 6. Every other round asks for one.

The repository's configuration turns automatic review on, but a review has not always started by
itself here, and the reviewer's check can be green for a review that was skipped. So a review is
asked for, after the round's push where it made one, in one comment that holds only the request,
posted from a file:

```bash
gh pr comment <number> --body-file <file>
```

The file holds `@coderabbitai review` only while the pull request has neither a request nor a
review on it, and `@coderabbitai full review` in every other case: a full review skips no file.
This is the one at-sign `AGENTS.md` allows. Once one request or one review is on the pull
request, every request is a full review, also when the first request was skipped or refused,
and also when the first review started by itself.

Ask once. If the reviewer refuses the request, do not ask again at once: read the refusal, wait
for the time it names if it names one, and report the wait. An acknowledgement of the request is
not a review: do not ask again, report that the pull request waits, and go back to step 1
when a verdict on the last head exists.

## 6. When a merge can be proposed

Every required check is green on the last head, and a review has read that head and left
nothing to change:

- it has no finding; or
- every finding it has was answered without a commit (step 2): declined with the reason, which
  names the commit when an earlier one had already fixed it, or set aside for a change of its
  own. The answer is in the finding's thread, or in the comment of step 4 for a finding that
  has none.

A push after a review makes a new head, and a fix is such a push: that head has no review of its
own, however green its checks. So the next round is asked for (step 5), and no merge is proposed
before its verdict. A finding marked minor is no exception. An AI reviewer almost always finds
something more: the way out of a round that only trades words is to decline, with its reason, a
finding that step 2 did not judge true, never to propose a commit that no review has read.

A head that a review has read and left with nothing to change takes no further push unless it
has to. A branch that falls behind `main` after that is not updated for that alone, because a
merge of `main` is a push too. It is updated when it conflicts, in the order of step 3 (fetch,
merge `origin/main`, resolve, commit, the checks on that commit, push), and then a full review
is asked for (step 5): no merge is proposed before its verdict.

When both hold, say that the merge can be proposed, with the title and the merge text, and stop;
where the next paragraph asks for a check of the combination, that check comes first. Otherwise
say what the pull request waits for and stop: the next round starts at step 1 when a verdict on
the last head exists, or when the person has decided (step 7). The merge is never the agent's.

Before a proposal, `git fetch origin`: the branch is behind `main` when the first number of
`git rev-list --left-right --count "origin/main...$first"` is above 0, with `first` set as in
the block below. When several pull requests are proposed together, or one whose branch is
behind `main`, check the combination first. Green alone, or green on an older `main`, does not
prove green together.

A pull request is taken by its number, and its head as a commit: no branch name is typed into a
command. Git accepts a branch name that holds `;` or `$(...)`, which a shell would run, and the
branch of a fork is not under `origin/` at all. Each commit read this way must be the
`headRefOid` that step 1 read for that pull request, the one its review names: when it is
another, somebody has pushed since, and that pull request goes back to step 1. The folder of
the scratch tree is named by the task, like every instruction the round follows: it is not a
value that somebody else can write. `git merge-tree --write-tree` needs git 2.38.

```bash
git fetch origin
git fetch origin pull/<number>/head && first=$(git rev-parse FETCH_HEAD)   # GitHub keeps each head there
t=$(git merge-tree --write-tree origin/main "$first")                      # exit 1: a conflict: no proposal
c=$(git commit-tree "$t" -p origin/main -p "$first" -m scratch)            # an object: no branch moves
git fetch origin pull/<number>/head && second=$(git rev-parse FETCH_HEAD)  # these three lines again,
t=$(git merge-tree --write-tree "$c" "$second")                            # once for each further one
c=$(git commit-tree "$t" -p "$c" -p "$second" -m scratch)
git worktree add --detach "<a folder the task names>" "$c"
```

Combine in the order the merges are proposed in. In that folder run the pre-pr-gate skill, step
2, for every part the pull requests change. Say in the proposal what was combined, in which
order, and the numbers. If the combined run fails, or two of them conflict, propose none of them
together: report which pair breaks and the failing numbers. A branch that conflicts with `main`
is updated first, as said above. Remove the folder afterwards with
`git worktree remove "<folder>"`.

## 7. When to stop and ask

- A finding is a judgement call: it changes behaviour, a decision record or the scope.
- A finding comes back after it was declined: do not argue twice.
- A required check fails for a reason that is not this change (its log shows no line of the
  diff, and either the same test passes locally on this head or the job died before any test
  ran: a container, the network, the runner): run it again once, with
  `gh run rerun <run id> --failed`. If it fails again, report the test and the address of the
  run. Never skip the test or weaken the check.

While the person is asked about one finding, the rest of the round goes on: the other findings
are fixed and answered, and the thread in question says that it waits for a decision. If the
round pushed, the next review is still asked for. No merge is proposed while a thread waits.

Every step above is complete on a fresh clone. Where a file named `LOCAL.md` exists beside this
one, it holds the steps of that machine: follow it too.
