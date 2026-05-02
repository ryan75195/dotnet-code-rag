namespace Sample.Lib;

public partial class AuditLogger
{
    private readonly List<string> _entries = new();

    public void Append(string entry)
    {
        _entries.Add(entry);
    }
}
