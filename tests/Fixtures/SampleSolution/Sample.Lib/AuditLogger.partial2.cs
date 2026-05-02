namespace Sample.Lib;

public partial class AuditLogger
{
    public IEnumerable<string> ReadAll()
    {
        return _entries.ToArray();
    }
}
