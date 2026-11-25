using System.Globalization;

namespace Laufen.Domain;

public sealed record SimulatedWellnessSpec
{
    public required int DayOffset { get; init; }
    public int RestingHr { get; init; } = 47;
    public double Hrv { get; init; } = 74.0;
    public double SleepHours { get; init; } = 7.8;
    public double SleepScore { get; init; } = 82.0;
    public double? InjuryScore { get; init; }
    public string? Comments { get; init; }
}

public sealed record SimulatedRunSpec
{
    public required int DayOffset { get; init; }
    public required string Name { get; init; }
    public required int DurationSeconds { get; init; }
    public required double DistanceMeters { get; init; }
    public required int AverageHeartRate { get; init; }
    public required int MaxHeartRate { get; init; }
    public required int[] HrZoneTimesSeconds { get; init; }
    public string? Note { get; init; }
    public double? CadenceSpm { get; init; }
    public IReadOnlyList<string>? IntervalSummary { get; init; }
}

public sealed record SimulatedCalendarSeed
{
    public required long EventId { get; init; }
    public required int DayOffset { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Ownership { get; init; }
    public required int DurationSeconds { get; init; }
    public required double EstimatedLoad { get; init; }
}

public sealed record SimulatedWeatherDaySpec
{
    public required int DayOffset { get; init; }
    public required double TempMinC { get; init; }
    public required double TempMaxC { get; init; }
    public required double PrecipitationMm { get; init; }
    public required double MaxWindGustKmh { get; init; }
    public required string Summary { get; init; }
}

public sealed record SimulatedTriggerStep
{
    public required int DayOffset { get; init; }
    public required string Trigger { get; init; }
    public required TimeOnly TimeOfDay { get; init; }
    public string? AthleteMessage { get; init; }
    public SimulatedWellnessSpec? WellnessBeforeStep { get; init; }
    public SimulatedRunSpec? RunBeforeStep { get; init; }
    public IReadOnlyList<SimulatedWeatherDaySpec>? WeatherBeforeStep { get; init; }
}

public sealed record SimulationScenario
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required DateOnly StartDate { get; init; }
    public string? InitialHealthState { get; init; }
    public IReadOnlyList<SimulatedWellnessSpec> InitialWellness { get; init; } = [];
    public IReadOnlyList<SimulatedRunSpec> InitialRuns { get; init; } = [];
    public IReadOnlyList<SimulatedCalendarSeed> InitialCalendar { get; init; } = [];
    public IReadOnlyList<SimulatedWeatherDaySpec>? InitialWeather { get; init; }
    public required IReadOnlyList<SimulatedTriggerStep> Steps { get; init; }
}

public sealed record SimulationAuditFinding(
    string AuditCode,
    string Severity,
    int DayOffset,
    DateOnly Date,
    string Trigger,
    string Message);

public sealed record SimulationStepReport
{
    public required int DayOffset { get; init; }
    public required DateOnly Date { get; init; }
    public required string Trigger { get; init; }
    public string? AthleteMessage { get; init; }
    public string? InjectedRunName { get; init; }
    public string? HealthStateBefore { get; init; }
    public string? HealthStateAfter { get; init; }
    public required CoachPlanningOutcome Outcome { get; init; }
    public required int ScheduledWatchCount { get; init; }
    public required int ScheduledPreviewCount { get; init; }
    public required IReadOnlyList<long> ActiveAutoEventIds { get; init; }
    public required IReadOnlyList<DateOnly> UserLockedDates { get; init; }
    public required IReadOnlyList<SimulationAuditFinding> AuditFindings { get; init; }
}

public sealed record SimulationRunResult
{
    public required string ScenarioName { get; init; }
    public required string Description { get; init; }
    public required string AthleteSlug { get; init; }
    public required string Provider { get; init; }
    public required DateOnly StartDate { get; init; }
    public required int TotalSteps { get; init; }
    public required bool Passed { get; init; }
    public required int TotalAttempts { get; init; }
    public required double TotalElapsedSeconds { get; init; }
    public required IReadOnlyList<SimulationStepReport> Steps { get; init; }
    public required IReadOnlyList<SimulationAuditFinding> AllFindings { get; init; }
    public required IReadOnlyDictionary<string, int> AuditSummaryByCode { get; init; }
}

/// <summary>
/// Built-in catalog of multi-day self-healing test scenarios aligned with DOTNET_AGENTIC_COACH_DESIGN §7.
/// </summary>
public static class AutopilotScenarioCatalog
{
    public const string MissedWorkdaysAutoRollover = "missed_workdays_auto_rollover";
    public const string SpontaneousOnWatchWorkoutSwap = "spontaneous_on_watch_workout_swap";
    public const string IllnessSickholdAndReentry = "illness_sickhold_and_reentry";
    public const string StormIntervalSwap = "storm_interval_swap";
    public const string TelegramUserLockedOverride = "telegram_user_locked_override";

    private static readonly DateOnly DefaultStartDate = new(2026, 12, 1);

    private static readonly string[] ScenarioNames =
    [
        MissedWorkdaysAutoRollover,
        SpontaneousOnWatchWorkoutSwap,
        IllnessSickholdAndReentry,
        StormIntervalSwap,
        TelegramUserLockedOverride
    ];

    public static IReadOnlyList<string> GetAllNames() => ScenarioNames;

    public static SimulationScenario GetScenario(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return name.Trim().ToLowerInvariant() switch
        {
            MissedWorkdaysAutoRollover => BuildMissedWorkdaysScenario(),
            SpontaneousOnWatchWorkoutSwap => BuildSpontaneousSwapScenario(),
            IllnessSickholdAndReentry => BuildIllnessSickholdScenario(),
            StormIntervalSwap => BuildStormIntervalSwapScenario(),
            TelegramUserLockedOverride => BuildTelegramUserLockedOverrideScenario(),
            _ => throw new ArgumentException(
                $"Unknown simulation scenario '{name}'. Expected one of: {string.Join(", ", ScenarioNames)}.",
                nameof(name))
        };
    }

    private static List<SimulatedWellnessSpec> BuildBaselineWellness(int countDays = 14)
    {
        var list = new List<SimulatedWellnessSpec>(countDays);
        for (var offset = -countDays; offset <= -1; offset++)
        {
            list.Add(new SimulatedWellnessSpec
            {
                DayOffset = offset,
                RestingHr = 47 + (Math.Abs(offset) % 2),
                Hrv = 73.0 + (Math.Abs(offset) % 3),
                SleepHours = 7.75,
                SleepScore = 82.0
            });
        }

        return list;
    }

