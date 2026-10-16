using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AAEmu.Login.Utils;

namespace AAEmu.Login.Core.Services.ManagerAccounts;

/// <summary>Explicit private-service provisioning; ordinary website sign-in remains read-only.</summary>
public sealed class ManagerBotAccountProvisioner(
    IMySqlConnectionFactory connections, IManagerAccountRepository repository,
    ManagerAccountService identities, IPasswordService passwords)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static bool ValidGeneratedCredentials(string username, string password) =>
        Regex.IsMatch(username, @"\AaccountBot_[a-f0-9]{16}\z", RegexOptions.CultureInvariant) &&
        Regex.IsMatch(password, @"\A[a-f0-9]{48}\z", RegexOptions.CultureInvariant);

    public async Task<ManagerIdentity?> ProvisionAsync(string username, string password, CancellationToken cancellationToken)
    {
        if (!ValidGeneratedCredentials(username, password)) return null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Retrying the same generated credentials resolves the existing identity;
            // a collision never overwrites a password or claims somebody else's account.
            var existing = await repository.FindByUsernameAsync(username, cancellationToken);
            if (existing != null)
                return await identities.AuthenticateAsync(username, password, cancellationToken);
            await using var connection = connections.CreateConnection();
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT id FROM users WHERE username=@name FOR UPDATE";
            command.Parameters.AddWithValue("@name", username);
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                if (await reader.ReadAsync(cancellationToken)) return null;
            command.CommandText = "INSERT INTO users (username,password,email,last_ip,created_at,updated_at,banned) VALUES (@name,@hash,'','',@now,@now,0)";
            // Preserve the emulator launcher's existing SHA-256 password compatibility.
            command.Parameters.AddWithValue("@hash", passwords.HashForStorage(Password.FromSha256Hex(
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password))))));
            command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await command.ExecuteNonQueryAsync(cancellationToken);
            var id = checked((uint)command.LastInsertedId);
            await transaction.CommitAsync(cancellationToken);
            return new ManagerIdentity(id, username);
        }
        finally { _gate.Release(); }
    }
}
