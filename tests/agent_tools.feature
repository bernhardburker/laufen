Feature: Legacy agent tools for assistant integration
  As an automation assistant
  I want CLI wrapper tools for activities, trends, and upcoming workouts
  So that the assistant can quickly query running stats and schedules with caching

  Scenario: Get activities caches downloaded results and respects 15-minute TTL
    Given a mock Intervals server with running activities
    And an isolated agent data directory
    When get_activities tool is executed for 14 days
    Then the agent cache file is created
    And the tool output contains formatted running activities
    When get_activities tool is executed again without force-refresh
    Then cached data is returned without downloading again

  Scenario: Get activities force refresh bypasses existing cache
    Given a mock Intervals server with running activities
    And an existing agent cache file
    When get_activities tool is executed with force-refresh
    Then the cache file timestamp is updated

  Scenario: Get trends aggregates weekly stats from cached activities
    Given an existing agent cache file with multi-week running activities
    When get_trends tool is executed for 4 weeks
    Then the trends output contains markdown table headers
    And the trends output reports total distance and weeks count

  Scenario: Get upcoming workouts parses planned calendar events
    Given a mock Intervals server with upcoming planned workouts
    When get_upcoming_workouts tool is executed for 7 days
    Then the upcoming workouts output lists planned workout sessions
    And the output includes a direct Intervals.icu calendar chip
