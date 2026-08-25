"""
Live 3D verification viewer for the Meta Quest pose stream (TCP / NDJSON).

Connects to the headset's PoseFramePublisher exactly like subscriber.py does, but
instead of printing a status line it DRAWS the whole published state in 3D and
continuously checks it against invariants that must hold if the data is correct.

    python Tools/skeleton_viewer.py --host <headset-ip> --port 5555

No external dependencies — tkinter only, like the rest of Tools/.

WHAT IT DRAWS (everything in the "robot_world" frame: +X forward, +Y left,
+Z up, metres — see SCHEMA.md)

  • ground grid at Z = 0 and a world axis triad at the origin
  • body      — 18-joint upper-body skeleton, bones coloured per chain
  • hands     — 21 MediaPipe landmarks per hand with their bone lines
  • hmd       — axis triad + view direction
  • controllers — axis triad each

WHY THE NUMBERS MATTER (right-hand panel)

Looking at a skeleton that "moves about right" does not prove much: a wrong
quaternion basis change, a swapped joint index or an off-by-one in the joint
table all still produce something that follows you around. So the panel tracks
quantities that are supposed to be CONSTANT, and shows how much each one drifted
over the session (`spread` = max − min). Drift is the tell:

  Bone lengths (upper arm, forearm, shoulder span)
      A real skeleton is rigid. If `spread` on a bone grows past a couple of
      centimetres while you move, the joints are not what we think they are.

  Body↔hand seam (wrist twist ↔ hand WRIST landmark)
      SCHEMA.md claims body joints 12/17 sit at the same place as landmark 0 of
      the matching hand. If that distance is large or erratic while both sources
      are tracked, the claim — or the joint mapping — is wrong.

  Body↔HMD (HEAD joint ↔ hmd position)
      Not expected to be zero (the HEAD joint is inside the skull, the HMD pose
      is at the camera), but it must be STABLE. A wandering value means body and
      HMD are not in the same frame.

  Body↔controller (wrist twist ↔ controller pose)
      Same idea for the controller path.

  Quaternion norm
      Every rotation on the wire must be a unit quaternion. Anything off 1.000
      means the serialisation is corrupting rotations.

  Arm chain candidates
      SCHEMA.md documents the parent chain as CHEST → SCAPULA → SHOULDER →
      ARM_UPPER. That ordering is the one part of the schema that was not
      confirmed against an OpenXR header. The panel prints the segment lengths
      for both candidate orderings; the correct one is the chain whose segments
      are short and steady. Whichever wins here is the one SCHEMA.md should say.

CONTROLS
  left-drag  orbit          wheel  zoom          right/shift-drag  pan
  r  reset view       f  follow body/HMD       a  per-joint axis triads
  h  hands            b  body                  d  devices (hmd/controllers)
  g  grid             space  pause             q  quit
"""

import argparse
import json
import math
import socket
import threading
import time
import tkinter as tk
from tkinter import font as tkfont
from typing import Dict, List, Optional, Sequence, Tuple

# --------------------------------------------------------------------------
# Skeleton tables — mirror BodyJointMapping.cs / HandLandmarkMapping.cs
# --------------------------------------------------------------------------

BODY_JOINT_NAMES = [
    "ROOT", "HIPS", "SPINE_LOWER", "SPINE_MIDDLE", "SPINE_UPPER", "CHEST",
    "NECK", "HEAD",
    "LEFT_SHOULDER", "LEFT_SCAPULA", "LEFT_ARM_UPPER", "LEFT_ARM_LOWER",
    "LEFT_WRIST_TWIST",
    "RIGHT_SHOULDER", "RIGHT_SCAPULA", "RIGHT_ARM_UPPER", "RIGHT_ARM_LOWER",
    "RIGHT_WRIST_TWIST",
]
BODY_JOINT_COUNT = 18

# Named indices, so the checks below read like the schema instead of like magic
# numbers.
J_CHEST = 5
J_HEAD = 7
J_L_SHOULDER, J_L_SCAPULA, J_L_ARM_UPPER, J_L_ARM_LOWER, J_L_WRIST = 8, 9, 10, 11, 12
J_R_SHOULDER, J_R_SCAPULA, J_R_ARM_UPPER, J_R_ARM_LOWER, J_R_WRIST = 13, 14, 15, 16, 17

# (parent, child, chain) — chain picks the colour.
BODY_BONES = [
    (0, 1, "spine"), (1, 2, "spine"), (2, 3, "spine"), (3, 4, "spine"), (4, 5, "spine"),
    (5, 6, "head"), (6, 7, "head"),
    (5, 9, "left"), (9, 8, "left"), (8, 10, "left"), (10, 11, "left"), (11, 12, "left"),
    (5, 14, "right"), (14, 13, "right"), (13, 15, "right"), (15, 16, "right"), (16, 17, "right"),
]

