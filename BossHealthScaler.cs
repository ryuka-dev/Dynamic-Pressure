using System.Collections.Generic;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.CharacterStats;
using PerfectRandom.Sulfur.Core.Stats;
using PerfectRandom.Sulfur.Core.UI;
using PerfectRandom.Sulfur.Core.Units;
using PerfectRandom.Sulfur.Core.Utilities;
using UnityEngine;

namespace Ryuka.Sulfur.DynamicPressure
{
    /// <summary>
    /// Owns the extra health bosses get. The multiplier is fixed when a level starts, so every
    /// boss of one level gets the same value and the host can tell a client what it used. On a
    /// co-op client the host's value replaces the local one, because the host owns the bosses.
    /// </summary>
    internal sealed class BossHealthScaler
    {
        public const float MinMultiplier = 1f;
        public const float MaxMultiplier = 3f;

        // Tags this mod's modifier so it can be replaced without touching anyone else's.
        private const uint ModifierSource = 0x44505248u;

        private readonly List<Npc> _bossesThisLevel = new List<Npc>();
        private float _levelMultiplier = 1f;
        private float _hostMultiplier = -1f;

        /// <summary>What this machine's own setting gave the current level.</summary>
        public float LevelMultiplier
        {
            get { return _levelMultiplier; }
        }

        /// <summary>
        /// What the host tells clients: the value its own bosses get right now, which is 1 in
        /// Endless and Crypt. A client applies it as it comes, so the two ends cannot disagree
        /// about whether this is a challenge mode.
        /// </summary>
        public float HostAnnouncement
        {
            get { return InChallengeMode() ? 1f : _levelMultiplier; }
        }

        /// <summary>The value bosses spawned now get: the host's on a client, otherwise this level's.</summary>
        public float Effective
        {
            get { return _hostMultiplier > 0f ? _hostMultiplier : _levelMultiplier; }
        }

        public bool FollowingHost
        {
            get { return _hostMultiplier > 0f; }
        }

        public int BossesThisLevel
        {
            get { return _bossesThisLevel.Count; }
        }

        public static float Clamp(float value)
        {
            if (float.IsNaN(value) || value < MinMultiplier)
            {
                return MinMultiplier;
            }

            return value > MaxMultiplier ? MaxMultiplier : value;
        }

        public static bool IsBoss(Unit unit)
        {
            return unit is Npc
                && !unit.isPlayer
                && unit.unitSO != null
                && (unit.unitSO.unitType & UnitType.Boss) != 0;
        }

        /// <summary>A level is about to be generated; its bosses get <paramref name="multiplier"/>.</summary>
        public void BeginLevel(float multiplier)
        {
            _levelMultiplier = Clamp(multiplier);
            _bossesThisLevel.Clear();
        }

        /// <summary>
        /// A co-op client takes the host's value. Bosses already in the level are brought to it
        /// at the health fraction they had.
        /// </summary>
        public void FollowHost(float value)
        {
            float next = Clamp(value);
            if (next == _hostMultiplier)
            {
                return;
            }

            _hostMultiplier = next;
            RescaleLiveBosses(next);
        }

        /// <summary>
        /// Leaving a session: the bosses still standing keep the value they have, and this
        /// machine's own setting takes over from the next level, as the setting says.
        /// </summary>
        public void StopFollowingHost()
        {
            if (_hostMultiplier > 0f)
            {
                _levelMultiplier = _hostMultiplier;
                _hostMultiplier = -1f;
            }
        }

        private void RescaleLiveBosses(float effective)
        {
            for (int i = _bossesThisLevel.Count - 1; i >= 0; i--)
            {
                Npc boss = _bossesThisLevel[i];
                if (boss == null)
                {
                    _bossesThisLevel.RemoveAt(i);
                    continue;
                }

                if (boss.IsAlive)
                {
                    Apply(boss, effective);
                }
            }
        }

        /// <summary>Called after the game gives a unit its starting stats.</summary>
        /// <returns>True when the unit is a boss whose health this call set.</returns>
        public bool OnUnitSpawned(Unit unit)
        {
            if (!IsBoss(unit))
            {
                return false;
            }

            // Endless and Crypt scale their own bosses, and the mod stays out of both. A client
            // goes by the host's value, which already says so.
            if (!FollowingHost && InChallengeMode())
            {
                return false;
            }

            Npc boss = (Npc)unit;
            if (!_bossesThisLevel.Contains(boss))
            {
                _bossesThisLevel.Add(boss);
            }

            return Apply(boss, Effective);
        }

        private static bool InChallengeMode()
        {
            GameManager gm = StaticInstance<GameManager>.Instance;
            return gm != null && (gm.IsEndless || CryptUI.IsRunning);
        }

        /// <summary>
        /// Replaces this mod's modifier on the boss's max health and keeps the health fraction it
        /// had, the same way the game itself raises max health for Endless loops.
        /// </summary>
        private static bool Apply(Npc boss, float multiplier)
        {
            EntityStats stats = boss.Stats;
            CharacterStat maxHealth;
            if (stats == null || !stats.TryGetEntityAttribute(EntityAttributes.Stat_MaxHealth, out maxHealth) || maxHealth == null)
            {
                return false;
            }

            // At 1 with nothing of ours on the boss there is nothing to do, and the default must
            // leave the game exactly as it was.
            if (multiplier <= 1f && !HasOwnModifier(maxHealth))
            {
                return false;
            }

            float oldMax = stats.GetAttribute(EntityAttributes.Stat_MaxHealth);
            // Above 1 when a co-op client already holds the host's current health, which is
            // larger than this copy's max until this call raises it.
            float fraction = oldMax > 0f ? stats.GetStatus(EntityAttributes.Status_CurrentHealth) / oldMax : 1f;
            fraction = Mathf.Clamp01(fraction);

            maxHealth.RemoveAllModifiersFromSource(ModifierSource);
            if (multiplier > 1f)
            {
                maxHealth.AddModifier(new StatModifier(multiplier - 1f, StatModType.PercentMult, ModifierSource));
            }

            float newMax = stats.GetAttribute(EntityAttributes.Stat_MaxHealth);
            stats.SetStatus(EntityAttributes.Status_CurrentHealth, newMax * fraction);
            return true;
        }

        private static bool HasOwnModifier(CharacterStat stat)
        {
            List<StatModifier> modifiers = stat.statModifiers;
            if (modifiers == null)
            {
                return false;
            }

            for (int i = 0; i < modifiers.Count; i++)
            {
                if (modifiers[i].SourceId == ModifierSource)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
