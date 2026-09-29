namespace Scry;

/// <summary>What an AI agent may do over MCP: see <see cref="ScryOptions.Mcp"/>.</summary>
public enum ScryMcpAccess
{
    /// <summary>No MCP route is mapped. The default.</summary>
    Off,

    /// <summary>The schema and queries: nothing that writes.</summary>
    Read,

    /// <summary>The schema, queries and commands.</summary>
    ReadWrite
}
