"""
Meta Quest unified subscriber (TCP / NDJSON).

Connects to the single Unity PoseFramePublisher — a TCP server socket on the
headset (default port 5555) that streams NDJSON: one JSON object per line,
'\\n' terminated, UTF-8. Decodes each line and prints the receive rate plus the
HMD / palm / hand tracking state. No external dependencies (standard library
only) — matches SCHEMA.md.

One line carries the whole device state:
  {
    "timestamp": <seconds since app start, double>,
    "frame": <Unity frame count, int>,
    "coordinate_system": "robot_world",
    "hmd": { "tracked": true, "position": [x,y,z], "rotation": [x,y,z,w] },
    "hands": [
      { "side": "left", "tracked": true,
        "palm": { "valid": true, "position": [x,y,z], "rotation": [x,y,z,w] },
        "landmarks": [ { "i": 0, "valid": true,
                         "position": [x,y,z], "rotation": [x,y,z,w] }, ... x21 ] },
      { "side": "right", "tracked": false }
    ],
    "controllers": [
      { "side": "left", "tracked": true,
        "controller": { "valid": true, "position": [x,y,z], "rotation": [x,y,z,w] },
        "grip_trigger": 0.85, "ctrl_trigger": 0.12, "thumbstick": [x, y],
        "buttons": { "primary": false, "secondary": false,
                     "menu": false, "thumbstick": false } },
      { "side": "right", "tracked": false }
    ],
    "body": { "tracked": true, "fidelity": "high", "confidence": 0.97,
              "joints": [ { "i": 0, "valid": true,
                            "position": [x,y,z], "rotation": [x,y,z,w] }, ... x18 ] }
  }

Hands vs controllers vs body: all three are INDEPENDENT, not a mode switch. Any
frame may carry any combination of them — pick whichever source is tracked.
"controller" is the handle POSE; "grip_trigger" is the INDEX (front) trigger —
named for its role, it drives the gripper — and "ctrl_trigger" is the hardware
grip (side) analog axis.

"body" is the upper-body skeleton from Meta's Inside-Out Body Tracking: 18
joints (torso + arms, indices 0..17 of OpenXR's XrBodyJointFB), ending at the
wrist twist because the hands are already published in full under "hands".
"fidelity" says how it was tracked — "high" is real inside-out camera tracking,
"low" is inferred from headset + controllers, "unspecified" is the runtime
default.

Frame: every pose/landmark is in the "robot_world" frame — RIGHT-HANDED,
+X forward, +Y left, +Z up (FLU, ROS REP-103 style), meters. The publisher
converts from Unity's left-handed Y-up world at serialization time, so no
conversion is needed on this (receiver) side. Quaternion (rotation) order is
(x, y, z, w). +X forward is the user's facing direction at app start/recenter.

Tracking loss:
  - An untracked hand is {"side": ..., "tracked": false} (no palm/landmarks).
  - An untracked controller is {"side": ..., "tracked": false} (nothing else).
  - An untracked HMD is {"tracked": false} (no position/rotation).
  - An untracked body is {"tracked": false} (no fidelity/confidence/joints).
    Expect this until the body-tracking permission is granted on the headset.
  - A landmark, palm, controller or body-joint pose with "valid": false omits
    its position/rotation.

Usage:
    python subscriber.py --host 192.168.0.42 --port 5555
"""

import argparse
import json
import socket
import time
from typing import Optional

LANDMARKS_PER_HAND = 21
JOINTS_PER_BODY = 18

# Upper-body joint indices worth printing: the elbows are what Inside-Out Body
# Tracking adds over the hand/HMD data we already had.
BODY_LEFT_ARM_LOWER = 11
BODY_RIGHT_ARM_LOWER = 16


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1",
                        help="Headset IP address, e.g. 192.168.0.42")
    parser.add_argument("--port", type=int, default=5555,
                        help="TCP port the headset publishes on (default 5555).")
    parser.add_argument("--print-every", type=float, default=1.0,
                        help="Seconds between status prints.")
    args = parser.parse_args()

    while True:
        try:
            run_once(args)
        except (ConnectionError, OSError) as exc:
            print(f"[subscriber] disconnected ({exc}); reconnecting in 1s…")
            time.sleep(1.0)
        except KeyboardInterrupt:
            break


def run_once(args) -> None:
    with socket.create_connection((args.host, args.port), timeout=5.0) as sock:
        sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        print(f"[subscriber] connected to {args.host}:{args.port}")

        buf = b""
        received = 0
        last_log = time.monotonic()

        while True:
            chunk = sock.recv(65536)
            if not chunk:
                raise ConnectionError("server closed the connection")
            buf += chunk

            # Consume every complete '\n'-terminated line; keep only the latest
            # for display (publish-and-forget — intermediate frames are fine to
            # skip when we fall behind).
            latest: Optional[dict] = None
            while b"\n" in buf:
                line, buf = buf.split(b"\n", 1)
                line = line.strip()
                if not line:
                    continue
                try:
                    latest = json.loads(line.decode("utf-8"))
                    received += 1
                except (UnicodeDecodeError, json.JSONDecodeError) as exc:
                    print(f"[subscriber] bad line: {exc}")

            now = time.monotonic()
            if latest is not None and now - last_log >= args.print_every:
                rate = received / (now - last_log)
                received = 0
                last_log = now
                print(format_packet(latest, rate))


