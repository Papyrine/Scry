; Unshipped analyzer release
; https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
SCRY001 | Scry | Error | Failed to read the Scry model assembly
SCRY002 | Scry | Error | Duplicate Scry source name
SCRY003 | Scry | Error | Scry source name cannot be a C# property name
SCRY004 | Scry | Error | [Attachment] must be a byte[] member
SCRY005 | Scry | Error | [Attachment] is only valid on a queryable entity
SCRY006 | Scry | Error | [Attachment] cannot combine with [BinaryTransfer]
SCRY007 | Scry | Error | Attachment keys are not derivable
SCRY100 | Scry | Warning | LINQ operator is not supported by Scry
SCRY101 | Scry | Warning | Cast is not supported by Scry
SCRY102 | Scry | Warning | SelectMany with a result selector is not supported by Scry
SCRY103 | Scry | Warning | Comparer overloads are not supported by Scry
SCRY104 | Scry | Warning | Operator may only appear once in a Scry query
SCRY105 | Scry | Warning | Ordering key must be a single value
SCRY106 | Scry | Warning | Projection must construct an object
SCRY107 | Scry | Warning | Function is not supported by Scry
SCRY108 | Scry | Warning | ToString with a format is not supported by Scry
SCRY109 | Scry | Warning | Scry query cannot be executed synchronously
SCRY110 | Scry | Warning | Reverse requires an ordered query
SCRY111 | Scry | Warning | GroupJoin may not project its group
SCRY112 | Scry | Warning | Client-side code cannot be carried by Scry
SCRY113 | Scry | Error | Attachment requires the row's key projected beside it
SCRY114 | Scry | Error | Attachment is not a value
SCRY115 | Scry | Error | Attachment cannot be carried through this operator
