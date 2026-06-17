# Pneumatic.Wire

The serializable query AST shared by the [Pneumatic](https://github.com/Papyrine/Pneumatic) client
and server. It is a restricted, closed node vocabulary — not arbitrary expression-tree
serialization — so every query is exhaustively validatable and free of arbitrary method calls.

Referenced by `Pneumatic.Client` and `Pneumatic.Server`; you rarely use it directly.