HAND_LANDMARK_COUNT = 21
HAND_BONES = [
    (0, 1), (1, 2), (2, 3), (3, 4),
    (0, 5), (5, 6), (6, 7), (7, 8),
    (5, 9), (9, 10), (10, 11), (11, 12),
    (9, 13), (13, 14), (14, 15), (15, 16),
    (13, 17), (17, 18), (18, 19), (19, 20),
    (0, 17),
]

# --------------------------------------------------------------------------
# Palette
# --------------------------------------------------------------------------

BG = "#0f1116"
FG = "#c0caf5"
DIM = "#3b4261"
GRID = "#1e2230"
COLORS = {
    "spine": "#7aa2f7",
    "head": "#bb9af7",
    "left": "#f7768e",
    "right": "#9ece6a",
    "hand": "#73daca",
    "hmd": "#e0af68",
    "ctrl": "#ff9e64",
    "axis_x": "#f7768e",
    "axis_y": "#9ece6a",
    "axis_z": "#7aa2f7",
}
OK, WARN, BAD = "#9ece6a", "#e0af68", "#f7768e"


# --------------------------------------------------------------------------
# Stream client — same transport contract as subscriber.py
# --------------------------------------------------------------------------

class StreamClient:
    """Reads NDJSON frames on a background thread, keeps only the newest.

    Mirrors the publisher's own latest-value semantics: we are not trying to see
    every frame, we are trying to see the CURRENT state with no lag build-up.
    Reconnects on its own so the viewer can be started before the headset app.
    """

    def __init__(self, host: str, port: int) -> None:
        self.host = host
        self.port = port
        self._lock = threading.Lock()
        self._packet: Optional[dict] = None
        self._connected = False
        self._received = 0
        self._rate = 0.0
        self._status = "connecting…"
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._run, daemon=True)

    def start(self) -> None:
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()

    def snapshot(self) -> Tuple[Optional[dict], bool, float, str]:
        with self._lock:
            return self._packet, self._connected, self._rate, self._status

    def _run(self) -> None:
        while not self._stop.is_set():
            sock = None
            try:
                sock = socket.create_connection((self.host, self.port), timeout=5.0)
                sock.settimeout(2.0)
                with self._lock:
                    self._connected = True
                    self._status = f"connected to {self.host}:{self.port}"
                self._read_loop(sock)
            except OSError as exc:
                with self._lock:
                    self._connected = False
                    self._status = f"{type(exc).__name__}: {exc} — retrying"
            finally:
                if sock is not None:
                    try:
                        sock.close()
                    except OSError:
                        pass
            if not self._stop.is_set():
                time.sleep(1.0)

    def _read_loop(self, sock: socket.socket) -> None:
        buffer = b""
        window_start = time.monotonic()
        window_count = 0

        while not self._stop.is_set():
            try:
                chunk = sock.recv(65536)
            except socket.timeout:
                continue
            if not chunk:
                raise OSError("publisher closed the connection")

            buffer += chunk
            # Keep only the last complete line: older frames in the same chunk
            # are already stale by the time we would draw them.
            *complete, buffer = buffer.split(b"\n")
            for raw in reversed(complete):
                raw = raw.strip()
                if not raw:
                    continue
                try:
                    packet = json.loads(raw)
                except (ValueError, UnicodeDecodeError):
                    continue
                with self._lock:
                    self._packet = packet
                break
            window_count += len(complete)

            now = time.monotonic()
            if now - window_start >= 0.5:
                with self._lock:
                    self._rate = window_count / (now - window_start)
                window_count = 0
                window_start = now


# --------------------------------------------------------------------------
# Small vector / quaternion helpers (plain tuples — no numpy)
# --------------------------------------------------------------------------

Vec3 = Tuple[float, float, float]


def sub(a: Vec3, b: Vec3) -> Vec3:
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def add(a: Vec3, b: Vec3) -> Vec3:
    return (a[0] + b[0], a[1] + b[1], a[2] + b[2])


def scale(a: Vec3, s: float) -> Vec3:
    return (a[0] * s, a[1] * s, a[2] * s)


def dot(a: Vec3, b: Vec3) -> float:
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def cross(a: Vec3, b: Vec3) -> Vec3:
    return (a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0])


def norm(a: Vec3) -> float:
    return math.sqrt(dot(a, a))


def unit(a: Vec3) -> Vec3:
    n = norm(a)
    return (0.0, 0.0, 0.0) if n < 1e-9 else scale(a, 1.0 / n)


def dist(a: Vec3, b: Vec3) -> float:
    return norm(sub(a, b))


def quat_axes(q: Sequence[float]) -> Tuple[Vec3, Vec3, Vec3]:
    """Local X/Y/Z axes of a quaternion, in world coordinates.

    Wire order is (x, y, z, w) — see SCHEMA.md; getting this order wrong is a
    classic receiver bug, so it is spelled out rather than unpacked blindly.
    """
    x, y, z, w = q[0], q[1], q[2], q[3]
    return (
        (1 - 2 * (y * y + z * z), 2 * (x * y + z * w), 2 * (x * z - y * w)),
        (2 * (x * y - z * w), 1 - 2 * (x * x + z * z), 2 * (y * z + x * w)),
        (2 * (x * z + y * w), 2 * (y * z - x * w), 1 - 2 * (x * x + y * y)),
    )


