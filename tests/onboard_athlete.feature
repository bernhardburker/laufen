Feature: Multi-tenant athlete onboarding and management
  As a coach or system administrator
  I want to onboard new athletes and inspect registered athletes
  So that multiple athletes can be managed with isolated tenant configurations and dashboards

  Scenario: Display overview table of all registered athletes
    Given a clean registry with test athletes
    When the athlete summary is displayed
    Then the output contains registered athlete slugs
    And the output displays table headers for slug, name, and intervals ID

  Scenario: Onboard new athlete in dry-run mode
    Given an isolated project workspace
    When onboarding athlete "max" with Intervals ID "i12345" in dry run mode
    Then the tenant directory for "max" is not created
    And the registry file remains unchanged

  Scenario: Onboard new athlete with mock Intervals server initializes tenant and updates registry
    Given an isolated project workspace
    And a running Intervals.icu mock server
    When onboarding athlete "clara" with Intervals ID "i98765" and name "Clara Runner"
    Then a tenant directory for "clara" is created
    And the tenant athlete config contains display name "Clara Runner"
    And the registry records "clara" with Intervals ID "i98765"
    And "clara" is assigned a privacy dashboard slug

  Scenario: Fail onboarding when Intervals ID is not provided
    Given an isolated project workspace
    When onboarding athlete "incomplete" without an Intervals ID
    Then the process aborts with an intervals ID required error

  Scenario: Execute athletes command via central CLI runner
    When the CLI runner is executed with "athletes"
    Then the CLI runner exits successfully
    And the output lists registered athletes
