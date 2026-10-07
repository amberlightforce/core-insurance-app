# Comments and MentionComposer

## MentionComposer accessibility pattern (D-FE-20)

Comments are multi-line, so the composer stays a `<textarea>`. ARIA in HTML forbids `role="combobox"` and
`aria-expanded` on a textarea, and we deliberately do not use `aria-activedescendant` (screen-reader support
for it on a textarea is unreliable). Instead:

- **Keyboard is handled on the textarea.** Focus never leaves it. Typing `@` opens the suggestions;
  ↑/↓ move the highlight, Enter or Tab insert the highlighted person, Esc closes the list, Ctrl/⌘+Enter sends.
- **Instructions** are a visually hidden text referenced by the textarea's `aria-describedby`
  (`mentionComposer.hint`).
- **Announcements** go through the design-system live announcer (`a11y/announce.ts`, polite): the number of
  suggestions when the list opens or changes, then «{name}, {position} από {count}» each time the highlight
  moves.
- The popup is a `role="listbox"` with `aria-selected` on the highlighted option, for pointer users and for
  screen-reader users who browse to it; options are chosen with a click (mouse-down keeps textarea focus).
- Inserted mentions are tokens in the value model `{ text, mentions: [{ id, name, start, end }] }`; editing
  inside a token removes the token.
