# Scry.Wire

The serializable query AST shared by the [Scry](https://github.com/Papyrine/Scry) client
and server. It is a restricted, closed node vocabulary — not arbitrary expression-tree
serialization — so every query is exhaustively validatable and free of arbitrary method calls.

Referenced by `Scry.Client` and `Scry.Server`; you rarely use it directly.
