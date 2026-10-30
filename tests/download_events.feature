Feature: Download calendar events from Intervals.icu
  As a runner
  I want to download my calendar events and scheduled workouts from Intervals.icu
  So that my dashboard, coach and planner know the planned training schedule locally

  Scenario: Execution fails when INTERVALS_API_KEY is missing
    Given INTERVALS_API_KEY is not configured for events
    When the download events script is executed
    Then the process exits with an error
    And the error output mentions "INTERVALS_API_KEY"

  Scenario: Successfully download calendar events and save to JSON
    Given a mock Intervals.icu event API server is running with 3 calendar events
    And INTERVALS_API_KEY is configured for events
    When the download events script is executed against the mock API
    Then the process exits with code 0
    And the output JSON file contains 3 calendar events
    And the stdout report displays the calendar events summary
