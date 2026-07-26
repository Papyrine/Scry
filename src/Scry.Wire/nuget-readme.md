# Scry.Wire

The serializable query AST shared by the [Scry](https://github.com/Papyrine/Scry) client
and server. It is a restricted, closed node vocabulary — not arbitrary expression-tree
serialization — so every query is exhaustively validatable and free of arbitrary method calls.

<!-- snippet: wireOperators -->
<a id='snippet-wireOperators'></a>
```cs
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(WhereOp), "where")]
[JsonDerivedType(typeof(OrderByOp), "orderBy")]
[JsonDerivedType(typeof(ThenByOp), "thenBy")]
[JsonDerivedType(typeof(SkipOp), "skip")]
[JsonDerivedType(typeof(TakeOp), "take")]
[JsonDerivedType(typeof(SelectOp), "select")]
[JsonDerivedType(typeof(GroupByOp), "groupBy")]
[JsonDerivedType(typeof(CountOp), "count")]
[JsonDerivedType(typeof(AnyOp), "any")]
[JsonDerivedType(typeof(FirstOp), "first")]
[JsonDerivedType(typeof(SingleOp), "single")]
public abstract record QueryOp;
```
<sup><a href='/src/Scry.Wire/QueryOp.cs#L8-L22' title='Snippet source file'>snippet source</a> | <a href='#snippet-wireOperators' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

<!-- snippet: wireExpressions -->
<a id='snippet-wireExpressions'></a>
```cs
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(MemberExpr), "member")]
[JsonDerivedType(typeof(ConstExpr), "const")]
[JsonDerivedType(typeof(BinaryExpr), "binary")]
[JsonDerivedType(typeof(UnaryExpr), "unary")]
[JsonDerivedType(typeof(CallExpr), "call")]
[JsonDerivedType(typeof(AggregateExpr), "aggregate")]
public abstract record Expr;
```
<sup><a href='/src/Scry.Wire/Expressions/Expr.cs#L7-L16' title='Snippet source file'>snippet source</a> | <a href='#snippet-wireExpressions' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Referenced by `Scry.Client` and `Scry.Server`; you rarely use it directly.

Docs: [Wire format](https://github.com/Papyrine/Scry/blob/main/docs/wire-format.md)
