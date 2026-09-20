# ADR 0013 — Scenes, overlays, branding and keyboard control

**Status:** Accepted
**Date:** 2026-09-06
**Phase:** 5 — Professional Live Studio

The production tools that sit on top of the compositor: prepared shots, captions, a watermark, an
audio mixer, and a keyboard.

## 1. What is configuration and what is a live control

The dividing line runs through this whole phase, and getting it wrong shows up as either data that
should not have been saved or controls that lose their state on reload.

| | Where it lives | Why |
|---|---|---|
| Scenes | Database | Prepared before a show, reused across it |
| Branding | Database | A property of the show, not a decision made in the moment |
| Caption **text** | Database, inside a scene | Prepared with the shot it belongs to |
| Caption **visibility** | The studio's memory | A live control, like a fader |
| Layout, program source | The studio + the server's `IsProgram` | The arrangement now, not a saved intention |
| Audio levels and mutes | The studio's memory | Overrides for the moment, not a stored mix |

Caption visibility is the one worth stating explicitly. It is tempting to persist "the caption is
up", and it is wrong: reloading the control room mid-show would put a caption back on air that the
operator had taken down, and nothing on screen would explain why.

## 2. A scene arranges the studio; it does not force anything on air

Recalling a scene sets the layout, loads the caption text and **requests** a cut. It does not
promote the source itself.

Promotion has rules — a camera that has stopped sending cannot go on air — and a scene saved an hour
ago knows none of them. Routing the cut through the same path as any other means a scene naming a
camera that has since been unplugged fails the way every other bad cut fails, rather than putting
black on air.

For the same reason, scenes hold source ids **without a foreign key**. A scene has to outlive the
camera it names so that recalling it can say "that camera has gone" rather than vanishing along with
it. Revoking a source clears it out of every scene in the same transaction as the revocation, so a
scene never points at something that no longer exists.

That absence of a foreign key also means nothing in the database stops one session's scene naming
another session's source, so the service checks it explicitly.

## 3. The logo is stored as a data URI, not in object storage

Two reasons, and the second is the load-bearing one.

It keeps the platform free of file-upload infrastructure for a 20 KB image.

**And it keeps the image same-origin.** Drawing a cross-origin image into a canvas taints it, and
`captureStream` on a tainted canvas throws — which would take the entire broadcast down for the sake
of a watermark. A data URI can never taint the canvas, so this failure mode does not exist.

The type is checked on the way in: PNG, JPEG or WebP only. **SVG is excluded along with everything
else** — it is a document format that can carry script, not just pixels, and this value is drawn
into the frame that goes to air.

## 4. The accent colour is validated because it reaches a rendering context

`#RGB` or `#RRGGBB`, nothing else. The value is assigned to a canvas `fillStyle`, so it is
user-controlled input reaching a renderer whose output is broadcast. Refusing anything that is not a
hex triple is cheaper than reasoning about what a canvas will do with the alternatives.

## 5. Overlays are sized as a share of the frame

A caption sized in pixels is legible at 720p and illegible at 1080p, and viewers watch at every
size. Everything — the lower-third band, the type inside it, the watermark, the margins — is a
fraction of the frame.

The watermark scales by frame **width** rather than being fitted to a box, so a wide logo and a
square one carry the same visual weight. Fitting to a box would make a wide logo dominate.

## 6. Composing only when there is something to compose

The rule from [ADR 0012](0012-program-switching-and-composition.md) is now stated precisely:
**compose when there is something to draw that the raw camera cannot provide.** A second source
actually on air, a caption, or a watermark.

A two-source *layout* with only one source is deliberately not enough. It would pay the full cost of
composition — a decode, a canvas, an encode, and vulnerability to the frame-rate throttling browsers
apply to background tabs — to draw one camera full frame, which is exactly what sending the camera
does for free.

Showing a caption therefore starts the compositor on its own, which is why the panel says so rather
than warning about a prerequisite.

## 7. Audio follows the picture, with overrides

The default is "everything on air is heard, and the studio microphone always is". That is right most
of the time and wrong exactly when it matters: a guest in a noisy room, or two cameras picking up
the same speaker.

The mixer exposes a level and a mute per source for those cases. It is not a stored mix — these are
overrides for the moment, and a saved mix would be applied to a show whose sources had changed.

## 8. Keyboard bindings, and the one that does not exist

Numbers select, letters modify — the vocabulary of a hardware switcher rather than of an application.

| Key | Action |
|---|---|
| `1`–`9` | Recall scene |
| `Shift`+`1`–`9` | Cut to source |
| `Q` / `W` / `E` | Full screen / side by side / picture in picture |
| `L` | Caption on or off |
| `M` | Microphone on or off |
| `V` | Camera on or off |

**There is deliberately no shortcut for going live or stopping.** Ending a broadcast with a stray
keystroke is a failure no undo repairs, so it stays a deliberate press of a button.

Two details that are easy to get wrong:

- Shift+number arrives as a **symbol** on most keyboard layouts, so the binding reads `event.code`
  rather than `event.key`. Keying on `key` works on a US layout and nowhere else.
- Keystrokes are ignored entirely while focus is in a field. A producer naming a scene "1" must get
  the character, not a cut.

## 9. Controls that are dragged commit on release

A colour picker and a slider both fire on every step of a drag. Saving each step sends a request per
pixel of travel, and the responses can land out of order — leaving the stored colour a shade the
operator passed through rather than the one they chose.

Both hold a local value while in use and save when the operator lets go, following the server's
value again once idle so a change made elsewhere still appears.

## 10. What this does not include

- **No transitions.** Cuts are hard cuts, and a scene recall is a cut.
- **No animation on the lower third.** It appears and disappears.
- **No scene reordering in the UI.** Scenes keep the order they were created in.
- **No workspace-level branding.** Branding belongs to a session; a new session starts from the
  defaults rather than inheriting the last one.
- **No stored audio mix.** See §7.
- **No customisable key bindings.**
