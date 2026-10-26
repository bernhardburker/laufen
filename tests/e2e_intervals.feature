@e2e
Feature: Intervals.icu live API end-to-end integration
  As a developer and running enthusiast
  I want to verify real REST API connectivity against the Intervals.icu staging athlete
  So that API drifts, authentication errors, and payload incompatibilities are caught before production runs

  Scenario: Verify staging athlete profile retrieval
    Given a configured Intervals.icu API key
    And target staging athlete is "berni-test"
    When the athlete profile is retrieved from the live Intervals.icu API
    Then the response contains the athlete id "i771119"
    And the response contains heart rate zones with LTHR greater than 160

  Scenario: Verify workout event lifecycle on staging calendar
    Given a configured Intervals.icu API key
    And target staging athlete is "berni-test"
    When a test workout event is uploaded to the live staging calendar
    Then the event is listed in the staging athlete events
    And the test workout event is successfully deleted during cleanup