    private static List<SimulatedRunSpec> BuildBaselineRuns() =>
    [
        new()
        {
            DayOffset = -14,
            Name = "Easy Base 45m",
            DurationSeconds = 2700,
            DistanceMeters = 6300,
            AverageHeartRate = 152,
            MaxHeartRate = 158,
            HrZoneTimesSeconds = [600, 2100, 0, 0, 0]
        },
        new()
        {
            DayOffset = -12,
            Name = "Kontrollierte Schwellenintervalle (4x 5m Z4)",
            DurationSeconds = 2880,
            DistanceMeters = 7400,
            AverageHeartRate = 161,
            MaxHeartRate = 175,
            HrZoneTimesSeconds = [900, 720, 120, 1140, 0],
            IntervalSummary = ["4x 5m 171bpm"]
        },
        new()
        {
            DayOffset = -9,
            Name = "Aerober Long Run (55m)",
            DurationSeconds = 3300,
            DistanceMeters = 7800,
            AverageHeartRate = 153,
            MaxHeartRate = 159,
            HrZoneTimesSeconds = [700, 2550, 50, 0, 0]
        },
        new()
        {
            DayOffset = -7,
            Name = "Aerobe Basis (45m)",
            DurationSeconds = 2700,
            DistanceMeters = 6400,
            AverageHeartRate = 152,
            MaxHeartRate = 158,
            HrZoneTimesSeconds = [600, 2080, 20, 0, 0]
        },
        new()
        {
            DayOffset = -5,
            Name = "Kontrollierte Schwellenintervalle (4x 5m Z4)",
            DurationSeconds = 2880,
            DistanceMeters = 7500,
            AverageHeartRate = 161,
            MaxHeartRate = 174,
            HrZoneTimesSeconds = [880, 700, 120, 1180, 0],
            IntervalSummary = ["4x 5m 172bpm"]
        },
        new()
        {
            DayOffset = -2,
            Name = "Aerober Grundlagenlauf (60m)",
            DurationSeconds = 3600,
            DistanceMeters = 8600,
            AverageHeartRate = 154,
            MaxHeartRate = 159,
            HrZoneTimesSeconds = [700, 2820, 80, 0, 0]
        }
    ];

    private static SimulationScenario BuildMissedWorkdaysScenario() => new()
    {
        Name = MissedWorkdaysAutoRollover,
        Description = "Two consecutive missed mid-week workouts force automatic 14-day rolling re-spacing without consecutive-day cramming.",
        StartDate = DefaultStartDate,
        InitialHealthState = "Normal",
        InitialWellness = BuildBaselineWellness(),
        InitialRuns = BuildBaselineRuns(),
        Steps =
        [
            new()
            {
                DayOffset = 0,
                Trigger = "morning_sync",
                TimeOfDay = new TimeOnly(6, 0),
                WellnessBeforeStep = new() { DayOffset = 0, RestingHr = 47, Hrv = 75.0, SleepHours = 7.9, SleepScore = 84.0 }
            },
            new()
            {
                DayOffset = 1,
                Trigger = "morning_sync",
                TimeOfDay = new TimeOnly(6, 0),
                WellnessBeforeStep = new() { DayOffset = 1, RestingHr = 47, Hrv = 76.0, SleepHours = 8.0, SleepScore = 85.0 }
            },
            new()
            {
                DayOffset = 2,
                Trigger = "morning_sync",
                TimeOfDay = new TimeOnly(6, 0),
                WellnessBeforeStep = new() { DayOffset = 2, RestingHr = 46, Hrv = 77.0, SleepHours = 7.8, SleepScore = 83.0 }
            },
            new()
            {
                DayOffset = 2,
                Trigger = "post_run",
                TimeOfDay = new TimeOnly(18, 0),
                RunBeforeStep = new()
                {
                    DayOffset = 2,
                    Name = "Feierabend Easy Base 45m",
                    DurationSeconds = 2700,
                    DistanceMeters = 6400,
                    AverageHeartRate = 152,
                    MaxHeartRate = 158,
                    HrZoneTimesSeconds = [600, 2080, 20, 0, 0]
                }
            }
        ]
    };

    private static SimulationScenario BuildSpontaneousSwapScenario() => new()
    {
        Name = SpontaneousOnWatchWorkoutSwap,
        Description = "Athlete spontaneously picks a 60m Long Run from the watch on Day 0; post_run rebalances future week and intraday_check freezes Day 2.",
        StartDate = DefaultStartDate,
        InitialHealthState = "Normal",
        InitialWellness = BuildBaselineWellness(),
        InitialRuns = BuildBaselineRuns(),
        Steps =
        [
            new()
            {
                DayOffset = 0,
                Trigger = "morning_sync",
                TimeOfDay = new TimeOnly(6, 0),
                WellnessBeforeStep = new() { DayOffset = 0, RestingHr = 47, Hrv = 75.0, SleepHours = 8.0, SleepScore = 85.0 }
            },
            new()
            {
                DayOffset = 0,
                Trigger = "post_run",
                TimeOfDay = new TimeOnly(17, 30),
                RunBeforeStep = new()
                {
                    DayOffset = 0,
                    Name = "Spontaneous Watch Long Run 60m",
                    DurationSeconds = 3600,
                    DistanceMeters = 8700,
                    AverageHeartRate = 153,
                    MaxHeartRate = 159,
                    HrZoneTimesSeconds = [660, 2860, 80, 0, 0],
                    Note = "Picked 60m Long Run spontaneously on watch"
                }
            },
            new()
            {
                DayOffset = 2,
                Trigger = "intraday_check",
                TimeOfDay = new TimeOnly(14, 0),
                WellnessBeforeStep = new() { DayOffset = 2, RestingHr = 47, Hrv = 74.0, SleepHours = 7.8, SleepScore = 82.0 }
            }
        ]
    };

    private static SimulationScenario BuildIllnessSickholdScenario() => new()
    {
        Name = IllnessSickholdAndReentry,
        Description = "Acute illness triggers SickHold clearing all open auto events, transitions strictly through ReEntry, and gates quality behind 2 clean Z2 runs.",
        StartDate = DefaultStartDate,
        InitialHealthState = "Normal",
        InitialWellness = BuildBaselineWellness(),
        InitialRuns = BuildBaselineRuns(),
        InitialCalendar =
        [
            new()
            {
                EventId = 800001,
                DayOffset = 1,
                Name = "Kontrollierter Schwellenlauf (4x 5m Z4)",
                Description = "Warmup\n- 10m Z1-Z2 HR\n\n4x\n- 5m Z4 HR\n- 2m Recovery Z1 HR\n\nCooldown\n- 10m Z1 HR\n",
                Ownership = "auto",
                DurationSeconds = 2880,
                EstimatedLoad = 48.0
            },
            new()
            {
                EventId = 800002,
                DayOffset = 3,
                Name = "Aerobe Basis & Z2 Dauerlauf (45m)",
                Description = "Warmup\n- 10m Z1 HR\n\nMain Set\n- 30m Z2 HR\n\nCooldown\n- 5m Z1 HR\n",
                Ownership = "auto",
                DurationSeconds = 2700,
                EstimatedLoad = 40.0
            },
            new()
            {
                EventId = 800003,
                DayOffset = 5,
                Name = "Aerober Long Run (60m Z2)",
                Description = "Warmup\n- 10m Z1 HR\n\nMain Set\n- 45m Z2 HR\n\nCooldown\n- 5m Z1 HR\n",
                Ownership = "auto",
                DurationSeconds = 3600,
                EstimatedLoad = 54.0
            }
        ],
        Steps =
        [
            new()
            {
                DayOffset = 1,
                Trigger = "telegram_override",
                TimeOfDay = new TimeOnly(8, 0),
                AthleteMessage = "Bin leider erkältet mit Halsschmerzen und leichtem Fieber.",
                WellnessBeforeStep = new()
                {
                    DayOffset = 1,
                    RestingHr = 54,
                    Hrv = 52.0,
                    SleepHours = 6.5,
                    SleepScore = 62.0,
                    Comments = "Erkältung und Halsschmerzen"
                }
            },
            new()
            {
                DayOffset = 3,
                Trigger = "morning_sync",
                TimeOfDay = new TimeOnly(6, 0),
                WellnessBeforeStep = new()
                {
                    DayOffset = 3,
                    RestingHr = 54,
                    Hrv = 55.0,
                    SleepHours = 7.2,
                    SleepScore = 68.0
                }
            },
            new()
            {
                DayOffset = 5,
                Trigger = "telegram_override",
                TimeOfDay = new TimeOnly(9, 0),
                AthleteMessage = "Fieber ist weg, bin wieder fit für einen lockeren Neustart.",
                WellnessBeforeStep = new()
                {
                    DayOffset = 5,
                    RestingHr = 47,
                    Hrv = 74.0,
                    SleepHours = 8.1,
                    SleepScore = 85.0
                }
            },
            new()
            {
                DayOffset = 6,
                Trigger = "post_run",
                TimeOfDay = new TimeOnly(17, 30),
                WellnessBeforeStep = new()
                {
                    DayOffset = 6,
                    RestingHr = 47,
                    Hrv = 75.0,
                    SleepHours = 8.0,
                    SleepScore = 84.0
                },
                RunBeforeStep = new()
                {
                    DayOffset = 6,
                    Name = "Re-Entry Locker Z2 (35m)",
                    DurationSeconds = 2100,
                    DistanceMeters = 4800,
                    AverageHeartRate = 148,
                    MaxHeartRate = 156,
                    HrZoneTimesSeconds = [900, 1200, 0, 0, 0]
                }
            },
            new()
            {
                DayOffset = 8,
                Trigger = "post_run",
                TimeOfDay = new TimeOnly(17, 30),
                WellnessBeforeStep = new()
                {
                    DayOffset = 8,
                    RestingHr = 47,
                    Hrv = 76.0,
                    SleepHours = 8.0,
                    SleepScore = 86.0
                },
                RunBeforeStep = new()
                {
                    DayOffset = 8,
                    Name = "Zweiter Re-Entry Basis-Lauf (45m)",
                    DurationSeconds = 2700,
                    DistanceMeters = 6350,
                    AverageHeartRate = 151,
                    MaxHeartRate = 158,
                    HrZoneTimesSeconds = [600, 2046, 54, 0, 0]
                }
            }
        ]
    };

