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

  Scenario: Recommend Slot 3 Zone 2 long run on Saturday
    Given a runner with pyramidal hybrid weekly slots configured
    And the runner completed a threshold workout on Thursday
    When the daily training recommendation is calculated for Saturday
    Then the recommendation type is "workout_day"
    And the recommended workout slot is 3
    And the recommended workout type is "easy_run"
    And the recommendation targets Zone 2 heart rate ceiling

  Scenario: Recommend Slot 1 interval workout on Tuesday when weekend run was completed
    Given a runner with pyramidal hybrid weekly slots configured
    And the runner completed a Zone 2 long run on Sunday
    When the daily training recommendation is calculated for Tuesday
    Then the recommendation type is "workout_day"
    And the recommended workout slot is 1
    And the recommended workout type is "intervals"
    And the recommendation badge indicates "Tempo / Speed"

  Scenario: Trigger Slot 1 catch-up Zone 2 run on Monday when weekend run was missed
    Given a runner with pyramidal hybrid weekly slots configured
    And the runner's last run was on Friday without a weekend run
    When the daily training recommendation is calculated for Monday
    Then the recommendation type is "workout_day"
    And the recommended workout slot is 1
    And the recommendation indicates a Zone 2 catch-up base run
    And the recommended target heart rate is 158

  Scenario: Recommend Slot 2 threshold workout on Thursday
    Given a runner with pyramidal hybrid weekly slots configured
    And the runner completed an interval workout on Tuesday
    When the daily training recommendation is calculated for Thursday
    Then the recommendation type is "workout_day"
    And the recommended workout slot is 2
    And the recommended workout type is "tempo_run"
    And the recommendation badge indicates "Schwellenreiz"

  Scenario: Recommend fourth recovery slot in a four-run weekly plan
    Given a runner with a four-slot weekly schedule configured
    And the runner completed a threshold workout on Friday
    When the daily training recommendation is calculated for Saturday
    Then the recommendation type is "workout_day"
    And the recommended workout slot is 4
    And the recommended workout type is "recovery_run"
    And the recommendation badge indicates "Regeneration"

  Scenario: Dynamically alternate quality workout to intervals when last quality was tempo
    Given a runner with pyramidal hybrid weekly slots configured
    And the runner completed a Zone 2 long run on Sunday
    And the runner completed a tempo run earlier
    When the daily training recommendation is calculated for Tuesday
    Then the recommendation type is "workout_day"
    And the recommended workout type is "intervals"

