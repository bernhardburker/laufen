Feature: Upload Strava archive activities to Intervals.icu
  As a runner
  I want to upload my archived Strava activity files to Intervals.icu
  So that I have full telemetry and historical metrics in Intervals.icu

  Scenario: Execution fails when export directory is missing
    Given INTERVALS_API_KEY is configured
    When the upload script is executed with non-existent export directory
    Then the process exits with an error
    And the error output mentions "Keine activities.csv"

  Scenario: Dry run lists files without performing network uploads
    Given a valid Strava export directory with 2 activity files
    And INTERVALS_API_KEY is configured
    When the upload script is executed with dry-run flag
    Then the process exits with code 0
    And the stdout mentions "DRY RUN"
    And the stdout lists 2 activities to upload

  Scenario: Upload activity files to Intervals.icu mock API
    Given a mock Intervals.icu upload API server is running
    And a valid Strava export directory with 2 activity files
    And INTERVALS_API_KEY is configured
    When the upload script is executed against the mock API
    Then the process exits with code 0
    And the mock API received 2 uploaded files
    And the stdout mentions "2 hochgeladen"
