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

  Scenario: Dashboard generator overrides summary when dynamic AI coach summary is provided
    Given an activities dataset with multiple running activities
    And the standard dashboard template is available
    And an AI coach summary JSON file with custom dynamic insights
    When the dashboard generator is executed with the AI coach summary file
    Then the process exits with code 0
    And the output HTML contains the custom dynamic AI insights

  Scenario: Dashboard contains interactive date range filters, sorting controls, and dataset transparency
    Given an activities dataset with multiple running activities and strava placeholders
    And the standard dashboard template is available
    When the dashboard generator is executed
    Then the process exits with code 0
    And the output HTML contains interactive date range filter buttons
    And the output HTML contains sortable table columns and pagination controls
    And the output HTML displays the data source transparency notice
    And the output HTML contains responsive layout styling for date range filters

  Scenario: Dashboard volume chart retains forecast progression across date range filters
    Given an activities dataset with multiple running activities
    And the standard dashboard template is available
    When the dashboard generator is executed
    Then the process exits with code 0
    And the output HTML volume chart preserves forecast visualization across date filters

  Scenario: Dashboard contains reload controls and mobile refresh mechanisms for iOS standalone web app
    Given an activities dataset with multiple running activities
    And the standard dashboard template is available
    When the dashboard generator is executed
    Then the process exits with code 0
    And the output HTML contains reload controls and mobile refresh mechanisms

  Scenario: Dashboard contains dynamically calibrated heart rate zones from athlete profile
    Given an activities dataset with multiple running activities
    And the standard dashboard template is available
    And an athlete profile with custom heart rate zones
    When the dashboard generator is executed with the athlete profile
    Then the process exits with code 0
    And the output HTML contains the calibrated zone ranges

  Scenario: Dashboard contains responsive mobile layout and expandable run analysis cards
    Given an activities dataset with multiple running activities and coach reviews
    And the standard dashboard template is available
    When the dashboard generator is executed with reviews data
    Then the process exits with code 0
    And the output HTML contains responsive mobile styling for activity cards
    And the output HTML contains expandable run review details
    And the output HTML contains compact two-column KPI grid for mobile


