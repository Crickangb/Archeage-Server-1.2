using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSResurrectCharacterPacket() : GamePacket(CSOffsets.CSResurrectCharacterPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var inPlace = stream.ReadBoolean();

        Logger.Debug("ResurrectCharacter, InPlace: {0}", inPlace);

        CharacterResurrectionService.TryResurrect(
            Connection.ActiveChar,
            inPlace,
            CharacterResurrectionMode.ClientMotion);
    }
}
