Feature: Download activities from Intervals.icu
  As a runner
  I want to download my activity history from Intervals.icu
  So that I can analyze pace and heart rate metrics locally

  Scenario: Execution fails when INTERVALS_API_KEY is missing
    Given INTERVALS_API_KEY is not configured
    When the download script is executed
    Then the process exits with an error
    And the error output mentions "INTERVALS_API_KEY"

  Scenario: Successfully download activities and save to JSON
    Given a mock Intervals.icu API server is running with 2 activities
    And INTERVALS_API_KEY is configured
    When the download script is executed against the mock API
    Then the process exits with code 0
    And the output JSON file contains 2 activities
    And the stdout report displays the activities summary table
