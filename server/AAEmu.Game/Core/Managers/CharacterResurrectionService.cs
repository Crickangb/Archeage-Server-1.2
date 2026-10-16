using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Core.Managers;

public enum CharacterResurrectionMode
{
    ClientMotion,
    ServerControlled
}

/// <summary>
/// Owns native character resurrection policy for both connected clients and
/// server-controlled characters.
/// </summary>
public static class CharacterResurrectionService
{
    private const int RespawnCooldownDurationMs = 300_000;
    private const uint TemplePriestessTemplateId = 502;

    public static bool TryResurrect(
        Character character,
        bool inPlace,
        CharacterResurrectionMode mode = CharacterResurrectionMode.ClientMotion)
    {
        return TryResurrect(character, inPlace, mode, CharacterResurrectionOperations.Default);
    }

    internal static bool TryResurrect(
        Character character,
        bool inPlace,
        CharacterResurrectionMode mode,
        CharacterResurrectionOperations operations)
    {
        if (character?.Transform?.World == null || operations == null)
            return false;

        var now = operations.UtcNow();
        if (mode == CharacterResurrectionMode.ServerControlled)
        {
            if (!character.IsDead || !IsDeathWaitComplete(character, now))
                return false;
        }

        var portal = SelectReturnPortal(character, operations);
        var hasPacketReturn = HasPacketReturn(portal);
        var hasServerReturn = IsValidServerReturn(portal) &&
                              (character.Transform.InstanceId != WorldManager.DefaultInstanceId || portal.Id > 0);
        if (!inPlace && mode == CharacterResurrectionMode.ServerControlled && !hasServerReturn)
            return false;

        portal ??= CurrentPositionPortal(character);
        if (mode == CharacterResurrectionMode.ClientMotion && !hasPacketReturn)
            portal = CurrentPositionPortal(character);

        if (mode == CharacterResurrectionMode.ServerControlled && !inPlace &&
            !CommitServerTransform(character, portal, operations))
            return false;

        if (inPlace)
        {
            character.Hp = (int)(character.MaxHp * (character.ResurrectHpPercent / 100.0f));
            character.Mp = (int)(character.MaxMp * (character.ResurrectMpPercent / 100.0f));
            character.ResurrectHpPercent = 1;
            character.ResurrectMpPercent = 1;
        }
        else
        {
            character.Hp = (int)(character.MaxHp * 0.1);
            character.Mp = (int)(character.MaxMp * 0.1);
        }

        character.PostUpdateCurrentHp(character, 0, character.Hp, KillReason.Unknown);

        character.BroadcastPacket(
            new SCCharacterResurrectedPacket(
                character.ObjId,
                portal.X,
                portal.Y,
                portal.Z,
                portal.ZRot),
            true);
        character.BroadcastPacket(new SCUnitPointsPacket(character.ObjId, character.Hp, character.Mp), true);

        ApplyRevivalDebuffs(character, inPlace, operations, now);
        character.IsUnderWater = false;
        character.Breath = character.LungCapacity;
        return true;
    }

