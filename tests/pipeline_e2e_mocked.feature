Feature: Full mocked pipeline end-to-end execution
  As a developer
  I want to run the full runner pipeline against a unified local mock API
  So that multi-step automatisms (sync, coach, and dashboard) execute completely offline in CI

  Scenario: Execute complete sync, AI coach, and report pipeline with mocked services
    Given a running unified mock Intervals.icu server
    And an isolated athlete environment configured for "berni-test"
    And LLM provider is configured to "mock"
    When the full sync pipeline runs for "berni-test" against the mock server
    And the AI coach analysis runs with mocked LLM
    Then athlete profile, activities, and wellness records are stored locally
    And coach summary artifact is generated
    And the mock Intervals.icu server received activity and wellness requests
