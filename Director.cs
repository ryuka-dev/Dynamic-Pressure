using System;

namespace Ryuka.Sulfur.DynamicPressure
{
    public enum PressureStyle
    {
        Light = 1,
        Heavy = 2,
        Nightmare = 3
    }

    public enum SpawnPosition
    {
        Balanced = 0,
        Ahead = 1,
        Anywhere = 2
    }

    internal enum DirectorPhase
    {
        BuildUp,
        Sustain,
        Fade,
        Relax
    }

    /// <summary>
    /// Everything a pressure style decides. Amplitude lives here and only here: how much extra
    /// threat a level can take, and how harsh a peak has to feel before the director backs off.
    /// </summary>
    internal sealed class PressurePreset
    {
        public float PeakIntensity;
        public float RelaxSeconds;
        public float BuildUpTimeoutSeconds;
        public float TargetThreatPerRoom;
        public float LevelBudgetFraction;
        public int MinimumUnitsPerLevel;
        public float AliveCapPerRoom;
        public float AliveCapFloor;
        public float SurgeIntervalMin;
        public float SurgeIntervalMax;
        public float MutationChance;
        public bool MutationsOnlyLate;
        public bool AllowTanks;
        public bool TanksOnlyLate;

        private static readonly PressurePreset Light = new PressurePreset
        {
            PeakIntensity = 0.5f,
            RelaxSeconds = 45f,
            BuildUpTimeoutSeconds = 40f,
            TargetThreatPerRoom = 1.0f,
            LevelBudgetFraction = 0.25f,
            MinimumUnitsPerLevel = 4,
            AliveCapPerRoom = 0.8f,
            AliveCapFloor = 6f,
            SurgeIntervalMin = 150f,
            SurgeIntervalMax = 210f,
            MutationChance = 0f,
            MutationsOnlyLate = true,
            AllowTanks = false,
            TanksOnlyLate = true
        };

        private static readonly PressurePreset Heavy = new PressurePreset
        {
            PeakIntensity = 0.65f,
            RelaxSeconds = 30f,
            BuildUpTimeoutSeconds = 60f,
            TargetThreatPerRoom = 1.5f,
            LevelBudgetFraction = 0.5f,
            MinimumUnitsPerLevel = 7,
            AliveCapPerRoom = 1.2f,
            AliveCapFloor = 10f,
            SurgeIntervalMin = 100f,
            SurgeIntervalMax = 150f,
            MutationChance = 0.15f,
            MutationsOnlyLate = true,
            AllowTanks = true,
            TanksOnlyLate = true
        };

        private static readonly PressurePreset Nightmare = new PressurePreset
        {
            PeakIntensity = 0.8f,
            RelaxSeconds = 20f,
            BuildUpTimeoutSeconds = 90f,
            TargetThreatPerRoom = 2.2f,
            LevelBudgetFraction = 0.8f,
            MinimumUnitsPerLevel = 11,
            AliveCapPerRoom = 2.0f,
            AliveCapFloor = 14f,
            SurgeIntervalMin = 60f,
            SurgeIntervalMax = 100f,
            MutationChance = 0.3f,
            MutationsOnlyLate = false,
            AllowTanks = true,
            TanksOnlyLate = false
        };

        public static PressurePreset For(PressureStyle style)
        {
            switch (style)
            {
                case PressureStyle.Heavy:
                    return Heavy;
                case PressureStyle.Nightmare:
                    return Nightmare;
                default:
                    return Light;
            }
        }
    }

    /// <summary>
    /// How much threat vanilla put into this level, in the game's own currency (UnitSO.SpawnCost).
    /// Budgets scale from this, so a later environment's larger packs raise them without a table.
    /// </summary>
    internal struct LevelCalibration
    {
        public float PackBudget;
        public float PackDensity;
        public int SpawnableRooms;
        public int RouteRooms;
        public float AverageUnitCost;

        public float LevelThreat
        {
            get
            {
                if (SpawnableRooms <= 0)
                {
                    return 0f;
                }

                return (float)Math.Ceiling(SpawnableRooms * PackDensity) * PackBudget;
            }
        }

