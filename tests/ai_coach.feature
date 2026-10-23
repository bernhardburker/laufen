Feature: Agentic AI Running Coach with Abstracted LLM Pipeline
  As an endurance runner and coach
  I want an automated AI Coach engine driven by an abstracted LLM client
  So that I receive data-driven, physiologically sound training feedback, structured summaries, and adaptive memory updates

  Scenario: Generate cold start prompt when no activities exist
    Given an empty activities dataset
    And an athlete profile for "Berni"
    When the AI coach prompt is generated
    Then the prompt contains "noch keine Laufaktivitäten"
    And the prompt addresses athlete "Berni"

  Scenario: Generate adaptive coaching prompt with runs and memory feedback loop
    Given an activities dataset with multiple running activities
    And an athlete profile for "Berni" with max Z2 heart rate 148
    And an active coach memory with an open recommendation from "2026-10-01"
    When the AI coach prompt is generated
    Then the prompt contains athlete display name "Berni"
    And the prompt contains the previous recommendation text
    And the prompt contains heart rate threshold "148"
    And the prompt asks for valid JSON output

  Scenario: Successfully execute AI coach pipeline with mocked LLM client
    Given an activities dataset with multiple running activities
    And an athlete profile for "Berni"
    And an active coach memory with an open recommendation from "2026-10-01"
    And a mock LLM client providing a valid structured coaching response
    When the AI coach engine is executed
    Then the execution status is "success"
    And the structured summary JSON file is created and contains status title "Solider Grundlagenaufbau"
    And the extracted commentary Markdown file is created and contains "Guter Dauerlauf"
    And the coach history is updated with the new cycle recommendation

  Scenario: Parse coach response wrapped in markdown code fence
    Given a raw LLM output wrapped in markdown json fences
    When the raw response is parsed by the coach engine
    Then the parsed object is valid and contains status title "Disziplinierter Grundlagenlauf"

  Scenario: Graceful fallback when LLM returns invalid JSON
    Given an activities dataset with multiple running activities
    And an athlete profile for "Berni"
    And an active coach memory with an open recommendation from "2026-10-01"
    And a mock LLM client providing unparseable malformed text
    When the AI coach engine is executed
    Then the execution status is "fallback"
    And the open cycle in coach history remains open

  Scenario: Graceful fallback when LLM execution fails with error
    Given an activities dataset with multiple running activities
    And an athlete profile for "Berni"
    And an active coach memory with an open recommendation from "2026-10-01"
    And a mock LLM client configured to fail with error "Model execution timeout"
    When the AI coach engine is executed
    Then the execution status is "fallback"
    And the error details mention "timeout"
    And heuristic per-run reviews are updated in the cache
