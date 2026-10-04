Feature: Recent running activities inspection
  As a runner
  I want to view my recent running activities with key performance metrics
  So that I can quickly assess recent workouts, pacing, heart rate, and training load

  Scenario: Display recent running activities table
    Given an activities dataset with multiple recent runs
    When the recent runs script is executed
    Then the process exits with code 0
    And the stdout report displays the recent runs table
    And the report displays distance, duration, pace, heart rate, and zone percentages

  Scenario: Gracefully handle dataset with no running activities
    Given an activities dataset with no running activities
    When the recent runs script is executed
    Then the process exits with code 0
    And the output mentions "No running activities found"

  Scenario: Output recent runs in JSON format
    Given an activities dataset with multiple recent runs
    When the recent runs script is executed with "--json"
    Then the process exits with code 0
    And the stdout contains valid JSON with a list of recent runs

  Scenario: Limit number of displayed runs
    Given an activities dataset with multiple recent runs
    When the recent runs script is executed with "--limit 2"
    Then the process exits with code 0
    And exactly 2 runs are displayed in the JSON output