        public float RoomThreat
        {
            get { return LevelThreat / Math.Max(1, SpawnableRooms); }
        }
    }

    internal struct DirectorInput
    {
        public float Now;
        public float DeltaSeconds;
        public bool SpawningAllowed;
        public bool Engaged;
        public float PlayerHealthFraction;
        public int RouteRoomIndex;
        public float WeightedThreat;
        public float ModAliveCost;
    }

    internal struct DirectorOrders
    {
        public float TrickleBudget;
        public float SurgeBudget;
        public bool SurgeWarning;
    }

    /// <summary>
    /// The pacing brain. It owns intensity, the phase cycle and the level's threat ledger, and
    /// knows nothing about Unity: the plugin feeds it a snapshot and acts on the orders it returns.
    /// </summary>
    /// <remarks>
    /// The mod can only add enemies, so a relax phase means no longer adding; whatever is already
    /// on the map stays. A player who keeps handling the pressure without ever reaching a peak
    /// shortens the rests and the gaps between surges. That changes how often things happen, never
    /// how much each one is worth, which stays the job of the pressure style.
    /// </remarks>
    internal sealed class Director
    {
        private const float MinimumHitIntensity = 0.04f;
        private const float DecayPerSecond = 0.05f;
        private const float DecayHoldAfterHitSeconds = 15f;
        private const float CriticalHealthFraction = 0.25f;
        private const float SustainSeconds = 4f;
        private const float FadeExitIntensity = 0.25f;
        private const float FadeMaxSeconds = 30f;
        private const int RelaxEndsAfterRooms = 2;
        private const float TrickleIntervalMin = 3f;
        private const float TrickleIntervalMax = 6f;
        private const float SurgeWarningSeconds = 2.5f;
        private const float PaceStepPerTimeout = 0.15f;
        private const float MinimumPace = 0.5f;
        private const float InitialUnlockedFraction = 0.25f;

        private readonly Func<float, float, float> _randomRange;

        private PressurePreset _preset;
        private LevelCalibration _level;
        private float _phaseElapsed;
        private float _secondsSinceHit;
        private float _nextTrickleAt;
        private float _nextSurgeAt;
        private float _surgeFireAt;
        private bool _surgeWarned;
        private float _surgeReserved;
        private bool _sawEngagementThisBuildUp;
        private bool _hasEngaged;
        private int _timeoutsWithoutPeak;
        private int _relaxStartRoom;
        private int _highestRoute;

        public Director(Func<float, float, float> randomRange)
        {
            if (randomRange == null)
            {
                throw new ArgumentNullException(nameof(randomRange));
            }

            _randomRange = randomRange;
            StartLevel(new LevelCalibration(), PressurePreset.For(PressureStyle.Light));
        }

        public DirectorPhase Phase { get; private set; }

        public float Intensity { get; private set; }

        public float SpentThreat { get; private set; }

        public float TotalBudget { get; private set; }

        public bool HasEngaged
        {
            get { return _hasEngaged; }
        }

        public float Pace
        {
            get { return Math.Max(MinimumPace, 1f - PaceStepPerTimeout * _timeoutsWithoutPeak); }
        }

        public float TargetThreat
        {
            get { return _level.RoomThreat * _preset.TargetThreatPerRoom; }
        }

        public float AliveCap
        {
            get { return Math.Max(_preset.AliveCapFloor, _level.RoomThreat * _preset.AliveCapPerRoom); }
        }

        /// <summary>
        /// How much of the level's budget the player has opened up so far. It follows the route so
        /// a budget cannot be spent in the first room and leave the rest of the level empty.
        /// </summary>
        public float UnlockedBudget
        {
            get
            {
                float progress = _level.RouteRooms <= 1
                    ? 1f
                    : Clamp01((float)_highestRoute / (_level.RouteRooms - 1));
                return TotalBudget * (InitialUnlockedFraction + (1f - InitialUnlockedFraction) * progress);
            }
        }

        public float RemainingBudget
        {
            get { return Math.Max(0f, UnlockedBudget - SpentThreat); }
        }

