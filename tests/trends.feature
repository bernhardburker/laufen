Feature: Running trend and weekly training analysis
  As a runner
  I want to analyze my running activities and weekly trends
  So that I can monitor volume, zone discipline, and aerobic efficiency

  Scenario: Display weekly running trends and summary table
    Given an activities dataset with runs across multiple weeks
    When the trend analysis script is executed
    Then the process exits with code 0
    And the stdout report displays the weekly trends table
    And the report contains weekly totals for distance, time, and training load
    And the report displays Zone 1 and 2 percentage and aerobic efficiency

  Scenario: Gracefully handle dataset with no running activities
    Given an activities dataset with no running activities
    When the trend analysis script is executed
    Then the process exits with code 0
    And the output mentions "No running activities found"

  Scenario: Output analysis results in JSON format
    Given an activities dataset with runs across multiple weeks
    When the trend analysis script is executed with "--json"
    Then the process exits with code 0
    And the stdout contains valid JSON with weekly metrics
