Feature: Coach memory and adaptive feedback loop
  As a runner
  I want an AI coach that remembers past recommendations and reviews compliance and effect
  So that my training progression is adaptively steered based on real execution

  Scenario: Initialize coach history with cold start from existing runs
    Given an empty coach history repository
    And recent running activities with low aerobic zone discipline
    When coach history is initialized from activity data
    Then an initial baseline entry is created
    And an open recommendation is scheduled for aerobic base building

  Scenario: AI coach prompt incorporates memory of previous recommendation and runs since
    Given an existing coach history with an open recommendation for Zone 2 running
    And new running activities completed after the recommendation date
    When an AI coach prompt is constructed with history context
    Then the prompt contains the previous recommendation text
    And the prompt includes details of the runs completed since the recommendation
    And the prompt instructs the coach to review compliance and physiological effect

  Scenario: Process coach evaluation when athlete complied with Zone 2 recommendation
    Given an existing coach history with an open recommendation for Zone 2 running
    And a coach response indicating successful Zone 2 compliance and positive aerobic effect
    When the coach response is processed into the history
    Then the previous recommendation is marked with status "erfüllt"
    And the entry records the actual runs and measured effect
    And a new open recommendation is appended for the next cycle

  Scenario: Adapt training when athlete executed a tempo run instead of planned easy run
    Given an existing coach history with an open recommendation for Zone 2 running
    And a coach response detecting an unplanned threshold tempo run
    When the coach response is processed into the history
    Then the previous entry is marked with status "angepasst_nach_tempo"
    And the next recommendation prescribes active recovery or easy base training
    And the history remains persistently saved without duplicates

  Scenario: Render coach memory and feedback table in the dashboard
    Given a coach history with multiple past and open coaching cycles
    When the HTML dashboard is generated with coach history data
    Then the dashboard includes a Coach Memory and Feedback section
    And each cycle displays date, recommendation, actual performance, status badge, and effect
