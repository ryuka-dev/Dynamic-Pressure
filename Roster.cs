using System.Collections.Generic;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;

namespace Ryuka.Sulfur.DynamicPressure
{
    internal enum RosterRole
    {
        Rank,
        Flanker,
        Ranged,
        Zoner,
        Tank
    }

    /// <summary>
    /// The enemies the director may add, listed one by one.
    /// </summary>
    /// <remarks>
    /// An allow list on purpose. The game has immortal hunters, vehicles that only work when a
    /// level sets them up, turrets, summoned parts of bosses and scripted scares that all look like
    /// ordinary enemies from their data, and a new enemy from an update should not be added
    /// mid-fight until someone has looked at it.
    /// </remarks>
    internal static class Roster
    {
        private struct Entry
        {
            public readonly UnitId Id;
            public readonly RosterRole Role;

            public Entry(UnitId id, RosterRole role)
            {
                Id = id;
                Role = role;
            }
        }

        private static readonly Entry[] Entries =
        {
            new Entry(UnitIds.GoblinYoung, RosterRole.Rank),
            new Entry(UnitIds.GoblinSpearman, RosterRole.Rank),
            new Entry(UnitIds.GoblinArcher, RosterRole.Rank),
            new Entry(UnitIds.GoblinWizardElectricity, RosterRole.Ranged),
            new Entry(UnitIds.GoblinWizardFire, RosterRole.Ranged),
            new Entry(UnitIds.GoblinWizardFrost, RosterRole.Ranged),
            new Entry(UnitIds.GoblinWizardPoison, RosterRole.Ranged),
            new Entry(UnitIds.GoblinBarrelBoy, RosterRole.Tank),

            new Entry(UnitIds.BlackGuildBruiser, RosterRole.Rank),
            new Entry(UnitIds.BlackGuildDog, RosterRole.Rank),
            new Entry(UnitIds.BlackGuildRifleman, RosterRole.Rank),
            new Entry(UnitIds.BlackGuildTracker, RosterRole.Rank),
            new Entry(UnitIds.BlackGuildAssassin, RosterRole.Flanker),
            new Entry(UnitIds.BlackGuildBishop, RosterRole.Ranged),
            new Entry(UnitIds.BlackGuildSapper, RosterRole.Zoner),
            new Entry(UnitIds.BlackGuildNovanianGuard, RosterRole.Tank),

            new Entry(UnitIds.CorruptedGuildsman, RosterRole.Rank),
            new Entry(UnitIds.CorruptedGuildsmanRanged, RosterRole.Rank),
            new Entry(UnitIds.CorruptedSpearman, RosterRole.Rank),
            new Entry(UnitIds.CorruptedGoblinArcher, RosterRole.Rank),

            new Entry(UnitIds.CrawWatcher, RosterRole.Flanker),
            new Entry(UnitIds.CrawSentinel, RosterRole.Tank),
            new Entry(UnitIds.CrawAbomination, RosterRole.Zoner),

            new Entry(UnitIds.HellshrewSticka, RosterRole.Flanker),
            new Entry(UnitIds.HellshrewShoota, RosterRole.Rank),
            new Entry(UnitIds.HellshrewSnipa, RosterRole.Ranged),

            new Entry(UnitIds.ShavwaSamurai, RosterRole.Rank),
            new Entry(UnitIds.ShavwaWarden, RosterRole.Ranged),
            new Entry(UnitIds.ShavwaGrenadier, RosterRole.Ranged),
            new Entry(UnitIds.ShavwaTwerker, RosterRole.Tank),
        };

        public static bool TryGetRole(UnitId id, out RosterRole role)
        {
            for (int i = 0; i < Entries.Length; i++)
            {
                if (Entries[i].Id.value == id.value)
                {
                    role = Entries[i].Role;
                    return true;
                }
            }

            role = RosterRole.Rank;
            return false;
        }

        /// <summary>
        /// Fills <paramref name="eligible"/> with the roster units this level may use: those the
        /// environment normally fields or that are fighting on the map right now, no dearer than
        /// one of the level's packs, and allowed for the style at this point in the game.
        /// </summary>
        public static void CollectEligible(
            GameManager gm,
            PressurePreset preset,
            float packBudget,
            bool lateGame,
            List<UnitSO> eligible)
        {
            eligible.Clear();

            if (gm == null || gm.currentEnvironment == null || gm.environmentsInOrder == null)
            {
                return;
            }

            WorldEnvironmentList.Metadata metadata = gm.environmentsInOrder.GetMetadata(gm.currentEnvironment.id);
            UnitId[] environmentUnits = metadata.availableEnemiesReadOnly;
            if (environmentUnits != null)
            {
                for (int i = 0; i < environmentUnits.Length; i++)
                {
                    if (environmentUnits[i].IsValid)
                    {
                        AddIfEligible(environmentUnits[i].GetAsset(), preset, packBudget, lateGame, eligible);
                    }
                }
            }

            List<Npc> alive = gm.aliveNpcs;
            if (alive == null)
            {
                return;
            }

            for (int i = 0; i < alive.Count; i++)
            {
                Npc npc = alive[i];
                if (npc != null && npc.IsAlive && npc.gameObject.activeInHierarchy)
                {
                    AddIfEligible(npc.unitSO, preset, packBudget, lateGame, eligible);
                }
            }
        }

        private static void AddIfEligible(
            UnitSO unit,
            PressurePreset preset,
            float packBudget,
            bool lateGame,
            List<UnitSO> eligible)
        {
            RosterRole role;
            if (unit == null || !TryGetRole(unit.id, out role) || eligible.Contains(unit))
            {
                return;
            }

            if (packBudget > 0f && unit.SpawnCost > packBudget)
            {
                return;
            }

            if (role == RosterRole.Tank && (!preset.AllowTanks || (preset.TanksOnlyLate && !lateGame)))
            {
                return;
            }

            if (unit.IsHostileTo(FactionIds.Player))
            {
                eligible.Add(unit);
            }
        }
    }
}
