Feature: System environment inspection and path initialization
  As a developer or automation agent
  I want an initialization script that verifies the system and exports vital executable paths
  So that I can reliably execute Python and testing tools without manual path discovery

  Scenario: Successfully initialize paths when virtual environment exists
    Given a workspace directory
    And an existing virtual environment with Python and Pytest
    When the initialization script is executed
    Then the process exits with code 0
    And the shell paths file is created
    And the shell paths file exports the Python and Pytest executables
    And the json paths file is created
    And the json paths file contains the absolute paths to Python and Pytest

  Scenario: Sourced shell paths file provides working Python and Pytest commands
    Given a workspace directory
    And an existing virtual environment with Python and Pytest
    When the initialization script is executed
    Then sourcing the shell paths file in bash makes Python and Pytest accessible in PATH

  Scenario: Automatically create virtual environment when missing
    Given a workspace directory without a virtual environment
    When the initialization script is executed
    Then the process exits with code 0
    And a new virtual environment is created
    And the shell paths file is created
    And the json paths file is created

  Scenario: Validate environment in check-only mode without file mutations
    Given a workspace directory
    And an existing virtual environment with Python and Pytest
    When the initialization script is executed with check-only mode
    Then the process exits with code 0
    And the shell paths file is not created
    And the json paths file is not created
    And the output displays the system inspection summary

  Scenario: Fail gracefully when system Python interpreter is missing or invalid
    Given an invalid custom Python interpreter path
    When the initialization script is executed with the invalid Python path
    Then the process exits with an error
    And the error output mentions "Python not found"
