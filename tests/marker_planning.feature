Feature: 4-Stage Marker-Based Adaptive Workout Planning
  As a running coach and planning engine
  I want to extract objective telemetry markers and evaluate physiological guardrails
  So that hard threshold workouts are never scheduled after recent quality stimuli or acute fatigue

  Scenario: High fatigue and short sleep trigger fatigue warning and prevent threshold workout
    Given an athlete with resting HR baseline of 48 bpm and max Z2 of 158 bpm
    And wellness recorded with 5.5 hours sleep and TSB of -6.8
    And a long run of 66 minutes occurred yesterday
    When planning markers are extracted for tomorrow
    Then the guardrail "fatigue_warning" is active
    And the guardrail "volume_spike_alert" is active
    And the guardrail "slot_allows_threshold" is inactive
    And the selected adaptive workout is an easy base run

  Scenario: Recent threshold run within 3 days triggers quality discouraged guardrail
    Given a threshold run occurred 2 days ago
    And an easy run occurred yesterday
    When planning markers are extracted for tomorrow
    Then the guardrail "quality_discouraged" is active
    And the guardrail "slot_allows_threshold" is inactive
    And the selected adaptive workout is not a threshold workout

  Scenario: Fresh athlete in Slot 1 receives speed cadence intervals instead of threshold
    Given a fresh athlete with TSB of 2.5 and 8.0 hours sleep
    And the last quality run occurred 6 days ago
    And the last run occurred 2 days ago
    And the target day is a Tuesday matching Slot 1
    When planning markers are extracted for the target day
    Then no fatigue or quality guardrails are active
    And the periodization slot is "Slot 1 (Wochenanfang)"
    And the selected adaptive workout is "Intervall- & Kadenztraining (5x 1m Speed)"

  Scenario: CLI runner outputs structured markers with json flag
    When the planner CLI is executed with "--markers" and "--json"
    Then the process exits with code 0
    And the stdout is valid JSON containing "guardrails" and "readiness"

  Scenario: Standard 5-zone athlete profile resolves Z4 threshold range and Z2 max without fallback
    Given an athlete configured with standard 5-zone model with Z2 max 144 bpm and Z4 range 155 to 165 bpm
    When planning markers are extracted for tomorrow
    Then the extracted athlete markers have Z2 max 144 bpm and Z4 range 155 to 165 bpm

  Scenario: Missed weekend base anchor and open coach directive prevent Slot 1 intervals on Monday
    Given a fresh athlete with TSB of -4.5 and 7.3 hours sleep
    And Slot 1 requires a completed weekend base anchor
    And the last run occurred 3 days ago with note "Nach dem ersten Schwellenteil haben die Waden begonnen zu krampfen"
    And an open coach directive requests "easy_run"
    And the target day is a Monday matching Slot 1
    When planning markers are extracted for the target day
    Then the guardrail "base_anchor_missing" is active
    And the guardrail "health_warning" is active
    And the guardrail "coach_directive_override" is active
    And the selected adaptive workout is "Aerober Anker & Grundlagenlauf (45m Z2)"

  Scenario: Decision trace CLI outputs evaluated criteria, rejected alternatives, and reasoning
    When the decision trace CLI is executed for reference date "2026-11-02"
    Then the process exits with code 0
    And the trace stdout contains "COACH DECISION TRACE & KRITERIEN-ANALYSE"
    And the trace stdout contains "Verworfen"
    And the trace stdout contains "Aerober Anker & Grundlagenlauf (45m Z2)"

  Scenario: Planning markers exclude future runs beyond reference date
    Given an athlete with a past run 4 days ago and a future run 3 days after reference date
    When planning markers are extracted for tomorrow
    Then the history marker days_since_last_run is 4

