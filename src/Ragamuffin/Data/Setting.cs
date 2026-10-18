namespace Ragamuffin.Data;

/// <summary>A setting changed from the web UI, kept in the database so it survives restarts and redeploys.</summary>
public sealed class Setting
{
    public required string Key { get; set; }
    public required string Value { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
