Feature: Per-Second HR-Zone Pace (GAP) & Split Analysis
  As an athlete and AI Coach
  I want per-second activity streams to be synced incrementally and analyzed by HR zone using Grade Adjusted Pace (GAP)
  So that walking pauses are filtered out, split efforts are visible, and pace trends per HR zone are tracked accurately.

  Scenario: Filter walking pauses and enforce 120s minimum per HR zone
    Given an athlete profile with Zone 2 from 151 to 159 bpm and Zone 3 from 160 to 168 bpm
    And an activity stream with 150 seconds in Zone 2 at 2.4 m/s, 40 seconds walking in Zone 2 at 1.2 m/s, and 60 seconds in Zone 3 at 2.8 m/s
    When I compute the zone pace statistics for the stream
    Then 40 walking seconds are filtered out
    And Zone 2 is valid for trends with 150 running seconds and GAP pace around "06:57/km"
    And Zone 3 has 60 running seconds and is marked not valid for trends

  Scenario: Detect first-half vs second-half split differences
    Given an athlete profile with Zone 2 from 151 to 159 bpm and Zone 3 from 160 to 168 bpm
    And an activity stream with 120 seconds slow first half at 2.0 m/s and 120 seconds fast second half at 3.0 m/s
    When I compute the zone pace statistics for the stream
    Then the first-half GAP pace is slower than the second-half GAP pace by at least 150 seconds per km

  Scenario: Incrementally compute zone_pace_stats only for runs missing it
    Given an activities file with two runs where cached streams exist for both
    And one run already has zone_pace_stats in run_reviews while the other run is missing it
    When I run incremental review_runs on the workspace
    Then only the run missing zone_pace_stats is re-analyzed
    And the already-analyzed run preserves its original analyzed_at timestamp

  Scenario: Incrementally sync missing run streams from Intervals.icu
    Given two run activities where one stream is already cached locally
    When I run sync_run_streams with a mocked Intervals.icu API
    Then only the missing run stream is fetched and saved
