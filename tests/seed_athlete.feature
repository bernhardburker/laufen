Feature: Synthetic test data seeder for staging athletes
  As a developer
  I want to seed synthetic wellness and workout records for test athletes
  So that I can verify coaching and planner behaviors without polluting production data

  Scenario: Seeding generation generates balanced scenario records
    Given target athlete is "berni-test"
    When synthetic wellness records are generated for 7 days under "balanced" scenario
    Then 7 daily wellness records are returned
    And average HRV is within normal range between 55 and 75

  Scenario: Safety abort protects production athletes from being seeded
    Given target athlete is "berni"
    When seed athlete process is attempted on the production athlete
    Then the process aborts with a safety violation error
