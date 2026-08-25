[한국어](SCHEMA_KOR.md) | **English**

# Data schema (TCP NDJSON)

Wire format of the stream the headset sends.
**One line = one UTF-8 JSON object = one frame** (`\n`-terminated, NDJSON).

## Common conventions

| Item | Value |
| --- | --- |
| Coordinate system | `robot_world` — **RIGHT-HANDED**, **+X forward / +Y left / +Z up (sky)**, unit **metres** (FLU: Forward-Left-Up, ROS REP-103 style) |
| Rotation | Quaternion, array order **`[x, y, z, w]`** (⚠ *not* `[w,x,y,z]`) |
| Position | `[x, y, z]` |
| Number format | At most 6 decimal places, decimal separator always `.` (InvariantCulture) |
| Array order | `hands` and `controllers` are both always **exactly 2 entries, in `[left, right]` order** |

> **The coordinate conversion is done on the sending side (the publisher).**
> Internally the headset (Unity) uses a left-handed, Y-up frame, but at
> serialization time it is converted to the right-handed FLU frame as follows:
>
> - Position: `(x, y, z)_unity → (z, −x, y)`
> - Quaternion: `(x, y, z, w)_unity → (−z, x, −y, w)`
>
> **+X forward** is the initial forward direction of the XR tracking space — the
> direction the user was facing when the app started (or was recentred). The
> receiver (robot, etc.) can use the values as-is, with no further conversion.

### Omission rules (your parser must handle these)

- An object with `"tracked": false` (hmd / hand / controller / body) **has none of
  its sub-keys at all** (e.g. an untracked hand ends at
  `{"side":"left","tracked":false}` — no `palm`/`landmarks`).
- A pose with `"valid": false` (palm / landmark / grip / aim / body joint)
  **omits** `position`/`rotation` (they are not zero-filled).

## Full example (actual serialization order)

```json
{
  "timestamp": 12.345,
  "frame": 1024,
  "coordinate_system": "robot_world",
  "hmd": { "tracked": true, "position": [0, 0, 1.6], "rotation": [0, 0, 0, 1] },
  "hands": [
    {
      "side": "left",
      "tracked": true,
      "palm": { "valid": true, "position": [0, 0, 0], "rotation": [0, 0, 0, 1] },
      "landmarks": [
        { "i": 0, "valid": true, "position": [0, 0, 0], "rotation": [0, 0, 0, 1] }
        /* … 21 entries, i = 0..20 … */
      ]
    },
    { "side": "right", "tracked": false }
  ],
  "controllers": [
    {
      "side": "left",
      "tracked": true,
      "controller": { "valid": true, "position": [0, 0, 0], "rotation": [0, 0, 0, 1] },
      "grip_trigger": 0.85,
      "ctrl_trigger": 0.12,
      "thumbstick": [0.0, -0.7],
      "buttons": { "primary": false, "secondary": false, "menu": false, "thumbstick": false }
    },
    { "side": "right", "tracked": false }
  ],
  "body": {
    "tracked": true,
    "fidelity": "high",
    "confidence": 0.97,
    "joints": [
      { "i": 0, "valid": true, "position": [0, 0, 0], "rotation": [0, 0, 0, 1] }
      /* … 18 entries, i = 0..17 … */
    ]
  }
}
```

- `timestamp` — seconds elapsed since app start (double).
- `frame` — render frame number at capture time (int).

## `hmd`

| Field | Type | Description |
| --- | --- | --- |
| `tracked` | bool | If false, the two fields below are omitted |
| `position` | [x,y,z] | Headset position (m) |
| `rotation` | [x,y,z,w] | Headset rotation |

---

## `hands` — hand tracking

![Hand landmark / palm mapping](docs/images/hands_keymap.jpg)

Always 2 entries, `[left, right]`. Each entry:

| Field | Type | Description |
| --- | --- | --- |
| `side` | `"left"` \| `"right"` | Which hand |
| `tracked` | bool | If false, everything below is omitted |
| `palm` | pose | Pose of the palm centre. `{valid, position, rotation}` |
| `landmarks` | array(21) | 21 landmarks, in the fixed order of the table below |

- **`palm` definition**: the OpenXR `Palm` joint is used when available. If the
  runtime does not provide it, position falls back to
  `midpoint(Wrist, MiddleProximal)` and rotation to the Wrist rotation.
- Each landmark entry is
  `{ "i": <0..20>, "valid": bool, "position": …, "rotation": … }`.
  If a single joint is missing in a given frame, only that entry becomes
  `{"i": n, "valid": false}`.

