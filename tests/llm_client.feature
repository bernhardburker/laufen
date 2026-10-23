Feature: Large Language Model Client Abstraction Layer
  As an AI coach developer and automation engineer
  I want a unified LLM abstraction layer supporting multiple providers
  So that I can execute coaching prompts reliably via agy CLI, direct API, or stub clients for testing

  Scenario: Mock LLM client generates configured text and records prompts
    Given a mock LLM client configured with a structured response
    When the mock LLM client is invoked with a coaching prompt
    Then the generation succeeds with status true
    And the generated text matches the configured response
    And the mock client recorded the prompt

  Scenario: Mock LLM client simulates failure and errors
    Given a mock LLM client configured to fail with error "API rate limit exceeded"
    When the mock LLM client is invoked with a coaching prompt
    Then the generation fails with status false
    And the error message contains "API rate limit exceeded"

  Scenario: Mock LLM client simulates timeout
    Given a mock LLM client configured to simulate timeout
    When the mock LLM client is invoked with a coaching prompt
    Then the generation fails with status false
    And the error message contains "timed out"

  Scenario: Agy LLM client executes binary and captures output
    Given a mock agy executable that echoes structured json
    And an agy LLM client pointing to the mock executable
    When the agy client generates a response for a prompt
    Then the generation succeeds with status true
    And the response contains valid json

  Scenario: Agy LLM client inlines file artifact links
    Given a mock agy executable that outputs a file artifact link
    And an agy LLM client pointing to the mock executable
    When the agy client generates a response for a prompt
    Then the generation succeeds with status true
    And the response text matches the file artifact content

  Scenario: Agy LLM client handles missing binary gracefully
    Given an agy LLM client pointing to a non-existent binary "/nonexistent/agy"
    When the agy client generates a response for a prompt
    Then the generation fails with status false
    And the error message contains "binary not found"

  Scenario: Factory creates correct client based on provider name
    When the LLM client factory is asked for provider "mock"
    Then the resulting client is a MockLLMClient
    When the LLM client factory is asked for provider "agy"
    Then the resulting client is an AgyLLMClient
    When the LLM client factory is asked for provider "gemini"
    Then the resulting client is a GeminiAPIClient
