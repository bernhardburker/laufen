Feature: Multi-Athlete Registry and Context Resolution

  Scenario: Resolve default athlete from registry
    Given the athlete registry contains a default athlete "berni"
    When the athlete context is resolved without an explicit athlete argument
    Then the resolved athlete slug is "berni"
    And the athlete base directory points to "athletes/berni"
    And the athlete activities path points to "athletes/berni/intervals_activities.json"

  Scenario: Resolve specific athlete from registry by slug
    Given the athlete registry contains athlete "anna" with intervals id "i849201"
    When the athlete context is resolved for athlete "anna"
    Then the resolved athlete slug is "anna"
    And the resolved athlete intervals id is "i849201"
    And the dashboard slug is "anna-7f9c2a"

  Scenario: Resolve athlete via environment variable
    Given the environment variable "ATHLETE_SLUG" is set to "anna"
    And the athlete registry contains athlete "anna" with intervals id "i849201"
    When the athlete context is resolved without an explicit athlete argument
    Then the resolved athlete slug is "anna"

  Scenario: Fallback to legacy single-tenant directory when registry is missing
    Given the athlete registry does not exist
    When the athlete context is resolved with fallback enabled
    Then the athlete activities path points to "data/intervals_activities.json"
    And the athlete config path points to "config/athlete.json"

  Scenario: List all active registered athletes
    Given the athlete registry contains active athlete "berni" and inactive athlete "archived_user"
    When active registered athletes are listed
    Then the list contains "berni"
    And the list does not contain "archived_user"

  Scenario: Onboard a new athlete into the registry
    Given a clean workspace without athlete "marcus"
    When the athlete "marcus" is onboarded with intervals id "i9999" and name "Marcus"
    Then the athlete registry contains athlete "marcus" with intervals id "i9999"

  Scenario: Format shell environment exports for runner integration
    Given the athlete registry contains a default athlete "berni"
    When shell environment is generated for athlete "berni"
    Then the shell export output contains 'export ATHLETE_SLUG="berni"'
    And the shell export output contains 'export INTERVALS_ATHLETE_ID="i697752"'
