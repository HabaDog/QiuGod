/// <summary>
/// Account operations exposed to protocol handlers. The current implementation
/// remains backed by MySQL and Redis, while keeping that choice out of the gateway.
/// </summary>
public interface IAccountService
{
    Task<AccountAuthenticationResult> AuthenticateAsync(string username, string password);
    Task<AccountRegistrationResult> RegisterAsync(string username, string password);
}

public sealed record AccountAuthenticationResult(bool Success, string Message, int PlayerId);
public sealed record AccountRegistrationResult(bool Success, string Message);

public sealed class AccountService : IAccountService
{
    private readonly HybridUserDatabase _userDatabase;

    public AccountService(HybridUserDatabase? userDatabase = null)
    {
        _userDatabase = userDatabase ?? HybridUserDatabase.Instance;
    }

    public async Task<AccountAuthenticationResult> AuthenticateAsync(string username, string password)
    {
        var (success, message, user) = await _userDatabase.AuthenticateUserAsync(username, password);
        return new AccountAuthenticationResult(success, message, user?.PlayerId ?? 0);
    }

    public async Task<AccountRegistrationResult> RegisterAsync(string username, string password)
    {
        var (success, message) = await _userDatabase.RegisterUserAsync(username, password);
        return new AccountRegistrationResult(success, message);
    }
}
