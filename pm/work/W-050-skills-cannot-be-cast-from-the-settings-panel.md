---
id: W-050
title: Skills cannot be cast from the settings panel
state: done
priority: 3
owner: 
opened: 2026-10-16
closed: 2026-10-16
depends: []
refs: [SkillForm.cs:168-174; MapSimSessionStore.CastSkill]
blocked_on: 
evidence: root cause: livewalk.skillFor checks keyFor() before skillKeys, and keyFor claimed WASD+Z; the panel offers all 26 letters so A/D/W/S/Z bound silently and could never cast (server accepted with rejected=[]). WASD REMOVED -- the reference binds only the four arrow keys (Keyboard.cpp:78-81). Z declared as the one remaining departure and refused by the server with a reason. skillkey-verify.py BAD=0; gate 16/16 green; three-way md5 equal
---
## Intent

Reported: skills cannot be cast from the settings panel.

## Reference

`SkillForm.Designer.cs:50` (the twenty-six letters) · `SkillForm.cs:156-162`
(the two exclusions) · `Keyboard.cpp:78-81` (the reference's movement keys) ·
`MapleCharacter.cs:436-450` (attack on LEFT_CONTROL).

## Approach

1. Find where a hotkey becomes a cast.
2. Check every key the client already spends before that point.
3. Compare the spent set against the twenty-six letters the panel offers.

## Acceptance

1. `python ...\skillkey-verify.py  # BAD=0`
2. `python tools/pm.py gate  # 16 suites green`

## Result

**The panel has no cast control at all** — it only BINDS a skill to a key.
Casting happens on the hotkey, in `livewalk.skillFor`. So the report reduces to
"the bound key never reaches a cast", and it does not, for one reason:

```javascript
skillFor(e) {
    if (this.keyFor(e.key)) return null;      // whatever owns the key wins
    const skillId = this.skillKeys.get(key);  // never reached for those keys
```

`keyFor` claimed **a/d/w/s (WASD) and z (attack)**. The panel offers all
twenty-six letters. So a skill bound to A/D/W/S/Z was accepted by the server,
reported as bound by the panel, and could never fire — silently, with
`rejected: []`.

**The collision was entirely this project's, and WASD was the cause.** The
reference binds movement to the four arrow keys and nothing else:

```cpp
// clean-client IO/Input/Keyboard.cpp:78-81
keymap[InputKey::LEFT]  = Mapping(KeyType::Id::ACTION, KeyAction::Id::LEFT);
keymap[InputKey::RIGHT] = Mapping(KeyType::Id::ACTION, KeyAction::Id::RIGHT);
keymap[InputKey::UP]    = Mapping(KeyType::Id::ACTION, KeyAction::Id::UP);
keymap[InputKey::DOWN]  = Mapping(KeyType::Id::ACTION, KeyAction::Id::DOWN);
```

`A`, `S`, `D`, `W` appear in that file's key LIST (:32) but are bound to nothing.
Reading the list as the bindings is the mistake that put WASD here, and it was
never declared as own-design — an R-6 violation the user caught.

**Two fixes:**

1. **WASD removed.** `keyFor` binds only the four arrow keys, Space for jump and
   Ctrl/Z for attack. A/D/W/S are the reference's skill letters again.
2. **Z is declared, and refused.** It stays because Ctrl cannot reach the
   handler on its own (app.js owns Ctrl for the palette/save/search, and the
   browser reserves Ctrl+W), but it is now a DECLARED departure: the server
   refuses a skill bound to Z with a reason, and the dropdown shows
   `Z — used to move or attack` rather than hiding it.

**The exclusion is the only one the reference does not have** — its two are a
duplicate skill and a duplicate key. Declared as own-design at the site, with the
reason, per R-6.

**Measured (skillkey-verify.py BAD=0):**

```
Z: bound=0  rejected=["row 0: 'Z' is bound to movement or attack in this
                         simulator, so a skill on it could never be pressed"]
A D W S F G H J K L: bound=1          (A/D/W/S freed by removing WASD)
Space: bound=0  rejected=["... is not a key the reference offers
                         (SkillForm.Designer.cs:50 hardcodes A-Z)"]
```

**Not proven here:** that pressing a bound key now casts. The client half of
that is `skillFor`, which reads a Map this server cannot see, and a browser
keystroke is not something an HTTP suite can send. `skill-test.ps1` already
covers the cast path itself.