### Landmark order (MediaPipe 21-point standard)

| i | Name | ← OpenXR joint | i | Name | ← OpenXR joint |
|---|---|---|---|---|---|
| 0 | WRIST | Wrist | 11 | MIDDLE_DIP | MiddleDistal |
| 1 | THUMB_CMC | ThumbMetacarpal | 12 | MIDDLE_TIP | MiddleTip |
| 2 | THUMB_MCP | ThumbProximal | 13 | RING_MCP | RingProximal |
| 3 | THUMB_IP | ThumbDistal | 14 | RING_PIP | RingIntermediate |
| 4 | THUMB_TIP | ThumbTip | 15 | RING_DIP | RingDistal |
| 5 | INDEX_MCP | IndexProximal | 16 | RING_TIP | RingTip |
| 6 | INDEX_PIP | IndexIntermediate | 17 | PINKY_MCP | LittleProximal |
| 7 | INDEX_DIP | IndexDistal | 18 | PINKY_PIP | LittleIntermediate |
| 8 | INDEX_TIP | IndexTip | 19 | PINKY_DIP | LittleDistal |
| 9 | MIDDLE_MCP | MiddleProximal | 20 | PINKY_TIP | LittleTip |
| 10 | MIDDLE_PIP | MiddleIntermediate | | | |

(Of the 26 OpenXR joints, only the 21 that map to MediaPipe are used. The
metacarpal joints of the index through little fingers, and the Palm joint, are
excluded from the landmarks — the palm is provided as its own field.)

---

## `controllers`

![HMD / controller key mapping](docs/images/meta_keymap.jpg)

*The figure labels each physical input with the JSON field it is published as.
Note `grip_trigger` = the index-finger (front) trigger and `ctrl_trigger` = the
grip (side) trigger — see the mapping table below. The same layout applies to
every supported device.*

Always 2 entries, `[left, right]`. This is a **completely independent source
from `hands`**, not a mode switch. Within a single frame hands and controllers
may **both** be present, **only one**, or **neither**; the receiver picks
whichever side reports `tracked`.

Each entry (when tracked=true, in serialization order):

| Field | Type | Description |
| --- | --- | --- |
| `side` | `"left"` \| `"right"` | Which hand's controller |
| `tracked` | bool | If false, everything below is omitted |
| `controller` | pose | **6DoF pose of the controller grip** (counterpart to `palm` in `hands`) |
| `grip_trigger` | float 0..1 | **Index-finger (front) trigger** — used as the gripper control input |
| `ctrl_trigger` | float 0..1 | **Grip (side) trigger**, analog |
| `thumbstick` | [x, y] | Each axis -1..1 |
| `buttons` | object | `{primary, secondary, menu, thumbstick}`, all bool |

- `buttons.primary` = A/X, `buttons.secondary` = B/Y, `buttons.thumbstick` =
  stick click.

### ⚠ Field names follow their purpose — mapping to physical inputs

Field names follow the **teleoperation role**, not OpenXR naming. `grip_trigger`
means "the trigger that operates the gripper" — it is *not* the hardware grip
button:

| JSON field | Physical input | OpenXR path |
| --- | --- | --- |
| `controller` | Grip pose | `/input/grip/pose` |
| `grip_trigger` | **Index-finger (front) trigger** | `/input/trigger/value` |
| `ctrl_trigger` | **Grip (side) trigger** | `/input/squeeze/value` |

### tracked / valid rules

- **If the `controller` pose cannot be obtained, the whole controller becomes
  `tracked: false`** (without a grip pose there is nothing to follow).
- Inputs absent from the interaction profile (stick, etc.) are filled with `0` /
  `false`.
- Even if controller sampling is turned off in the app settings
  (`publishControllers = false`), the `controllers` field does not disappear —
  both entries are reported as `tracked: false`. **The schema shape is always
  the same.**

---

## `body` — upper-body skeleton (Inside-Out Body Tracking)

A single object (not an array — there is only one body). Like `controllers`, it is
a **completely independent source**: a frame may carry a tracked body with
untracked hands, or the reverse, or both, or neither.

The joints come from Meta's **Inside-Out Body Tracking (IOBT)** — the headset
cameras observing the user's real torso and arms. The tracking is performed by
the Horizon OS runtime; the app enables it (OpenXR `XR_FB_body_tracking`) and
requests the high-fidelity mode (`XR_META_body_tracking_fidelity`).

