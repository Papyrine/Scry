The legal operator orderings form a small state machine — every absent edge is an illegal ordering:

```mermaid
stateDiagram-v2
    direction TB
    [*] --> Source
    Source --> Restricting: Where / OrderBy / ThenBy / Skip / Take
    Restricting --> Restricting: (any order, any number)
    Source --> Grouped: GroupBy
    Restricting --> Grouped: GroupBy
    Grouped --> Grouped: Where (HAVING)
    Restricting --> Restricting: Reverse
    Source --> Projected: Select
    Restricting --> Projected: Select
    Grouped --> Projected: Select (mandatory)
    Source --> Deduplicated: Distinct
    Restricting --> Deduplicated: Distinct
    Projected --> Deduplicated: Distinct
    Deduplicated --> Projected: Select
    Source --> [*]: terminal
    Restricting --> [*]: terminal
    Projected --> [*]: terminal
    Deduplicated --> [*]: terminal
```

Nothing orders, skips, or takes after `GroupBy`; a `GroupBy` cannot reach a terminal without a
`Select` in between; and there is no second `GroupBy` or `Select`. A `Where` after `GroupBy` is the
one exception — it filters the groups rather than the rows, and reads only the key and aggregates.
`ThenBy` and `Reverse` without a preceding `OrderBy` are rejected, and nothing may follow a terminal.

`Distinct` deduplicates the projected rows, so the only thing that may follow it is the `Select` it
deduplicates and a terminal. Filtering or ordering after it would be describing the rows that fed it,
and paging after it would be slicing an order that a deduplication cannot preserve.