        public void StartLevel(LevelCalibration level, PressurePreset preset)
        {
            if (preset == null)
            {
                throw new ArgumentNullException(nameof(preset));
            }

            level.PackBudget = NonNegative(level.PackBudget);
            level.PackDensity = NonNegative(level.PackDensity);
            level.AverageUnitCost = NonNegative(level.AverageUnitCost);

            _level = level;
            _preset = preset;
            TotalBudget = Math.Max(
                preset.MinimumUnitsPerLevel * level.AverageUnitCost,
                level.LevelThreat * preset.LevelBudgetFraction);

            Phase = DirectorPhase.BuildUp;
            Intensity = 0f;
            SpentThreat = 0f;
            _phaseElapsed = 0f;
            _secondsSinceHit = float.MaxValue;
            _nextTrickleAt = 0f;
            _nextSurgeAt = float.MaxValue;
            _surgeFireAt = 0f;
            _surgeWarned = false;
            _surgeReserved = 0f;
            _sawEngagementThisBuildUp = false;
            _hasEngaged = false;
            _timeoutsWithoutPeak = 0;
            _relaxStartRoom = 0;
            _highestRoute = 0;
        }

        /// <summary>Reports health the player actually lost, as a fraction of maximum health.</summary>
        /// <param name="lostHealthFraction">Health lost, from 0 to 1.</param>
        /// <param name="periodic">
        /// True for burn or poison ticks. Those get no minimum, or a small dot would count as a
        /// string of solid hits.
        /// </param>
        public void ReportPlayerHit(float lostHealthFraction, bool periodic = false)
        {
            if (lostHealthFraction <= 0f || float.IsNaN(lostHealthFraction))
            {
                return;
            }

            float gain = periodic ? lostHealthFraction : Math.Max(MinimumHitIntensity, lostHealthFraction);
            Intensity = Clamp01(Intensity + gain);
            _secondsSinceHit = 0f;
        }

        /// <summary>Applies a new style to the level in progress without refunding what was spent.</summary>
        public void ChangeStyle(PressurePreset preset)
        {
            if (preset == null)
            {
                throw new ArgumentNullException(nameof(preset));
            }

            _preset = preset;
            TotalBudget = Math.Max(
                preset.MinimumUnitsPerLevel * _level.AverageUnitCost,
                _level.LevelThreat * preset.LevelBudgetFraction);
        }

        /// <summary>Commits the cost of a unit that was actually placed.</summary>
        public void ReportSpent(float cost)
        {
            if (cost > 0f)
            {
                SpentThreat += cost;
            }
        }

        public DirectorOrders Tick(DirectorInput input)
        {
            DirectorOrders orders = new DirectorOrders();
            float delta = NonNegative(input.DeltaSeconds);

            UpdateIntensity(input, delta);
            _highestRoute = Math.Max(_highestRoute, input.RouteRoomIndex);

            if (!input.SpawningAllowed)
            {
                // A warning only means something if the surge follows it shortly. One held up
                // here is withdrawn and warned about again once spawning can resume.
                _surgeWarned = false;
                _surgeReserved = 0f;
                return orders;
            }

            AdvancePhase(input, delta);

            if (input.Engaged)
            {
                _sawEngagementThisBuildUp = true;

                if (!_hasEngaged)
                {
                    _hasEngaged = true;
                    ScheduleNextSurge(input.Now);
                }
            }

            if (!_hasEngaged)
            {
                return orders;
            }

            IssueSurge(input, ref orders);

            if (orders.SurgeBudget <= 0f)
            {
                IssueTrickle(input, ref orders);
            }

            return orders;
        }

        private void UpdateIntensity(DirectorInput input, float delta)
        {
            if (_secondsSinceHit < float.MaxValue)
            {
                _secondsSinceHit += delta;
            }

            if (input.PlayerHealthFraction <= CriticalHealthFraction)
            {
                Intensity = 1f;
                return;
            }

            if (!input.Engaged || _secondsSinceHit > DecayHoldAfterHitSeconds)
            {
                Intensity = Clamp01(Intensity - DecayPerSecond * delta);
            }
        }

