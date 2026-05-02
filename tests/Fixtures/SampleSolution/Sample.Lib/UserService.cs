namespace Sample.Lib;

public sealed class UserService
{
    private readonly ILogger _logger;

    public UserService(ILogger logger)
    {
        _logger = logger;
    }

    public Task<User?> FindAsync(string name, CancellationToken cancellationToken)
    {
        _logger.Log($"Looking up user {name}");
        cancellationToken.ThrowIfCancellationRequested();
        User? user = string.IsNullOrEmpty(name) ? null : new User(name, 0);
        return Task.FromResult(user);
    }
}
