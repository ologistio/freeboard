## 1. Re-calibrate neutral text tokens (fix(web): neutral text tokens meet AAA)

- [x] 1.1 In `src/Freeboard/assets/css/app.css`, set light `--color-muted` to
  `#464b48` and `--color-faint` to `#4a4f4c` in both the `@theme` block and the
  re-asserted `[data-theme="light"]` block.
- [x] 1.2 In the `[data-theme="dark"]` block set `--color-muted` to `#b5beb8` and
  `--color-faint` to `#a9b2ac`.
- [x] 1.3 Mirror the four new values into `stories/Colors.stories.js`.

## 2. Migrate failing body text to the token (fix(web): body copy uses text-muted)

- [x] 2.1 Replace `text-neutral-600` with `text-muted` on the rendered body text of
  the audited pages under `Pages/Account`, `Pages/Compliance`, and the Settings-hosted
  admin pages (`Users`, `UserCredential`, `CustomRoles`). Do not touch layout classes
  or the non-audited public auth pages.

## 3. Raise the local contrast guard (test(web): neutral text guard at AAA)

- [x] 3.1 In `tests/Freeboard.Web.Tests/ContrastGuardTests.cs`, assert the neutral
  text tokens (`ink`/`muted`/`faint`) clear 7:1 on every ground; keep semantic `-ink`
  and `brand-ink` at 4.5:1 and seals/fills at 3:1.

## 4. Verify

- [x] 4.1 `dotnet build` clean.
- [x] 4.2 `dotnet test tests/Freeboard.Web.Tests` passes, including the raised guard.
- [ ] 4.3 If a Playwright Chromium is available, run
  `FREEBOARD_TEST_E2E=1 dotnet test tests/Freeboard.WebE2E` and confirm zero
  `color-contrast-enhanced` violations; otherwise rely on the computed ratios.
