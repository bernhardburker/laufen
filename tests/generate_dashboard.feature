Feature: Generate visual HTML dashboard from running activities
  As a runner
  I want to generate a dynamic HTML dashboard from my training data
  So that I can visualize weekly volume, heart rate zone discipline, and aerobic efficiency trends

  Scenario: Successfully generate dashboard HTML with metrics and charts
    Given an activities dataset with multiple running activities
    And the standard dashboard template is available
    When the dashboard generator is executed
    Then the process exits with code 0
    And the output HTML file is created
    And the output HTML contains the rendered KPI metrics
    And the output HTML embeds the weekly trends and activities data

  Scenario: Generate dashboard when dataset contains no running activities
    Given an activities dataset with no running activities
    And the standard dashboard template is available
    When the dashboard generator is executed
    Then the process exits with code 0
    And the output HTML file is created
    And the output HTML displays the empty state message

  Scenario: Generator fails with clear error when input file does not exist
    Given the activities dataset file does not exist
    When the dashboard generator is executed
    Then the process exits with an error
    And the error output mentions "not found"

  Scenario: Generator fails with clear error when template file does not exist
    Given an activities dataset with multiple running activities
    When the dashboard generator is executed with a non-existent template
    Then the process exits with an error
    And the error output mentions "Template not found"

  Scenario: Dashboard contains German localized UI, training recommendations, and forecasts
    Given an activities dataset with multiple running activities
    And the standard dashboard template is available
    When the dashboard generator is executed
    Then the process exits with code 0
    And the output HTML contains German navigation and KPI headers
    And the output HTML contains training summary and coaching recommendations
    And the output HTML embeds race time predictions and volume forecast data

