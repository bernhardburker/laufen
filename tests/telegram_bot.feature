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

  Scenario: Athlete views notification settings and toggles morning briefing via interactive button
    Given the athlete "berni" is linked to Telegram chat id "987654321" with briefing time "07:00"
    When the Telegram bot receives message "/settings" from chat id "987654321"
    Then the last Telegram message sent to "987654321" contains "Benachrichtigungen"
    And the last Telegram message sent to "987654321" includes settings keyboard buttons
    When the Telegram bot receives callback query "toggle:morning" from chat id "987654321"
    Then the athlete "berni" has morning briefing disabled in the registry
    And the last Telegram message sent to "987654321" contains "deaktiviert"

  Scenario: Athlete toggles notifications via direct commands
    Given the athlete "berni" is linked to Telegram chat id "987654321" with briefing time "07:00"
    When the Telegram bot receives message "/morgen off" from chat id "987654321"
    Then the athlete "berni" has morning briefing disabled in the registry
    And the last Telegram message sent to "987654321" contains "Morgen-Check deaktiviert"
    When the Telegram bot receives message "/abend on" from chat id "987654321"
    Then the athlete "berni" has evening briefing enabled in the registry
    And the last Telegram message sent to "987654321" contains "Abend-Vorschau aktiviert"

  Scenario: Scheduled evening preview sends tomorrow workout logistics
    Given the athlete "berni" is linked to Telegram chat id "987654321" with briefing time "07:00"
    And the athlete "berni" has evening briefing enabled with time "20:30"
    And the athlete "berni" last ran 2 days ago before "2026-10-26"
    When the scheduled evening preview check runs at "2026-10-26T20:35:00"
    Then the last Telegram message sent to "987654321" contains "Abend"
    And the last Telegram message sent to "987654321" contains "Vorschau auf morgen"

  Scenario: Post-run coach review focuses on coach assessment and future plan implications
    Given the athlete "berni" is linked to Telegram chat id "987654321" with briefing time "07:00"
    And the athlete "berni" has completed a run "act_100" with name "Lockerer Abendlauf" on "2026-10-26"
    When the post-run review check is triggered for athlete "berni"
    Then the last Telegram message sent to "987654321" contains "Coach-Review"
    And the last Telegram message sent to "987654321" contains "Plan-Erfüllung"
    And the last Telegram message sent to "987654321" contains "Auswirkung auf deinen weiteren Plan"

  Scenario: Post-run coach review is skipped when athlete disabled review notifications
    Given the athlete "berni" is linked to Telegram chat id "987654321" with briefing time "07:00"
    And the athlete "berni" has post run reviews disabled
    And the athlete "berni" has completed a run "act_101" with name "Tempo Run" on "2026-10-26"
    When the post-run review check is triggered for athlete "berni"
    Then exactly 0 Telegram message was sent to "987654321"

  Scenario: Unauthorized user message is rejected when user id whitelist is active
    Given the athlete registry contains athlete "berni" with intervals id "i697752"
    And the Telegram bot has allowed user ids "123456"
    When the Telegram bot receives message "/start berni" from user id "999999" in chat id "999999"
    Then the athlete "berni" does not have linked Telegram chat id "999999" in the registry
    And the last Telegram message sent to "999999" contains "Zugriff verweigert"

  Scenario: Unauthorized callback query is rejected when user id whitelist is active
    Given the athlete "berni" is linked to Telegram chat id "987654321" with briefing time "07:00"
    And the athlete "berni" has an active briefing proposal on "2026-10-26"
    And the Telegram bot has allowed user ids "123456"
    When the Telegram bot receives callback query "rest:2026-10-26" from user id "999999" in chat id "987654321"
    Then the planned workout on "2026-10-26" is not modified on Intervals.icu

  Scenario: Athlete submits subjective run feedback and bot asks for confirmation with matched run
    Given the athlete "berni" is linked to Telegram chat id "987654321" with briefing time "07:00"
    And the athlete "berni" has completed a run "act_200" with name "3x8m Schwellenintervalle" on "2026-10-30"
    When the Telegram bot receives message "heute haben nach dem 1. intervall meine waden begonnen zu krampfen, habe gedehnt und bin langsam ausgelaufen" from chat id "987654321"
    Then the last Telegram message sent to "987654321" contains "Ich habe folgenden Lauf gefunden"
    And the last Telegram message sent to "987654321" contains "3x8m Schwellenintervalle"
    And the last Telegram message sent to "987654321" includes note confirmation keyboard buttons

  Scenario: Athlete confirms matched run note via inline button click
    Given the athlete "berni" is linked to Telegram chat id "987654321" with briefing time "07:00"
    And the athlete "berni" has completed a run "act_200" with name "3x8m Schwellenintervalle" on "2026-10-30"
    And the athlete "berni" has a pending run note for "act_200" with text "Wadenkrämpfe nach 1. Intervall"
    When the Telegram bot receives callback query "note_confirm:act_200" from chat id "987654321"
    Then the activity "act_200" is updated on Intervals.icu with description "Wadenkrämpfe nach 1. Intervall"
    And the last Telegram message sent to "987654321" contains "Notiz erfolgreich"
    And the last Telegram message sent to "987654321" contains "Coach-Update"
