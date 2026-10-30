Feature: Schedule structured workouts to Intervals.icu
  As a runner
  I want to schedule structured workouts to Intervals.icu
  So that they automatically synchronize to my Garmin watch and other connected devices

  Scenario: Missing INTERVALS_API_KEY causes failure when uploading
    Given INTERVALS_API_KEY is not set
    When the workout schedule script is executed with archetype "easy_run"
    Then the process exits with an error
    And the error output mentions "INTERVALS_API_KEY"

  Scenario: Dry run generates structured payload without sending HTTP requests
    Given INTERVALS_API_KEY is not set
    When the workout schedule script is executed in dry-run mode for "intervals"
    Then the process exits with code 0
    And the stdout contains "[DRY-RUN]"
    And the stdout contains "Workout-Struktur:"
    And the stdout contains "4x"

  Scenario: Successfully schedule an easy run workout to Intervals.icu
    Given a mock Intervals.icu event server is running
    And INTERVALS_API_KEY is configured
    When the workout schedule script is executed for "easy_run" with duration "45m"
    Then the process exits with code 0
    And the mock server received a POST request to "/api/v1/athlete/0/events"
    And the received event has type "Run" and category "WORKOUT"
    And the received event description contains "30m Z2 HR"

  Scenario: Successfully schedule an intervals workout with custom targets
    Given a mock Intervals.icu event server is running
    And INTERVALS_API_KEY is configured
    When the workout schedule script is executed for "intervals" with 4 reps of "1km" in "Z4 HR"
    Then the process exits with code 0
    And the received event description contains "4x"
    And the received event description contains "1km Z4 HR"

  Scenario: Successfully schedule a workout using raw text
    Given a mock Intervals.icu event server is running
    And INTERVALS_API_KEY is configured
    When the workout schedule script is executed with raw text "Warmup\n- 10m Z1 HR\nMain Set\n- 20m Z3 HR"
    Then the process exits with code 0
    And the received event description contains "20m Z3 HR"

  Scenario: Automatically sync weekly plan including both options for flexible slot
    Given a mock Intervals.icu event server is running
    And INTERVALS_API_KEY is configured
    When the workout schedule script is executed with extra argument "--sync-plan"
    Then the process exits with code 0
    And the mock server received at least 3 POST requests to "/api/v1/athlete/0/events"
    And one scheduled event name contains "Slot 2A"
    And one scheduled event name contains "Slot 2B"

  Scenario: Adaptive planner cleans up past uncompleted workouts and schedules for today
    Given a mock Intervals.icu event server is running
    And INTERVALS_API_KEY is configured
    And a mock uncompleted workout exists for yesterday
    And no run occurred yesterday
    When the workout schedule script is executed with adaptive planning arguments
    Then the process exits with code 0
    And the mock server received a DELETE request for the uncompleted workout
    And one scheduled event has date for today

  Scenario: Adaptive planner recognizes yesterday run and schedules for tomorrow
    Given a mock Intervals.icu event server is running
    And INTERVALS_API_KEY is configured
    And an activity was recorded for yesterday
    When the workout schedule script is executed with adaptive planning arguments
    Then the process exits with code 0
    And one scheduled event has date for tomorrow

  Scenario: Adaptive planner is idempotent across multiple executions on the same day
    Given a mock Intervals.icu event server is running
    And INTERVALS_API_KEY is configured
    And no run occurred yesterday
    When the workout schedule script is executed with adaptive planning arguments
    And the workout schedule script is executed with adaptive planning arguments
    Then the process exits with code 0
    And exactly 1 workout event is scheduled for today

  Scenario: Existing rest day note in calendar prevents scheduling a workout
    Given a mock Intervals.icu event server is running
    And INTERVALS_API_KEY is configured
    And no run occurred yesterday
    And a rest day note exists in calendar for today
    When the workout schedule script is executed with adaptive planning arguments
    Then the process exits with code 0
    And no workout event is scheduled for today

  Scenario: Paired completed workout for today treats today as completed run and plans next for day after tomorrow
    Given a mock Intervals.icu event server is running
    And INTERVALS_API_KEY is configured
    And a paired completed workout exists in calendar for today
    When the workout schedule script is executed with adaptive planning arguments and events-file
    Then the process exits with code 0
    And one scheduled event has date for day after tomorrow
    And the calendar events file contains the today paired workout
