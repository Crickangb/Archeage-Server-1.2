using System.Linq;
using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Chat;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSSendChatMessagePacket() : GamePacket(CSOffsets.CSSendChatMessagePacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var type = (ChatType)stream.ReadInt16();
        var unk1 = stream.ReadInt16();
        var unk2 = stream.ReadInt32();

        var targetName = stream.ReadString();
        var message = stream.ReadString();
        var languageType = stream.ReadByte();
        var ability = stream.ReadInt32();

        Logger.Debug(message);

        if (message.StartsWith(CommandManager.CommandPrefix))
        {
            if (CommandManager.Instance.Handle(
                    Connection.ActiveChar,
                    message.Substring(CommandManager.CommandPrefix.Length).Trim(),
                    out _))
            {
                return;
            }
        }

        var activeChar = Connection.ActiveChar;

        var activeMotherFactionId =
            activeChar.Faction?.MotherId
            ?? FactionsEnum.Invalid;

        if (activeMotherFactionId == FactionsEnum.Invalid)
            activeMotherFactionId = activeChar.Faction?.Id ?? FactionsEnum.Invalid;

        var activeFactionSide = FactionSide(activeMotherFactionId);
        var activeNationSide = NationSide(activeChar.Race);

        var activeTeamForDiscord =
            type == ChatType.Party || type == ChatType.Raid || type == ChatType.RaidLeader
                ? TeamManager.Instance.GetActiveTeamByUnit(activeChar.Id)
                : null;

        var teamOnlineMembers =
            activeTeamForDiscord == null
                ? System.Array.Empty<object>()
                : activeTeamForDiscord.Members
                    .Where(m => m?.Character != null && m.Character.IsOnline)
                    .Select(m => (object)new
                    {
                        accountId = m.Character.AccountId,
                        characterId = m.Character.Id,
                        characterName = m.Character.Name
                    })
                    .ToArray();

        var guildId =
            activeChar.Expedition != null
                ? (uint)activeChar.Expedition.Id
                : 0u;

        var guildName =
            activeChar.Expedition?.Name
            ?? string.Empty;

        _ = AAEmu.Game.Services.DiscordBotService.Instance?.PostEventAsync(
            new
            {
                type = "game_chat",
                accountId = activeChar.AccountId,
                characterId = activeChar.Id,
                characterName = activeChar.Name,

                factionId = (uint)(activeChar.Faction?.Id ?? FactionsEnum.Invalid),
                faction =
                    activeChar.Faction?.Name
                    ?? activeChar.FactionName
                    ?? "Unknown",
                motherFactionId = (uint)activeMotherFactionId,
                side = activeFactionSide,
                race = (byte)activeChar.Race,
                nationSide = activeNationSide,

                guildId,
                guildName,
                familyId = activeChar.Family,
                level = activeChar.Level,

                teamId = activeTeamForDiscord?.Id ?? 0u,
                teamIsParty = activeTeamForDiscord?.IsParty ?? true,
                teamMemberCount = activeTeamForDiscord?.MembersCount() ?? 0,
                teamOnlineMembers,

                chatType = type.ToString().ToLowerInvariant(),
                targetName,
                message
            }
        );

        // Sidenote: Trino mixed up /faction and /nation back then,
        // it was supposed to be the other way around.
        switch (type)
        {
            case ChatType.Whisper:
                var target = WorldManager.Instance.GetCharacter(targetName);

                if (target == null || !target.IsOnline)
                {
                    activeChar.SendErrorMessage(ErrorMessageType.WhisperNoTarget);
                }
                else if (target.Faction.MotherId != activeChar.Faction.MotherId)
                {
                    // TODO: proper hostile check
                    activeChar.SendErrorMessage(ErrorMessageType.ChatCannotWhisperToHostile);
                }
                else
                {
                    var packet =
                        new SCChatMessagePacket(
                            ChatType.Whisper,
                            activeChar,
                            message,
                            ability,
                            languageType
                        );

                    target.SendPacket(packet);

                    var packetMe =
                        new SCChatMessagePacket(
                            ChatType.Whispered,
                            target,
                            message,
                            ability,
                            languageType
                        );

                    Connection.SendPacket(packetMe);

                    // Dedicated successful-whisper event for the private Discord admin log.
                    // The generic game_chat event is emitted before validation, so Node ignores
                    // generic whisper events and only logs this authoritative success event.
                    _ = AAEmu.Game.Services.DiscordBotService.Instance?.PostEventAsync(
                        new
                        {
                            type = "whisper_chat",
                            accountId = activeChar.AccountId,
                            characterId = activeChar.Id,
                            characterName = activeChar.Name,
                            factionId = (uint)(activeChar.Faction?.Id ?? FactionsEnum.Invalid),
                            faction = activeChar.Faction?.Name ?? activeChar.FactionName ?? "Unknown",
                            motherFactionId = (uint)activeMotherFactionId,
                            side = activeFactionSide,
                            guildId = activeChar.Expedition != null ? (uint)activeChar.Expedition.Id : 0u,
                            guildName = activeChar.Expedition?.Name ?? string.Empty,
                            familyId = activeChar.Family,
                            targetAccountId = target.AccountId,
                            targetCharacterId = target.Id,
                            targetName = target.Name,
                            message
                        }
                    );
                }

                break;

            case ChatType.White:
                activeChar.BroadcastPacket(
                    new SCChatMessagePacket(
                        type,
                        activeChar,
                        message,
                        ability,
                        languageType
                    ),
                    true
                );

                break;

            case ChatType.RaidLeader:
            case ChatType.Raid:
                var teamRaid =
                    TeamManager.Instance.GetActiveTeamByUnit(activeChar.Id);

                if (teamRaid != null)
                {
                    if (
                        type == ChatType.RaidLeader &&
                        teamRaid.OwnerId != activeChar.Id
                    )
                    {
                        activeChar.SendErrorMessage(
                            ErrorMessageType.ChatNotRaidOwner
                        );
                    }
                    else
                    {
                        ChatManager.Instance
                            .GetRaidChat(teamRaid)
                            .SendPacket(
                                new SCChatMessagePacket(
                                    type,
                                    activeChar,
                                    message,
                                    ability,
                                    languageType
                                )
                            );
                    }
                }
                else
                {
                    activeChar.SendErrorMessage(
                        ErrorMessageType.ChatNotInRaid
                    );
                }

                break;

            case ChatType.Party:
                var partyRaid =
                    TeamManager.Instance.GetActiveTeamByUnit(activeChar.Id);

                if (partyRaid != null)
                {
                    ChatManager.Instance
                        .GetPartyChat(partyRaid, activeChar)
                        .SendMessage(
                            activeChar,
                            message,
                            ability,
                            languageType
                        );
                }
                else
                {
                    activeChar.SendErrorMessage(
                        ErrorMessageType.ChatNotInParty
                    );
                }

                break;

            case ChatType.Trade:
            case ChatType.GroupFind:
            case ChatType.Shout:
                ChatManager.Instance
                    .GetZoneChat(activeChar.Transform.ZoneId)
                    .SendPacket(
                        new SCChatMessagePacket(
                            type,
                            activeChar,
                            message,
                            ability,
                            languageType
                        )
                    );

                break;

            case ChatType.Clan:
                if (activeChar.Expedition != null)
                {
                    ChatManager.Instance
                        .GetGuildChat(activeChar.Expedition)
                        .SendMessage(
                            activeChar,
                            message,
                            ability,
                            languageType
                        );
                }
                else
                {
                    activeChar.SendErrorMessage(
                        ErrorMessageType.ChatNotInExpedition
                    );
                }

                break;

            case ChatType.Family:
                if (activeChar.Family > 0)
                {
                    ChatManager.Instance
                        .GetFamilyChat(activeChar.Family)
                        .SendMessage(
                            activeChar,
                            message,
                            ability,
                            languageType
                        );
                }
                else
                {
                    activeChar.SendErrorMessage(
                        ErrorMessageType.ChatNotInFamily
                    );
                }

                break;

            case ChatType.Region:
                ChatManager.Instance
                    .GetNationChat(activeChar.Race)
                    .SendMessage(
                        activeChar,
                        message,
                        ability,
                        languageType
                    );

                break;

            case ChatType.Ally:
                ChatManager.Instance
                    .GetFactionChat(activeChar.Faction.MotherId)
                    .SendMessage(
                        activeChar,
                        message,
                        ability,
                        languageType
                    );

                break;

            case ChatType.Judge:
                ChatManager.Instance
                    .GetTrialChat(activeChar)
                    ?.SendMessage(
                        activeChar,
                        message,
                        ability,
                        languageType
                    );

                break;

            default:
                Logger.Warn(
                    "Unsupported chat type {0} from {1}",
                    type,
                    activeChar.Name
                );

                break;
        }
    }
    private static string NationSide(Race race)
    {
        if (race == Race.None)
            return string.Empty;

        // Match ChatManager.GetNationChat birth-continent grouping exactly.
        var raceGroup = ((byte)race - 1) & 0xFC;
        var nuiaGroup = ((byte)Race.Nuian - 1) & 0xFC;
        var haranyaGroup = ((byte)Race.Hariharan - 1) & 0xFC;

        if (raceGroup == nuiaGroup)
            return "Nuia";
        if (raceGroup == haranyaGroup)
            return "Haranya";

        return string.Empty;
    }

    private static string FactionSide(FactionsEnum motherFactionId)
    {
        return motherFactionId switch
        {
            FactionsEnum.NuiaAlliance => "Nuia",
            FactionsEnum.HaranyaAlliance => "Haranya",
            FactionsEnum.Pirate => "Pirate",
            _ => "Other"
        };
    }

}
