"""
Mock PoseFramePublisher — a synthetic headset, for testing receivers without one.

Serves the exact SCHEMA.md wire format (TCP server, NDJSON, one frame per line)
driven by a simple animated upper-body model, so you can bring up and sanity-check
a receiver before a Quest is in the room.

    python Tools/mock_publisher.py --port 5556
    python Tools/skeleton_viewer.py --host 127.0.0.1 --port 5556

WHY THIS IS USEFUL FOR VERIFICATION

The model is rigid by construction: bone lengths are constants in the code, the
hand's WRIST landmark is placed exactly on the body's WRIST_TWIST joint, and every
quaternion is built from an orthonormal frame. So a correct receiver must report
~0 cm spread on every invariant and |q| = 1.0000.

That makes this a KNOWN-GOOD BASELINE. Run the viewer against the mock first: if
the panel is all "ok" here but shows drift against the real headset, the problem is
in the device data or the publisher — not in the viewer. If it already misbehaves
here, the receiver is at fault.

`--fault` deliberately breaks one thing at a time, so you can confirm the checks
actually fire before trusting them on real data:

    --fault stretch   forearm length oscillates      → "forearm" spread goes BAD
    --fault nonunit   quaternions scaled off unit    → quaternion check goes BAD
    --fault swap      scapula/shoulder swapped       → arm-chain A looks wrong,
                                                       arm-chain B looks right

No external dependencies — standard library only.
"""

import argparse
import json
import math
import socket
import threading
import time
from typing import Dict, List, Optional, Sequence, Tuple

Vec3 = Tuple[float, float, float]
Mat3 = Tuple[Vec3, Vec3, Vec3]        # rows

# --- fixed body proportions (metres). Rigid on purpose — see module docstring.
HIP_HEIGHT = 0.95
SHOULDER_HALF_SPAN = 0.18
UPPER_ARM = 0.28
FOREARM = 0.26
SCAPULA_OFFSET = 0.07

BODY_JOINT_COUNT = 18
HAND_LANDMARK_COUNT = 21


# --------------------------------------------------------------------------
# Tiny linear algebra
# --------------------------------------------------------------------------

def add(a: Vec3, b: Vec3) -> Vec3:
    return (a[0] + b[0], a[1] + b[1], a[2] + b[2])


def scale(a: Vec3, s: float) -> Vec3:
    return (a[0] * s, a[1] * s, a[2] * s)


def cross(a: Vec3, b: Vec3) -> Vec3:
    return (a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0])


def unit(a: Vec3) -> Vec3:
    n = math.sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2])
    return (0.0, 0.0, 1.0) if n < 1e-9 else (a[0] / n, a[1] / n, a[2] / n)


def rot_z(angle: float) -> Mat3:
    c, s = math.cos(angle), math.sin(angle)
    return ((c, -s, 0.0), (s, c, 0.0), (0.0, 0.0, 1.0))


def rot_y(angle: float) -> Mat3:
    c, s = math.cos(angle), math.sin(angle)
    return ((c, 0.0, s), (0.0, 1.0, 0.0), (-s, 0.0, c))


def matvec(m: Mat3, v: Vec3) -> Vec3:
    return (m[0][0] * v[0] + m[0][1] * v[1] + m[0][2] * v[2],
            m[1][0] * v[0] + m[1][1] * v[1] + m[1][2] * v[2],
            m[2][0] * v[0] + m[2][1] * v[1] + m[2][2] * v[2])


def matmul(a: Mat3, b: Mat3) -> Mat3:
    return tuple(
        tuple(sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3))
        for i in range(3)
    )  # type: ignore[return-value]


def frame_from_x(x_axis: Vec3) -> Mat3:
    """Right-handed orthonormal frame whose +X follows `x_axis`, +Z near world up.

    Returned as ROWS of the world-from-local matrix, i.e. the columns are the
    local axes expressed in world coordinates.
    """
    x = unit(x_axis)
    up = (0.0, 0.0, 1.0)
    if abs(x[2]) > 0.98:
        up = (1.0, 0.0, 0.0)
    y = unit(cross(up, x))
    z = cross(x, y)
    return ((x[0], y[0], z[0]),
            (x[1], y[1], z[1]),
            (x[2], y[2], z[2]))


