# Skry.Wire

The serializable query AST shared by the [Skry](https://github.com/Papyrine/Skry) client
and server. It is a restricted, closed node vocabulary — not arbitrary expression-tree
serialization — so every query is exhaustively validatable and free of arbitrary method calls.

Referenced by `Skry.Client` and `Skry.Server`; you rarely use it directly.