# --------------------------------------------------------------------------
# Packet accessors — tolerant of every omission rule in SCHEMA.md
# --------------------------------------------------------------------------

def by_side(items) -> Dict[str, dict]:
    result: Dict[str, dict] = {}
    if isinstance(items, list):
        for item in items:
            if isinstance(item, dict) and "side" in item:
                result[item["side"]] = item
    return result


def as_vec(value) -> Optional[Vec3]:
    if isinstance(value, (list, tuple)) and len(value) == 3:
        try:
            return (float(value[0]), float(value[1]), float(value[2]))
        except (TypeError, ValueError):
            return None
    return None


def body_points(body: Optional[dict]) -> List[Optional[Vec3]]:
    """18 positions (None where the joint is missing or the body is untracked)."""
    points: List[Optional[Vec3]] = [None] * BODY_JOINT_COUNT
    if not body or not body.get("tracked"):
        return points
    for joint in body.get("joints") or []:
        if not isinstance(joint, dict) or not joint.get("valid"):
            continue
        index = joint.get("i")
        if isinstance(index, int) and 0 <= index < BODY_JOINT_COUNT:
            points[index] = as_vec(joint.get("position"))
    return points


def body_rotations(body: Optional[dict]) -> List[Optional[Sequence[float]]]:
    rotations: List[Optional[Sequence[float]]] = [None] * BODY_JOINT_COUNT
    if not body or not body.get("tracked"):
        return rotations
    for joint in body.get("joints") or []:
        if not isinstance(joint, dict) or not joint.get("valid"):
            continue
        index = joint.get("i")
        rotation = joint.get("rotation")
        if isinstance(index, int) and 0 <= index < BODY_JOINT_COUNT \
                and isinstance(rotation, (list, tuple)) and len(rotation) == 4:
            rotations[index] = rotation
    return rotations


def hand_points(hand: Optional[dict]) -> List[Optional[Vec3]]:
    points: List[Optional[Vec3]] = [None] * HAND_LANDMARK_COUNT
    if not hand or not hand.get("tracked"):
        return points
    for landmark in hand.get("landmarks") or []:
        if not isinstance(landmark, dict) or not landmark.get("valid"):
            continue
        index = landmark.get("i")
        if isinstance(index, int) and 0 <= index < HAND_LANDMARK_COUNT:
            points[index] = as_vec(landmark.get("position"))
    return points


def pose_of(obj: Optional[dict], key: str) -> Tuple[Optional[Vec3], Optional[Sequence[float]]]:
    """(position, rotation) of a {valid, position, rotation} sub-pose."""
    if not obj or not obj.get("tracked"):
        return None, None
    pose = obj.get(key)
    if not isinstance(pose, dict) or not pose.get("valid"):
        return None, None
    rotation = pose.get("rotation")
    return as_vec(pose.get("position")), rotation if isinstance(rotation, (list, tuple)) else None


def hmd_pose(packet: dict) -> Tuple[Optional[Vec3], Optional[Sequence[float]]]:
    hmd = packet.get("hmd")
    if not isinstance(hmd, dict) or not hmd.get("tracked"):
        return None, None
    rotation = hmd.get("rotation")
    return as_vec(hmd.get("position")), rotation if isinstance(rotation, (list, tuple)) else None


def all_rotations(packet: dict):
    """Every rotation quaternion in the frame — used for the unit-norm check."""
    hmd = packet.get("hmd")
    if isinstance(hmd, dict) and hmd.get("tracked"):
        rotation = hmd.get("rotation")
        if isinstance(rotation, (list, tuple)) and len(rotation) == 4:
            yield rotation
    for hand in (packet.get("hands") or []):
        if not isinstance(hand, dict) or not hand.get("tracked"):
            continue
        for holder in [hand.get("palm")] + list(hand.get("landmarks") or []):
            if isinstance(holder, dict) and holder.get("valid"):
                rotation = holder.get("rotation")
                if isinstance(rotation, (list, tuple)) and len(rotation) == 4:
                    yield rotation
    for ctrl in (packet.get("controllers") or []):
        if isinstance(ctrl, dict) and ctrl.get("tracked"):
            pose = ctrl.get("controller")
            if isinstance(pose, dict) and pose.get("valid"):
                rotation = pose.get("rotation")
                if isinstance(rotation, (list, tuple)) and len(rotation) == 4:
                    yield rotation
    body = packet.get("body")
    if isinstance(body, dict) and body.get("tracked"):
        for joint in body.get("joints") or []:
            if isinstance(joint, dict) and joint.get("valid"):
                rotation = joint.get("rotation")
                if isinstance(rotation, (list, tuple)) and len(rotation) == 4:
                    yield rotation


# --------------------------------------------------------------------------
# Invariant tracking
# --------------------------------------------------------------------------

