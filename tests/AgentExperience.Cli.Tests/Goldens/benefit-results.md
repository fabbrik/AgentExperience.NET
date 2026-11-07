# Holdout results, <date>

- Scope: application `cli-tests`, project `repo`
- Holdout rate: 0.5 (lessons withheld from that share of the runs that had one to give)
- Verifier: check `tests`, the configured command's exit code (0 = verified)
- Verified runs each arm needs for a verdict: 20

## The library's estimate

From `AgentExperienceBenefit.EstimateAsync` over the holdout's rows and each run's finalized outcome.

| Arm | Runs | Successes | Rate |
| --- | ---: | ---: | ---: |
| treatment | 3 | 2 | 67% |
| holdout | 2 | 1 | 50% |

`treatment` runs were given the lessons (and hints) that matched; `holdout` runs had them withheld. Runs with nothing to give are in neither arm.

- Difference (treatment minus holdout): +0.17, 95% interval -0.45 to +0.65
- Verdict: **NoEvidence**
- Pending: 1, Unverified: 0, Mixed arm: 0

The verdict is `NoEvidence` for lack of runs: each arm needs at least 20 runs with a verified outcome, and this has 3 treatment and 2 holdout. It says nothing yet about whether the lessons help.

## Reading rules

- A run counts once the verifier decided it: exit 0 is a success, any other exit a failure. Pending runs (not finalized yet) and unverified runs (no verdict) are counted above and left out of the rates.
- `Helps` needs the 95% interval of the difference (Newcombe's hybrid score, from each arm's Wilson interval) wholly above zero, `Hurts` wholly below. Anything else, or fewer than 20 verified runs in an arm, is `NoEvidence`.
- Read it at the point fixed in advance. Reading again and again until the verdict looks good overstates it: each look is another test.

## What this can and cannot say

- A success means the verifier passed, nothing more. A run whose tests pass is a success even if the tests did not cover its change, and the agent can edit the code the verifier runs.
- The interval treats every run as independent. Under the Claude Code hooks the session is what is randomized and each turn that changed something is a run: the runs of one session share its arm and are correlated, so the interval is narrower than the evidence supports and a long session weighs as much as many short ones. The store does not record which session a run came from, so this page cannot count sessions.
- Arms are assigned only on runs that had a lesson or a hint to give, so the comparison is about runs where memory had something to say, not about every run.
- One lesson's own comparison is in `aexp lessons`; this page pools them all.
