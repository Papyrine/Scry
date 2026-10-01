// Bases for a compiled model (CompiledModel) to derive from: a library the model does not own, as a
// model package's own dependency would be. Each marks one virtual member, which a model overriding it
// has to mark again for the generator to see. Opted into nothing, so a schema built over this assembly
// reads them as plain types.
// ReSharper disable UnusedMember.Global

public abstract class ForeignIgnoredBase
{
    [QueryIgnore]
    public virtual string Secret { get; set; } = "";
}

public abstract class ForeignSensitiveBase
{
    [Sensitive]
    public virtual string Salary { get; set; } = "";
}

public abstract class ForeignAttachmentBase
{
    [Attachment]
    public virtual byte[]? Document { get; set; }
}

public abstract class ForeignCollectionBase
{
    [QueryableCollection]
    public virtual List<string> Tags { get; set; } = [];
}

public abstract class ForeignKeyBase
{
    [System.ComponentModel.DataAnnotations.Key]
    public virtual int Code { get; set; }
}

public abstract class ForeignCommandIgnoredBase
{
    [CommandIgnore]
    public virtual string By { get; set; } = "";
}

public abstract class ForeignBinaryBase
{
    [BinaryTransfer]
    public virtual byte[]? Blob { get; set; }
}

public abstract class ForeignRenamedBase
{
    [PreviousNames("Former")]
    public virtual string Current { get; set; } = "";
}

[Microsoft.EntityFrameworkCore.Keyless]
public abstract class ForeignKeylessBase
{
    public int Total { get; set; }
}

// Not virtual, and hidden by its own declaration: nothing for a model to repeat.
public abstract class ForeignPlainBase
{
    [QueryIgnore]
    public string Hidden { get; set; } = "";
}
