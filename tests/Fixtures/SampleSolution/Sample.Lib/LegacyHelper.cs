namespace Sample.Lib;

public static class LegacyHelper
{
    [Obsolete("Use NewHelper instead")]
    public static string Format(string input)
    {
        return input.ToUpperInvariant();
    }

    public static string FormatNew(string input)
    {
        return input.Trim().ToUpperInvariant();
    }
}