    internal static bool IsDeathWaitComplete(Character character, DateTime now)
    {
        if (character == null || character.DeadTime == DateTime.MinValue || character.RezWaitDuration <= 0)
            return true;

        try
        {
            return now >= character.DeadTime.AddMilliseconds(character.RezWaitDuration);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static Portal SelectReturnPortal(Character character, CharacterResurrectionOperations operations)
    {
        if (character.Transform.InstanceId != WorldManager.DefaultInstanceId)
        {
            var priestess = operations.GetInstanceNpcs(character)
                .FirstOrDefault(npc => npc?.TemplateId == TemplePriestessTemplateId);
            if (priestess == null)
                return null;

            return new Portal
            {
                WorldId = character.Transform.WorldId,
                ZoneId = priestess.Transform.ZoneId,
                X = priestess.Transform.World.Position.X + operations.NextPriestessOffset(),
                Y = priestess.Transform.World.Position.Y + operations.NextPriestessOffset(),
                Z = priestess.Transform.World.Position.Z,
                ZRot = priestess.Transform.World.Rotation.Z,
                Yaw = priestess.Transform.World.Rotation.Z
            };
        }

        Portal portal = null;
        var usePortalId = 0u;
        var currentZone = operations.GetZone(character.Transform.ZoneId);
        if (currentZone != null)
        {
            var conflict = operations.GetConflicts()
                .FirstOrDefault(candidate => candidate.ZoneGroupId == currentZone.GroupId);
            if (conflict?.CurrentZoneState == ZoneConflictType.War)
            {
                usePortalId = character.Faction?.MotherId switch
                {
                    FactionsEnum.NuiaAlliance => conflict.NuiaReturnPointId,
                    FactionsEnum.HaranyaAlliance => conflict.HariharaReturnPointId,
                    _ => 0u
                };
            }
        }

        if (usePortalId > 0)
            portal = operations.GetRespawnById(usePortalId);

        if (usePortalId == 0 || portal == null)
            portal = operations.GetClosestReturnPortal(character);

        return portal;
    }

    private static bool HasPacketReturn(Portal portal)
    {
        return portal?.X != 0f;
    }

    private static bool IsValidServerReturn(Portal portal)
    {
        return portal != null && portal.ZoneId != 0 &&
               float.IsFinite(portal.X) && float.IsFinite(portal.Y) &&
               float.IsFinite(portal.Z) && float.IsFinite(portal.ZRot);
    }

    private static bool CommitServerTransform(
        Character character,
        Portal portal,
        CharacterResurrectionOperations operations)
    {
        var previousZoneId = character.Transform.ZoneId;
        character.Transform.StickyParent = null;
        character.Transform.ApplyWorldSpawnPosition(new WorldSpawnPosition
        {
            WorldId = portal.WorldId,
            ZoneId = portal.ZoneId,
            X = portal.X,
            Y = portal.Y,
            Z = portal.Z,
            Yaw = portal.ZRot
        });
        var committed = character.Transform.Parent == null && character.Transform.StickyParent == null &&
                        character.Transform.ZoneId == portal.ZoneId &&
                        character.Transform.World.Position == new System.Numerics.Vector3(portal.X, portal.Y, portal.Z) &&
                        character.Transform.World.Rotation.Z == portal.ZRot;
        if (!committed)
            return false;

        operations.RefreshVisibility(character);
        if (previousZoneId != portal.ZoneId)
            operations.NotifyZoneChanged(character, previousZoneId, portal.ZoneId);
        return true;
    }

    private static Portal CurrentPositionPortal(Character character)
    {
        return new Portal
        {
            WorldId = character.Transform.WorldId,
            ZoneId = character.Transform.ZoneId,
            X = character.Transform.World.Position.X,
            Y = character.Transform.World.Position.Y,
            Z = character.Transform.World.Position.Z,
            ZRot = 0f,
            Yaw = 0f
        };
    }

    private static void ApplyRevivalDebuffs(
        Character character,
        bool inPlace,
        CharacterResurrectionOperations operations,
        DateTime now)
    {
        if (inPlace)
        {
            character.DiedInPvpWarZone = false;
            character.DiedInPvp = false;
            return;
        }

        if (character.DiedInPvpWarZone)
        {
            character.DiedInPvpWarZone = false;
            character.DiedInPvp = false;
            operations.ApplyBuff(character, (uint)BuffConstants.WarZoneLeech, 0, now);
            operations.ApplyBuff(character, (uint)BuffConstants.RespawnCooldown, RespawnCooldownDurationMs, now);
        }
        else if (character.DiedInPvp)
        {
            character.DiedInPvp = false;
            operations.ApplyBuff(character, (uint)BuffConstants.RespawnCooldown, RespawnCooldownDurationMs, now);
        }
        else
        {
            operations.ApplyBuff(character, (uint)BuffConstants.WeakenedBody, 0, now);
            operations.ApplyBuff(character, (uint)BuffConstants.RespawnCooldown, RespawnCooldownDurationMs, now);
        }
    }
}

internal sealed class CharacterResurrectionOperations
{
    internal static CharacterResurrectionOperations Default { get; } = new(
        () => DateTime.UtcNow,
        character => character.ParentWorld?.GetAllNpcs() ?? [],
        zoneId => ZoneManager.Instance.GetZoneByKey(zoneId),
        () => ZoneManager.Instance.GetConflicts(),
        portalId => PortalManager.Instance.GetRespawnById(portalId),
        character => PortalManager.Instance.GetClosestReturnPortal(character),
        () => Random.Shared.Next(1, 3),
        ApplyNativeBuff,
        character => WorldManager.Instance.AddVisibleObject(character),
        (character, previousZoneId, newZoneId) => character.OnZoneChange(previousZoneId, newZoneId));

    internal CharacterResurrectionOperations(
        Func<DateTime> utcNow,
        Func<Character, IEnumerable<Npc>> getInstanceNpcs,
        Func<uint, Zone> getZone,
        Func<IEnumerable<ZoneConflict>> getConflicts,
        Func<uint, Portal> getRespawnById,
        Func<Character, Portal> getClosestReturnPortal,
        Func<int> nextPriestessOffset,
        Action<Character, uint, int, DateTime> applyBuff,
        Action<Character> refreshVisibility,
        Action<Character, uint, uint> notifyZoneChanged)
    {
        UtcNow = utcNow;
        GetInstanceNpcs = getInstanceNpcs;
        GetZone = getZone;
        GetConflicts = getConflicts;
        GetRespawnById = getRespawnById;
        GetClosestReturnPortal = getClosestReturnPortal;
        NextPriestessOffset = nextPriestessOffset;
        ApplyBuff = applyBuff;
        RefreshVisibility = refreshVisibility;
        NotifyZoneChanged = notifyZoneChanged;
    }

    internal Func<DateTime> UtcNow { get; }
    internal Func<Character, IEnumerable<Npc>> GetInstanceNpcs { get; }
    internal Func<uint, Zone> GetZone { get; }
    internal Func<IEnumerable<ZoneConflict>> GetConflicts { get; }
    internal Func<uint, Portal> GetRespawnById { get; }
    internal Func<Character, Portal> GetClosestReturnPortal { get; }
    internal Func<int> NextPriestessOffset { get; }
    internal Action<Character, uint, int, DateTime> ApplyBuff { get; }
    internal Action<Character> RefreshVisibility { get; }
    internal Action<Character, uint, uint> NotifyZoneChanged { get; }

    private static void ApplyNativeBuff(Character character, uint buffId, int forcedDurationMs, DateTime now)
    {
        var template = SkillManager.Instance.GetBuffTemplate(buffId);
        if (template == null)
            return;

        var caster = new SkillCasterUnit(character.ObjId);
        var buff = new Buff(character, character, caster, template, null, now);
        if (forcedDurationMs > 0)
            character.Buffs.AddBuff(buff, forcedDuration: forcedDurationMs);
        else
            character.Buffs.AddBuff(buff);
    }
}
