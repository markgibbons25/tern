# Plans are saved and applied with Signature preconditions

Every Pull, Push and Endpoint-to-Endpoint diff first produces a Plan, and only then changes anything. The Official CLI does this internally and throws the list away. We keep it:

- `--what-if --json` emits the Plan.
- `--plan-out` saves it.
- `apply` carries out a saved Plan exactly as reviewed.

A Plan records the Signature it expects each target item to have. Apply refuses when any of them has changed since, rather than overwriting someone else's work. This is the review-then-apply step that unscoped content syncs have been missing, and it gives agents a safe two-step workflow.

## Consequences

- The Plan format is a public, versioned contract, as stable as the command line.
- Pull and Push share one comparison engine. It compares any two item trees, whether files or a CM.
