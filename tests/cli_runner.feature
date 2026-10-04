Feature: Central task runner and command line interface
  As a developer or automation agent
  I want a central runner script "./run"
  So that I can execute common training analysis, sync, dashboard, and testing tasks via concise commands

  Scenario: Display help and task list when requested
    When the cli runner is executed with "help"
    Then the process exits with code 0
    And the output contains the task runner overview
    And the output lists available commands like "sync", "report", and "dashboard"

  Scenario: Display help when no arguments are provided
    When the cli runner is executed without arguments
    Then the process exits with code 0
    And the output contains the task runner overview

  Scenario: Fail gracefully when an unknown command is invoked
    When the cli runner is executed with "unknown_cmd_xyz"
    Then the process exits with an error
    And the error output mentions "Unknown command 'unknown_cmd_xyz'"

  Scenario: Run report task displays recent runs and weekly trends
    Given an activities dataset with multiple running activities
    When the cli runner is executed with "report"
    Then the process exits with code 0
    And the output contains "RECENT RUNNING ACTIVITIES"
    And the output contains "WEEKLY RUNNING TRENDS"
