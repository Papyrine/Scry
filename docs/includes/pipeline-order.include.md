The legal operator orderings form a small state machine — every absent edge is an illegal ordering:

```mermaid
stateDiagram-v2
    direction TD
    [*] --> Source
    Source --> Restricting: Where / OrderBy / ThenBy / Skip / Take
    Restricting --> Restricting: (any order, any number)
    Source --> Grouped: GroupBy
    Restricting --> Grouped: GroupBy
    Source --> Projected: Select
    Restricting --> Projected: Select
    Grouped --> Projected: Select (mandatory)
    Source --> [*]: terminal
    Restricting --> [*]: terminal
    Projected --> [*]: terminal
```

Nothing filters, orders, skips, or takes after `GroupBy`; a `GroupBy` cannot reach a terminal without
a `Select` in between; and there is no second `GroupBy` or `Select`. `ThenBy` without a preceding
`OrderBy` is rejected, and nothing may follow a terminal.