def quat_from_matrix(m: Mat3) -> List[float]:
    """Rotation matrix -> quaternion in wire order (x, y, z, w)."""
    trace = m[0][0] + m[1][1] + m[2][2]
    if trace > 0.0:
        s = math.sqrt(trace + 1.0) * 2.0
        w = 0.25 * s
        x = (m[2][1] - m[1][2]) / s
        y = (m[0][2] - m[2][0]) / s
        z = (m[1][0] - m[0][1]) / s
    elif m[0][0] > m[1][1] and m[0][0] > m[2][2]:
        s = math.sqrt(1.0 + m[0][0] - m[1][1] - m[2][2]) * 2.0
        w = (m[2][1] - m[1][2]) / s
        x = 0.25 * s
        y = (m[0][1] + m[1][0]) / s
        z = (m[0][2] + m[2][0]) / s
    elif m[1][1] > m[2][2]:
        s = math.sqrt(1.0 + m[1][1] - m[0][0] - m[2][2]) * 2.0
        w = (m[0][2] - m[2][0]) / s
        x = (m[0][1] + m[1][0]) / s
        y = 0.25 * s
        z = (m[1][2] + m[2][1]) / s
    else:
        s = math.sqrt(1.0 + m[2][2] - m[0][0] - m[1][1]) * 2.0
        w = (m[1][0] - m[0][1]) / s
        x = (m[0][2] + m[2][0]) / s
        y = (m[1][2] + m[2][1]) / s
        z = 0.25 * s
    return [x, y, z, w]


# --------------------------------------------------------------------------
# Animated body model
# --------------------------------------------------------------------------

class BodyModel:
    """Upper body posed from a handful of joint angles driven by a clock."""

    def __init__(self, fault: str = "none") -> None:
        self.fault = fault

    def forearm_length(self, t: float) -> float:
        # The "stretch" fault breaks rigidity — exactly what a bad joint mapping
        # looks like on the wire.
        if self.fault == "stretch":
            return FOREARM + 0.05 * math.sin(t * 1.7)
        return FOREARM

    def pose(self, t: float) -> Tuple[List[Optional[Vec3]], List[Optional[List[float]]]]:
        torso_yaw = 0.25 * math.sin(t * 0.35)
        bob = 0.03 * math.sin(t * 1.3)
        torso = rot_z(torso_yaw)
        origin: Vec3 = (0.0, 0.0, HIP_HEIGHT + bob)

        def place(local: Vec3) -> Vec3:
            return add(origin, matvec(torso, local))

        positions: List[Optional[Vec3]] = [None] * BODY_JOINT_COUNT
        rotations: List[Optional[List[float]]] = [None] * BODY_JOINT_COUNT

        spine_locals = {
            0: (0.0, 0.0, -0.05),   # ROOT
            1: (0.0, 0.0, 0.00),    # HIPS
            2: (0.0, 0.0, 0.10),    # SPINE_LOWER
            3: (0.0, 0.0, 0.20),    # SPINE_MIDDLE
            4: (0.0, 0.0, 0.30),    # SPINE_UPPER
            5: (0.0, 0.0, 0.40),    # CHEST
            6: (0.0, 0.0, 0.52),    # NECK
            7: (0.0, 0.0, 0.62),    # HEAD
        }
        for index, local in spine_locals.items():
            positions[index] = place(local)
            rotations[index] = quat_from_matrix(torso)

        chest_local = spine_locals[5]

        # side: +1 left, -1 right. Indices follow SCHEMA.md's joint table.
        for side, (i_shoulder, i_scapula, i_upper, i_lower, i_wrist) in (
            (+1.0, (8, 9, 10, 11, 12)),
            (-1.0, (13, 14, 15, 16, 17)),
        ):
            phase = 0.0 if side > 0 else math.pi * 0.6
            shoulder_pitch = 0.9 + 0.6 * math.sin(t * 0.8 + phase)
            shoulder_yaw = 0.35 * math.sin(t * 0.5 + phase)
            elbow = 0.55 + 0.55 * math.sin(t * 1.1 + phase)

            scapula_local = add(chest_local, (0.0, side * SCAPULA_OFFSET, 0.03))
            shoulder_local = add(chest_local, (0.0, side * SHOULDER_HALF_SPAN, 0.05))
            upper_local = add(shoulder_local, (0.0, 0.0, -0.02))

            # Upper arm hangs down, then swings; the forearm adds elbow flex.
            r_shoulder = matmul(rot_z(side * shoulder_yaw), rot_y(shoulder_pitch))
            dir_upper = matvec(r_shoulder, (0.0, 0.0, -1.0))
            r_elbow = matmul(r_shoulder, rot_y(elbow))
            dir_fore = matvec(r_elbow, (0.0, 0.0, -1.0))

            lower_local = add(upper_local, scale(dir_upper, UPPER_ARM))
            wrist_local = add(lower_local, scale(dir_fore, self.forearm_length(t)))

            if self.fault == "swap":
                # Pretend the scapula and shoulder are the other way round — the
                # arm-chain readout should now favour ordering B over A.
                scapula_local, shoulder_local = shoulder_local, scapula_local

            positions[i_scapula] = place(scapula_local)
            positions[i_shoulder] = place(shoulder_local)
            positions[i_upper] = place(upper_local)
            positions[i_lower] = place(lower_local)
            positions[i_wrist] = place(wrist_local)

            frame_upper = frame_from_x(matvec(torso, dir_upper))
            frame_fore = frame_from_x(matvec(torso, dir_fore))
            rotations[i_scapula] = quat_from_matrix(torso)
            rotations[i_shoulder] = quat_from_matrix(frame_upper)
            rotations[i_upper] = quat_from_matrix(frame_upper)
            rotations[i_lower] = quat_from_matrix(frame_fore)
            rotations[i_wrist] = quat_from_matrix(frame_fore)

        return positions, rotations