    private static SimulationScenario BuildStormIntervalSwapScenario() => new()
    {
        Name = StormIntervalSwap,
        Description = "Storm gusts and heavy rain on Day 0 prevent pace_range intervals on storm day and shift quality to calm Day 2.",
        StartDate = DefaultStartDate,
        InitialHealthState = "Normal",
        InitialWellness = BuildBaselineWellness(),
        InitialRuns = BuildBaselineRuns(),
        InitialWeather =
        [
            new() { DayOffset = 0, TempMinC = 3, TempMaxC = 7, PrecipitationMm = 18.5, MaxWindGustKmh = 64.0, Summary = "Storm & Heavy Rain" },
            new() { DayOffset = 1, TempMinC = 4, TempMaxC = 9, PrecipitationMm = 3.0, MaxWindGustKmh = 26.0, Summary = "Cloudy" },
            new() { DayOffset = 2, TempMinC = 5, TempMaxC = 12, PrecipitationMm = 0.0, MaxWindGustKmh = 14.0, Summary = "Clear & Calm" },
            new() { DayOffset = 3, TempMinC = 4, TempMaxC = 11, PrecipitationMm = 0.5, MaxWindGustKmh = 16.0, Summary = "Partly Cloudy" },
            new() { DayOffset = 4, TempMinC = 5, TempMaxC = 12, PrecipitationMm = 0.0, MaxWindGustKmh = 15.0, Summary = "Clear" }
        ],
        Steps =
        [
            new()
            {
                DayOffset = 0,
                Trigger = "morning_sync",
                TimeOfDay = new TimeOnly(6, 0),
                WellnessBeforeStep = new() { DayOffset = 0, RestingHr = 47, Hrv = 75.0, SleepHours = 7.9, SleepScore = 84.0 }
            },
            new()
            {
                DayOffset = 2,
                Trigger = "morning_sync",
                TimeOfDay = new TimeOnly(6, 0),
                WellnessBeforeStep = new() { DayOffset = 2, RestingHr = 47, Hrv = 76.0, SleepHours = 8.0, SleepScore = 85.0 },
                WeatherBeforeStep =
                [
                    new() { DayOffset = 2, TempMinC = 5, TempMaxC = 12, PrecipitationMm = 0.0, MaxWindGustKmh = 14.0, Summary = "Clear & Calm" },
                    new() { DayOffset = 3, TempMinC = 4, TempMaxC = 11, PrecipitationMm = 0.5, MaxWindGustKmh = 16.0, Summary = "Partly Cloudy" },
                    new() { DayOffset = 4, TempMinC = 5, TempMaxC = 12, PrecipitationMm = 0.0, MaxWindGustKmh = 15.0, Summary = "Clear" },
                    new() { DayOffset = 5, TempMinC = 4, TempMaxC = 10, PrecipitationMm = 0.0, MaxWindGustKmh = 18.0, Summary = "Clear" },
                    new() { DayOffset = 6, TempMinC = 5, TempMaxC = 11, PrecipitationMm = 0.0, MaxWindGustKmh = 15.0, Summary = "Clear" }
                ]
            }
        ]
    };

    private static SimulationScenario BuildTelegramUserLockedOverrideScenario() => new()
    {
        Name = TelegramUserLockedOverride,
        Description = "user_locked workouts remain immutable during morning_sync and intraday_check, and are deleted cleanly only when telegram_override requests rest.",
        StartDate = DefaultStartDate,
        InitialHealthState = "Normal",
        InitialWellness = BuildBaselineWellness(),
        InitialRuns = BuildBaselineRuns(),
        InitialCalendar =
        [
            new()
            {
                EventId = 810001,
                DayOffset = 0,
                Name = "User Locked Easy 45m",
                Description = "Warmup\n- 10m Z1 HR\n\nMain Set\n- 30m Z2 HR\n\nCooldown\n- 5m Z1 HR\n",
                Ownership = "user_locked",
                DurationSeconds = 2700,
                EstimatedLoad = 40.0
            },
            new()
            {
                EventId = 810002,
                DayOffset = 2,
                Name = "User Locked Speed 5x2m",
                Description = "Warmup\n- 10m Z1-Z2 HR\n\n5x\n- 2m Z4 HR\n- 2m Recovery Z1 HR\n\nCooldown\n- 10m Z1 HR\n",
                Ownership = "user_locked",
                DurationSeconds = 2400,
                EstimatedLoad = 42.0
            },
            new()
            {
                EventId = 810003,
                DayOffset = 5,
                Name = "Aerober Long Run (60m Z2)",
                Description = "Warmup\n- 10m Z1 HR\n\nMain Set\n- 45m Z2 HR\n\nCooldown\n- 5m Z1 HR\n",
                Ownership = "auto",
                DurationSeconds = 3600,
                EstimatedLoad = 54.0
            }
        ],
        Steps =
        [
            new()
            {
                DayOffset = 0,
                Trigger = "morning_sync",
                TimeOfDay = new TimeOnly(6, 0),
                WellnessBeforeStep = new() { DayOffset = 0, RestingHr = 47, Hrv = 75.0, SleepHours = 7.9, SleepScore = 84.0 }
            },
            new()
            {
                DayOffset = 0,
                Trigger = "intraday_check",
                TimeOfDay = new TimeOnly(14, 0)
            },
            new()
            {
                DayOffset = 1,
                Trigger = "morning_sync",
                TimeOfDay = new TimeOnly(6, 0),
                WellnessBeforeStep = new() { DayOffset = 1, RestingHr = 47, Hrv = 75.0, SleepHours = 7.9, SleepScore = 84.0 },
                RunBeforeStep = new()
                {
                    DayOffset = 0,
                    Name = "User Locked Easy 45m",
                    DurationSeconds = 2700,
                    DistanceMeters = 6400,
                    AverageHeartRate = 152,
                    MaxHeartRate = 158,
                    HrZoneTimesSeconds = [600, 2080, 20, 0, 0]
                }
            },
            new()
            {
                DayOffset = 2,
                Trigger = "telegram_override",
                TimeOfDay = new TimeOnly(8, 30),
                AthleteMessage = "Beine sind heute ziemlich schwer, bitte das heutige Training streichen und einen Ruhetag einplanen.",
                WellnessBeforeStep = new() { DayOffset = 2, RestingHr = 48, Hrv = 71.0, SleepHours = 7.2, SleepScore = 76.0 }
            }
        ]
    };
}

