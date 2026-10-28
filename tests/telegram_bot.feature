Feature: Bidirectional Telegram Coach Bot and Daily Briefing

  Scenario: Athlete links their Telegram Chat ID via /start command
    Given the athlete registry contains athlete "berni" with intervals id "i697752"
    When the Telegram bot receives message "/start berni" from chat id "987654321"
    Then the athlete "berni" has linked Telegram chat id "987654321" in the registry
    And the last Telegram message sent to "987654321" contains "Erfolgreich verknüpft mit **Berni**"

  Scenario: Proactive daily briefing auto-syncs workout to Intervals.icu and sends Telegram proposal
    Given the athlete "berni" is linked to Telegram chat id "987654321" with briefing time "07:00"
    And the athlete "berni" last ran 2 days ago before "2026-10-26"
    When the daily briefing is triggered for athlete "berni" on "2026-10-26"
    Then a workout is scheduled on Intervals.icu for "2026-10-26"
    And the last Telegram message sent to "987654321" contains "Guten Morgen Berni"
    And the last Telegram message sent to "987654321" includes inline keyboard buttons

  Scenario: Athlete modifies scheduled workout via natural language text message (short on time and tired)
    Given the athlete "berni" is linked to Telegram chat id "987654321" with briefing time "07:00"
    And the athlete "berni" has an active briefing proposal on "2026-10-26"
    When the Telegram bot receives message "Habe heute nur 35 min Zeit und fühle mich schlapp" from chat id "987654321"
    Then the previous workout on "2026-10-26" is replaced on Intervals.icu with a workout containing "35m"
    And the last Telegram message sent to "987654321" contains "35m"

  Scenario: Athlete switches today's workout to a rest day via inline button click
    Given the athlete "berni" is linked to Telegram chat id "987654321" with briefing time "07:00"
    And the athlete "berni" has an active briefing proposal on "2026-10-26"
    When the Telegram bot receives callback query "rest:2026-10-26" from chat id "987654321"
    Then the planned workout on "2026-10-26" is deleted from Intervals.icu
    And a rest day note is created on Intervals.icu for "2026-10-26"
    And the last Telegram message sent to "987654321" contains "Ruhetag"

  Scenario: Rest day briefing presents dedicated rest day options and preserves tomorrow plan
    Given the athlete "berni" is linked to Telegram chat id "987654321" with briefing time "07:00"
    And the athlete "berni" last ran 1 days ago before "2026-10-26"
    When the daily briefing is triggered for athlete "berni" on "2026-10-26"
    Then the last Telegram message sent to "987654321" contains "Ruhetag"
    And the last Telegram message sent to "987654321" includes rest day keyboard buttons
    When the Telegram bot receives callback query "keep_rest:2026-10-26" from chat id "987654321"
    Then a workout is scheduled on Intervals.icu for "2026-10-27"
    And the last Telegram message sent to "987654321" contains "Ruhetag"

  Scenario: Scheduled briefing check sends briefing only once when time is reached
    Given the athlete "berni" is linked to Telegram chat id "987654321" with briefing time "07:00"
    And the athlete "berni" last ran 2 days ago before "2026-10-26"
    When the scheduled briefing check runs at "2026-10-26T07:05:00"
    And the scheduled briefing check runs again at "2026-10-26T07:15:00"
    Then exactly 1 Telegram message was sent to "987654321"

  Scenario: Athlete refreshes sleep data via inline button click when data becomes available
    Given the athlete "berni" is linked to Telegram chat id "987654321" with briefing time "07:00"
    And the athlete "berni" has an active briefing proposal on "2026-10-26"
    When the Telegram bot receives callback query "refresh_sleep:2026-10-26" from chat id "987654321"
    Then the last Telegram message sent to "987654321" contains "Schlaf"