def hand_landmarks(wrist: Vec3, forward: Vec3) -> List[Vec3]:
    """A flat 21-point hand rooted exactly on the body's wrist joint.

    Landmark 0 sits ON the wrist by construction, so the viewer's body↔hand seam
    check should read ~0 cm on the mock.
    """
    frame = frame_from_x(forward)
    x_axis = (frame[0][0], frame[1][0], frame[2][0])
    y_axis = (frame[0][1], frame[1][1], frame[2][1])

    points: List[Vec3] = [wrist]
    # (lateral offset, knuckle distance, segment length) per finger
    fingers = [(-0.035, 0.035, 0.030),   # thumb, set lower and shorter
               (-0.015, 0.085, 0.035),   # index
               (0.000, 0.088, 0.038),    # middle
               (0.015, 0.084, 0.034),    # ring
               (0.030, 0.076, 0.028)]    # pinky
    for lateral, knuckle, segment in fingers:
        base = add(wrist, add(scale(x_axis, knuckle), scale(y_axis, lateral)))
        for step in range(4):
            points.append(add(base, scale(x_axis, segment * step)))
    return points


# --------------------------------------------------------------------------
# Frame serialisation — must match SCHEMA.md exactly
# --------------------------------------------------------------------------

def fmt(value: float) -> float:
    return round(value, 6)


def pose_obj(position: Optional[Vec3], rotation: Optional[Sequence[float]]) -> dict:
    if position is None or rotation is None:
        return {"valid": False}
    return {"valid": True,
            "position": [fmt(c) for c in position],
            "rotation": [fmt(c) for c in rotation]}