        private void AdvancePhase(DirectorInput input, float delta)
        {
            // A build-up is timed from the first fight in it: exploring an empty level is not the
            // player handling pressure, so it must neither end the build-up nor speed up the pace.
            if (Phase != DirectorPhase.BuildUp || _sawEngagementThisBuildUp)
            {
                _phaseElapsed += delta;
            }

            switch (Phase)
            {
                case DirectorPhase.BuildUp:
                    if (Intensity >= _preset.PeakIntensity)
                    {
                        _timeoutsWithoutPeak = 0;
                        Enter(DirectorPhase.Sustain);
                    }
                    else if (_phaseElapsed >= _preset.BuildUpTimeoutSeconds)
                    {
                        _timeoutsWithoutPeak++;
                        Enter(DirectorPhase.Sustain);
                    }

                    break;

                case DirectorPhase.Sustain:
                    if (Intensity >= _preset.PeakIntensity)
                    {
                        _timeoutsWithoutPeak = 0;
                    }

                    if (_phaseElapsed >= SustainSeconds)
                    {
                        Enter(DirectorPhase.Fade);
                    }

                    break;

                case DirectorPhase.Fade:
                    if (Intensity < FadeExitIntensity || _phaseElapsed >= FadeMaxSeconds)
                    {
                        _relaxStartRoom = _highestRoute;
                        Enter(DirectorPhase.Relax);
                    }

                    break;

                case DirectorPhase.Relax:
                    if (_phaseElapsed >= _preset.RelaxSeconds * Pace
                        || _highestRoute >= _relaxStartRoom + RelaxEndsAfterRooms)
                    {
                        Enter(DirectorPhase.BuildUp);
                    }

                    break;
            }
        }

        private void Enter(DirectorPhase phase)
        {
            Phase = phase;
            _phaseElapsed = 0f;

            if (phase == DirectorPhase.BuildUp)
            {
                _sawEngagementThisBuildUp = false;
            }
        }

        /// <summary>Budget not yet spent or promised to a surge the player has been warned about.</summary>
        private float AvailableBudget
        {
            get { return Math.Max(0f, RemainingBudget - _surgeReserved); }
        }

        private bool IsAddingPhase
        {
            get { return Phase == DirectorPhase.BuildUp || Phase == DirectorPhase.Sustain; }
        }

        private void IssueTrickle(DirectorInput input, ref DirectorOrders orders)
        {
            if (!IsAddingPhase || input.Now < _nextTrickleAt)
            {
                return;
            }

            _nextTrickleAt = input.Now + _randomRange(TrickleIntervalMin, TrickleIntervalMax);

            if (input.ModAliveCost >= AliveCap)
            {
                return;
            }

            float deficit = TargetThreat - input.WeightedThreat;
            if (deficit > 0f)
            {
                orders.TrickleBudget = Math.Min(deficit, AvailableBudget);
            }
        }

        private void IssueSurge(DirectorInput input, ref DirectorOrders orders)
        {
            if (_surgeWarned)
            {
                // A warned surge always arrives: the player was told it is coming.
                if (input.Now >= _surgeFireAt)
                {
                    _surgeWarned = false;
                    orders.SurgeBudget = Math.Min(_surgeReserved, RemainingBudget);
                    _surgeReserved = 0f;
                    ScheduleNextSurge(input.Now);
                }

                return;
            }

            float surgeBudget = Math.Min(_level.PackBudget, AvailableBudget);

            if (Phase != DirectorPhase.BuildUp
                || input.Now < _nextSurgeAt
                || surgeBudget <= 0f
                || input.ModAliveCost >= AliveCap)
            {
                return;
            }

            _surgeWarned = true;
            _surgeReserved = surgeBudget;
            _surgeFireAt = input.Now + SurgeWarningSeconds;
            orders.SurgeWarning = true;
        }

        private void ScheduleNextSurge(float now)
        {
            _nextSurgeAt = now + _randomRange(_preset.SurgeIntervalMin, _preset.SurgeIntervalMax) * Pace;
        }

        private static float NonNegative(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) || value < 0f ? 0f : value;
        }

        private static float Clamp01(float value)
        {
            if (float.IsNaN(value) || value < 0f)
            {
                return 0f;
            }

            return value > 1f ? 1f : value;
        }
    }
}
