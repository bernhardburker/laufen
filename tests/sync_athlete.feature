Feature: Sync athlete profile and heart rate zones from Intervals.icu
  As a runner or training planner
  I want to automatically download my athlete profile and heart rate zones from Intervals.icu
  So that workout planning and dashboards use my calibrated threshold and zone values

  Scenario: Execution fails when INTERVALS_API_KEY is missing
    Given INTERVALS_API_KEY is not configured
    When the athlete profile sync script is executed
    Then the process exits with an error
    And the error output mentions "INTERVALS_API_KEY is not set"

  Scenario: Successfully sync athlete profile from mock API server
    Given INTERVALS_API_KEY is configured
    And a mock Intervals.icu API server is running with athlete profile data
    When the athlete profile sync script is executed against the mock API
    Then the process exits with code 0
    And the athlete configuration JSON file is created
    And the athlete configuration contains the calibrated heart rate zones
    And the stdout report displays the athlete name and heart rate summary