class Stat:
    """One scalar that is supposed to hold still, plus how much it actually moved.

    `spread` (max − min over the session) is the whole point: a single sample of
    a bone length tells you nothing, but a bone length that varies by 8 cm while
    you wave your arm is proof that the two joints are not the two joints you
    think they are.
    """

    __slots__ = ("current", "lo", "hi", "count")

    def __init__(self) -> None:
        self.current: Optional[float] = None
        self.lo: Optional[float] = None
        self.hi: Optional[float] = None
        self.count = 0

    def update(self, value: Optional[float]) -> None:
        if value is None:
            self.current = None
            return
        self.current = value
        self.count += 1
        self.lo = value if self.lo is None else min(self.lo, value)
        self.hi = value if self.hi is None else max(self.hi, value)

    @property
    def spread(self) -> Optional[float]:
        if self.lo is None or self.hi is None:
            return None
        return self.hi - self.lo

    def reset(self) -> None:
        self.current = self.lo = self.hi = None
        self.count = 0


class Metrics:
    """All the invariants, refreshed once per drawn frame."""

    # spread thresholds in metres: below `good` is fine, above `bad` is a red flag
    RIGID_GOOD, RIGID_BAD = 0.02, 0.05
    SEAM_GOOD, SEAM_BAD = 0.03, 0.08

    def __init__(self) -> None:
        self.stats: Dict[str, Stat] = {}
        self.quat_lo: Optional[float] = None
        self.quat_hi: Optional[float] = None

    def stat(self, key: str) -> Stat:
        if key not in self.stats:
            self.stats[key] = Stat()
        return self.stats[key]

    def reset(self) -> None:
        for stat in self.stats.values():
            stat.reset()
        self.quat_lo = self.quat_hi = None

    @staticmethod
    def _seg(points: Sequence[Optional[Vec3]], a: int, b: int) -> Optional[float]:
        if points[a] is None or points[b] is None:
            return None
        return dist(points[a], points[b])

    def update(self, packet: dict) -> None:
        body = packet.get("body") if isinstance(packet.get("body"), dict) else None
        bp = body_points(body)
        hands = by_side(packet.get("hands"))
        ctrls = by_side(packet.get("controllers"))
        hmd_p, _ = hmd_pose(packet)

        # --- rigid bones: must not change length while you move ---------------
        self.stat("upperarm L").update(self._seg(bp, J_L_ARM_UPPER, J_L_ARM_LOWER))
        self.stat("upperarm R").update(self._seg(bp, J_R_ARM_UPPER, J_R_ARM_LOWER))
        self.stat("forearm L").update(self._seg(bp, J_L_ARM_LOWER, J_L_WRIST))
        self.stat("forearm R").update(self._seg(bp, J_R_ARM_LOWER, J_R_WRIST))
        self.stat("shoulder span").update(self._seg(bp, J_L_SHOULDER, J_R_SHOULDER))

        # --- seams between the three independent sources ----------------------
        lhand = hand_points(hands.get("left"))
        rhand = hand_points(hands.get("right"))
        self.stat("body↔hand L").update(
            None if bp[J_L_WRIST] is None or lhand[0] is None else dist(bp[J_L_WRIST], lhand[0]))
        self.stat("body↔hand R").update(
            None if bp[J_R_WRIST] is None or rhand[0] is None else dist(bp[J_R_WRIST], rhand[0]))

        self.stat("body↔hmd").update(
            None if bp[J_HEAD] is None or hmd_p is None else dist(bp[J_HEAD], hmd_p))

        lctrl, _ = pose_of(ctrls.get("left"), "controller")
        rctrl, _ = pose_of(ctrls.get("right"), "controller")
        self.stat("body↔ctrl L").update(
            None if bp[J_L_WRIST] is None or lctrl is None else dist(bp[J_L_WRIST], lctrl))
        self.stat("body↔ctrl R").update(
            None if bp[J_R_WRIST] is None or rctrl is None else dist(bp[J_R_WRIST], rctrl))

        # --- arm chain: which parent ordering is real? -------------------------
        # Documented chain  : CHEST → SCAPULA → SHOULDER → ARM_UPPER
        # Alternative chain : CHEST → SHOULDER → SCAPULA → ARM_UPPER
        # The true chain is the one whose segments are short and steady.
        self.stat("A: chest→scap").update(self._seg(bp, J_CHEST, J_L_SCAPULA))
        self.stat("A: scap→shldr").update(self._seg(bp, J_L_SCAPULA, J_L_SHOULDER))
        self.stat("A: shldr→arm").update(self._seg(bp, J_L_SHOULDER, J_L_ARM_UPPER))
        self.stat("B: chest→shldr").update(self._seg(bp, J_CHEST, J_L_SHOULDER))
        self.stat("B: shldr→scap").update(self._seg(bp, J_L_SHOULDER, J_L_SCAPULA))
        self.stat("B: scap→arm").update(self._seg(bp, J_L_SCAPULA, J_L_ARM_UPPER))

        # --- every quaternion on the wire must be unit length -----------------
        for rotation in all_rotations(packet):
            try:
                length = math.sqrt(sum(float(c) * float(c) for c in rotation))
            except (TypeError, ValueError):
                continue
            self.quat_lo = length if self.quat_lo is None else min(self.quat_lo, length)
            self.quat_hi = length if self.quat_hi is None else max(self.quat_hi, length)


