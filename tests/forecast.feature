Feature: Race predictions, volume forecasting, and training recommendations
  As a runner
  I want automated forecasting and coaching recommendations based on my activities
  So that I can plan my training progression and estimate race performance

  Scenario: Predict race times across standard distances
    Given an activities dataset with multiple running activities
    When race time predictions are calculated
    Then race predictions are returned for standard distances
    And each prediction includes target pace, predicted time, and readiness note

  Scenario: Generate progressive volume forecast for upcoming weeks
    Given weekly training trends with recent running activities
    When training volume forecast is generated for 4 weeks
    Then 4 progressive weekly volume targets are produced
    And the progression includes a deload or recovery week

  Scenario: Synthesize training summary and coaching recommendations
    Given an activities dataset with multiple running activities
    When training summary and recommendations are generated
    Then the summary identifies training status and key observations
    And actionable recommendations are provided for zone discipline and volume

  Scenario: Prepare coaching prompt for Antigravity AI Coach
    Given an activities dataset with multiple running activities
    When an AI coach prompt is constructed
    Then the prompt includes athlete heart rate zones and recent runs
    And the prompt instructs the AI coach on 80/20 zone discipline

