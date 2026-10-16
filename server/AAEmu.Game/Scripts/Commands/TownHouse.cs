using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Utils.DB;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

// Visual experiment only. Never registered with HousingManager or saved.
public class TownHouse : ICommand
{
    private const string RegistryKey = "AAEmu.MistmerrowHousePreview.v1";
    private static readonly object RegistryLock = string.Intern(RegistryKey);
    public string[] CommandNames { get; set; } = ["townhouse"];

    public void OnLoad()
    {
        CommandManager.Instance.Register(CommandNames, this);
    }

    public string GetCommandLineHelp()
    {
        return "[list|clear|designId] [distance=12] [heightOffset=0] [yawDegrees]";
    }

    public string GetCommandHelpText()
    {
        return "Temporary finished house model in Mistmerrow. Uses a design in your bag without consuming it. " +
            "Up to 8 previews. /townhouse clear removes your previews, including after script reload. " +
            "Stand on the ground first. This is scenery, not owned housing; doors/furniture are omitted.";
    }

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        // Explicit gate also applies if somebody lowers the configured command access.
        if (CharacterManager.Instance.GetEffectiveAccessLevel(character) < 100)
        {
            CommandManager.SendErrorText(this, messageOutput, "Admin access level 100 is required.");
            return;
        }

        lock (RegistryLock)
        {
            var registry = AppDomain.CurrentDomain.GetData(RegistryKey) as Dictionary<uint, List<House>>;
            if (registry == null)
            {
                registry = new Dictionary<uint, List<House>>();
                AppDomain.CurrentDomain.SetData(RegistryKey, registry);
            }

            if (args.Length == 1 && args[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                if (!registry.TryGetValue(character.Id, out var existing))
                {
                    CommandManager.SendNormalText(this, messageOutput, "No house previews to remove.");
                    return;
                }
                foreach (var preview in existing.ToArray())
                {
                    RemovePreview(preview);
                    existing.Remove(preview);
                }
                registry.Remove(character.Id);
                CommandManager.SendNormalText(this, messageOutput, "Your temporary house previews have been removed.");
                return;
            }

            var designs = GetBagDesigns(character);
            if (args.Length == 1 && args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                ShowDesigns(designs, messageOutput);
                return;
            }

            if (character.ParentWorld?.Template.Name != "main_world" || character.Transform.ZoneId != 307)
            {
                CommandManager.SendErrorText(this, messageOutput, "This experiment is restricted to Mistmerrow (zone key 307). Clear works anywhere.");
                return;
            }

            var defaultDesign = designs.Count == 1 ? designs.Keys.First() : 0u;
            if (args.Length == 0 && defaultDesign == 0)
            {
                ShowDesigns(designs, messageOutput);
                return;
            }
            if (!TryParsePlacement(args, defaultDesign, out var designId, out var distance, out var heightOffset, out var yawDegrees))
            {
                CommandManager.SendErrorText(this, messageOutput, "Usage: /townhouse <designId> [distance 5..40] [heightOffset -10..10] [yawDegrees -360..360]. Use decimal points.");
                return;
            }
            if (!designs.TryGetValue(designId, out var template))
            {
                CommandManager.SendErrorText(this, messageOutput, "That housing design is not in your bag. Use /townhouse list.");
                return;
            }

            if (!registry.TryGetValue(character.Id, out var previews))
            {
                previews = new List<House>();
                registry.Add(character.Id, previews);
            }
            if (previews.Count >= 8)
            {
                CommandManager.SendErrorText(this, messageOutput, "Preview limit reached. Use /townhouse clear first.");
                return;
            }

            var house = new House();
            try
            {
                house.ObjId = ObjectIdManager.Instance.GetNextId();
                house.TlId = (ushort)HousingTldManager.Instance.GetNextId();
                house.Id = HousingIdManager.Instance.GetNextId();
                house.Template = template;
                house.TemplateId = template.Id;
                house.Faction = character.Faction;
                house.Name = "Temporary house preview";
                house.OwnerId = character.Id;
                house.CoOwnerId = character.Id;
                house.AccountId = character.AccountId;
                house.Permission = HousingPermission.Private;
                house.AllowRecover = false;
                house.PlaceDate = DateTime.UtcNow;
                house.ProtectionEndDate = DateTime.UtcNow.AddDays(21);
                house.Transform = character.Transform.CloneDetached(house);
                house.Transform.Local.AddDistanceToFront(distance);
                var position = house.Transform.Local.Position;
                house.Transform.Local.SetPosition(position.X, position.Y, position.Z + heightOffset);
                var yaw = yawDegrees.HasValue ? yawDegrees.Value * MathF.PI / 180f : character.Transform.World.Rotation.Z;
                house.Transform.Local.SetRotation(0f, 0f, yaw);

                // Suppress bindings: House.CurrentStep may otherwise save doors to MySQL.
                house.IsBeingLoadedFromDb = true;
                house.CurrentStep = -1;
                house.Hp = house.MaxHp;
                house.IsDirty = false;

                // Match normal housing protection without registering a persistent property.
                var protection = SkillManager.Instance.GetBuffTemplate((uint)BuffConstants.Untouchable);
                if (protection == null)
                    throw new InvalidOperationException("Untouchable protection template is missing; preview cancelled.");
                house.Buffs.AddBuff(new Buff(house, house, new SkillCasterUnit(house.ObjId), protection, null, DateTime.UtcNow));

                house.Spawn();
                previews.Add(house);
                CommandManager.SendNormalText(this, messageOutput,
                    $"House preview placed: design {designId}, model {template.MainModelId}. " +
                    "No design or taxes consumed. Height uses your standing Z plus offset. Use /townhouse clear to remove.");
            }
            catch
            {
                // Keep failed previews tracked if cleanup itself fails, so clear can retry.
                if (!previews.Contains(house))
                    previews.Add(house);
                RemovePreview(house);
                previews.Remove(house);
                throw;
            }
        }
    }

