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

  Scenario: Scheduled evening preview announces rest day when athlete completed run today
    Given the athlete "berni" is linked to Telegram chat id "987654321" with briefing time "07:00"
    And the athlete "berni" has evening briefing enabled with time "20:30"
    And the athlete "berni" last ran 0 days ago before "2026-10-26"
    When the scheduled evening preview check runs at "2026-10-26T20:35:00"
    Then the last Telegram message sent to "987654321" contains "Abend"
    And the last Telegram message sent to "987654321" contains "Ruhetag"
    And the last Telegram message sent to "987654321" includes rest day keyboard buttons

  Scenario: Evening preview preserves uncompleted workout of today and schedules tomorrow
    Given the athlete "berni" is linked to Telegram chat id "987654321" with briefing time "07:00"
    And the athlete "berni" has evening briefing enabled with time "20:30"
    And the athlete "berni" last ran 2 days ago before "2026-10-26"
    And an uncompleted workout exists on Intervals.icu for "2026-10-26"
    When the scheduled evening preview check runs at "2026-10-26T20:35:00"
    Then the planned workout on "2026-10-26" is not deleted from Intervals.icu
    And a workout is scheduled on Intervals.icu for "2026-10-27"
    And the last Telegram message sent to "987654321" contains "Vorschau auf morgen"
    And the last Telegram message sent to "987654321" contains "Geplantes Training"

  Scenario: Post-run coach review focuses on coach assessment and future plan implications
    Given the athlete "berni" is linked to Telegram chat id "987654321" with briefing time "07:00"
    And the athlete "berni" has completed a run "act_100" with name "Lockerer Abendlauf" on "2026-10-26"
    When the post-run review check is triggered for athlete "berni"
    Then the last Telegram message sent to "987654321" contains "Coach-Review: Lockerer Abendlauf"
    And the last Telegram message sent to "987654321" contains "Plan-Erfüllung"
    And the last Telegram message sent to "987654321" contains "Erholungsfenster: ca. 24 Stunden"
    And the last Telegram message sent to "987654321" contains "Coach-Beobachtung & Hebel"
    And the last Telegram message sent to "987654321" contains "Auswirkung auf deinen weiteren Plan"
    And the last Telegram message sent to "987654321" contains "Slot 2"

  Scenario: Post-run coach review dynamically reflects cramps and 48 hour recovery
    Given the athlete "berni" is linked to Telegram chat id "987654321" with briefing time "07:00"
    And the athlete "berni" has completed a run "act_102" with name "3x8m Schwellenintervalle" on "2026-10-26"
    And the athlete "berni" has run note "Wadenkrämpfe nach erstem Intervall" on activity "act_102"
    When the post-run review check is triggered for athlete "berni"
    Then the last Telegram message sent to "987654321" contains "Erholungsfenster: ca. 48 Stunden"
    And the last Telegram message sent to "987654321" contains "Waden"
    And the last Telegram message sent to "987654321" contains "recovery_run"

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

  Scenario: Morning briefing is idempotent and not sent twice on the same day unless forced
    Given the athlete "berni" is linked to Telegram chat id "987654321" with briefing time "07:00"
    When the scheduled briefing check runs at "2026-10-26T07:05:00"
    Then the last Telegram message sent to "987654321" contains "Guten Morgen"
    When the daily briefing is triggered manually for athlete "berni" on "2026-10-26"
    Then exactly 1 Telegram message was sent to "987654321"
    When the daily briefing is triggered with force for athlete "berni" on "2026-10-26"
    Then exactly 2 Telegram messages were sent to "987654321"

  Scenario: Post-run review is not sent for yesterday run during morning briefing check
    Given the athlete "berni" is linked to Telegram chat id "987654321" with briefing time "07:00"
    And the athlete "berni" has completed a run "act_old_1" with name "Gestriger Abendlauf" on "2026-10-25"
    When the post-run review check runs at "2026-10-26T07:00:05"
    Then exactly 0 Telegram message was sent to "987654321"

  Scenario: Post-run review polls fresh activities and sends review for today run
    Given the athlete "berni" is linked to Telegram chat id "987654321" with briefing time "07:00"
    And Intervals.icu has a new completed run "act_new_2" with name "Heutiger Nachmittagslauf" on "2026-10-26T17:30:00" for "berni"
    When the scheduled post-run review check runs at "2026-10-26T17:45:00"
    Then the last Telegram message sent to "987654321" contains "Coach-Review"
    And the last Telegram message sent to "987654321" contains "Heutiger Nachmittagslauf"

  Scenario: Athlete picks another run to assign subjective note
    Given the athlete "berni" is linked to Telegram chat id "987654321" with briefing time "07:00"
    And the athlete "berni" has completed a run "act_101" with name "Intervalle" on "2026-10-28"
    And the athlete "berni" has completed a run "act_102" with name "Dauerlauf" on "2026-10-29"
    When the Telegram bot receives callback query "note_pick_other" from chat id "987654321"
    Then the last Telegram message sent to "987654321" contains "Welchem Lauf möchtest du die Notiz zuordnen?"
    And the last Telegram message sent to "987654321" includes inline keyboard buttons

  Scenario: Athlete cancels pending run note assignment
    Given the athlete "berni" is linked to Telegram chat id "987654321" with briefing time "07:00"
    When the Telegram bot receives callback query "note_cancel" from chat id "987654321"
    Then the last Telegram message sent to "987654321" contains "Notiz wurde verworfen"

  Scenario: Athlete requests on-demand evening preview via command
    Given the athlete "berni" is linked to Telegram chat id "987654321" with briefing time "07:00"
    When the Telegram bot receives message "/abend" from chat id "987654321"
    Then the last Telegram message sent to "987654321" contains "Guten Abend"

  Scenario: Athlete requests on-demand post-run review via command
    Given the athlete "berni" is linked to Telegram chat id "987654321" with briefing time "07:00"
    And the athlete "berni" has completed a run "act_301" with name "Morgenrunde" on "2026-10-30"
    When the Telegram bot receives message "/review" from chat id "987654321"
    Then the last Telegram message sent to "987654321" contains "Coach-Review"

  Scenario: Athlete requests on-demand Macro AI Coach commentary via /coach command
    Given the athlete "berni" is linked to Telegram chat id "987654321" with briefing time "07:00"
    And the athlete "berni" has AI coach commentary available
    When the Telegram bot receives message "/coach" from chat id "987654321"
    Then the last Telegram message sent to "987654321" contains "AI Coach"
    And the last Telegram message sent to "987654321" contains "Wochenkompass"

  Scenario: Athlete asks for workout adaptation for commute with short intervals and is not intercepted as run note
    Given the athlete "berni" is linked to Telegram chat id "987654321" with briefing time "07:00"
    And the athlete "berni" has an active briefing proposal on "2026-10-26"
    When the Telegram bot receives message "Ich glaube ich würde lieber mal kürzere Intervalle probieren. Das Problem für morgen ist eher, dass ich ca 7km Strecke habe. Ich kann zwar abkürzen und mit der ubahn weiter in die Arbeit fahren aber es wäre cool wenn da ein Training hineinpassen würde was auch nicht länger als eine Stunde dauert" from chat id "987654321"
    Then the last Telegram message sent to "987654321" does not contain "Ich habe folgenden Lauf gefunden"
    And the last Telegram message sent to "987654321" contains "Speed- & Kadenztraining"

  Scenario: Thin event router in dispatch mode routes inline button clicks and text feedback to coach-plan.yml
    Given the athlete "berni" is linked to Telegram chat id "987654321" with briefing time "07:00"
    And the athlete "berni" has an active briefing proposal on "2026-10-26"
    And the Telegram bot runs in GitHub Actions dispatch mode
    When the Telegram bot receives callback query "accept_b:2026-10-26" from chat id "987654321"
    Then the last Telegram message sent to "987654321" contains "GitHub Actions"
    And the workflow "coach-plan.yml" was dispatched for athlete "berni" with mode "adhoc" and action "accept_b"
    When the Telegram bot receives message "Habe morgen nur 30 min Zeit" from chat id "987654321"
    Then the workflow "coach-plan.yml" was dispatched for athlete "berni" with mode "adhoc" and feedback "Habe morgen nur 30 min Zeit"

  Scenario: Thin event router in dispatch mode routes new run detection to post-run-review.yml exactly once
    Given the athlete "berni" is linked to Telegram chat id "987654321" with briefing time "07:00"
    And the Telegram bot runs in GitHub Actions dispatch mode
    And Intervals.icu has a new completed run "act_disp_99" with name "Schwellenlauf" on "2026-10-26T17:30:00" for "berni"
    When the scheduled post-run review check runs at "2026-10-26T17:45:00"
    And the scheduled post-run review check runs at "2026-10-26T17:55:00"
    Then the workflow "post-run-review.yml" was dispatched exactly 1 time for run "act_disp_99"

  Scenario: Thin event router in dispatch mode dispatches morning briefing without blocking downstream Telegram delivery
    Given the athlete "berni" is linked to Telegram chat id "987654321" with briefing time "07:00"
    And the athlete "berni" last ran 2 days ago before "2026-10-26"
    And the Telegram bot runs in GitHub Actions dispatch mode
    When the scheduled briefing check runs at "2026-10-26T07:05:00"
    And the scheduled briefing check runs again at "2026-10-26T07:10:00"
    Then the workflow "coach-plan.yml" was dispatched exactly 1 time for mode "morning"
    When the daily briefing is triggered manually for athlete "berni" on "2026-10-26"
    Then exactly 1 Telegram message was sent to "987654321"



