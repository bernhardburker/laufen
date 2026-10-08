Feature: Incremental per-run deep analysis and persistent review cache
  As an endurance runner
  I want each running activity to be analyzed in detail once and cached persistently
  So that the coach evaluates every run thoroughly without redundant re-analysis

  Scenario: Extract detailed physiological metrics from run data
    Given an activity with heart rate zone times, cadence, elevation, and intervals
    When deep run metrics are extracted
    Then the metrics contain exact seconds for all heart rate zones
    And the metrics contain cadence in strides per minute
    And the metrics identify the primary intensity classification

  Scenario: Incrementally review unanalyzed runs and cache results
    Given a repository with running activities and an empty review cache
    When run reviews are generated with a limit of 2 runs
    Then exactly 2 run reviews are created and stored in the cache
    And subsequent review execution detects zero unanalyzed runs

  Scenario: Macro coach prompt incorporates cached deep run reviews
    Given an existing review cache containing detailed reviews for recent runs
    When an AI coach prompt is constructed
    Then the prompt includes deep metrics and cached review summaries for those runs