# --------------------------------------------------------------------------
# Camera
# --------------------------------------------------------------------------

class Camera:
    """Orbit camera for a Z-up right-handed world.

    Azimuth 0 places the eye on +X looking back along −X, i.e. face-on to a user
    who is facing the published +X forward direction.
    """

    def __init__(self) -> None:
        self.reset()

    def reset(self) -> None:
        self.azimuth = 0.0
        self.elevation = math.radians(8.0)
        # Framed so an upper body fills a good half of the view: at 2 m with a
        # 50° vertical FOV the visible height is ~1.9 m.
        self.distance = 2.0
        self.target: Vec3 = (0.0, 0.0, 1.25)
        self.fov = 50.0

    def eye(self) -> Vec3:
        ce, se = math.cos(self.elevation), math.sin(self.elevation)
        return add(self.target, scale(
            (ce * math.cos(self.azimuth), ce * math.sin(self.azimuth), se), self.distance))

    def basis(self) -> Tuple[Vec3, Vec3, Vec3]:
        forward = unit(sub(self.target, self.eye()))
        world_up = (0.0, 0.0, 1.0)
        if abs(dot(forward, world_up)) > 0.999:      # looking straight down/up
            world_up = (1.0, 0.0, 0.0)
        right = unit(cross(forward, world_up))
        up = cross(right, forward)
        return right, up, forward

    def orbit(self, dx: float, dy: float) -> None:
        self.azimuth -= dx * 0.008
        limit = math.radians(89.0)
        self.elevation = max(-limit, min(limit, self.elevation + dy * 0.008))

    def pan(self, dx: float, dy: float) -> None:
        right, up, _ = self.basis()
        step = self.distance * 0.0016
        self.target = add(self.target, add(scale(right, -dx * step), scale(up, dy * step)))

    def zoom(self, factor: float) -> None:
        self.distance = max(0.35, min(25.0, self.distance * factor))


class Projector:
    """Snapshot of the camera for one frame: world point → (sx, sy, depth)."""

    def __init__(self, camera: Camera, width: int, height: int) -> None:
        self.eye = camera.eye()
        self.right, self.up, self.forward = camera.basis()
        self.cx = width * 0.5
        self.cy = height * 0.5
        self.focal = (height * 0.5) / math.tan(math.radians(camera.fov) * 0.5)

    def project(self, point: Vec3) -> Optional[Tuple[float, float, float]]:
        rel = sub(point, self.eye)
        depth = dot(rel, self.forward)
        if depth <= 0.05:                     # behind the eye / too close
            return None
        k = self.focal / depth
        return (self.cx + dot(rel, self.right) * k,
                self.cy - dot(rel, self.up) * k,
                depth)


# --------------------------------------------------------------------------
# Canvas item pools — reuse items instead of recreating them every frame
# --------------------------------------------------------------------------

class Pool:
    """Fixed-cost drawing: allocate canvas items once, then move them."""

    def __init__(self, canvas: tk.Canvas, kind: str) -> None:
        self.canvas = canvas
        self.kind = kind
        self.items: List[int] = []
        self.used = 0

    def begin(self) -> None:
        self.used = 0

    def _next(self, coords, **options) -> int:
        if self.used < len(self.items):
            item = self.items[self.used]
            self.canvas.coords(item, *coords)
            self.canvas.itemconfigure(item, state="normal", **options)
        else:
            factory = getattr(self.canvas, f"create_{self.kind}")
            item = factory(*coords, **options)
            self.items.append(item)
        self.used += 1
        return item

    def line(self, x1, y1, x2, y2, fill, width=1.0) -> None:
        self._next((x1, y1, x2, y2), fill=fill, width=width)

    def dot(self, x, y, radius, fill) -> None:
        self._next((x - radius, y - radius, x + radius, y + radius), fill=fill, outline="")

    def label(self, x, y, text, fill, anchor="nw") -> None:
        self._next((x, y), text=text, fill=fill, anchor=anchor)

    def end(self) -> None:
        for item in self.items[self.used:]:
            self.canvas.itemconfigure(item, state="hidden")


# --------------------------------------------------------------------------
# Viewer
# --------------------------------------------------------------------------

