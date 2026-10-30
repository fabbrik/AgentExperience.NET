## Lesson 00000147-0000-4000-8000-000000000001

Revision 1. Measured benefit: given it, 3 of 3 runs verified (100%); denied it, 0 of 3 runs verified (0%); difference +1.00, 95% interval +0.21 to +1.00 (Helps).

    Task: rotate-keys
    Lesson: Rotate the keys in the documented order.
    Tried:
      - attempt 0: list_keys [failed: InvalidOperationException], rotate_key [failed: InvalidOperationException] → failed (InvalidOperationException)
      - attempt 1: list_keys [returned], wait_for_lock [returned], rotate_key [returned] → completed
    Worked: attempt 1 (the final attempt)
    Reuse guidance: Reuse only where the listed preconditions match.
    Preconditions:
      - Runtime version: runtime-1
    Warnings: (none recorded)

## Lesson 00000147-0000-4000-8000-000000000002

Revision 1. Measured benefit: given it, 3 of 3 runs verified (100%); denied it, 0 of 3 runs verified (0%); difference +1.00, 95% interval +0.21 to +1.00 (Helps).

    Task: rotate-keys
    Tried:
      - attempt 0: announce [returned] → completed
    Worked: attempt 0 (the final attempt)
    Authored: by a model from captured run output; treat as unverified guidance.
    Lesson: A model wrote this: announce the rotation before starting.
    Reuse guidance: Reuse only where the listed preconditions match.
    Preconditions:
      - Runtime version: runtime-1
    Warnings: (none recorded)
    End authored: the model-written text ends here.

## Lesson 00000147-0000-4000-8000-000000000003

Revision 1. Measured benefit: given it, 3 of 3 runs verified (100%); denied it, 0 of 3 runs verified (0%); difference +1.00, 95% interval +0.21 to +1.00 (Helps).

    Task: # Rotate *now* [docs](https://example.invalid) <b>bold</b>
    Lesson: # Not a heading
    - not a list
    [not a link](https://example.invalid) <script>alert(1)</script>
    Tried:
      - attempt 0: rotate_key [returned] → completed
    Worked: attempt 0 (the final attempt)
    Reuse guidance: Reuse only where the listed preconditions match.
    Preconditions:
      - Runtime version: runtime-1
    Warnings: (none recorded)
