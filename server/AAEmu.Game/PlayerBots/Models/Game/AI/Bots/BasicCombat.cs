using System;
using System.Numerics;
using AAEmu.Game.Bots.Body;
using AAEmu.Game.Bots.Diagnostics;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Models.Game.AI.Bots
{
    /// <summary>
    /// Fallback combat handler for bots that have no archetype or no specific implementation.
    /// Uses simple melee auto‑attack at melee range.
    /// For ranged (Primeval) we will have a separate handler later.
    /// </summary>
    public static class BasicCombat
    {
        public static bool Execute(Character bot, BotCombatState state, Unit target)
        {
            if (bot == null || target == null || state == null)
                return false;

            if (IsCleric(bot))
                return ExecuteCleric(bot, state, target);

            // Check if we are in melee range
            float dist = (float)bot.GetDistanceTo(target, true);
            float meleeRange = (float)BotConfig.Instance.AttackRange;

            // Before pathing into melee, let learned skills evaluate at the
            // bot's CURRENT range. This is required for gap closers such as
            // Battlerage Charge: previously learned skills were only evaluated
            // after reaching melee range, making Charge impossible to open with.
            if (dist > meleeRange && UseLearnedSkill(bot, state, target))
                return true;

            // If no legal learned skill can be used from here, move closer.
            if (dist > meleeRange)
            {
                var dest = CombatDestination(bot, target, meleeRange) ?? ComputeChaseDestination(
                    bot.Transform.World.Position,
                    target.Transform.World.Position,
                    meleeRange,
                    bot.ParentWorld == null ? null : bot.ParentWorld.GetHeight);
                // v0.17.0: combat pursuit must follow a moving target. float.MaxValue
                // effectively made every later destination look "close enough", so the
                // first chase point could stay frozen until Albert reached it.
                BotManager.Instance.SetBotDestinationIfChanged(bot, dest, run: true, tolerance: 0.75f);
                return true;
            }

            if (CombatDestination(bot, target, meleeRange) is { } spaced &&
                Vector2.Distance(new Vector2(bot.Transform.World.Position.X, bot.Transform.World.Position.Y),
                    new Vector2(spaced.X, spaced.Y)) > 0.75f)
            {
                BotManager.Instance.SetBotDestinationIfChanged(bot, spaced, run: true, tolerance: 0.25f);
                return true;
            }

            // Stop moving if we were moving
            BotManager.Instance.StopIfMoving(bot);

            // Face target
            if (state.LastFacingAngle == float.MinValue || Math.Abs(state.LastFacingAngle - (float)MathUtil.CalculateAngleFrom(bot.Transform.World.Position, target.Transform.World.Position)) > 0.01f)
            {
                FaceTarget(bot, target);
                state.LastFacingAngle = (float)MathUtil.CalculateAngleFrom(bot.Transform.World.Position, target.Transform.World.Position);
            }

            // Start auto‑attack if not already
#if !PLAYERBOTS_AAEMU_3_0
            if (!AAEmu.Game.Bots.Host.BotDrivers.For(bot).Owns(bot))
#endif
            if (!bot.IsAutoAttack)
                StartAutoAttack(bot, target);

            UseLearnedSkill(bot, state, target);

            return true;
        }

        private static bool IsCleric(Character bot)
        {
            var archetype = BotArchetypeManager.Instance.GetState(bot);
            return string.Equals(archetype?.ArchetypeName ?? archetype?.PlannedArchetype, "Cleric", StringComparison.OrdinalIgnoreCase);
        }

        private static Vector3? CombatDestination(Character bot, Unit target, float range)
        {
            var peers = BotManager.Instance.GetAllBots().Where(other => !other.IsDead &&
                ReferenceEquals(other.ParentWorld, bot.ParentWorld) && other.Transform.InstanceId == bot.Transform.InstanceId &&
                BotCombatManager.Instance.GetState(other)?.Target?.ObjId == target.ObjId && IsCleric(other) == IsCleric(bot))
                .OrderBy(other => other.Id).ToArray();
            var slot = Array.FindIndex(peers, other => other.Id == bot.Id);
            if (peers.Length < 2 || slot < 0) return null;
            var position = BotCombatSpacing.Position(target.Transform.World.Position, target.ObjId, slot, peers.Length, range);
            if (bot.ParentWorld != null) position.Z = bot.ParentWorld.GetHeight(position.X, position.Y);
            return position;
        }

        private static bool ExecuteCleric(Character bot, BotCombatState state, Unit target)
        {
            if (bot.SkillTask != null) return true;
            var learned = bot.Skills?.Skills?.Values.FirstOrDefault(skill => skill.Template?.Id == 10534);
            var template = learned?.Template;
            if (template == null) { BotManager.Instance.StopIfMoving(bot); return true; }
            var distance = (float)bot.GetDistanceTo(target, true);
            var preferredRange = MathF.Max(1f, MathF.Min(12f, template.MaxRange * 0.7f));
            var formationDestination = CombatDestination(bot, target, preferredRange);
            var destination = formationDestination ?? BotCombatSpacing.RangedPosition(
                bot.Transform.World.Position, target.Transform.World.Position, preferredRange);
            if (bot.ParentWorld != null) destination.Z = bot.ParentWorld.GetHeight(destination.X, destination.Y);
#if !PLAYERBOTS_AAEMU_3_0
            var runtime = AAEmu.Game.Bots.Host.BotDrivers.Runtime(bot);
            if (runtime != null && runtime.Social.CommitLowestHealthMember(template.MaxRange, maximumHealthPercent: 75f) != null)
            {
                var context = new AAEmu.Game.Bots.Kernel.BotContext(bot, runtime, runtime.Blackboard, DateTime.UtcNow,
                    runtime.ConfigurationOverride ?? BotConfig.Instance, AAEmu.Game.Bots.Kernel.BotEngineKind.Combat);
                var result = new BotCastSkillAction(template.Id, TargetSource.PartyLowest, templateResolver: _ => template,
                    requireKnownSkill: true).Execute(context, default);
                if (result is AAEmu.Game.Bots.Kernel.BotActionResult.Success or AAEmu.Game.Bots.Kernel.BotActionResult.Pending) return true;
            }
#endif
            if (distance > preferredRange + 0.75f || distance < preferredRange * 0.6f ||
                formationDestination.HasValue && Vector2.Distance(
                    new Vector2(bot.Transform.World.Position.X, bot.Transform.World.Position.Y),
                    new Vector2(destination.X, destination.Y)) > 0.75f)
            {
                BotManager.Instance.SetBotDestinationIfChanged(bot, destination, run: true, tolerance: 0.5f);
                return true;
            }
            BotManager.Instance.StopIfMoving(bot);
            FaceTarget(bot, target);
            UseLearnedSkill(bot, state, target);
            return true;
        }

        internal static Vector3 ComputeChaseDestination(
            Vector3 botPosition,
            Vector3 targetPosition,
            float meleeRange,
            Func<float, float, float> groundHeight = null)
        {
            var offset = new Vector2(targetPosition.X - botPosition.X, targetPosition.Y - botPosition.Y);
            var planarDistance = offset.Length();
            if (!float.IsFinite(planarDistance) || planarDistance <= 0.0001f)
                return botPosition;

            var direction = offset / planarDistance;
            var stopDistance = MathF.Min(MathF.Max(0f, meleeRange), planarDistance);
            var destination = new Vector3(
                targetPosition.X - direction.X * stopDistance,
                targetPosition.Y - direction.Y * stopDistance,
                botPosition.Z);

            if (groundHeight == null)
                return destination;

            try
            {
                var surfaceZ = groundHeight(destination.X, destination.Y);
                if (float.IsFinite(surfaceZ) && surfaceZ > 0f)
                    destination.Z = surfaceZ;
            }
            catch
            {
                // The navigation boundary remains the final authority when height data is unavailable.
            }

            return destination;
        }

        private static void StartAutoAttack(Character bot, Unit target)
        {
            uint skillId = 2; // melee auto‑attack
            var template = SkillManager.Instance.GetSkillTemplate(skillId);
            if (template == null) return;
            var skill = new Skill(template);
            var caster = new SkillCasterUnit(bot.ObjId);
            var targetCaster = new SkillCastUnitTarget(target.ObjId);
            var result = skill.Use(bot, caster, targetCaster, null, false, out _);

            if (result == SkillResult.Success)
            {
                bot.CurrentTarget = target;
                bot.IsAutoAttack = true;
                bot.StartAutoSkill(skill);
                // ---- CRITICAL FIX: Set IsInBattle on both parties ----
                // This ensures regen drops to PersistentHpRegen (low) instead of out-of-combat HpRegen (high).
                bot.IsInBattle = true;
                target.IsInBattle = true;
            }
        }

        private static bool UseLearnedSkill(Character bot, BotCombatState state, Unit target)
        {
            var now = DateTime.UtcNow;
            var template = LearnedSkillDecision.Select(bot, target, state, now, BotConfig.Instance);
            if (template == null)
            {
                BotCombatTelemetry.RecordDiagnostic(bot.Id, target, (float)bot.GetDistanceTo(target, true),
                    bot.IsAutoAttack ? "no-usable-learned-skill; auto-attack-active" : "no-usable-learned-skill; auto-attack-inactive");
                return false;
            }

            // v0.16.8: Charge [11918] is a gap closer. The generic BasicCombat
            // learned-skill path used to bypass the Darkrunner rotation's minimum
            // range and could spend Charge while already standing in melee.
            // Leave the skill itself untouched; only decline this AI choice below 5m.
            if (template.Id == 11918 && bot.GetDistanceTo(target, true) < 5f)
            {
                BotCombatTelemetry.RecordDiagnostic(bot.Id, target, (float)bot.GetDistanceTo(target, true),
                    "charge-rejected-below-5m");
                return false;
            }

#if !PLAYERBOTS_AAEMU_3_0
            if (AAEmu.Game.Bots.Host.BotDrivers.For(bot).Owns(bot))
            {
                var runtime = AAEmu.Game.Bots.Host.BotDrivers.Runtime(bot);
                if (runtime == null) return false;
                var context = new AAEmu.Game.Bots.Kernel.BotContext(bot, runtime, runtime.Blackboard,
                    now, runtime.ConfigurationOverride ?? BotConfig.Instance, AAEmu.Game.Bots.Kernel.BotEngineKind.Combat);
                var result = new BotCastSkillAction(template.Id, templateResolver: _ => template, requireKnownSkill: true)
                    .Execute(context, default);
                return result is AAEmu.Game.Bots.Kernel.BotActionResult.Success or AAEmu.Game.Bots.Kernel.BotActionResult.Pending;
            }
#endif
            var skill = new Skill(template, bot);
            var caster = new SkillCasterUnit(bot.ObjId);
            var skillTarget = new SkillCastUnitTarget(target.ObjId);
            var castResult = skill.Use(bot, caster, skillTarget, null, false, out _);
            BotCombatTelemetry.Record(bot.Id, template.Id, target, (float)bot.GetDistanceTo(target, true), castResult.ToString(), "basic-combat");
            if (castResult != SkillResult.Success)
                return false;

            state.LastSkillTime = now;
            bot.IsInBattle = true;
            target.IsInBattle = true;
            return true;
        }

        private static void FaceTarget(Character bot, Unit target)
        {
            var angle = MathUtil.CalculateAngleFrom(bot.Transform.World.Position, target.Transform.World.Position);
            var pos = bot.Transform.World.Position;
            BotManager.Instance.GetBroadcaster(bot.Id)?.SendFaceTarget(pos, (float)angle - 90, bot.IsInBattle);
            bot.Transform.FinalizeTransform();
        }
    }
}