    private Dictionary<uint, HousingTemplate> GetBagDesigns(Character character)
    {
        var bagIds = character.Inventory.Bag.Items.Where(item => item != null).Select(item => item.TemplateId).ToHashSet();
        var designs = new Dictionary<uint, HousingTemplate>();
        using var connection = SQLite.CreateConnection(); // Mode=ReadOnly in AAEmu.
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT item_id, design_id FROM item_housings ORDER BY design_id";
        using var reader = query.ExecuteReader();
        while (reader.Read())
        {
            var itemId = Convert.ToUInt32(reader["item_id"]);
            var designId = Convert.ToUInt32(reader["design_id"]);
            if (!bagIds.Contains(itemId))
                continue;
            var template = HousingGameData.Instance.GetTemplate(designId);
            if (template != null && template.MainModelId != 0)
                designs[designId] = template;
        }
        return designs;
    }

    private void ShowDesigns(Dictionary<uint, HousingTemplate> designs, IMessageOutput messageOutput)
    {
        if (designs.Count == 0)
        {
            CommandManager.SendErrorText(this, messageOutput, "No usable housing design was found in your bag.");
            return;
        }
        foreach (var design in designs.Take(20))
            CommandManager.SendNormalText(this, messageOutput,
                $"Design {design.Key}: {design.Value.Name} (model {design.Value.MainModelId}) - /townhouse {design.Key}");
    }

    public static bool TryParsePlacement(string[] args, uint defaultDesign, out uint designId,
        out float distance, out float heightOffset, out float? yawDegrees)
    {
        designId = defaultDesign;
        distance = 12f;
        heightOffset = 0f;
        yawDegrees = null;
        if (args.Length > 4 || (args.Length > 0 && !uint.TryParse(args[0], out designId)) || designId == 0)
            return false;
        if (args.Length > 1 && (!TryNumber(args[1], out distance) || distance < 5f || distance > 40f))
            return false;
        if (args.Length > 2 && (!TryNumber(args[2], out heightOffset) || heightOffset < -10f || heightOffset > 10f))
            return false;
        if (args.Length > 3)
        {
            if (!TryNumber(args[3], out var yaw) || yaw < -360f || yaw > 360f)
                return false;
            yawDegrees = yaw;
        }
        return true;
    }

    private static bool TryNumber(string value, out float number)
    {
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && float.IsFinite(number);
    }

    private static void RemovePreview(House house)
    {
        if (house.ParentWorld != null)
            house.Delete();
        if (house.ObjId != 0)
        {
            ObjectIdManager.Instance.ReleaseId(house.ObjId);
            house.ObjId = 0;
        }
        if (house.TlId != 0)
        {
            HousingTldManager.Instance.ReleaseId(house.TlId);
            house.TlId = 0;
        }
        if (house.Id != 0)
        {
            HousingIdManager.Instance.ReleaseId(house.Id);
            house.Id = 0;
        }
    }
}