/// <summary>
/// Isolated, time-stepped domain simulator running <see cref="ICoachPlanningStrategy"/> across multi-day disruptions.
/// Never touches production files or system wall clocks.
/// </summary>
public static class AutopilotSimulator
{
    private const long SyntheticEventIdSeed = 900_000_000L;

    public static async Task<SimulationRunResult> RunAsync(
        SimulationScenario scenario,
        AthleteProfile profile,
        string athleteSlug,
        string providerName,
        ICoachPlanningStrategy strategy,
        int maxValidationRetries = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(athleteSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(strategy);

        var simProfile = profile with
        {
            AllowConsecutiveRunDays = false,
            PreferredRunDays = ["tuesday", "thursday", "sunday"]
        };

        var startDate = scenario.StartDate;
        var activities = new List<IntervalsActivityEntry>();
        var wellnessByDate = new Dictionary<DateOnly, IntervalsWellnessEntry>();
        var calendar = new List<CalendarEventEntry>();
        var autoEventIds = new HashSet<long>();
        var userLockedDates = new HashSet<DateOnly>();

        var ctl = 18.0;
        var atl = 16.0;
        var actSeq = 1;
        var nextEventId = SyntheticEventIdSeed;

        foreach (var w in scenario.InitialWellness)
        {
            var date = startDate.AddDays(w.DayOffset);
            wellnessByDate[date] = BuildWellnessEntry(date, w, ctl, atl);
        }

        foreach (var r in scenario.InitialRuns.OrderBy(x => x.DayOffset))
        {
            var date = startDate.AddDays(r.DayOffset);
            var (entry, newCtl, newAtl) = BuildActivityEntry(simProfile, $"sim-act-{actSeq++:D4}", date, r, pairedEventId: null, ctl, atl);
            ctl = newCtl;
            atl = newAtl;
            activities.Add(entry);
            UpdateWellnessLoads(wellnessByDate, date, ctl, atl);
        }

        foreach (var seed in scenario.InitialCalendar)
        {
            var date = startDate.AddDays(seed.DayOffset);
            calendar.Add(BuildCalendarEntry(simProfile.Id, seed.EventId, date, seed.Name, seed.Description, seed.DurationSeconds, seed.EstimatedLoad));
            if (string.Equals(seed.Ownership, "auto", StringComparison.Ordinal))
            {
                autoEventIds.Add(seed.EventId);
            }
            else if (string.Equals(seed.Ownership, "user_locked", StringComparison.Ordinal))
            {
                userLockedDates.Add(date);
            }
        }

        WeatherForecastDossier? activeWeather = scenario.InitialWeather is not null
            ? BuildWeatherDossier(startDate, scenario.InitialWeather)
            : null;

        BlockCompassSnapshot? currentCompass = string.IsNullOrWhiteSpace(scenario.InitialHealthState)
            ? null
            : new BlockCompassSnapshot
            {
                UpdatedAt = startDate.AddDays(-1),
                CurrentBlockFocus = "Balanced base & threshold progression",
                BaseMilestone = "60m Z2 Long Run",
                QualityMilestone = "4x 5m Z4 Threshold",
                ObservedAthletePatterns = ["Alternating run days"],
                LastDecisionSummary = "Seeded initial normal block state",
                HealthState = scenario.InitialHealthState,
                AutoEventIds = [.. autoEventIds]
            };

        var stepReports = new List<SimulationStepReport>(scenario.Steps.Count);
        var allFindings = new List<SimulationAuditFinding>();
        var totalAttempts = 0;
        var totalElapsed = 0.0;

        foreach (var step in scenario.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var refDate = startDate.AddDays(step.DayOffset);

            if (step.WellnessBeforeStep is { } wSpec)
            {
                var wDate = startDate.AddDays(wSpec.DayOffset);
                wellnessByDate[wDate] = BuildWellnessEntry(wDate, wSpec, ctl, atl);
            }

            if (step.WeatherBeforeStep is not null)
            {
                activeWeather = BuildWeatherDossier(startDate, step.WeatherBeforeStep);
            }

            string? injectedRunName = null;
            if (step.RunBeforeStep is { } runSpec)
            {
                var runDate = startDate.AddDays(runSpec.DayOffset);
                var pairedEvt = calendar.FirstOrDefault(e =>
                    DateOnly.FromDateTime(e.StartDateLocal) == runDate
                    && string.IsNullOrEmpty(e.PairedActivityId));

                var actId = $"sim-act-{actSeq++:D4}";
                var (actEntry, newCtl, newAtl) = BuildActivityEntry(
                    simProfile,
                    actId,
                    runDate,
                    runSpec,
                    pairedEvt?.Id,
                    ctl,
                    atl);

                ctl = newCtl;
                atl = newAtl;
                activities.Add(actEntry);
                injectedRunName = runSpec.Name;
                UpdateWellnessLoads(wellnessByDate, runDate, ctl, atl);

                if (pairedEvt is not null)
                {
                    var idx = calendar.IndexOf(pairedEvt);
                    calendar[idx] = pairedEvt with { PairedActivityId = actId };
                    autoEventIds.Remove(pairedEvt.Id);
                }
            }

            var wellnessList = wellnessByDate.Values.OrderBy(w => w.Id).ToList();
            var dossier = CoachIntelligenceDossierBuilder.Build(
                simProfile,
                activities,
                wellnessList,
                runReviews: null,
                calendar,
                refDate,
                refDate,
                autoEventIds,
                userLockedDates,
                activeWeather,
                currentCompass);

            var healthBefore = currentCompass?.HealthState;
            var timestampUtc = new DateTimeOffset(
                refDate.Year,
                refDate.Month,
                refDate.Day,
                step.TimeOfDay.Hour,
                step.TimeOfDay.Minute,
                0,
                TimeSpan.Zero);

            var outcome = await strategy.PlanAsync(new CoachPlanningContext
            {
                Dossier = dossier,
                Trigger = step.Trigger,
                AthleteMessage = step.AthleteMessage,
                MaxValidationRetries = maxValidationRetries,
                TimestampUtc = timestampUtc
            }, cancellationToken).ConfigureAwait(false);

            totalAttempts += outcome.Attempts.Count;
            totalElapsed += outcome.Attempts.Sum(a => a.ElapsedSeconds);

            var scheduledWatchCount = 0;
            var scheduledPreviewCount = 0;

            if (outcome.Success && outcome.Decision is not null && outcome.ReconciledSchedule is not null)
            {
                ApplyScheduleReconciliation(
                    simProfile.Id,
                    outcome.Decision,
                    outcome.ReconciledSchedule,
                    calendar,
                    autoEventIds,
                    userLockedDates,
                    ref nextEventId);

                currentCompass = outcome.Decision.UpdatedCompass with
                {
                    UpdatedAt = refDate,
                    AutoEventIds = [.. autoEventIds.OrderBy(id => id)]
                };

                scheduledWatchCount = outcome.ReconciledSchedule.WatchCommitWindow.Count;
                scheduledPreviewCount = outcome.ReconciledSchedule.PreviewWindow.Count;
            }

            var stepFindings = AutopilotSimulationAuditor.AuditStep(
                step.DayOffset,
                dossier,
                step,
                healthBefore,
                outcome,
                calendar,
                autoEventIds);

            allFindings.AddRange(stepFindings);

            stepReports.Add(new SimulationStepReport
            {
                DayOffset = step.DayOffset,
                Date = refDate,
                Trigger = step.Trigger,
                AthleteMessage = step.AthleteMessage,
                InjectedRunName = injectedRunName,
                HealthStateBefore = healthBefore,
                HealthStateAfter = outcome.Decision?.HealthState ?? healthBefore,
                Outcome = outcome,
                ScheduledWatchCount = scheduledWatchCount,
                ScheduledPreviewCount = scheduledPreviewCount,
                ActiveAutoEventIds = [.. autoEventIds.OrderBy(id => id)],
                UserLockedDates = [.. userLockedDates.Where(d => d >= refDate).OrderBy(d => d)],
                AuditFindings = stepFindings
            });
        }

        var passed = allFindings.All(f => !string.Equals(f.Severity, "fail", StringComparison.Ordinal));
        var summaryByCode = allFindings
            .GroupBy(f => f.AuditCode, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return new SimulationRunResult
        {
            ScenarioName = scenario.Name,
            Description = scenario.Description,
            AthleteSlug = athleteSlug,
            Provider = providerName,
            StartDate = startDate,
            TotalSteps = scenario.Steps.Count,
            Passed = passed,
            TotalAttempts = totalAttempts,
            TotalElapsedSeconds = Math.Round(totalElapsed, 2, MidpointRounding.ToEven),
            Steps = stepReports,
            AllFindings = allFindings,
            AuditSummaryByCode = summaryByCode
        };
    }

    private static void ApplyScheduleReconciliation(
        string athleteId,
        AutopilotPlanDecision decision,
        ReconciledAutopilotSchedule reconciled,
        List<CalendarEventEntry> calendar,
        HashSet<long> autoEventIds,
        HashSet<DateOnly> userLockedDates,
        ref long nextEventId)
    {
        if (reconciled.DeletedEventIds.Count > 0)
        {
            var delSet = reconciled.DeletedEventIds.ToHashSet();
            calendar.RemoveAll(e => delSet.Contains(e.Id));
            autoEventIds.ExceptWith(delSet);
        }

        if (string.Equals(decision.Trigger, "telegram_override", StringComparison.Ordinal))
        {
            foreach (var op in decision.Operations)
            {
                if (op.Operation is "delete" or "upsert")
                {
                    userLockedDates.Add(op.Date);
                }
            }
        }

        foreach (var upsert in reconciled.CompiledUpserts)
        {
            var newId = ++nextEventId;
            var dslText = upsert.CompiledWorkout?.DslText ?? upsert.Title;
            var dur = upsert.TotalDurationSeconds ?? 2700;
            calendar.Add(BuildCalendarEntry(athleteId, newId, upsert.Date, upsert.Title, dslText, dur, upsert.EstimatedLoad));

            if (string.Equals(upsert.Ownership, "auto", StringComparison.Ordinal))
            {
                autoEventIds.Add(newId);
            }
            else if (string.Equals(upsert.Ownership, "user_locked", StringComparison.Ordinal))
            {
                autoEventIds.Remove(newId);
                userLockedDates.Add(upsert.Date);
            }
        }
    }

    private static WeatherForecastDossier BuildWeatherDossier(DateOnly startDate, IReadOnlyList<SimulatedWeatherDaySpec> specs) => new()
    {
        Source = "simulated-open-meteo",
        LocationLabel = "Simulation Track",
        Days = specs
            .Select(s => new DailyWeatherForecast
            {
                Date = startDate.AddDays(s.DayOffset),
                TempMinC = s.TempMinC,
                TempMaxC = s.TempMaxC,
                PrecipitationMm = s.PrecipitationMm,
                MaxWindGustKmh = s.MaxWindGustKmh,
                Summary = s.Summary
            })
            .OrderBy(d => d.Date)
            .ToList()
    };

    private static IntervalsWellnessEntry BuildWellnessEntry(
        DateOnly date,
        SimulatedWellnessSpec spec,
        double ctl,
        double atl) => new()
        {
            Id = date,
            Updated = new DateTimeOffset(date.Year, date.Month, date.Day, 6, 0, 0, TimeSpan.Zero),
            Ctl = Math.Round(ctl, 1, MidpointRounding.ToEven),
            Atl = Math.Round(atl, 1, MidpointRounding.ToEven),
            RestingHr = spec.RestingHr,
            Hrv = spec.Hrv,
            SleepSecs = (int)Math.Round(spec.SleepHours * 3600.0),
            SleepScore = spec.SleepScore,
            Injury = spec.InjuryScore,
            Comments = spec.Comments,
            TempWeight = false,
            TempRestingHr = false
        };

    private static void UpdateWellnessLoads(
        Dictionary<DateOnly, IntervalsWellnessEntry> wellnessByDate,
        DateOnly date,
        double ctl,
        double atl)
    {
        if (wellnessByDate.TryGetValue(date, out var existing))
        {
            wellnessByDate[date] = existing with
            {
                Ctl = Math.Round(ctl, 1, MidpointRounding.ToEven),
                Atl = Math.Round(atl, 1, MidpointRounding.ToEven)
            };
        }
    }

    private static (IntervalsActivityEntry Entry, double Ctl, double Atl) BuildActivityEntry(
        AthleteProfile profile,
        string id,
        DateOnly date,
        SimulatedRunSpec spec,
        long? pairedEventId,
        double prevCtl,
        double prevAtl)
    {
        var tss = Math.Round((spec.DurationSeconds / 3600.0) * 55.0, 1, MidpointRounding.ToEven);
        var kCtl = Math.Exp(-1.0 / 42.0);
        var kAtl = Math.Exp(-1.0 / 7.0);
        var nextCtl = (prevCtl * kCtl) + (tss * (1.0 - kCtl));
        var nextAtl = (prevAtl * kAtl) + (tss * (1.0 - kAtl));

        var entry = new IntervalsActivityEntry
        {
            Id = id,
            IcuAthleteId = profile.Id,
            Source = "GARMIN_CONNECT",
            Type = "Run",
            Name = spec.Name,
            Description = spec.Note,
            StartDateLocal = new DateTime(date.Year, date.Month, date.Day, 17, 0, 0, DateTimeKind.Unspecified),
            MovingTime = spec.DurationSeconds,
            ElapsedTime = spec.DurationSeconds,
            Distance = spec.DistanceMeters,
            AverageHeartrate = spec.AverageHeartRate,
            MaxHeartrate = spec.MaxHeartRate,
            AverageCadence = spec.CadenceSpm.HasValue ? spec.CadenceSpm.Value / 2.0 : 82.0,
            IcuHrZoneTimes = spec.HrZoneTimesSeconds,
            IcuHrZones = profile.HeartRate.RawHrZones,
            IcuCtl = Math.Round(nextCtl, 1, MidpointRounding.ToEven),
            IcuAtl = Math.Round(nextAtl, 1, MidpointRounding.ToEven),
            IcuTrainingLoad = (int)Math.Round(tss),
            PairedEventId = pairedEventId,
            IntervalSummary = spec.IntervalSummary
        };

        return (entry, nextCtl, nextAtl);
    }

    private static CalendarEventEntry BuildCalendarEntry(
        string athleteId,
        long id,
        DateOnly date,
        string name,
        string description,
        int durationSeconds,
        double estimatedLoad) => new()
        {
            Id = id,
            AthleteId = athleteId,
            Category = "WORKOUT",
            StartDateLocal = new DateTime(date.Year, date.Month, date.Day, 7, 0, 0, DateTimeKind.Unspecified),
            EndDateLocal = new DateTime(date.Year, date.Month, date.Day, 8, 0, 0, DateTimeKind.Unspecified),
            Type = "Run",
            Name = name,
            Description = description,
            MovingTime = durationSeconds,
            IcuTrainingLoad = estimatedLoad
        };
}

/// <summary>
/// Deterministic invariant auditor evaluating every simulation step against Coaching DNA, ownership, health, weather, and clock hygiene rules.
/// </summary>
public static class AutopilotSimulationAuditor
{
    private static readonly string[] IllnessPromptKeywords =
        ["krank", "erkält", "fieber", "infekt", "sick", "ill", "flu"];

    private static readonly string[] RecoveryPromptKeywords =
        ["wieder fit", "fieber ist weg", "kein fieber", "keine beschwerden", "gesund", "erholt"];

    private static readonly string[] RestOverrideKeywords =
        ["ruhetag", "streichen", "pausier", "rest day", "skip today"];

    public static List<SimulationAuditFinding> AuditStep(
        int dayOffset,
        CoachIntelligenceDossier dossier,
        SimulatedTriggerStep step,
        string? healthStateBefore,
        CoachPlanningOutcome outcome,
        List<CalendarEventEntry> calendarAfterStep,
        HashSet<long> autoEventIdsAfterStep)
    {
        ArgumentNullException.ThrowIfNull(dossier);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(calendarAfterStep);
        ArgumentNullException.ThrowIfNull(autoEventIdsAfterStep);

        var findings = new List<SimulationAuditFinding>();
        var refDate = dossier.ReferenceDate;

        // AUDIT-07: Strict clock hygiene check on input dossier regardless of LLM success
        CheckClockHygiene(dayOffset, dossier, step.Trigger, outcome.Decision, findings);

        // AUDIT-01: Decision validity & gatekeeper acceptance
        if (!outcome.Success || outcome.Decision is null || outcome.ReconciledSchedule is null)
        {
            findings.Add(new SimulationAuditFinding(
                "AUDIT-01",
                "fail",
                dayOffset,
                refDate,
                step.Trigger,
                $"Planning failed: {outcome.FailureReason ?? "no decision produced"}"));
            return findings;
        }

        var decision = outcome.Decision;
        var schedule = outcome.ReconciledSchedule;

        // AUDIT-02: HR vs. Pace separation and DSL compilation completeness
        CheckModalityAndCompilation(dayOffset, refDate, step.Trigger, decision, schedule, findings);

        // AUDIT-03: Health & SickHold guardrails
        CheckHealthAndSickHold(dayOffset, dossier, step, decision, schedule, calendarAfterStep, autoEventIdsAfterStep, findings);

        // AUDIT-04: Ownership immutability, Same-Day Intraday Freeze & Telegram rest override
        CheckOwnershipAndFreeze(dayOffset, dossier, step, decision, schedule, findings);

        // AUDIT-05: Calendar handoff parity & AllowConsecutiveRunDays enforcement
        CheckCalendarParityAndSpacing(dayOffset, dossier, step.Trigger, schedule, calendarAfterStep, findings);

        // AUDIT-06: SickHold -> ReEntry -> Normal state machine & ramp-up gating
        CheckStateMachine(dayOffset, dossier, step.Trigger, healthStateBefore, decision, schedule, findings);

        // AUDIT-08: Weather pace-interval safety
        CheckWeatherSafety(dayOffset, dossier, step.Trigger, decision, findings);

        return findings;
    }

    private static void CheckClockHygiene(
        int dayOffset,
        CoachIntelligenceDossier dossier,
        string trigger,
        AutopilotPlanDecision? decision,
        List<SimulationAuditFinding> findings)
    {
        var refDate = dossier.ReferenceDate;

        if (dossier.Readiness.LatestWellnessDate is { } wDate && wDate > refDate)
        {
            findings.Add(new SimulationAuditFinding(
                "AUDIT-07",
                "fail",
                dayOffset,
                refDate,
                trigger,
                $"Future wellness data leak detected: {wDate:yyyy-MM-dd} > {refDate:yyyy-MM-dd}."));
        }

        var futureBase = dossier.StimulusProgression.BaseProgressionChain.FirstOrDefault(s => s.Date > refDate);
        var futureQuality = dossier.StimulusProgression.QualityProgressionChain.FirstOrDefault(q => q.Date > refDate);
        if (futureBase is not null || futureQuality is not null)
        {
            findings.Add(new SimulationAuditFinding(
                "AUDIT-07",
                "fail",
                dayOffset,
                refDate,
                trigger,
                $"Future activity data leak detected in progression chain (> {refDate:yyyy-MM-dd})."));
        }

        if (decision is not null)
        {
            var horizonEnd = refDate.AddDays(13);
            foreach (var op in decision.Operations)
            {
                if (op.Date < refDate || op.Date > horizonEnd)
                {
                    findings.Add(new SimulationAuditFinding(
                        "AUDIT-07",
                        "fail",
                        dayOffset,
                        refDate,
                        trigger,
                        $"Operation {op.Operation} on {op.Date:yyyy-MM-dd} falls outside 14-day horizon [{refDate:yyyy-MM-dd}, {horizonEnd:yyyy-MM-dd}]."));
                }
            }
        }
    }

    private static void CheckModalityAndCompilation(
        int dayOffset,
        DateOnly refDate,
        string trigger,
        AutopilotPlanDecision decision,
        ReconciledAutopilotSchedule schedule,
        List<SimulationAuditFinding> findings)
    {
        foreach (var upsert in schedule.CompiledUpserts)
        {
            if (upsert.CompiledWorkout is null || string.IsNullOrWhiteSpace(upsert.CompiledWorkout.DslText))
            {
                findings.Add(new SimulationAuditFinding(
                    "AUDIT-02",
                    "fail",
                    dayOffset,
                    refDate,
                    trigger,
                    $"Upserted workout '{upsert.Title}' on {upsert.Date:yyyy-MM-dd} failed to compile Intervals.icu DSL."));
            }
        }

        foreach (var op in decision.Operations)
        {
            if (string.Equals(op.Operation, "upsert", StringComparison.Ordinal) && op.Workout is not null)
            {
                var validation = WorkoutModalityValidator.ValidateWorkout(op.Workout);
                if (!validation.IsValid)
                {
                    findings.Add(new SimulationAuditFinding(
                        "AUDIT-02",
                        "fail",
                        dayOffset,
                        refDate,
                        trigger,
                        $"Workout modality validation failed on {op.Date:yyyy-MM-dd}: {string.Join("; ", validation.Errors.Select(e => e.Message))}"));
                }
            }
        }
    }

    private static void CheckHealthAndSickHold(
        int dayOffset,
        CoachIntelligenceDossier dossier,
        SimulatedTriggerStep step,
        AutopilotPlanDecision decision,
        ReconciledAutopilotSchedule schedule,
        List<CalendarEventEntry> calendarAfterStep,
        HashSet<long> autoEventIdsAfterStep,
        List<SimulationAuditFinding> findings)
    {
        var refDate = dossier.ReferenceDate;
        var hasRecoveryMessage = !string.IsNullOrWhiteSpace(step.AthleteMessage)
            && RecoveryPromptKeywords.Any(k => step.AthleteMessage.Contains(k, StringComparison.OrdinalIgnoreCase));
        var hasIllnessMessage = !hasRecoveryMessage
            && !string.IsNullOrWhiteSpace(step.AthleteMessage)
            && IllnessPromptKeywords.Any(k => step.AthleteMessage.Contains(k, StringComparison.OrdinalIgnoreCase));

        if (hasIllnessMessage || dossier.Readiness.MedicalRedFlags.Count > 0)
        {
            if (string.Equals(decision.HealthState, "Normal", StringComparison.Ordinal))
            {
                findings.Add(new SimulationAuditFinding(
                    "AUDIT-03",
                    "fail",
                    dayOffset,
                    refDate,
                    step.Trigger,
                    "Illness symptom or medical red flag active, yet coach kept health_state='Normal'."));
            }

            if (hasIllnessMessage && !string.Equals(decision.HealthState, "SickHold", StringComparison.Ordinal))
            {
                findings.Add(new SimulationAuditFinding(
                    "AUDIT-03",
                    "fail",
                    dayOffset,
                    refDate,
                    step.Trigger,
                    $"Acute illness message '{step.AthleteMessage}' must transition to health_state='SickHold' (got '{decision.HealthState}')."));
            }

            if (schedule.WatchCommitWindow.Any(w =>
                WorkoutModalityValidator.IsQualitySessionType(w.SessionType)
                || string.Equals(w.SessionType, "long_run", StringComparison.Ordinal)))
            {
                findings.Add(new SimulationAuditFinding(
                    "AUDIT-03",
                    "fail",
                    dayOffset,
                    refDate,
                    step.Trigger,
                    "Quality or long_run scheduled in Watch Commit Window despite active illness/red flags."));
            }
        }

        if (string.Equals(decision.HealthState, "SickHold", StringComparison.Ordinal))
        {
            if (schedule.CompiledUpserts.Count > 0)
            {
                findings.Add(new SimulationAuditFinding(
                    "AUDIT-03",
                    "fail",
                    dayOffset,
                    refDate,
                    step.Trigger,
                    $"SickHold forbids upserting new workouts (found {schedule.CompiledUpserts.Count})."));
            }

            if (!string.Equals(step.Trigger, "intraday_check", StringComparison.Ordinal))
            {
                var openAuto = calendarAfterStep
                    .Where(e => DateOnly.FromDateTime(e.StartDateLocal) >= refDate
                        && string.Equals(e.Category, "WORKOUT", StringComparison.OrdinalIgnoreCase)
                        && string.IsNullOrEmpty(e.PairedActivityId)
                        && autoEventIdsAfterStep.Contains(e.Id))
                    .ToList();

                if (openAuto.Count > 0)
                {
                    findings.Add(new SimulationAuditFinding(
                        "AUDIT-03",
                        "fail",
                        dayOffset,
                        refDate,
                        step.Trigger,
                        $"SickHold left {openAuto.Count} open auto workout(s) un-cleared on the calendar."));
                }
            }
        }
    }

    private static void CheckOwnershipAndFreeze(
        int dayOffset,
        CoachIntelligenceDossier dossier,
        SimulatedTriggerStep step,
        AutopilotPlanDecision decision,
        ReconciledAutopilotSchedule schedule,
        List<SimulationAuditFinding> findings)
    {
        var refDate = dossier.ReferenceDate;
        var eventsById = dossier.Calendar.UpcomingEventsNext14Days.ToDictionary(e => e.Id);
        var lockedDates = dossier.Calendar.UserLockedDates.ToHashSet();

        foreach (var op in decision.Operations)
        {
            if (op.Operation is not ("upsert" or "delete"))
            {
                continue;
            }

            if (string.Equals(step.Trigger, "intraday_check", StringComparison.Ordinal) && op.Date == refDate)
            {
                findings.Add(new SimulationAuditFinding(
                    "AUDIT-04",
                    "fail",
                    dayOffset,
                    refDate,
                    step.Trigger,
                    $"Same-day intraday freeze violated on {refDate:yyyy-MM-dd}."));
            }

            if (op.EventId.HasValue && eventsById.TryGetValue(op.EventId.Value, out var evt))
            {
                if (string.Equals(evt.Ownership, "completed", StringComparison.Ordinal))
                {
                    findings.Add(new SimulationAuditFinding(
                        "AUDIT-04",
                        "fail",
                        dayOffset,
                        refDate,
                        step.Trigger,
                        $"Completed event {evt.Id} was mutated."));
                }
                else if (string.Equals(evt.Ownership, "user_locked", StringComparison.Ordinal)
                    && !string.Equals(step.Trigger, "telegram_override", StringComparison.Ordinal))
                {
                    findings.Add(new SimulationAuditFinding(
                        "AUDIT-04",
                        "fail",
                        dayOffset,
                        refDate,
                        step.Trigger,
                        $"user_locked event {evt.Id} was mutated without telegram_override."));
                }
            }
            else if (lockedDates.Contains(op.Date)
                && !string.Equals(step.Trigger, "telegram_override", StringComparison.Ordinal))
            {
                findings.Add(new SimulationAuditFinding(
                    "AUDIT-04",
                    "fail",
                    dayOffset,
                    refDate,
                    step.Trigger,
                    $"user_locked date {op.Date:yyyy-MM-dd} was mutated without telegram_override."));
            }
        }

        if (string.Equals(step.Trigger, "telegram_override", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(step.AthleteMessage)
            && RestOverrideKeywords.Any(k => step.AthleteMessage.Contains(k, StringComparison.OrdinalIgnoreCase)))
        {
            var hasUpsertToday = schedule.CompiledUpserts.Any(u => u.Date == refDate);
            var hadOpenWorkoutToday = dossier.Calendar.EventsOnTargetDate.Any(e => !e.IsCompleted);
            var keptOpenWorkoutToday = schedule.WatchCommitWindow.Any(w => w.Date == refDate);

            if (hasUpsertToday || (hadOpenWorkoutToday && keptOpenWorkoutToday))
            {
                findings.Add(new SimulationAuditFinding(
                    "AUDIT-04",
                    "fail",
                    dayOffset,
                    refDate,
                    step.Trigger,
                    $"Telegram rest override on {refDate:yyyy-MM-dd} failed to clear/avoid today's workout."));
            }
        }
    }

    private static void CheckCalendarParityAndSpacing(
        int dayOffset,
        CoachIntelligenceDossier dossier,
        string trigger,
        ReconciledAutopilotSchedule schedule,
        List<CalendarEventEntry> calendarAfterStep,
        List<SimulationAuditFinding> findings)
    {
        var refDate = dossier.ReferenceDate;
        var calendarIdsAfter = calendarAfterStep.Select(e => e.Id).ToHashSet();

        foreach (var deletedId in schedule.DeletedEventIds)
        {
            if (calendarIdsAfter.Contains(deletedId))
            {
                findings.Add(new SimulationAuditFinding(
                    "AUDIT-05",
                    "fail",
                    dayOffset,
                    refDate,
                    trigger,
                    $"Deleted event ID {deletedId} still exists in calendar after reconciliation."));
            }
        }

        foreach (var upsert in schedule.CompiledUpserts)
        {
            var matched = calendarAfterStep.Any(e =>
                DateOnly.FromDateTime(e.StartDateLocal) == upsert.Date
                && string.Equals(e.Name, upsert.Title, StringComparison.Ordinal));
            if (!matched)
            {
                findings.Add(new SimulationAuditFinding(
                    "AUDIT-05",
                    "fail",
                    dayOffset,
                    refDate,
                    trigger,
                    $"Compiled upsert '{upsert.Title}' on {upsert.Date:yyyy-MM-dd} missing from world calendar."));
            }
        }

        if (dossier.Athlete.AllowConsecutiveRunDays == false)
        {
            var upsertedDates = schedule.CompiledUpserts.Select(u => u.Date).ToHashSet();
            var dates = schedule.WatchCommitWindow
                .Concat(schedule.PreviewWindow)
                .Select(w => w.Date)
                .Distinct()
                .OrderBy(d => d)
                .ToList();

            if (dossier.StimulusProgression.Ledger.DaysSinceLastRun == 0 && !dates.Contains(refDate))
            {
                dates.Add(refDate);
                dates.Sort();
            }

            for (var i = 1; i < dates.Count; i++)
            {
                if (dates[i].DayNumber - dates[i - 1].DayNumber == 1
                    && (upsertedDates.Contains(dates[i - 1]) || upsertedDates.Contains(dates[i])))
                {
                    findings.Add(new SimulationAuditFinding(
                        "AUDIT-05",
                        "fail",
                        dayOffset,
                        refDate,
                        trigger,
                        $"Consecutive run days {dates[i - 1]:yyyy-MM-dd} and {dates[i]:yyyy-MM-dd} scheduled while AllowConsecutiveRunDays=false."));
                }
            }
        }
    }

    private static void CheckStateMachine(
        int dayOffset,
        CoachIntelligenceDossier dossier,
        string trigger,
        string? healthStateBefore,
        AutopilotPlanDecision decision,
        ReconciledAutopilotSchedule schedule,
        List<SimulationAuditFinding> findings)
    {
        var refDate = dossier.ReferenceDate;
        var cleanRunsSinceCompass = dossier.CurrentCompass is not null
            ? dossier.StimulusProgression.BaseProgressionChain.Count(s =>
                s.Date >= dossier.CurrentCompass.UpdatedAt
                && !s.IsCompromised
                && (s.Z1Z2Percentage is null or >= 80.0))
            : 0;

        if (string.Equals(healthStateBefore, "SickHold", StringComparison.Ordinal)
            && string.Equals(decision.HealthState, "Normal", StringComparison.Ordinal)
            && cleanRunsSinceCompass < 2)
        {
            findings.Add(new SimulationAuditFinding(
                "AUDIT-06",
                "fail",
                dayOffset,
                refDate,
                trigger,
                "Invalid direct state transition SickHold -> Normal without stepping through ReEntry."));
        }

        if (string.Equals(decision.HealthState, "ReEntry", StringComparison.Ordinal))
        {
            var count = dossier.StimulusProgression.BreakReentry.HasRecentExtendedBreak
                ? dossier.StimulusProgression.BreakReentry.CleanBaseRunsSinceRecentBreak
                : cleanRunsSinceCompass;

            foreach (var item in schedule.WatchCommitWindow.Concat(schedule.PreviewWindow))
            {
                var isQuality = item.SessionType is not null
                    ? WorkoutModalityValidator.IsQualitySessionType(item.SessionType)
                    : item.Title.Contains("interval", StringComparison.OrdinalIgnoreCase)
                        || item.Title.Contains("schwelle", StringComparison.OrdinalIgnoreCase)
                        || item.Title.Contains("threshold", StringComparison.OrdinalIgnoreCase)
                        || item.Title.Contains("tempo", StringComparison.OrdinalIgnoreCase);

                if (isQuality && !string.Equals(item.Ownership, "completed", StringComparison.Ordinal) && count < 2)
                {
                    findings.Add(new SimulationAuditFinding(
                        "AUDIT-06",
                        "fail",
                        dayOffset,
                        refDate,
                        trigger,
                        $"Premature quality workout '{item.Title}' on {item.Date:yyyy-MM-dd} with only {count} clean base run(s)."));
                }
                else if (!isQuality && !string.Equals(item.Ownership, "completed", StringComparison.Ordinal))
                {
                    count++;
                }
            }
        }
    }

    private static void CheckWeatherSafety(
        int dayOffset,
        CoachIntelligenceDossier dossier,
        string trigger,
        AutopilotPlanDecision decision,
        List<SimulationAuditFinding> findings)
    {
        if (dossier.Weather is null)
        {
            return;
        }

        var unfriendlyDates = dossier.Weather.Days
            .Where(d => !d.PaceIntervalFriendly)
            .Select(d => d.Date)
            .ToHashSet();

        if (unfriendlyDates.Count == 0)
        {
            return;
        }

        foreach (var op in decision.Operations)
        {
            if (!string.Equals(op.Operation, "upsert", StringComparison.Ordinal)
                || op.Workout is null
                || !unfriendlyDates.Contains(op.Date))
            {
                continue;
            }

            if (HasPaceRangeTarget(op.Workout))
            {
                findings.Add(new SimulationAuditFinding(
                    "AUDIT-08",
                    "fail",
                    dayOffset,
                    dossier.ReferenceDate,
                    trigger,
                    $"Upserted pace_range workout '{op.Workout.Title}' on adverse weather day {op.Date:yyyy-MM-dd}."));
            }
        }
    }

    private static bool HasPaceRangeTarget(WorkoutDefinition workout)
    {
        foreach (var step in workout.Steps)
        {
            if (step.Target is not null && string.Equals(step.Target.Modality, "pace_range", StringComparison.Ordinal))
            {
                return true;
            }

            if (step.Work is not null && string.Equals(step.Work.Target.Modality, "pace_range", StringComparison.Ordinal))
            {
                return true;
            }

            if (step.Recovery is not null && string.Equals(step.Recovery.Target.Modality, "pace_range", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