class Viewer:
    def __init__(self, root: tk.Tk, client: StreamClient, fps: int) -> None:
        self.root = root
        self.client = client
        self.interval = max(1, int(1000 / max(1, fps)))
        self.camera = Camera()
        self.metrics = Metrics()

        self.show_grid = True
        self.show_body = True
        self.show_hands = True
        self.show_devices = True
        self.show_axes = False
        self.follow = True
        self.paused = False
        self.frozen: Optional[dict] = None

        root.title("Meta Quest pose stream — 3D verification viewer")
        root.configure(bg=BG)
        root.geometry("1360x820")

        self.canvas = tk.Canvas(root, bg=BG, highlightthickness=0, width=960, height=820)
        self.canvas.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)

        mono = tkfont.nametofont("TkFixedFont").copy()
        mono.configure(size=10)
        self.panel = tk.Label(root, bg=BG, fg=FG, font=mono, justify=tk.LEFT,
                              anchor="nw", width=52, padx=12, pady=10)
        self.panel.pack(side=tk.RIGHT, fill=tk.Y)

        self.lines = Pool(self.canvas, "line")
        self.dots = Pool(self.canvas, "oval")
        self.texts = Pool(self.canvas, "text")

        self._drag: Optional[Tuple[int, int, str]] = None
        self._bind()
        self.root.after(self.interval, self._tick)

    # ---- input ----------------------------------------------------------
    def _bind(self) -> None:
        c = self.canvas
        c.bind("<ButtonPress-1>", lambda e: self._press(e, "orbit"))
        c.bind("<ButtonPress-3>", lambda e: self._press(e, "pan"))
        c.bind("<Shift-ButtonPress-1>", lambda e: self._press(e, "pan"))
        c.bind("<B1-Motion>", self._motion)
        c.bind("<B3-Motion>", self._motion)
        c.bind("<ButtonRelease-1>", self._release)
        c.bind("<ButtonRelease-3>", self._release)
        c.bind("<MouseWheel>", lambda e: self.camera.zoom(0.9 if e.delta > 0 else 1.1))
        c.bind("<Button-4>", lambda e: self.camera.zoom(0.9))      # X11 wheel up
        c.bind("<Button-5>", lambda e: self.camera.zoom(1.1))      # X11 wheel down
        self.root.bind("<Key>", self._key)
        c.focus_set()

    def _press(self, event, mode: str) -> None:
        self._drag = (event.x, event.y, mode)

    def _motion(self, event) -> None:
        if self._drag is None:
            return
        x, y, mode = self._drag
        dx, dy = event.x - x, event.y - y
        if mode == "orbit":
            self.camera.orbit(dx, dy)
        else:
            self.camera.pan(dx, dy)
            self.follow = False          # manual pan wins over auto-follow
        self._drag = (event.x, event.y, mode)

    def _release(self, _event) -> None:
        self._drag = None

    def _key(self, event) -> None:
        key = event.keysym.lower()
        if key == "q":
            self.root.destroy()
        elif key == "r":
            self.camera.reset()
            self.follow = True
            self.metrics.reset()
        elif key == "f":
            self.follow = not self.follow
        elif key == "a":
            self.show_axes = not self.show_axes
        elif key == "g":
            self.show_grid = not self.show_grid
        elif key == "b":
            self.show_body = not self.show_body
        elif key == "h":
            self.show_hands = not self.show_hands
        elif key == "d":
            self.show_devices = not self.show_devices
        elif key == "space":
            self.paused = not self.paused

    # ---- frame loop -----------------------------------------------------
    def _tick(self) -> None:
        packet, connected, rate, status = self.client.snapshot()

        if self.paused:
            packet = self.frozen if self.frozen is not None else packet
        else:
            self.frozen = packet
            if packet:
                self.metrics.update(packet)

        self._draw(packet, connected, rate, status)
        self.root.after(self.interval, self._tick)

    def _draw(self, packet: Optional[dict], connected: bool, rate: float, status: str) -> None:
        width = max(self.canvas.winfo_width(), 2)
        height = max(self.canvas.winfo_height(), 2)

        body = packet.get("body") if packet and isinstance(packet.get("body"), dict) else None
        bp = body_points(body)
        hands = by_side(packet.get("hands")) if packet else {}
        ctrls = by_side(packet.get("controllers")) if packet else {}
        hmd_p, hmd_q = hmd_pose(packet) if packet else (None, None)

        if self.follow:
            self._follow(bp, hmd_p)

        projector = Projector(self.camera, width, height)
        self.lines.begin()
        self.dots.begin()
        self.texts.begin()

        if self.show_grid:
            self._draw_grid(projector)
            self._draw_triad(projector, (0.0, 0.0, 0.0), None, 0.35, 2.0)

        if self.show_body and body is not None:
            self._draw_body(projector, bp, body_rotations(body))

        if self.show_hands and packet:
            for side in ("left", "right"):
                self._draw_hand(projector, hand_points(hands.get(side)))

        if self.show_devices and packet:
            if hmd_p is not None:
                self._draw_triad(projector, hmd_p, hmd_q, 0.16, 2.0)
                self._label(projector, hmd_p, "HMD", COLORS["hmd"])
            for side in ("left", "right"):
                pos, rot = pose_of(ctrls.get(side), "controller")
                if pos is not None:
                    self._draw_triad(projector, pos, rot, 0.12, 2.0)
                    self._label(projector, pos, f"ctrl {side[0].upper()}", COLORS["ctrl"])

        self.lines.end()
        self.dots.end()
        self.texts.end()
        self.panel.configure(text=self._panel_text(packet, connected, rate, status))

    def _follow(self, bp: Sequence[Optional[Vec3]], hmd_p: Optional[Vec3]) -> None:
        """Keep the subject centred without fighting the user's orbit."""
        anchor = bp[J_CHEST] if bp[J_CHEST] is not None else (
            bp[J_HEAD] if bp[J_HEAD] is not None else hmd_p)
        if anchor is None:
            return
        target = (anchor[0], anchor[1], anchor[2] - 0.05)
        # Ease in, so tracking jitter does not shake the whole view.
        self.camera.target = add(scale(self.camera.target, 0.85), scale(target, 0.15))

    # ---- primitives -----------------------------------------------------
    def _segment(self, projector: Projector, a: Vec3, b: Vec3, color: str, width: float) -> None:
        pa, pb = projector.project(a), projector.project(b)
        if pa is None or pb is None:
            return
        self.lines.line(pa[0], pa[1], pb[0], pb[1], color, width)

    def _point(self, projector: Projector, p: Vec3, color: str, radius: float) -> None:
        pp = projector.project(p)
        if pp is None:
            return
        self.dots.dot(pp[0], pp[1], radius, color)

    def _label(self, projector: Projector, p: Vec3, text: str, color: str) -> None:
        pp = projector.project(p)
        if pp is None:
            return
        self.texts.label(pp[0] + 7, pp[1] - 7, text, color, anchor="sw")

    def _draw_grid(self, projector: Projector) -> None:
        extent, step = 3.0, 0.5
        count = int(extent / step)
        for i in range(-count, count + 1):
            v = i * step
            self._segment(projector, (v, -extent, 0.0), (v, extent, 0.0), GRID, 1.0)
            self._segment(projector, (-extent, v, 0.0), (extent, v, 0.0), GRID, 1.0)

    def _draw_triad(self, projector: Projector, origin: Vec3,
                    rotation: Optional[Sequence[float]], length: float, width: float) -> None:
        """Axis triad — the fastest way to spot a wrong quaternion basis change.

        With no rotation this draws the world axes; with one it draws that pose's
        local frame. A correct frame's X should run along the bone / forward.
        """
        if rotation is None:
            axes = ((1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0))
        else:
            axes = quat_axes(rotation)
        for axis, color in zip(axes, (COLORS["axis_x"], COLORS["axis_y"], COLORS["axis_z"])):
            self._segment(projector, origin, add(origin, scale(axis, length)), color, width)

    def _draw_body(self, projector: Projector, bp: Sequence[Optional[Vec3]],
                   rotations: Sequence[Optional[Sequence[float]]]) -> None:
        for a, b, chain in BODY_BONES:
            if bp[a] is not None and bp[b] is not None:
                self._segment(projector, bp[a], bp[b], COLORS[chain], 3.0)
        for index, point in enumerate(bp):
            if point is None:
                continue
            self._point(projector, point, FG, 3.5)
            if self.show_axes and rotations[index] is not None:
                self._draw_triad(projector, point, rotations[index], 0.07, 1.0)

    def _draw_hand(self, projector: Projector, points: Sequence[Optional[Vec3]]) -> None:
        for a, b in HAND_BONES:
            if points[a] is not None and points[b] is not None:
                self._segment(projector, points[a], points[b], COLORS["hand"], 1.5)
        for point in points:
            if point is not None:
                self._point(projector, point, COLORS["hand"], 2.0)

    # ---- verification panel ---------------------------------------------
    def _panel_text(self, packet: Optional[dict], connected: bool,
                    rate: float, status: str) -> str:
        rows: List[str] = []
        rows.append("STREAM")
        rows.append(f"  {'connected' if connected else 'DISCONNECTED'}   {rate:5.1f} Hz")
        rows.append(f"  {status[:46]}")
        if self.paused:
            rows.append("  ** PAUSED (space to resume) **")
        rows.append("")

        if not packet:
            rows.append("waiting for the first frame…")
            return "\n".join(rows)

        body = packet.get("body") if isinstance(packet.get("body"), dict) else None
        hands = by_side(packet.get("hands"))
        ctrls = by_side(packet.get("controllers"))

        rows.append("SOURCES")
        rows.append(f"  frame {packet.get('frame', '?')}   t={packet.get('timestamp', 0.0):.1f}s")
        hmd = packet.get("hmd") or {}
        rows.append(f"  hmd     {'ON' if hmd.get('tracked') else 'off'}")
        for side in ("left", "right"):
            hand = hands.get(side) or {}
            valid = sum(1 for lm in (hand.get("landmarks") or []) if lm.get("valid"))
            rows.append(f"  hand {side[0].upper()}  "
                        f"{'ON' if hand.get('tracked') else 'off':3}  {valid:2}/21")
        for side in ("left", "right"):
            rows.append(f"  ctrl {side[0].upper()}  "
                        f"{'ON' if (ctrls.get(side) or {}).get('tracked') else 'off'}")
        if body is None:
            rows.append("  body    field absent (old publisher?)")
        elif not body.get("tracked"):
            rows.append("  body    off  (permission pending?)")
        else:
            valid = sum(1 for j in (body.get("joints") or []) if j.get("valid"))
            rows.append(f"  body    ON   {valid:2}/18  "
                        f"fid={body.get('fidelity', '?')}  conf={body.get('confidence', 0.0):.2f}")
        rows.append("")

        rows.append("RIGID BONES        cur     spread")
        rows.append("  (spread must stay small while you move)")
        for key in ("upperarm L", "upperarm R", "forearm L", "forearm R", "shoulder span"):
            rows.append(self._stat_row(key, Metrics.RIGID_GOOD, Metrics.RIGID_BAD))
        rows.append("")

        rows.append("SOURCE SEAMS       cur     spread")
        for key in ("body↔hand L", "body↔hand R", "body↔ctrl L", "body↔ctrl R"):
            rows.append(self._stat_row(key, Metrics.SEAM_GOOD, Metrics.SEAM_BAD))
        rows.append(self._stat_row("body↔hmd", Metrics.SEAM_GOOD, Metrics.SEAM_BAD))
        rows.append("")

        rows.append("ARM CHAIN (left) — which parent order is real?")
        rows.append("  A = documented: chest→scap→shldr→arm")
        for key in ("A: chest→scap", "A: scap→shldr", "A: shldr→arm"):
            rows.append(self._stat_row(key, Metrics.RIGID_GOOD, Metrics.RIGID_BAD))
        rows.append("  B = alternative: chest→shldr→scap→arm")
        for key in ("B: chest→shldr", "B: shldr→scap", "B: scap→arm"):
            rows.append(self._stat_row(key, Metrics.RIGID_GOOD, Metrics.RIGID_BAD))
        rows.append("  " + self._chain_verdict())
        rows.append("")

        lo, hi = self.metrics.quat_lo, self.metrics.quat_hi
        if lo is None:
            rows.append("QUATERNIONS  (no samples yet)")
        else:
            verdict = "OK" if abs(lo - 1.0) < 0.01 and abs(hi - 1.0) < 0.01 else "BAD — not unit!"
            rows.append(f"QUATERNIONS  |q| {lo:.4f}..{hi:.4f}  {verdict}")
        rows.append("")
        rows.append("keys: r reset  f follow  a axes  b/h/d/g layers")
        rows.append("      space pause   q quit")
        return "\n".join(rows)

    def _chain_verdict(self) -> str:
        """Decide the arm's parent chain by total path length.

        Both candidate orderings visit the same four joints, so whichever chain
        is real is the one that walks the shorter total distance — the wrong
        ordering has to double back through the shoulder every segment. A clear
        winner here settles the one hierarchy claim in SCHEMA.md that was never
        confirmed against an OpenXR header.
        """
        def total(keys: Sequence[str]) -> Optional[float]:
            values = [self.metrics.stat(k).current for k in keys]
            return None if any(v is None for v in values) else sum(values)  # type: ignore[arg-type]

        a = total(("A: chest→scap", "A: scap→shldr", "A: shldr→arm"))
        b = total(("B: chest→shldr", "B: shldr→scap", "B: scap→arm"))
        if a is None or b is None:
            return "→ totals: waiting for a tracked left arm…"
        if abs(a - b) < 0.02:
            return f"→ totals A={a*100:.1f} B={b*100:.1f}cm — too close to call"
        winner = "A (as documented)" if a < b else "B — SCHEMA.md needs fixing"
        return f"→ totals A={a*100:.1f} B={b*100:.1f}cm — {winner}"

    def _stat_row(self, key: str, good: float, bad: float) -> str:
        stat = self.metrics.stat(key)
        if stat.current is None and stat.count == 0:
            return f"  {key:<16} —       —"
        current = f"{stat.current * 100:6.1f}" if stat.current is not None else "   ---"
        spread = stat.spread
        if spread is None:
            return f"  {key:<16}{current}cm    —"
        mark = " ok " if spread < good else ("WARN" if spread < bad else "BAD!")
        return f"  {key:<16}{current}cm {spread * 100:6.1f}cm {mark}"


# --------------------------------------------------------------------------

def main() -> None:
    parser = argparse.ArgumentParser(
        description="Live 3D verification viewer for the Meta Quest pose stream.")
    parser.add_argument("--host", required=True, help="headset IP address")
    parser.add_argument("--port", type=int, default=5555, help="publisher port (default 5555)")
    parser.add_argument("--fps", type=int, default=30, help="redraw rate (default 30)")
    args = parser.parse_args()

    client = StreamClient(args.host, args.port)
    client.start()

    root = tk.Tk()
    Viewer(root, client, args.fps)
    try:
        root.mainloop()
    finally:
        client.stop()


if __name__ == "__main__":
    main()
