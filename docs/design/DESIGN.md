# PeakMode UI design (v0.2.0)

Reference mockups: `boost.reference.html`, `tweaks.reference.html`, `history.reference.html`,
`checkup.reference.html` (0.3.0).
They were made in a web design tool, so they are HTML, not XAML. Use them for layout, colors,
copy and behavior. The `class Component` script at the bottom of each file shows the exact
state logic of the mockup. Do not ship HTML; rebuild the UI in WPF.

Live mockup (owner only): https://claude.ai/artifact/EzmNk9XcEjQ536qb5nwC44

## Window

- One main window with a left sidebar (Boost, Tweaks, Checkup, History, Settings) and a content area.
- Opened from the tray icon. The tray stays as it is today; closing the window hides it to the tray.
- Min size about 1100 x 720. Dark only.

## Tokens

| Token | Value | Use |
|---|---|---|
| bg | #07080C | window background, faint dot grid (1 px dots, #1A1D27, 22 px spacing) |
| sidebar | #0B0C12 | sidebar background, right border #1C1F2A |
| surface | #0D0F16 | cards, border #1C1F2A |
| surface-hot | #160B12 | card when its tweak is applied, border #5A2140 |
| text | #F3F1EC | primary text |
| muted | #A0A6AF | secondary text, labels |
| faint | #767D8D | tertiary text (was #6B7180, below 4.5:1 on every surface) |
| accent | #FF3D7F | active state, switches, applied |
| accent-soft | #FF8FB5 | accent text on dark |
| amber | #FFB020 | in progress, warnings (network blip, restore pending) |
| green | #5BD18B | restored, SAFE tag |
| heat gradient | #7C5CFF → #FF3D7F → #FFB020 | logo rim, redline ticks, nothing else |

Fonts: Chakra Petch 700 for headings and big numbers, Inter for body, JetBrains Mono for
labels, values and tags (uppercase, letter spacing 1 to 2 px). All three are OFL licensed;
bundle them with the app and add their licenses to third-party notices. WPF has no letter
spacing: `c:Tracking.Spacing` (Controls/Tracking.cs) shifts each character with a TextEffect,
for single-line text only. Tags 1 px (10 px text), labels 1.5 px (11 px), eyebrows 2 px (12 px).

Shape language: cards have one or two cut corners (top right, sometimes bottom left),
not rounded corners. Switches are pill shaped. In WPF: `c:CutCorners.TopRight` /
`c:CutCorners.BottomLeft` (Controls/CutCorners.cs), a clip like the mockups' `clip-path`.
Sizes from the mockups: hero 28 + 28, history detail 22, banner 16, module and stat
cards and tweak rows 14, sidebar version box 12.

## Screen 1: Boost

- Header: "PROFILE / GAMING" label, big title (READY TO BOOST / BOOSTING... / BOOST ACTIVE /
  RESTORING...), status chip on the right (IDLE / APPLYING / ACTIVE / RESTORING).
- Hero: the logo as a big button (about 290 px). It is the same drawing as assets/peakmode logo:
  hexagon with heat-gradient rim, inner hex line, 13 ticks from -120 to +120 degrees every 20,
  grey ticks below +40, thicker redline ticks (pink to amber, with glow) from +40 to +120,
  white needle with glow, small dark pivot ring with gradient stroke. Label under the pivot
  inside the hexagon: BOOST / BOOSTING / ACTIVE / RESTORING.
- Under the button: "<n> / <total> SETTINGS APPLIED" counter, then one line of hint text.
- Right of the hero: 4 stat cards (Changes applied, Snapshot, Power plan, Timer).
- Below: "TWEAKS IN THIS PROFILE" grid of module cards with include/skip switch and a status
  tag: READY, QUEUED, APPLYING (amber, blinking), APPLIED (pink), RESTORING, RESTORED, SKIPPED.

### Boost animation (the important part)

Drive it from real progress, not a timer. The core already applies modules one at a time;
report progress per module (callback or polling) so the UI moves when the core moves.

