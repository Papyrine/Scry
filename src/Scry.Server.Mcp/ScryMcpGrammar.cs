using System.Text.Json.Serialization;

// What an agent is told about the wire format: enough to write a query without reading docs/wire-format.md.
// The closed sets — operators, nodes, functions, operators on values, constant tags — are read from the wire
// types themselves, so what the agent is told can never drift from what the server reads.
static class ScryMcpGrammar
{
    public static string Instructions(ScryMcpAccess access)
    {
        var builder = new StringBuilder(
            """
            This server answers queries over a data model through Scry. Everything it will read is on an
            allow-list: call `describe_schema` first, and name only the sources, members and enum values it
            lists. A query is a pipeline of operators over one source, sent to the `query` tool as JSON.
            Rejections name what was wrong: read the error, fix the query, and ask again.
            """);
        if (access == ScryMcpAccess.ReadWrite)
        {
            builder.Append(
                """


                Writes are commands, listed under `commands` in the schema. Send one with `send_command`.
                A command may answer `Pending`; ask for its outcome with `command_receipt`. `capabilities`
                says which commands this caller may send at all.
                """);
        }

        return builder.ToString();
    }

    public static string Query { get; } =
        $$"""
          Runs one query and answers its result as JSON: `{"kind": "List"|"Single"|"Scalar"|"Page", "payload": ...}`.

          `root` is a source name from `describe_schema`. `pipeline` is an array of operators, each an object
          with a `$type`, applied in order. At most one terminal operator (count, any, all, first, single, last,
          aggregate, page) and it must be last; without one the rows are answered as a list, capped by the
          server's page size — use `take` or `page` to bound it.

          Operators ($type: payload):
            where: predicate  |  orderBy / thenBy: key, descending  |  skip / take: count
            select: projection  |  distinct  |  reverse  |  groupBy: keys (one key)
            count / longCount / any / all: predicate?  |  first / single / last: orDefault, predicate?
            page: size?, cursor?
            All operators: {{Discriminators<QueryOp>()}}

          Expressions (Node, by $type):
            member: {"$type":"member","path":"Name"} — a path of more than one segment is an array:
                    {"$type":"member","path":["Manager","Name"]}. Never a one-element array.
            const:  {"$type":"const","value":"100","tag":"Decimal"} — value is always a string, omitted for null.
                    Tags: {{Names<ClrTypeTag>()}}.
                    Enum values are their names: {"$type":"const","value":"FullTime","tag":"Enum"}.
                    Dates: "2026-09-03T00:00:00.0000000Z" (DateTime), "2026-09-03" (DateOnly).
            binary: {"$type":"binary","op":"GreaterThan","left":Node,"right":Node}
                    Ops: {{Names<BinaryOp>()}}.
            unary:  {"$type":"unary","op":"Not","operand":Node}. Ops: {{Names<UnaryOp>()}}.
            call:   {"$type":"call","function":"StringContains","target":Node,"arguments":[Node]}
                    Functions: {{Names<KnownFunction>()}}.
            subquery: over a collection member, with paths inside it read from the element:
                    {"$type":"subquery","path":"Orders","function":"Any","predicate":Node?,"selector":Node?}
                    Functions: {{Names<SubqueryFn>()}}.
            conditional: {"$type":"conditional","test":Node,"ifTrue":Node,"ifFalse":Node}
            All nodes: {{Discriminators<Node>()}}


          """ +
        """
          Projection (select): {"members":[...]}. A member read under its own name is a bare string
          ("Name"); anything else is {"name":"ManagerName","value":{"$type":"node","node":Node}}, or a
          nested object: {"name":"Manager","value":{"$type":"nested","path":"Manager","projection":{"members":["Name"]}}}.

          Examples:
            Active employees by name, first 10, two columns:
              root "Employee", pipeline [
                {"$type":"where","predicate":{"$type":"member","path":"Active"}},
                {"$type":"orderBy","key":{"$type":"member","path":"Name"},"descending":false},
                {"$type":"take","count":10},
                {"$type":"select","projection":{"members":["Name",{"name":"Department","value":{"$type":"node","node":{"$type":"member","path":["Department","Name"]}}}]}}]
            How many orders over 100:
              root "Order", pipeline [{"$type":"count","predicate":{"$type":"binary","op":"GreaterThan",
                "left":{"$type":"member","path":"Amount"},"right":{"$type":"const","value":"100","tag":"Decimal"}}}]
            Names starting with A:
              {"$type":"where","predicate":{"$type":"call","function":"StringStartsWith",
                "target":{"$type":"member","path":"Name"},"arguments":[{"$type":"const","value":"A","tag":"String"}]}}
          """;

    public const string DescribeSchema =
        """
        Describes everything this server will answer: the sources a query may start from, each type's
        members (with their types, and which are navigations or collections), the enums and their values,
        and the commands. Call this before writing a query.
        """;

    public const string SendCommand =
        """
        Sends one command, named as `describe_schema` lists it under `commands`, with its properties as
        `payload` (camel-cased). Answers the command's receipt: `Completed` with its `result`, `Failed` with
        its `error`, or `Pending` with the `id` to ask `command_receipt` about.
        """;

    public const string CommandReceipt =
        """
        Answers the receipt of a command this caller sent, by the `id` its first receipt carried: `Pending`
        while it is still being handled, then `Completed` or `Failed`.
        """;

    public const string Capabilities =
        """
        Lists the commands this caller may send at all. Advisory: a command is decided again when it is sent,
        and one aimed at a row can still be refused for that row.
        """;

    static string Names<T>()
        where T : struct, Enum =>
        string.Join(", ", Enum.GetNames<T>());

    static string Discriminators<T>() =>
        string.Join(
            ", ",
            typeof(T)
                .GetCustomAttributes(typeof(JsonDerivedTypeAttribute), false)
                .Cast<JsonDerivedTypeAttribute>()
                .Select(_ => _.TypeDiscriminator));
}
