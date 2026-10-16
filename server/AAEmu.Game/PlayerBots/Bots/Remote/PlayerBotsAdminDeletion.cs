using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;

namespace AAEmu.Game.Bots.Remote;

public sealed partial class PlayerBotsRemoteService
{
    public static string DeletionActorLabel(object account, uint accountId)
    {
        var data = JsonSerializer.SerializeToElement(account);
        return data.TryGetProperty("username", out var username) && username.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(username.GetString()) ? username.GetString()! : $"Account #{accountId}";
    }
    private static string DeletionHash(object snapshot) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot))));

    private object DeleteAdminBot(AdminBot bot, AdminCommand request, uint actorAccountId)
    {
        if (string.IsNullOrWhiteSpace(request.reason) || request.reason.Trim().Length > 500 ||
            request.reason.Any(char.IsControl) || !Guid.TryParse(request.operationId, out _))
            throw new InvalidOperationException("Enter a deletion reason of 1–500 characters and review again.");
        lock (BotAccountQuota.Sync)
        {
            if (_botManager.GetBot(bot.id) != null || AccountManager.Instance.Contains(bot.accountId))
                throw new InvalidOperationException("Dismiss the bot and log out of its dedicated account before deleting.");
            if (!ReadBotPool().accounts.Any(account => account.id == bot.accountId))
                throw new InvalidOperationException("Only characters on registered dedicated bot accounts can be deleted here.");
            var snapshot = ReadSavedAdminDetails(bot.id);
            if (DeletionHash(snapshot) != request.snapshotHash)
                throw new InvalidOperationException("Bot data changed. Review deletion again.");
            var actor = DeletionActorLabel(ReadManagerAccount(actorAccountId), actorAccountId);
            var folder = Path.Combine(AppContext.BaseDirectory, "Logs");
            Directory.CreateDirectory(folder);
            var journal = Path.Combine(folder, "PlayerBotsDeletions.jsonl");
            void Record(string outcome)
            {
                using var stream = new FileStream(journal, FileMode.Append, FileAccess.Write, FileShare.Read);
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
                    timestampUtc = DateTime.UtcNow, request.operationId, actor, actorAccountId,
                    botId = bot.id, bot.name, bot.level, bot.accountId, bot.ownerId,
                    reason = request.reason.Trim(), outcome, snapshot
                }) + "\n");
                stream.Write(bytes); stream.Flush(true);
            }
            // A complete durable snapshot is required before marking the character deleted.
            Record("requested");
            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE characters SET deleted=1, delete_request_time=@time, delete_time=@time WHERE id=@id AND account_id=@account AND deleted=0";
            command.Parameters.AddWithValue("@time", DateTime.UtcNow);
            command.Parameters.AddWithValue("@id", bot.id);
            command.Parameters.AddWithValue("@account", bot.accountId);
            if (command.ExecuteNonQuery() != 1)
            { Record("failed"); throw new InvalidOperationException("Character changed or was already deleted. Refresh the roster."); }
            // The archived database row must also leave the factory roster.
            // A backed-up repair on the next creation covers interrupted cleanup.
            var rosterCleaned = true;
            try { CleanupArchivedFactoryRoster(); } catch { rosterCleaned = false; }
            var completionSaved = true;
            try { Record("completed"); } catch { completionSaved = false; }
            return new { ok = true, deletedBotId = bot.id, deletion = snapshot,
                message = !rosterCleaned ? "Bot archived, but factory roster cleanup failed. Check the roster file before creating another bot." : completionSaved ? "Bot deleted. Its character and items remain archived in the database." : "Bot deleted; the pre-deletion record is saved, but completion logging failed. Check server disk permissions." };
        }
    }
}
