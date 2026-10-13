Feature: Daily adaptive training recommendation
  As a runner
  I want a daily evaluated training recommendation based on recovery and days since last run
  So that I know every day whether to rest, do active recovery, execute my workout, or gently restart after a break

  Scenario: Recommend rest day on the day of a completed workout
    Given a runner who completed a workout earlier today with significant training load
    When the daily training recommendation is calculated for today
    Then the recommendation type is "rest_day"
    And the recommendation advises recovery and processing the training stimulus

  Scenario: Recommend workout execution when recovered after two days
    Given a runner whose last run was two days ago with restored positive form
    When the daily training recommendation is calculated for today
    Then the recommendation type is "workout_day"
    And the recommendation advises executing the planned aerobic base workout

  Scenario: Recommend gentle re-entry after an extended break of eight days
    Given a runner whose last activity was eight days ago
    When the daily training recommendation is calculated for today
    Then the recommendation type is "reentry_run"
    And the recommended duration is reduced to 25-30 minutes for safe re-entry

  Scenario: Render daily recommendation card in the dashboard
    Given a daily recommendation with type "rest_day"
    When the HTML dashboard is generated
    Then the dashboard contains a Daily Recommendation card with appropriate action badge
