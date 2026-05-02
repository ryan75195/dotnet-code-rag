namespace Sample.Lib;

public sealed class UserService
{
    private readonly ILogger _logger;

    public UserService(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<User?> FindAsync(string name, CancellationToken cancellationToken)
    {
        _logger.Log($"Looking up user {name}");
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        User? user = string.IsNullOrEmpty(name) ? null : new User(name, 0);
        return user;
    }
}
