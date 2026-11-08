# Valorant hip-fire calibration plan

The player enters the hip-fire sensitivity they use in Valorant and keeps the same mouse and hardware DPI. The recommendation changes their Raw Accel profile. Success means a repeatable first-shot improvement on matched targets.

| Work | Owner | Acceptance check |
| --- | --- | --- |
| Model the view and first shot | Terra aiming agent | Raw mouse counts rotate the camera once. A click scores the target at that instant. A later correction cannot recover a miss. |
| Compare settings | Terra scoring agent | Test small corrections, short flicks and wide turns. Keep misses and timeouts in the denominator. Protect close-shot accuracy when assessing faster turns. |
| Integrate with calibration | Terra UI agent | Offer desktop and Valorant modes, conceal the trial profile during comparison, run a fresh confirmation, and allow explicit apply and restore. |
| Verify the combined workflow | Supervising agent | Build the GUI, exercise the form against a test driver, run the existing regression suites, and check cancellation, confirmation and persistence. |

## Validate transfer to Valorant with players

1. On day one, record both Raw Accel profiles, the in-game hip-fire sensitivity, mouse DPI and the calibration result. Include sessions where the app recommended keeping the original.
2. On another day, use the same Range exercise, weapon, distance, stationary bots and warm-up for both profiles. Fire one hip-fire shot per target. Give each profile equal practice and equal numbers of scored attempts.
3. Alternate profiles across blocks. Reverse the starting order across players and keep profile identities concealed where possible. Count every first shot, including misses. Record hits out of attempts and time separately.
4. Compare each player's profiles on the same exercise. Report how often the in-app winner also wins in the Range, how often results are inconclusive, and any losses in close-shot accuracy. Use that evidence to revisit the targets and decision thresholds before making an in-game performance claim.
