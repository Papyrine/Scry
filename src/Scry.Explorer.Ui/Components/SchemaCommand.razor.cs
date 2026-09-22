using Microsoft.AspNetCore.Components;

namespace Scry;

public partial class SchemaCommand
{
    [Parameter]
    [EditorRequired]
    public ScryCommandInfo Command { get; set; } = null!;

    string Payload =>
        string.Join(", ", Command.Properties.Select(_ => _.Name));
}