def format_packet(pkt: dict, rate: float) -> str:
    hands = by_side(pkt.get("hands"))
    ctrls = by_side(pkt.get("controllers"))

    header = (
        f"rate={rate:6.1f} Hz  "
        f"frame={pkt.get('frame', '?'):>7}  "
        f"hmd={fmt_xyz(hmd_position(pkt.get('hmd')))}"
    )
    hand_line = (
        f"  hands  L={hand_flag(hands.get('left'))} palm={fmt_xyz(pose_position(hands.get('left'), 'palm'))}"
        f"   R={hand_flag(hands.get('right'))} palm={fmt_xyz(pose_position(hands.get('right'), 'palm'))}"
    )
    ctrl_line = (
        f"  ctrls  L={controller_flag(ctrls.get('left'))}"
        f"   R={controller_flag(ctrls.get('right'))}"
    )
    body_line = f"  body   {body_flag(pkt.get('body'))}"
    return "\n".join((header, hand_line, ctrl_line, body_line))


def by_side(items) -> dict:
    """Turn a `hands`/`controllers` array into a {side: obj} map (ignores malformed)."""
    result: dict = {}
    if isinstance(items, list):
        for item in items:
            if isinstance(item, dict) and "side" in item:
                result[item["side"]] = item
    return result


def hmd_position(hmd: Optional[dict]) -> Optional[list]:
    if hmd is None or not hmd.get("tracked"):
        return None
    return hmd.get("position")


def hand_flag(hand: Optional[dict]) -> str:
    if hand is None or not hand.get("tracked"):
        return "off        "
    valid = sum(1 for lm in hand.get("landmarks", []) if lm.get("valid"))
    return f"ON({valid}/{LANDMARKS_PER_HAND})"


def pose_position(obj: Optional[dict], key: str) -> Optional[list]:
    """[x, y, z] of a { "valid", "position", "rotation" } sub-pose, or None."""
    if obj is None or not obj.get("tracked"):
        return None
    pose = obj.get(key)
    if pose is None or not pose.get("valid"):
        return None
    return pose.get("position")


def controller_flag(ctrl: Optional[dict]) -> str:
    """One-line controller summary: handle pose + analogs + whichever buttons are down."""
    if ctrl is None or not ctrl.get("tracked"):
        return "off"

    buttons = ctrl.get("buttons") or {}
    pressed = "".join(
        label for name, label in
        (("primary", "P"), ("secondary", "S"), ("menu", "M"), ("thumbstick", "T"))
        if buttons.get(name)
    )
    stick = ctrl.get("thumbstick") or [0.0, 0.0]
    return (
        f"ON pose={fmt_xyz(pose_position(ctrl, 'controller'))} "
        f"gripTrig={ctrl.get('grip_trigger', 0.0):.2f} "
        f"ctrlTrig={ctrl.get('ctrl_trigger', 0.0):.2f} "
        f"stick=({stick[0]:+.2f},{stick[1]:+.2f}) [{pressed or '-':4}]"
    )


def body_flag(body: Optional[dict]) -> str:
    """One-line upper-body summary: joint count, tracking fidelity, both elbows.

    `fidelity` is the interesting part — "high" means the runtime is really doing
    Inside-Out Body Tracking (headset cameras on the arms), while "low" means the
    arms are only inferred from the headset and controller poses.
    """
    if body is None or not body.get("tracked"):
        return "off"

    joints = body.get("joints") or []
    valid = sum(1 for j in joints if j.get("valid"))
    return (
        f"ON({valid}/{JOINTS_PER_BODY}) "
        f"fid={body.get('fidelity', '?'):11} "
        f"conf={body.get('confidence', 0.0):.2f} "
        f"elbowL={fmt_xyz(body_joint_position(joints, BODY_LEFT_ARM_LOWER))} "
        f"elbowR={fmt_xyz(body_joint_position(joints, BODY_RIGHT_ARM_LOWER))}"
    )


def body_joint_position(joints: list, index: int) -> Optional[list]:
    """[x, y, z] of body joint `index`, or None when missing/invalid.

    Matched on the "i" field rather than list position: the array is always 18
    entries in order, but keying on "i" keeps this correct regardless.
    """
    for joint in joints:
        if isinstance(joint, dict) and joint.get("i") == index:
            return joint.get("position") if joint.get("valid") else None
    return None


def fmt_xyz(p: Optional[list]) -> str:
    if p is None:
        return "     (none)     "
    return f"({p[0]:+.3f}, {p[1]:+.3f}, {p[2]:+.3f})"


if __name__ == "__main__":
    main()
