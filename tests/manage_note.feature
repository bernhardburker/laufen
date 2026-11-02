Feature: Subjective run notes and athlete feedback management
  As a runner or coach
  I want to attach subjective comments, feel, and RPE to running activities
  So that qualitative athlete perception is stored in Intervals.icu and incorporated into 4-stage coach reviews

  Scenario: Attach subjective note and feel score to the latest run
    Given an athlete with recorded running activities
    And a running Intervals.icu mock server
    When note "Schwere Beine in den ersten 15 Minuten, danach flüssig" with feel 3 is attached to the latest run
    Then the latest activity contains athlete note "Schwere Beine in den ersten 15 Minuten, danach flüssig"
    And the latest activity records feel 3
    And the Intervals.icu server receives the updated note for the latest run

  Scenario: Attach subjective note to a specific run by activity ID
    Given an athlete with recorded running activities
    And a running Intervals.icu mock server
    When note "Starke Wadenkrämpfe beim Intervall" with RPE 8 is attached to activity "run-001"
    Then activity "run-001" contains athlete note "Starke Wadenkrämpfe beim Intervall"
    And activity "run-001" records perceived exertion 8

  Scenario: Dry run mode does not mutate local files or external API
    Given an athlete with recorded running activities
    And a running Intervals.icu mock server
    When dry run mode is used to attach note "Test Notiz" to the latest run
    Then local activities file remains unchanged
    And the Intervals.icu server receives no activity update

  Scenario: Attaching note re-evaluates coach review and updates run reviews cache
    Given an athlete with recorded running activities
    And an existing run reviews cache
    When note "Wadenkrämpfe nach 1. Intervall" is attached to the latest run
    Then the run reviews cache contains an updated review for the latest run
    And the updated review includes the athlete note "Wadenkrämpfe nach 1. Intervall"

  Scenario: Fail gracefully when specified activity ID is not found
    Given an athlete with recorded running activities
    When attempting to attach note "Geisterlauf" to non-existent activity "invalid-999"
    Then the process aborts with an activity not found error

  Scenario: Execute note command via central CLI runner
    Given an athlete with recorded running activities
    When the CLI runner is executed with note "Guter Dauerlauf am Morgen"
    Then the CLI runner exits successfully
    And the latest activity contains athlete note "Guter Dauerlauf am Morgen"
