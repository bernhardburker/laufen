Feature: Download wellness and sleep data from Intervals.icu
  As a runner or coach
  I want to download my wellness and sleep metrics from Intervals.icu
  So that I can evaluate recovery, resting heart rate, and sleep quality locally

  Scenario: Execution fails when INTERVALS_API_KEY is missing
    Given INTERVALS_API_KEY is not configured
    When the wellness download script is executed
    Then the process exits with an error
    And the error output mentions "INTERVALS_API_KEY"

  Scenario: Successfully download wellness data and save to JSON
    Given a mock Intervals.icu API server is running with 2 wellness records
    And INTERVALS_API_KEY is configured
    When the wellness download script is executed against the mock API
    Then the process exits with code 0
    And the output JSON file contains 2 wellness records
    And the stdout report displays the wellness and sleep summary table