1. Idle: needle at -120 degrees, all ticks dim.
2. Each module applied: needle rotates to `-120 + 215 * done / total` degrees with an
   overshoot ease (like a throttle blip, about 450 ms, overshoot ~15%). Ticks up to the
   needle light up (grey ticks turn #E9E7F2, redline ticks turn on with glow). Counter and
   stat cards update. The hexagon shakes slightly (about 0.6 px) while applying.
3. All applied = revving: needle settles around +95 degrees and keeps bouncing irregularly
   between about +92 and +118 (like a motorcycle being revved; keyframes roughly
   +19, +9, +23, +3, +17, -3, +21, +6, 0 over 1.4 s, repeat). Redline ticks flicker.
   The hexagon pulses a pink glow and vibrates very slightly.
4. Restore: needle steps back down module by module in reverse order, ticks go out, tags show
   RESTORING then RESTORED, back to idle.
5. Respect Windows "Show animations" (SystemParameters.ClientAreaAnimation): if off, jump
   between states without the rev loop.

## Screen 2: Tweaks

- Profile pills at the top (GAMING, + NEW PROFILE; the second can be disabled for now).
- 3 summary counters: modules on, settings saved before boost, modules that need a network restart.
- One row per module: icon, name, one-line description, risk tag (SAFE green, NETWORK BLIP amber),
  value count, expand chevron, include switch. Expanded: what changes, how it is restored, what
  it needs. Process priority row has a target process text box (game.exe).
- All data comes from the JSON profile. Switches write back to it. Disabled while active.
- Replace the placeholder text in the mockup with the real values from the code (registry value
  names, NVIDIA setting names).

## Screen 3: History

- Restore-pending banner (amber) when a snapshot from a previous session exists:
  "PeakMode was still active when it last closed", RESTORE NOW and Later.
- Left: list of sessions (when, settings count, how it ended), badge CLEAN or AFTER CRASH.
- Right: selected session: active for, restored by, result; then snapshot entries in order,
  before → during, green check when restored.
- Needs a small session log (append-only JSON in C:\ProgramData\PeakMode, same ACL as the
  snapshot). Never store anything outside that folder.

## Screen 4: PC Checkup (0.3.0)

Reference: `checkup.reference.html` (static HTML, no state script). What each check reads and
the status rules: [../checkup.md](../checkup.md).

- Sidebar item after Tweaks. Eyebrow "READ ONLY · NOTHING IS CHANGED", title PC CHECKUP,
  RE-CHECK button on the right.
- 3 summary counters like Tweaks: OK (green number), WORTH CHECKING (amber when above 0), INFO
  (muted). Under them: "Checked HH:mm. Read only: nothing here changes your PC."
- One row per check, same card as a tweak row (cut corner 14): icon, name, one-line detail,
  value (mono, right), status tag, expand chevron. Tags: OK green, WORTH CHECKING amber, INFO
  muted with the strong border.
- HOW TO FIX: amber left bar on the amber background, amber-text body. Always shown when the
  row has a fix, open or not. Rows that are worth checking start open.
- Expanded: a neutral note (dim surface, strong-border left bar, muted label, soft text; ON
  LAPTOPS or ABOUT), then key/value lines like the Tweaks details (read from, also, can't tell).
  Memory Integrity's note has a MICROSOFT link button.
- Memory Integrity and Temperatures are always INFO. The thermal zone is never shown without
  "not the CPU core temperature".
- The checks run off the UI thread when the page is shown and on RE-CHECK; a row keeps its
  open state while its status stays the same.

## Accessibility

Every control is a real button or toggle with an automation name. Text contrast 4.5:1 or more.
The text tokens (text, muted, faint, accent, accent-soft, amber, green) pass on bg, sidebar,
surface and surface-hot; text, amber and the banner text pass on the amber banner background.
Faint is the exception there: it is not used on the banner. `ContrastTests` in
`ui/PeakModeTray.Tests` checks these pairs against `Themes/Tokens.xaml` on every build. Keyboard: Tab through sidebar, hero button,
switches; Space or Enter activates.
