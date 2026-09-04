/// <summary>Which view of a run the output column is showing.</summary>
public enum OutputTab
{
    /// <summary>The rows, as a grid or a single scalar.</summary>
    Result,

    /// <summary>The response envelope exactly as the server sent it.</summary>
    Response,

    /// <summary>The SQL the query would run. Only ever present when the server offers the preview.</summary>
    Sql
}