def build_frame(model: BodyModel, t: float, frame_index: int) -> dict:
    positions, rotations = model.pose(t)

    if model.fault == "nonunit":
        rotations = [None if q is None else [c * 1.05 for c in q] for q in rotations]

    head = positions[7] or (0.0, 0.0, 1.6)
    torso_yaw = 0.25 * math.sin(t * 0.35)
    head_frame = rot_z(torso_yaw)
    hmd_position = add(head, matvec(head_frame, (0.09, 0.0, 0.06)))
    hmd_rotation = quat_from_matrix(head_frame)

    hands = []
    controllers = []
    for side, i_lower, i_wrist in (("left", 11, 12), ("right", 16, 17)):
        wrist = positions[i_wrist]
        lower = positions[i_lower]
        if wrist is None or lower is None:
            hands.append({"side": side, "tracked": False})
            controllers.append({"side": side, "tracked": False})
            continue

        forward = unit((wrist[0] - lower[0], wrist[1] - lower[1], wrist[2] - lower[2]))
        points = hand_landmarks(wrist, forward)
        rotation = quat_from_matrix(frame_from_x(forward))
        hands.append({
            "side": side,
            "tracked": True,
            "palm": pose_obj(add(wrist, scale(forward, 0.045)), rotation),
            "landmarks": [
                {"i": i, "valid": True,
                 "position": [fmt(c) for c in points[i]],
                 "rotation": [fmt(c) for c in rotation]}
                for i in range(HAND_LANDMARK_COUNT)
            ],
        })
        controllers.append({
            "side": side,
            "tracked": True,
            "controller": pose_obj(add(wrist, scale(forward, 0.03)), rotation),
            "grip_trigger": fmt(0.5 + 0.5 * math.sin(t * 1.9)),
            "ctrl_trigger": fmt(0.5 + 0.5 * math.sin(t * 1.3)),
            "thumbstick": [fmt(math.sin(t)), fmt(math.cos(t))],
            "buttons": {"primary": math.sin(t * 2.0) > 0.8, "secondary": False,
                        "menu": False, "thumbstick": False},
        })

    return {
        "timestamp": fmt(t),
        "frame": frame_index,
        "coordinate_system": "robot_world",
        "hmd": {"tracked": True,
                "position": [fmt(c) for c in hmd_position],
                "rotation": [fmt(c) for c in hmd_rotation]},
        "hands": hands,
        "controllers": controllers,
        "body": {
            "tracked": True,
            "fidelity": "high",
            "confidence": 0.97,
            "joints": [
                {"i": i, "valid": True,
                 "position": [fmt(c) for c in positions[i]],
                 "rotation": [fmt(c) for c in rotations[i]]}
                if positions[i] is not None and rotations[i] is not None
                else {"i": i, "valid": False}
                for i in range(BODY_JOINT_COUNT)
            ],
        },
    }


# --------------------------------------------------------------------------
# Server — same publish-and-forget broadcast model as the real publisher
# --------------------------------------------------------------------------

class MockPublisher:
    def __init__(self, port: int, rate: float, fault: str) -> None:
        self.port = port
        self.rate = rate
        self.model = BodyModel(fault)
        self.clients: List[socket.socket] = []
        self.lock = threading.Lock()
        self.stop = threading.Event()

    def serve_forever(self) -> None:
        listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        listener.bind(("0.0.0.0", self.port))
        listener.listen(8)
        print(f"[mock] TCP NDJSON server on 0.0.0.0:{self.port} "
              f"({self.rate:.0f} Hz, fault={self.model.fault})")

        threading.Thread(target=self._accept_loop, args=(listener,), daemon=True).start()
        try:
            self._publish_loop()
        except KeyboardInterrupt:
            print("\n[mock] stopping")
        finally:
            self.stop.set()
            try:
                listener.close()
            except OSError:
                pass

    def _accept_loop(self, listener: socket.socket) -> None:
        while not self.stop.is_set():
            try:
                sock, addr = listener.accept()
            except OSError:
                break
            sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            with self.lock:
                self.clients.append(sock)
            print(f"[mock] client connected: {addr}")

    def _publish_loop(self) -> None:
        period = 1.0 / self.rate
        start = time.monotonic()
        index = 0
        while not self.stop.is_set():
            t = time.monotonic() - start
            payload = (json.dumps(build_frame(self.model, t, index),
                                  separators=(",", ":")) + "\n").encode("utf-8")
            index += 1

            with self.lock:
                targets = list(self.clients)
            for sock in targets:
                try:
                    sock.sendall(payload)
                except OSError:
                    with self.lock:
                        if sock in self.clients:
                            self.clients.remove(sock)
                    try:
                        sock.close()
                    except OSError:
                        pass
                    print("[mock] client dropped")

            time.sleep(max(0.0, period - ((time.monotonic() - start) - t)))


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Synthetic Meta Quest pose publisher (SCHEMA.md compatible).")
    parser.add_argument("--port", type=int, default=5556, help="listen port (default 5556)")
    parser.add_argument("--rate", type=float, default=72.0, help="frames per second (default 72)")
    parser.add_argument("--fault", default="none",
                        choices=("none", "stretch", "nonunit", "swap"),
                        help="inject a specific defect so receiver checks can be proven to fire")
    args = parser.parse_args()
    MockPublisher(args.port, args.rate, args.fault).serve_forever()


if __name__ == "__main__":
    main()
