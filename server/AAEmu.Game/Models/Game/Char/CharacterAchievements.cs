using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Achievement;
using AAEmu.Game.Models.Game.Achievement.Enums;

using MySql.Data.MySqlClient;

namespace AAEmu.Game.Models.Game.Char;

public class CharacterAchievements(Character owner, AchievementGameData gameData)
{
    private readonly object _sync = new();
    private AchievementInfo _party;

    public CharacterAchievements(Character owner) : this(owner, AchievementGameData.Instance)
    {
    }

    public void Record(CharRecordKind kind)
    {
        if (kind != CharRecordKind.EnrollParty || !gameData.CanEnrollParty())
            return;

        lock (_sync)
        {
            if (_party?.Complete > DateTime.MinValue)
                return;

            _party = new AchievementInfo { Id = 1382, Amount = 1, Complete = DateTime.UtcNow };
            owner.SendPacket(new SCAchievementChangedPacket(_party.Id, (int)_party.Amount));
            owner.SendPacket(new SCAchievementCompletedPacket(_party.Id));
        }
    }

    public List<AchievementInfo> GetSnapshot()
    {
        lock (_sync)
        {
            return _party == null ? [] :
                [new AchievementInfo { Id = _party.Id, Amount = _party.Amount, Complete = _party.Complete }];
        }
    }

    public void Send()
    {
        owner.SendPacket(new SCAchievementsPacket(GetSnapshot()));
    }

    public void Load(MySqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, amount, complete FROM character_achievements WHERE owner = @owner AND id = 1382";
        command.Parameters.AddWithValue("@owner", owner.Id);
        using var reader = command.ExecuteReader();
        lock (_sync)
        {
            _party = reader.Read() ? new AchievementInfo
            {
                Id = reader.GetUInt32("id"),
                Amount = reader.GetUInt32("amount"),
                Complete = reader.IsDBNull(reader.GetOrdinal("complete")) ? DateTime.MinValue : reader.GetDateTime("complete")
            } : null;
        }
    }

    public void Save(MySqlConnection connection, MySqlTransaction transaction)
    {
        foreach (var achievement in GetSnapshot())
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "REPLACE INTO character_achievements (owner, id, amount, complete) VALUES (@owner, @id, @amount, @complete)";
            command.Parameters.AddWithValue("@owner", owner.Id);
            command.Parameters.AddWithValue("@id", achievement.Id);
            command.Parameters.AddWithValue("@amount", achievement.Amount);
            command.Parameters.AddWithValue("@complete", achievement.Complete == DateTime.MinValue ? DBNull.Value : achievement.Complete);
            command.ExecuteNonQuery();
        }
    }
}