| Field | Type | Description |
| --- | --- | --- |
| `tracked` | bool | If false, everything below is omitted |
| `fidelity` | `"high"` \| `"low"` \| `"unspecified"` | Which tracking mode produced these joints — see below |
| `confidence` | float 0..1 | Runtime confidence in the body pose |
| `joints` | array(18) | 18 upper-body joints, in the fixed order of the table below |

- Each joint entry is
  `{ "i": <0..17>, "valid": bool, "position": …, "rotation": … }`.
  If a single joint is missing in a given frame, only that entry becomes
  `{"i": n, "valid": false}` — the same rule hand landmarks follow.

### `fidelity` — how much to trust the arms

| Value | Meaning |
| --- | --- |
| `"high"` | **IOBT.** The runtime accepted the high-fidelity request: the arms are tracked by the headset cameras |
| `"low"` | The runtime declined high fidelity. The upper body is *inferred* from headset and controller poses — much weaker, especially for elbows |
| `"unspecified"` | `XR_META_body_tracking_fidelity` was unavailable, so the runtime's default mode is in effect (on Quest 3 / 3S with current Horizon OS that is IOBT) |

### Joint order (OpenXR `XrBodyJointFB` 0–17)

| i | Name | i | Name |
|---|---|---|---|
| 0 | ROOT | 9 | LEFT_SCAPULA |
| 1 | HIPS | 10 | LEFT_ARM_UPPER |
| 2 | SPINE_LOWER | 11 | LEFT_ARM_LOWER |
| 3 | SPINE_MIDDLE | 12 | LEFT_WRIST_TWIST |
| 4 | SPINE_UPPER | 13 | RIGHT_SHOULDER |
| 5 | CHEST | 14 | RIGHT_SCAPULA |
| 6 | NECK | 15 | RIGHT_ARM_UPPER |
| 7 | HEAD | 16 | RIGHT_ARM_LOWER |
| 8 | LEFT_SHOULDER | 17 | RIGHT_WRIST_TWIST |

**Why 18 and not 70.** `XR_FB_body_tracking`'s default joint set has 70 joints,
but it numbers the torso and arms first (0–17) and the two hands after (18–69).
The hands are already published under `hands` at higher fidelity, from the
dedicated hand-tracking subsystem, so indices 18–69 are dropped: this keeps the
frame small and leaves **zero duplication** between `body` and `hands`.

The skeleton therefore ends at the wrist twist joints (12 / 17), which sit at the
same place as landmark 0 (`WRIST`) of the matching hand — that is where the two
skeletons join.

### Skeleton hierarchy

Parent → child, for a receiver that wants to draw or rig the skeleton:

```
0 ROOT → 1 HIPS → 2 SPINE_LOWER → 3 SPINE_MIDDLE → 4 SPINE_UPPER → 5 CHEST
5 CHEST → 6 NECK → 7 HEAD
5 CHEST → 9 LEFT_SCAPULA  → 8 LEFT_SHOULDER  → 10 LEFT_ARM_UPPER  → 11 LEFT_ARM_LOWER  → 12 LEFT_WRIST_TWIST
5 CHEST → 14 RIGHT_SCAPULA → 13 RIGHT_SHOULDER → 15 RIGHT_ARM_UPPER → 16 RIGHT_ARM_LOWER → 17 RIGHT_WRIST_TWIST
```

### tracked / valid rules

- Body tracking needs the **`com.oculus.permission.BODY_TRACKING`** runtime
  permission. Until the user accepts the consent dialog the body is published as
  `tracked: false`; it starts reporting on its own once granted (the app retries
  once a second, no restart needed).
- On a headset or runtime without body tracking, the body is simply always
  `tracked: false` — hands, HMD and controllers are unaffected.
- Even if body sampling is turned off in the app settings (`publishBody = false`),
  the `body` field does not disappear — it is reported as `tracked: false`.
  **The schema shape is always the same.**

---

## Parsing branches at a glance

| Condition | Output | Note |
| --- | --- | --- |
| HMD not tracked | `"hmd": {"tracked": false}` | No `position`/`rotation` keys |
| Hand not tracked | `{"side": …, "tracked": false}` | No `palm`/`landmarks` keys |
| palm/landmark `valid:false` | `{"valid": false}` / `{"i": n, "valid": false}` | No `position`/`rotation` keys |
| Controller not tracked | `{"side": …, "tracked": false}` | No `controller` or any key below it |
| Body not tracked | `"body": {"tracked": false}` | No `fidelity`/`confidence`/`joints` keys |
| Body joint `valid:false` | `{"i": n, "valid": false}` | No `position`/`rotation` keys |
