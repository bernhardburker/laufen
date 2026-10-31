Feature: Documentation and Data Contract Drift Prevention
  As an AI coding agent or maintainer
  I want automated checks verifying documentation links, CLI command parity, workout templates, and executable data contracts
  So that repository documentation and inter-module schemas never silently drift out of sync with the codebase

  Scenario: All markdown file links in active documentation use relative paths and resolve to existing workspace paths
    Given the active repository documentation files
    When all file links and workspace path references are inspected
    Then every referenced file path exists in the workspace
    And no document contains absolute filesystem paths or file URI schemes

  Scenario: Central CLI runner help and README cover all implemented subcommands
    Given the central CLI runner script "./run"
    When implemented subcommands are compared against "./run help" and "README.md"
    Then every primary subcommand is documented in the help output
    And every primary subcommand is documented in "README.md"

  Scenario: All workout DSL templates are documented in system specification and scheduler skill
    Given the workout templates directory "workouts/templates"
    When template names are compared against "docs/SYSTEM_SPEC.md" and ".agents/skills/workout-scheduler/SKILL.md"
    Then every workout template is documented in both files

  Scenario: Active documentation does not reference superseded plans outside the archive
    Given the active repository documentation files
    When links to specification documents are inspected
    Then no active document links to unarchived "docs/plans/" paths

  Scenario: Core analysis and planning outputs conform to executable data contracts
    Given the executable data contracts in "src/core/contracts.py"
    When sample outputs from the 4-stage run reviewer, planning marker extractor, coach history, and athlete profile are validated
    Then all outputs pass their respective contract validators without errors
