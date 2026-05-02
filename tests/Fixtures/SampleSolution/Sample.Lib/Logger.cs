namespace Sample.Lib;

public interface ILogger
{
    void Log(string message);
}

public sealed class ConsoleLogger : ILogger
{
    public void Log(string message)
    {
        Console.WriteLine(message);
    }
}
