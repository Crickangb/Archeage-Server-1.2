using AAEmu.Login.Core.Services.TwoFactor;
using AAEmu.Login.Models;
using AAEmu.Login.Utils;

namespace AAEmu.Login.Core.Services.ManagerAccounts;

public sealed record ManagerAccount(uint Id, string Username, string PasswordHash, bool Banned);
public sealed record ManagerIdentity(uint AccountId, string Username);

public interface IManagerAccountRepository
{
    Task<ManagerAccount?> FindByUsernameAsync(string username, CancellationToken cancellationToken);
    Task<ManagerAccount?> FindByIdAsync(uint id, CancellationToken cancellationToken);
}

public sealed class ManagerAccountRepository(IMySqlConnectionFactory connections) : IManagerAccountRepository
{
    public Task<ManagerAccount?> FindByUsernameAsync(string username, CancellationToken cancellationToken) =>
        FindAsync("username", username, cancellationToken);

    public Task<ManagerAccount?> FindByIdAsync(uint id, CancellationToken cancellationToken) =>
        FindAsync("id", id, cancellationToken);

    private async Task<ManagerAccount?> FindAsync(string column, object value, CancellationToken cancellationToken)
    {
        await using var connection = connections.CreateConnection();
        await using var command = connection.CreateCommand();
        // Column is chosen only by the two methods above; values are always parameters.
        command.CommandText = $"SELECT id, username, password, banned FROM users WHERE {column} = @value LIMIT 2";
        command.Parameters.AddWithValue("@value", value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        var account = new ManagerAccount(Convert.ToUInt32(reader["id"]), Convert.ToString(reader["username"])!,
            Convert.ToString(reader["password"])!, Convert.ToInt32(reader["banned"]) != 0);
        // The schema does not guarantee unique usernames. Ambiguous matches must not authenticate.
        return await reader.ReadAsync(cancellationToken) ? null : account;
    }
}

/// <summary>Read-only website authentication. Never invokes the game's AutoAccount login path.</summary>
public sealed class ManagerAccountService(
    IManagerAccountRepository accounts,
    IPasswordService passwords,
    ITwoFactorService twoFactor)
{
    public async Task<ManagerIdentity?> AuthenticateAsync(string username, string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > 32 || string.IsNullOrEmpty(password) ||
            password.Length > 256)
            return null;
        var account = await accounts.FindByUsernameAsync(username, cancellationToken);
        if (account is null || string.IsNullOrEmpty(account.PasswordHash))
            return null;
        if (passwords.VerifyPassword(account.PasswordHash, Password.FromPlaintext(password)) ==
            PasswordVerificationResult.Failed)
            return null;
        return await AllowedIdentityAsync(account, cancellationToken);
    }

    public async Task<ManagerIdentity?> GetIdentityAsync(uint accountId, CancellationToken cancellationToken)
    {
        var account = await accounts.FindByIdAsync(accountId, cancellationToken);
        return account is null ? null : await AllowedIdentityAsync(account, cancellationToken);
    }

    private async Task<ManagerIdentity?> AllowedIdentityAsync(ManagerAccount account,
        CancellationToken cancellationToken)
    {
        if (account.Id == 0 || account.Banned)
            return null;
        // A username/password-only website must not bypass configured second factors.
        if ((await twoFactor.GetRequirementsAsync(new AccountId(account.Id), cancellationToken)).AnyRequired)
            return null;
        return new ManagerIdentity(account.Id, account.Username);
    }
}
